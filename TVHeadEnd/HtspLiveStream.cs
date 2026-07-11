using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;

namespace TVHeadEnd
{
    public class HtspLiveStreamException : IOException
    {
        private HtspLiveStreamException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public static HtspLiveStreamException Create(string channelId, Exception innerException)
        {
            var reason = innerException == null || string.IsNullOrWhiteSpace(innerException.Message)
                ? "unknown HTSP error"
                : innerException.Message;

            return new HtspLiveStreamException(
                "HTSP streaming is selected and TVHeadend HTTP fallback is disabled. " +
                "Unable to open HTSP live stream for channel '" + channelId + "': " + reason,
                innerException);
        }
    }

    public class HtspLiveStream : ILiveStream, IDirectStreamProvider, HTSConnectionListener
    {
        private const int MaxOpenAttempts = 3;
        private const int MaxLiveReconnectAttempts = 5;
        private const long StartupCacheMaxBytes = 32L * 1024L * 1024L;
        private static readonly byte[] H264AccessUnitDelimiter = { 0x00, 0x00, 0x00, 0x01, 0x09, 0xF0 };
        private const int MinStallWatchdogSeconds = 5;
        private const int MaxStallWatchdogSeconds = 120;
        private const int MaxHtspQueueDepth = 20000000;
        private const int SignalErrorWindowSeconds = 5;
        private const int SignalRecoveryAttemptWindowSeconds = 60;

        private static readonly ConcurrentDictionary<string, HtspLiveStream> SharedHubsByChannelId = new ConcurrentDictionary<string, HtspLiveStream>();
        private static readonly ConcurrentDictionary<string, HtspLiveStream> ActiveProducersByUniqueId = new ConcurrentDictionary<string, HtspLiveStream>();

        private static readonly TimeSpan SubscribeResponseTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan FirstPacketTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ReaderIdleCloseDelay = TimeSpan.FromSeconds(10);

        private static int _nextSubscriptionId;

        private readonly string _channelId;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServerApplicationHost _appHost;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<HtspLiveStream> _logger;
        private readonly BlockingByteStream _stream;
        private readonly HtspTransportStreamMuxer _muxer = new HtspTransportStreamMuxer();
        private readonly TaskCompletionSource<bool> _firstPacket = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<int> _ignoredMuxStreams = new HashSet<int>();
        private readonly Dictionary<int, HtspTransportStreamMuxer.StreamInfo> _muxedStreamsByIndex = new Dictionary<int, HtspTransportStreamMuxer.StreamInfo>();
        private readonly Dictionary<int, long> _muxPacketCounts = new Dictionary<int, long>();
        private readonly Dictionary<int, long> _muxPacketBytes = new Dictionary<int, long>();
        private readonly Dictionary<int, long> _muxKeyFrameCounts = new Dictionary<int, long>();
        private readonly object _muxPacketStatsLock = new object();
        private readonly object _signalStateLock = new object();
        private readonly object _connectionStateLock = new object();
        private readonly object _clientAbortSync = new object();
        private readonly object _readerStateLock = new object();
        private readonly object _broadcastLock = new object();
        private readonly object _sharedReferenceLock = new object();
        private readonly object _producerOpenLock = new object();
        private readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new CancellationTokenSource();

        private CancellationTokenRegistration _clientAbortRegistration;
        private CancellationTokenSource _readerIdleDisconnectCancellationTokenSource;
        private CancellationTokenSource _sharedHubIdleCloseCancellationTokenSource;
        private CancellationTokenSource _stallWatchdogCancellationTokenSource;
        private Task _stallWatchdogTask;
        private HTSConnectionAsync _connection;
        private TaskCompletionSource<bool> _connectionFirstPacket = CreateFirstPacketSource();
        private TaskCompletionSource<Exception> _connectionError = CreateConnectionErrorSource();
        private int _subscriptionId;
        private int _reconnectScheduled;
        private int _liveReconnectAttempts;
        private int _closeStarted;
        private int _playbackCloseStarted;
        private int _activeStreamReaders;
        private int _lastMetadataSubscriptionId;
        private int _lastFilteredSubscriptionId;
        private int _stallWatchdogTriggered;
        private int _awaitingCleanVideoRandomAccess;
        private int _signalRecoveryReconnectScheduled;
        private int _signalRecoveryAttempts;
        private int _signalRecoverySuppressionLogged;
        private bool _closing;
        private bool _started;
        private bool _recovering;
        private bool _registeredAsSharedHub;
        private HtspLiveStream _sharedProducer;
        private Task _producerOpenTask;
        private DateTime _lastMuxPacketStatsLogUtc = DateTime.MinValue;
        private DateTime _producerOpenedUtc = DateTime.MinValue;
        private long _startupCacheBytes;
        private long _lastPlayableMuxPacketUtcTicks;
        private long _lastQueueIdrops;
        private long _lastQueuePdrops;
        private long _lastQueueBdrops;
        private long _lastQueuePackets;
        private long _lastQueueBytes;
        private long _lastQueueDelayUs;
        private long _frontendUnlockSinceUtcTicks;
        private long _videoDamageSinceUtcTicks;
        private long _signalErrorWindowStartUtcTicks;
        private long _signalErrorWindowUncIncrease;
        private long _lastSignalRecoveryUtcTicks;
        private long _signalRecoveryAttemptWindowStartUtcTicks;
        private SignalSnapshot _signalSnapshot = new SignalSnapshot();
        private string _sourceAdapter;
        private string _sourceService;
        private string _sourceNetwork;
        private string _sourceMux;
        private string _sourceProvider;
        private int? _primaryVideoStreamIndex;
        private bool _startupCacheKeyframeAligned;
        private bool _startupCacheOverflowLogged;
        private byte[] _cachedH264Sps;
        private byte[] _cachedH264Pps;
        private byte[] _cachedHevcVps;
        private byte[] _cachedHevcSps;
        private byte[] _cachedHevcPps;
        private readonly Queue<byte[]> _startupCache = new Queue<byte[]>();
        private readonly HashSet<BlockingByteStream> _pendingBootstrapQueues = new HashSet<BlockingByteStream>();
        private readonly Dictionary<string, List<BlockingByteStream>> _consumerQueuesByOwner = new Dictionary<string, List<BlockingByteStream>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CancellationTokenSource> _ownerIdleDisconnects = new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _sharedPlaybackReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private sealed class SignalSnapshot
        {
            public DateTime UpdatedUtc { get; set; }

            public string Status { get; set; }

            public int? SignalRaw { get; set; }

            public long? SignalAbsolute { get; set; }

            public int? SnrRaw { get; set; }

            public long? SnrAbsolute { get; set; }

            public long? Ber { get; set; }

            public long? Unc { get; set; }

            public SignalSnapshot Clone()
            {
                return (SignalSnapshot)MemberwiseClone();
            }
        }

        private static TaskCompletionSource<bool> CreateFirstPacketSource()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static TaskCompletionSource<Exception> CreateConnectionErrorSource()
        {
            return new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal string ChannelId => _channelId;

        private bool IsSharedProxy => _sharedProducer != null && !ReferenceEquals(_sharedProducer, this);

        private bool IsSharedHubUsable
        {
            get
            {
                return !_closing
                    && !_lifetimeCancellationTokenSource.IsCancellationRequested
                    && !_stream.IsCompleted;
            }
        }

        public HtspLiveStream(MediaSourceInfo mediaSource, string channelId, ILoggerFactory loggerFactory, IServerApplicationHost appHost, IHttpContextAccessor httpContextAccessor)
        {
            MediaSource = mediaSource;
            _channelId = channelId;
            _loggerFactory = loggerFactory;
            _appHost = appHost;
            _httpContextAccessor = httpContextAccessor;
            _logger = loggerFactory.CreateLogger<HtspLiveStream>();
            _stream = new BlockingByteStream(OnConsumerClosed);
            UniqueId = Guid.NewGuid().ToString("N");
            OriginalStreamId = mediaSource?.Id;
            ConsumerCount = 1;
            EnableStreamSharing = false;
        }

        public int ConsumerCount { get; set; }

        public string OriginalStreamId { get; set; }

        public string TunerHostId => null;

        public bool EnableStreamSharing { get; set; }

        public MediaSourceInfo MediaSource { get; set; }

        public string UniqueId { get; }

        public async Task Open(CancellationToken openCancellationToken)
        {
            if (GetConfiguredStreamSharingEnabled())
            {
                await OpenThroughSharedHubAsync(openCancellationToken).ConfigureAwait(false);
                return;
            }

            _sharedProducer = this;
            AddSharedPlaybackReference(UniqueId);
            try
            {
                await EnsureProducerOpenAsync(openCancellationToken).ConfigureAwait(false);
                ConfigureOpenedMediaSourceFromHub(this);
                _logger.LogInformation(
                    "HTSP standalone producer opened for channel {ChannelId}; upstream subscription {SubscriptionId}; playback {PlaybackId}",
                    _channelId,
                    _subscriptionId,
                    UniqueId);
            }
            catch
            {
                ReleaseSharedPlaybackReference(UniqueId, "failed to open standalone producer");
                await CloseProducerNow("failed to open standalone producer").ConfigureAwait(false);
                throw;
            }
        }

        private async Task OpenProducerAsync(CancellationToken openCancellationToken)
        {
            using var openLifetime = CancellationTokenSource.CreateLinkedTokenSource(openCancellationToken, _lifetimeCancellationTokenSource.Token);
            var openToken = openLifetime.Token;
            var config = Plugin.Instance.Configuration;
            var hostname = config.TVH_ServerName.Trim();
            var username = config.Username.Trim();
            var password = config.Password.Trim();
            var profile = config.Profile?.Trim();
            Exception lastException = null;

            for (var attempt = 1; attempt <= MaxOpenAttempts; attempt++)
            {
                openToken.ThrowIfCancellationRequested();

                try
                {
                    await ConnectAndSubscribeAsync(
                        hostname,
                        config.HTSP_Port,
                        username,
                        password,
                        profile,
                        openToken,
                        waitForMuxPacket: true).ConfigureAwait(false);

                    ConfigureOpenedMediaSource();
                    _producerOpenedUtc = DateTime.UtcNow;
                    ActiveProducersByUniqueId[UniqueId] = this;
                    EnsureStallWatchdogStarted();
                    return;
                }
                catch (OperationCanceledException) when (openCancellationToken.IsCancellationRequested || _closing || _lifetimeCancellationTokenSource.IsCancellationRequested)
                {
                    await Close().ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    CloseCurrentConnection(unsubscribe: false);

                    if (attempt >= MaxOpenAttempts)
                    {
                        break;
                    }

                    var delay = GetRetryDelay(attempt);
                    _logger.LogWarning(
                        ex,
                        "HTSP live stream open attempt {Attempt}/{MaxAttempts} failed for channel {ChannelId}; retrying in {DelayMs}ms",
                        attempt,
                        MaxOpenAttempts,
                        _channelId,
                        (int)delay.TotalMilliseconds);

                    await Task.Delay(delay, openToken).ConfigureAwait(false);
                }
            }

            await Close().ConfigureAwait(false);

            var htspException = HtspLiveStreamException.Create(_channelId, lastException);
            _logger.LogError(htspException, "HTSP live stream open failed for channel {ChannelId}; TVHeadend HTTP fallback is disabled", _channelId);
            throw htspException;
        }

        private async Task OpenThroughSharedHubAsync(CancellationToken openCancellationToken)
        {
            while (true)
            {
                if (SharedHubsByChannelId.TryGetValue(_channelId, out var existingHub))
                {
                    if (!existingHub.IsSharedHubUsable)
                    {
                        SharedHubsByChannelId.TryRemove(_channelId, out _);
                        continue;
                    }

                    _sharedProducer = existingHub;
                    existingHub.AddSharedPlaybackReference(UniqueId);

                    try
                    {
                        await existingHub.EnsureProducerOpenAsync(openCancellationToken).ConfigureAwait(false);
                        ConfigureOpenedMediaSourceFromHub(existingHub);

                        _logger.LogInformation(
                            "HTSP shared channel hub attached playback {PlaybackId} to channel {ChannelId}; active shared playback count is {PlaybackCount}",
                            UniqueId,
                            _channelId,
                            existingHub.GetSharedPlaybackReferenceCount());
                        return;
                    }
                    catch
                    {
                        existingHub.ReleaseSharedPlaybackReference(UniqueId, "failed to attach shared playback");
                        _sharedProducer = null;
                        throw;
                    }
                }

                _registeredAsSharedHub = true;
                _sharedProducer = this;
                if (!SharedHubsByChannelId.TryAdd(_channelId, this))
                {
                    _registeredAsSharedHub = false;
                    _sharedProducer = null;
                    continue;
                }

                AddSharedPlaybackReference(UniqueId);

                try
                {
                    await EnsureProducerOpenAsync(openCancellationToken).ConfigureAwait(false);
                    ConfigureOpenedMediaSourceFromHub(this);
                    _logger.LogInformation(
                        "HTSP shared channel hub opened for channel {ChannelId}; upstream subscription {SubscriptionId}; playback {PlaybackId}",
                        _channelId,
                        _subscriptionId,
                        UniqueId);
                    return;
                }
                catch
                {
                    ReleaseSharedPlaybackReference(UniqueId, "failed to open shared channel hub");
                    SharedHubsByChannelId.TryRemove(_channelId, out _);
                    _registeredAsSharedHub = false;
                    _sharedProducer = null;
                    await CloseProducerNow("failed to open shared channel hub").ConfigureAwait(false);
                    throw;
                }
            }
        }

        private Task EnsureProducerOpenAsync(CancellationToken openCancellationToken)
        {
            lock (_producerOpenLock)
            {
                if (_producerOpenTask == null || (_producerOpenTask.IsCompleted && !_started && !_closing))
                {
                    _producerOpenTask = OpenProducerAsync(openCancellationToken);
                }

                return _producerOpenTask;
            }
        }

        private void ConfigureOpenedMediaSourceFromHub(HtspLiveStream hub)
        {
            var liveStreamPath = "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
            MediaSource.Id = UniqueId;
            MediaSource.Path = GetClientApiBaseUrl() + liveStreamPath;
            MediaSource.EncoderPath = _appHost.GetApiUrlForLocalAccess() + liveStreamPath;
            MediaSource.EncoderProtocol = MediaProtocol.Http;
            MediaSource.Protocol = MediaProtocol.Http;
            MediaSource.Container = "ts";
            MediaSource.GenPtsInput = true;
            MediaSource.UseMostCompatibleTranscodingProfile = true;
            MediaSource.SupportsDirectPlay = true;
            MediaSource.SupportsDirectStream = true;
            MediaSource.SupportsTranscoding = true;
            MediaSource.SupportsProbing = false;
            MediaSource.RequiresOpening = true;
            MediaSource.RequiresClosing = true;
            MediaSource.IsInfiniteStream = true;
            MediaSource.AnalyzeDurationMs = 2000;

            if (hub?.MediaSource?.MediaStreams != null && hub.MediaSource.MediaStreams.Count > 0)
            {
                MediaSource.MediaStreams = hub.MediaSource.MediaStreams.Select(CloneMediaStream).ToList();
                MediaSource.DefaultAudioStreamIndex = hub.MediaSource.DefaultAudioStreamIndex;
                MediaSource.DefaultSubtitleStreamIndex = hub.MediaSource.DefaultSubtitleStreamIndex;
                MediaSource.Bitrate = hub.MediaSource.Bitrate;
            }
        }

        private static MediaStream CloneMediaStream(MediaStream source)
        {
            if (source == null)
            {
                return null;
            }

            var target = new MediaStream();
            foreach (var property in typeof(MediaStream).GetProperties().Where(i => i.CanRead && i.CanWrite && i.GetIndexParameters().Length == 0))
            {
                try
                {
                    property.SetValue(target, property.GetValue(source));
                }
                catch
                {
                }
            }

            return target;
        }

        private void ConfigureOpenedMediaSource()
        {
            var liveStreamPath = "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
            MediaSource.Id = UniqueId;
            MediaSource.Path = GetClientApiBaseUrl() + liveStreamPath;
            MediaSource.EncoderPath = _appHost.GetApiUrlForLocalAccess() + liveStreamPath;
            MediaSource.EncoderProtocol = MediaProtocol.Http;
            MediaSource.Protocol = MediaProtocol.Http;
            MediaSource.Container = "ts";
            MediaSource.GenPtsInput = true;
            // Prefer the most-compatible live TV transcoding profile for HTSP so
            // clients that need server-side HLS get TS/H.264-family output rather
            // than fMP4/AV1 when the server cannot open av1_vaapi.
            MediaSource.UseMostCompatibleTranscodingProfile = true;
            MediaSource.SupportsDirectPlay = true;
            MediaSource.SupportsDirectStream = true;
            MediaSource.SupportsTranscoding = true;
        }

        private async Task ConnectAndSubscribeAsync(
            string hostname,
            int port,
            string username,
            string password,
            string profile,
            CancellationToken cancellationToken,
            bool waitForMuxPacket)
        {
            await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_closing || _lifetimeCancellationTokenSource.IsCancellationRequested || _stream.IsCompleted)
                {
                    throw new OperationCanceledException("HTSP live stream is closing.", cancellationToken);
                }

                CloseCurrentConnection(unsubscribe: false);
                ResetConnectionAttemptSignals();
                ResetStallWatchdogClock();
                ResetQueueDiagnostics();
                ResetStartupCacheForNewSubscription(clearParameterSets: false);

                _subscriptionId = Interlocked.Increment(ref _nextSubscriptionId);
                var connection = new HTSConnectionAsync(this, "TVHclient4Jellyfin-HTSP", "" + HTSMessage.HTSP_VERSION, _loggerFactory);
                lock (_connectionStateLock)
                {
                    _connection = connection;
                }

                connection.open(hostname, port, cancellationToken, maxAttempts: 1);
                if (!connection.authenticate(username, password, false, cancellationToken, SubscribeResponseTimeout))
                {
                    throw new InvalidOperationException("TVHeadend HTSP authentication failed.");
                }

                LogServerCapabilities(connection);

                var subscribe = BuildSubscribeMessage(profile);
                var response = new TaskResponseHandler();
                connection.sendMessage(subscribe, response);

                var subscribeTask = response.Task;
                var errorTask = GetConnectionErrorTask();
                var subscribeTimeoutTask = Task.Delay(SubscribeResponseTimeout, cancellationToken);
                var subscribeResult = await Task.WhenAny(subscribeTask, errorTask, subscribeTimeoutTask).ConfigureAwait(false);

                if (subscribeResult == errorTask)
                {
                    throw await errorTask.ConfigureAwait(false);
                }

                if (subscribeResult != subscribeTask)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("Timed out waiting for HTSP subscribe response.");
                }

                ParseSubscribeResponse(await subscribeTask.ConfigureAwait(false));

                if (waitForMuxPacket)
                {
                    var firstPacketTask = GetConnectionFirstPacketTask();
                    errorTask = GetConnectionErrorTask();
                    var firstPacketTimeoutTask = Task.Delay(FirstPacketTimeout, cancellationToken);
                    var firstPacketResult = await Task.WhenAny(firstPacketTask, errorTask, firstPacketTimeoutTask).ConfigureAwait(false);

                    if (firstPacketResult == errorTask)
                    {
                        throw await errorTask.ConfigureAwait(false);
                    }

                    if (firstPacketResult != firstPacketTask)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("Timed out waiting for the first HTSP mux packet.");
                    }

                    await firstPacketTask.ConfigureAwait(false);
                }
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        private HTSMessage BuildSubscribeMessage(string profile)
        {
            var subscribe = new HTSMessage { Method = "subscribe" };
            subscribe.putField("channelId", HtspFieldHelper.ParseUInt32Id(_channelId, "channelId"));
            subscribe.putField("subscriptionId", _subscriptionId);
            subscribe.putField("weight", 100);
            subscribe.putField("90khz", 1);
            subscribe.putField("normts", 1);

            var queueDepth = GetConfiguredQueueDepth();
            if (queueDepth > 0)
            {
                subscribe.putField("queueDepth", queueDepth);
            }

            if (!string.IsNullOrWhiteSpace(profile))
            {
                subscribe.putField("profile", profile.Trim());
            }

            return subscribe;
        }

        private static int GetConfiguredQueueDepth()
        {
            var configured = Plugin.Instance?.Configuration?.HTSPQueueDepth ?? 0;
            return Math.Max(0, Math.Min(MaxHtspQueueDepth, configured));
        }

        private static int GetConfiguredStallTimeoutSeconds()
        {
            var configured = Plugin.Instance?.Configuration?.HTSPStallTimeoutSeconds ?? 0;
            if (configured <= 0)
            {
                return 0;
            }

            return Math.Max(MinStallWatchdogSeconds, Math.Min(MaxStallWatchdogSeconds, configured));
        }

        private void LogServerCapabilities(HTSConnectionAsync connection)
        {
            if (connection == null)
            {
                return;
            }

            var capabilities = connection.getServerCapabilities();
            _logger.LogInformation(
                "HTSP server hello: name={ServerName}, version={ServerVersion}, protocol server/client/negotiated={ServerProtocol}/{ClientProtocol}/{NegotiatedProtocol}, capabilities={Capabilities}, webroot={WebRoot}",
                connection.getServername(),
                connection.getServerversion(),
                connection.getServerProtocolVersion(),
                HTSMessage.HTSP_CLIENT_VERSION,
                connection.getNegotiatedProtocolVersion(),
                capabilities.Count > 0 ? string.Join(",", capabilities.OrderBy(i => i, StringComparer.OrdinalIgnoreCase)) : "<none>",
                string.IsNullOrWhiteSpace(connection.getServerWebRoot()) ? "/" : connection.getServerWebRoot());

            if ((Plugin.Instance?.Configuration?.HTSPFilterControlStreams ?? false)
                && connection.getNegotiatedProtocolVersion() < 12)
            {
                _logger.LogWarning(
                    "HTSP control-stream filtering is enabled, but negotiated protocol {ProtocolVersion} is below v12; subscriptionFilterStream will be skipped",
                    connection.getNegotiatedProtocolVersion());
            }
        }

        private string GetClientApiBaseUrl()
        {
            try
            {
                var request = _httpContextAccessor?.HttpContext?.Request;
                if (request != null)
                {
                    return _appHost.GetSmartApiUrl(request).TrimEnd('/');
                }

                return _appHost.GetSmartApiUrl(string.Empty).TrimEnd('/');
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to resolve a client-facing Jellyfin URL; falling back to the local API URL for HTSP live stream {UniqueId}", UniqueId);
                return _appHost.GetApiUrlForLocalAccess().TrimEnd('/');
            }
        }

        public Task Close()
        {
            if (Interlocked.Exchange(ref _playbackCloseStarted, 1) != 0)
            {
                return Task.CompletedTask;
            }

            if (IsSharedProxy)
            {
                CloseOwnedConsumerQueues(UniqueId, "shared playback instance closed");
                _sharedProducer.ReleaseSharedPlaybackReference(UniqueId, "shared playback instance closed");
                return Task.CompletedTask;
            }

            CloseOwnedConsumerQueues(UniqueId, "shared playback instance closed");
            var remainingReferences = ReleaseSharedPlaybackReference(UniqueId, "shared playback instance closed");
            if (remainingReferences > 0)
            {
                _logger.LogInformation(
                    "HTSP shared channel hub for channel {ChannelId} kept open after playback {PlaybackId} closed; {RemainingPlaybackCount} shared playback(s) remain",
                    _channelId,
                    UniqueId,
                    remainingReferences);
                return Task.CompletedTask;
            }

            ScheduleSharedHubIdleClose("last shared playback instance closed");
            return Task.CompletedTask;
        }

        private Task CloseProducerNow(string reason)
        {
            if (Interlocked.Exchange(ref _closeStarted, 1) != 0)
            {
                return Task.CompletedTask;
            }

            _closing = true;
            EnableStreamSharing = false;
            _lifetimeCancellationTokenSource.Cancel();
            CancelReaderIdleDisconnect();
            CancelSharedHubIdleClose();
            CancelAllOwnerIdleDisconnects();
            CancelStallWatchdog();
            DisposeClientAbortRegistration();
            CompleteAllConsumerQueues();
            _stream.Complete();
            CloseCurrentConnection(unsubscribe: true);
            ActiveProducersByUniqueId.TryRemove(UniqueId, out _);

            if (_registeredAsSharedHub)
            {
                SharedHubsByChannelId.TryRemove(_channelId, out _);
                _registeredAsSharedHub = false;
            }

            _logger.LogInformation(
                "HTSP shared channel hub closed for channel {ChannelId}; subscription {SubscriptionId} stopped. Reason: {Reason}",
                _channelId,
                _subscriptionId,
                reason);

            return Task.CompletedTask;
        }

        public Stream GetStream()
        {
            // Each Jellyfin playback instance gets a unique stream URL, but all
            // instances for the same TVHeadend channel attach to the same upstream
            // HTSP hub.  Do not return the producer queue directly: every reader
            // needs its own bounded queue so one client cannot consume bytes that
            // another client needs or block every other client.
            var hub = IsSharedProxy ? _sharedProducer : this;
            return hub.CreateFanoutReader(UniqueId);
        }

        private Stream CreateFanoutReader(string playbackId)
        {
            var readerId = Guid.NewGuid().ToString("N");
            BlockingByteStream queue = null;
            queue = new BlockingByteStream(reason => OnBroadcastQueueClosed(playbackId, queue, reason));

            lock (_broadcastLock)
            {
                if (_closing || _lifetimeCancellationTokenSource.IsCancellationRequested || _stream.IsCompleted)
                {
                    queue.Complete();
                }
                else
                {
                    if (!_consumerQueuesByOwner.TryGetValue(playbackId, out var queues))
                    {
                        queues = new List<BlockingByteStream>();
                        _consumerQueuesByOwner[playbackId] = queues;
                    }

                    queues.Add(queue);
                    if (GetConfiguredKeyframeStartupEnabled() && _primaryVideoStreamIndex.HasValue && !_startupCacheKeyframeAligned)
                    {
                        _pendingBootstrapQueues.Add(queue);
                        _logger.LogDebug(
                            "HTSP shared reader {ReaderId} for channel {ChannelId} is waiting for a keyframe-aligned startup cache",
                            readerId,
                            _channelId);
                    }
                    else
                    {
                        foreach (var cachedChunk in _startupCache)
                        {
                            queue.WriteChunk(cachedChunk);
                        }
                    }
                }
            }

            OnStreamReaderOpened(readerId);
            return new ConsumerReadStream(
                queue,
                hasRead => OnBroadcastReaderClosed(playbackId, queue, readerId, hasRead),
                _logger,
                _channelId,
                readerId);
        }

        private void OnBroadcastReaderClosed(string playbackId, BlockingByteStream queue, string readerId, bool consumedBytes)
        {
            var removed = RemoveConsumerQueue(playbackId, queue, complete: true);
            if (!removed)
            {
                return;
            }

            int activeReaders;
            lock (_readerStateLock)
            {
                if (_activeStreamReaders > 0)
                {
                    _activeStreamReaders--;
                }

                activeReaders = _activeStreamReaders;
            }

            if (_closing || _lifetimeCancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            if (!consumedBytes)
            {
                _logger.LogDebug(
                    "HTSP shared reader {ReaderId} for channel {ChannelId}, playback {PlaybackId}, was disposed before consuming bytes; keeping shared hub open",
                    readerId,
                    _channelId,
                    playbackId);
                return;
            }

            _logger.LogDebug(
                "HTSP shared reader {ReaderId} closed for channel {ChannelId}, playback {PlaybackId}; active shared reader count is {ActiveReaderCount}",
                readerId,
                _channelId,
                playbackId,
                activeReaders);

            if (!OwnerHasConsumerQueues(playbackId))
            {
                ScheduleOwnerIdleDisconnect(playbackId, "Jellyfin disposed the direct stream reader after consuming data");
            }
        }

        private void OnBroadcastQueueClosed(string playbackId, BlockingByteStream queue, string reason)
        {
            RemoveConsumerQueue(playbackId, queue, complete: true);
            if (!OwnerHasConsumerQueues(playbackId))
            {
                _logger.LogInformation(
                    "HTSP shared playback {PlaybackId} for channel {ChannelId} stopped because its reader queue closed. Reason: {Reason}",
                    playbackId,
                    _channelId,
                    reason);
                ReleaseSharedPlaybackReference(playbackId, reason);
            }
        }

        private bool RemoveConsumerQueue(string playbackId, BlockingByteStream queue, bool complete)
        {
            if (queue == null)
            {
                return false;
            }

            var removed = false;
            lock (_broadcastLock)
            {
                if (_consumerQueuesByOwner.TryGetValue(playbackId, out var queues))
                {
                    removed = queues.Remove(queue);
                    _pendingBootstrapQueues.Remove(queue);
                    if (queues.Count == 0)
                    {
                        _consumerQueuesByOwner.Remove(playbackId);
                    }
                }
            }

            if (removed && complete)
            {
                queue.Complete();
            }

            return removed;
        }

        private bool OwnerHasConsumerQueues(string playbackId)
        {
            lock (_broadcastLock)
            {
                return _consumerQueuesByOwner.TryGetValue(playbackId, out var queues) && queues.Count > 0;
            }
        }

        private void CloseOwnedConsumerQueues(string playbackId, string reason)
        {
            List<BlockingByteStream> queues = null;
            lock (_broadcastLock)
            {
                if (_consumerQueuesByOwner.TryGetValue(playbackId, out queues))
                {
                    _consumerQueuesByOwner.Remove(playbackId);
                    queues = queues.ToList();
                    foreach (var queue in queues)
                    {
                        _pendingBootstrapQueues.Remove(queue);
                    }
                }
            }

            if (queues != null)
            {
                foreach (var queue in queues)
                {
                    queue.Complete();
                }
            }

            CancelOwnerIdleDisconnect(playbackId);
        }

        private void CompleteAllConsumerQueues()
        {
            List<BlockingByteStream> queues;
            lock (_broadcastLock)
            {
                queues = _consumerQueuesByOwner.Values.SelectMany(i => i).ToList();
                _consumerQueuesByOwner.Clear();
                _pendingBootstrapQueues.Clear();
            }

            foreach (var queue in queues)
            {
                queue.Complete();
            }
        }

        private bool BroadcastOutput(byte[] chunk, bool randomAccess, bool forceBootstrapReady)
        {
            List<BlockingByteStream> liveQueues;
            List<BlockingByteStream> bootstrapQueues = null;
            List<byte[]> bootstrapSnapshot = null;
            bool cacheReady;
            bool cacheOverflowed;
            bool cacheBecameReady;
            long cacheBytes;

            lock (_broadcastLock)
            {
                var cacheWasReady = _startupCacheKeyframeAligned;
                cacheReady = AddStartupCacheChunkLocked(chunk, randomAccess, forceBootstrapReady, out cacheOverflowed);
                cacheBecameReady = !cacheWasReady && cacheReady;
                cacheBytes = _startupCacheBytes;
                var allQueues = _consumerQueuesByOwner.Values.SelectMany(i => i).ToList();

                if (cacheReady && _pendingBootstrapQueues.Count > 0)
                {
                    bootstrapQueues = _pendingBootstrapQueues.Where(allQueues.Contains).ToList();
                    bootstrapSnapshot = _startupCache.ToList();
                    foreach (var queue in bootstrapQueues)
                    {
                        _pendingBootstrapQueues.Remove(queue);
                    }
                }

                liveQueues = allQueues.Where(i => !_pendingBootstrapQueues.Contains(i)
                    && (bootstrapQueues == null || !bootstrapQueues.Contains(i))).ToList();
            }

            if (cacheBecameReady)
            {
                _logger.LogInformation(
                    "HTSP startup cache aligned for channel {ChannelId}: randomAccess={RandomAccess}, cacheBytes={CacheBytes}, releasedReaders={ReleasedReaderCount}",
                    _channelId,
                    randomAccess,
                    cacheBytes,
                    bootstrapQueues?.Count ?? 0);
            }

            if (cacheOverflowed && !_startupCacheOverflowLogged)
            {
                _startupCacheOverflowLogged = true;
                _logger.LogWarning(
                    "HTSP keyframe startup cache exceeded {MaxBytes} bytes for channel {ChannelId}; new readers will wait for the next random-access frame",
                    StartupCacheMaxBytes,
                    _channelId);
            }

            if (bootstrapQueues != null && bootstrapSnapshot != null)
            {
                foreach (var queue in bootstrapQueues)
                {
                    foreach (var cachedChunk in bootstrapSnapshot)
                    {
                        queue.WriteChunk(cachedChunk);
                    }
                }
            }

            foreach (var queue in liveQueues)
            {
                queue.WriteChunk(chunk);
            }

            return cacheReady;
        }

        private bool AddStartupCacheChunkLocked(
            byte[] chunk,
            bool randomAccess,
            bool forceBootstrapReady,
            out bool cacheOverflowed)
        {
            cacheOverflowed = false;
            if (chunk == null || chunk.Length == 0 || !LooksLikeTransportStream(chunk))
            {
                return _startupCacheKeyframeAligned;
            }

            if (!GetConfiguredKeyframeStartupEnabled())
            {
                _startupCache.Enqueue(chunk);
                _startupCacheBytes += chunk.Length;
                while (_startupCacheBytes > StartupCacheMaxBytes && _startupCache.Count > 1)
                {
                    var removed = _startupCache.Dequeue();
                    _startupCacheBytes -= removed.Length;
                }

                _startupCacheKeyframeAligned = true;
                return true;
            }

            if (!_primaryVideoStreamIndex.HasValue || forceBootstrapReady)
            {
                _startupCache.Enqueue(chunk);
                _startupCacheBytes += chunk.Length;
                while (_startupCacheBytes > StartupCacheMaxBytes && _startupCache.Count > 1)
                {
                    var removed = _startupCache.Dequeue();
                    _startupCacheBytes -= removed.Length;
                }

                _startupCacheKeyframeAligned = true;
                return true;
            }

            if (randomAccess)
            {
                _startupCache.Clear();
                _startupCacheBytes = 0;
                _startupCacheKeyframeAligned = true;
                _startupCacheOverflowLogged = false;
            }

            if (!_startupCacheKeyframeAligned)
            {
                return false;
            }

            if (_startupCacheBytes + chunk.Length > StartupCacheMaxBytes)
            {
                _startupCache.Clear();
                _startupCacheBytes = 0;
                _startupCacheKeyframeAligned = false;
                cacheOverflowed = true;
                return false;
            }

            _startupCache.Enqueue(chunk);
            _startupCacheBytes += chunk.Length;
            return true;
        }

        private void ResetStartupCacheForNewSubscription(bool clearParameterSets)
        {
            lock (_broadcastLock)
            {
                _startupCache.Clear();
                _startupCacheBytes = 0;
                _startupCacheKeyframeAligned = false;
                _startupCacheOverflowLogged = false;

                _pendingBootstrapQueues.Clear();
                if (_primaryVideoStreamIndex.HasValue)
                {
                    foreach (var queue in _consumerQueuesByOwner.Values.SelectMany(i => i))
                    {
                        _pendingBootstrapQueues.Add(queue);
                    }
                }
            }

            if (clearParameterSets)
            {
                _cachedH264Sps = null;
                _cachedH264Pps = null;
                _cachedHevcVps = null;
                _cachedHevcSps = null;
                _cachedHevcPps = null;
            }
        }

        private void AddSharedPlaybackReference(string playbackId)
        {
            lock (_sharedReferenceLock)
            {
                var wasEmpty = _sharedPlaybackReferences.Count == 0;
                _sharedPlaybackReferences.Add(playbackId);
                CancelSharedHubIdleCloseLocked();
                if (wasEmpty)
                {
                    MarkPlayableMuxPacketReceived();
                }
            }
        }

        private int ReleaseSharedPlaybackReference(string playbackId, string reason)
        {
            var remaining = 0;
            var shouldScheduleClose = false;
            lock (_sharedReferenceLock)
            {
                _sharedPlaybackReferences.Remove(playbackId);
                remaining = _sharedPlaybackReferences.Count;
                shouldScheduleClose = remaining == 0;
            }

            CancelOwnerIdleDisconnect(playbackId);

            if (shouldScheduleClose && !IsSharedProxy)
            {
                ScheduleSharedHubIdleClose(reason);
            }

            return remaining;
        }

        private int GetSharedPlaybackReferenceCount()
        {
            lock (_sharedReferenceLock)
            {
                return _sharedPlaybackReferences.Count;
            }
        }

        private void ScheduleOwnerIdleDisconnect(string playbackId, string reason)
        {
            CancellationTokenSource idleCts;
            lock (_broadcastLock)
            {
                if (OwnerHasConsumerQueues(playbackId) || _closing || _lifetimeCancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                CancelOwnerIdleDisconnectLocked(playbackId);
                idleCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationTokenSource.Token);
                _ownerIdleDisconnects[playbackId] = idleCts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ReaderIdleCloseDelay, idleCts.Token).ConfigureAwait(false);
                    if (!OwnerHasConsumerQueues(playbackId))
                    {
                        _logger.LogInformation(
                            "HTSP shared playback {PlaybackId} for channel {ChannelId} became idle; releasing playback reference. Reason: {Reason}",
                            playbackId,
                            _channelId,
                            reason);
                        ReleaseSharedPlaybackReference(playbackId, reason);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    lock (_broadcastLock)
                    {
                        if (_ownerIdleDisconnects.TryGetValue(playbackId, out var current) && ReferenceEquals(current, idleCts))
                        {
                            _ownerIdleDisconnects.Remove(playbackId);
                        }
                    }

                    idleCts.Dispose();
                }
            });
        }

        private void CancelOwnerIdleDisconnect(string playbackId)
        {
            lock (_broadcastLock)
            {
                CancelOwnerIdleDisconnectLocked(playbackId);
            }
        }

        private void CancelOwnerIdleDisconnectLocked(string playbackId)
        {
            if (_ownerIdleDisconnects.TryGetValue(playbackId, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                _ownerIdleDisconnects.Remove(playbackId);
            }
        }

        private void CancelAllOwnerIdleDisconnects()
        {
            List<CancellationTokenSource> tokens;
            lock (_broadcastLock)
            {
                tokens = _ownerIdleDisconnects.Values.ToList();
                _ownerIdleDisconnects.Clear();
            }

            foreach (var token in tokens)
            {
                try
                {
                    token.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void ScheduleSharedHubIdleClose(string reason)
        {
            CancellationTokenSource idleCts;
            lock (_sharedReferenceLock)
            {
                if (_sharedPlaybackReferences.Count > 0 || _closing || _lifetimeCancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                CancelSharedHubIdleCloseLocked();
                idleCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationTokenSource.Token);
                _sharedHubIdleCloseCancellationTokenSource = idleCts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ReaderIdleCloseDelay, idleCts.Token).ConfigureAwait(false);
                    lock (_sharedReferenceLock)
                    {
                        if (!ReferenceEquals(_sharedHubIdleCloseCancellationTokenSource, idleCts) || _sharedPlaybackReferences.Count > 0)
                        {
                            return;
                        }
                    }

                    await CloseProducerNow(reason).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    lock (_sharedReferenceLock)
                    {
                        if (ReferenceEquals(_sharedHubIdleCloseCancellationTokenSource, idleCts))
                        {
                            _sharedHubIdleCloseCancellationTokenSource = null;
                        }
                    }

                    idleCts.Dispose();
                }
            });
        }

        private void CancelSharedHubIdleClose()
        {
            lock (_sharedReferenceLock)
            {
                CancelSharedHubIdleCloseLocked();
            }
        }

        private void CancelSharedHubIdleCloseLocked()
        {
            try
            {
                _sharedHubIdleCloseCancellationTokenSource?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _sharedHubIdleCloseCancellationTokenSource = null;
        }

        private void OnStreamReaderOpened(string readerId)
        {
            if (_closing || _lifetimeCancellationTokenSource.IsCancellationRequested || _stream.IsCompleted)
            {
                return;
            }

            int activeReaders;
            lock (_readerStateLock)
            {
                _activeStreamReaders++;
                activeReaders = _activeStreamReaders;
                CancelReaderIdleDisconnectLocked();
            }

            _logger.LogDebug(
                "HTSP direct stream reader {ReaderId} opened for channel {ChannelId}; active reader count is {ActiveReaderCount}",
                readerId,
                _channelId,
                activeReaders);
        }

        private void OnStreamReaderClosed(string readerId, bool consumedBytes)
        {
            int activeReaders;
            lock (_readerStateLock)
            {
                if (_activeStreamReaders > 0)
                {
                    _activeStreamReaders--;
                }

                activeReaders = _activeStreamReaders;
            }

            if (_closing || _lifetimeCancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            if (!consumedBytes)
            {
                _logger.LogDebug(
                    "HTSP direct stream reader {ReaderId} for channel {ChannelId} was disposed before consuming bytes; keeping Tvheadend subscription {SubscriptionId} open for Jellyfin/ffmpeg",
                    readerId,
                    _channelId,
                    _subscriptionId);
                return;
            }

            _logger.LogDebug(
                "HTSP direct stream reader {ReaderId} closed for channel {ChannelId}; active reader count is {ActiveReaderCount}",
                readerId,
                _channelId,
                activeReaders);

            if (activeReaders <= 0)
            {
                ScheduleReaderIdleDisconnect("Jellyfin disposed the direct stream reader after consuming data");
            }
        }

        private void ScheduleReaderIdleDisconnect(string reason)
        {
            CancellationTokenSource idleCts;
            lock (_readerStateLock)
            {
                if (_activeStreamReaders > 0 || _closing || _lifetimeCancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                CancelReaderIdleDisconnectLocked();
                idleCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationTokenSource.Token);
                _readerIdleDisconnectCancellationTokenSource = idleCts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ReaderIdleCloseDelay, idleCts.Token).ConfigureAwait(false);

                    lock (_readerStateLock)
                    {
                        if (!ReferenceEquals(_readerIdleDisconnectCancellationTokenSource, idleCts) || _activeStreamReaders > 0)
                        {
                            return;
                        }
                    }

                    OnConsumerClosed(reason);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    lock (_readerStateLock)
                    {
                        if (ReferenceEquals(_readerIdleDisconnectCancellationTokenSource, idleCts))
                        {
                            _readerIdleDisconnectCancellationTokenSource = null;
                        }
                    }

                    idleCts.Dispose();
                }
            });
        }

        private void CancelReaderIdleDisconnect()
        {
            lock (_readerStateLock)
            {
                CancelReaderIdleDisconnectLocked();
            }
        }

        private void CancelReaderIdleDisconnectLocked()
        {
            try
            {
                _readerIdleDisconnectCancellationTokenSource?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _readerIdleDisconnectCancellationTokenSource = null;
        }

        private void EnsureStallWatchdogStarted()
        {
            var timeoutSeconds = GetConfiguredStallTimeoutSeconds();
            if (timeoutSeconds <= 0 || IsSharedProxy)
            {
                return;
            }

            lock (_connectionStateLock)
            {
                if (_stallWatchdogTask != null && !_stallWatchdogTask.IsCompleted)
                {
                    return;
                }

                _stallWatchdogCancellationTokenSource?.Dispose();
                _stallWatchdogCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationTokenSource.Token);
                var token = _stallWatchdogCancellationTokenSource.Token;
                _stallWatchdogTask = Task.Run(() => MonitorStallAsync(token), token);
            }

            _logger.LogInformation(
                "HTSP silent-stream watchdog enabled for channel {ChannelId}: timeout={TimeoutSeconds}s",
                _channelId,
                timeoutSeconds);
        }

        private async Task MonitorStallAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (_closing || _recovering || GetSharedPlaybackReferenceCount() <= 0)
                {
                    continue;
                }

                var timeoutSeconds = GetConfiguredStallTimeoutSeconds();
                if (timeoutSeconds <= 0)
                {
                    continue;
                }

                var lastTicks = Interlocked.Read(ref _lastPlayableMuxPacketUtcTicks);
                if (lastTicks <= 0)
                {
                    continue;
                }

                var idle = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);
                if (idle.TotalSeconds < timeoutSeconds)
                {
                    continue;
                }

                if (Interlocked.CompareExchange(ref _stallWatchdogTriggered, 1, 0) != 0)
                {
                    continue;
                }

                var error = new IOException(
                    $"No playable HTSP mux packet was received for {idle.TotalSeconds:F1} seconds.");
                _logger.LogWarning(
                    error,
                    "HTSP shared producer stalled for channel {ChannelId}; subscription {SubscriptionId} will reconnect",
                    _channelId,
                    _subscriptionId);
                onError(error);
            }
        }

        private void MarkPlayableMuxPacketReceived()
        {
            Interlocked.Exchange(ref _lastPlayableMuxPacketUtcTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _stallWatchdogTriggered, 0);
            GetConnectionFirstPacketTaskSource().TrySetResult(true);
        }

        private void ResetStallWatchdogClock()
        {
            Interlocked.Exchange(ref _lastPlayableMuxPacketUtcTicks, 0);
            Interlocked.Exchange(ref _stallWatchdogTriggered, 0);
        }

        private void CancelStallWatchdog()
        {
            CancellationTokenSource cts;
            lock (_connectionStateLock)
            {
                cts = _stallWatchdogCancellationTokenSource;
                _stallWatchdogCancellationTokenSource = null;
            }

            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cts?.Dispose();
            }
        }

        private void RegisterClientAbortCallback()
        {
            try
            {
                var context = _httpContextAccessor?.HttpContext;
                if (context == null || !context.RequestAborted.CanBeCanceled)
                {
                    return;
                }

                lock (_clientAbortSync)
                {
                    _clientAbortRegistration.Dispose();
                    _clientAbortRegistration = context.RequestAborted.Register(
                        state => ((HtspLiveStream)state).OnClientRequestAborted(),
                        this);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to register HTSP direct-stream client abort callback for channel {ChannelId}", _channelId);
            }
        }

        private void OnClientRequestAborted()
        {
            OnConsumerClosed("Jellyfin streaming HTTP request was aborted");
        }

        private void OnConsumerClosed(string reason)
        {
            if (_closing || _lifetimeCancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            ReleaseSharedPlaybackReference(UniqueId, reason);
        }

        private void DisposeClientAbortRegistration()
        {
            lock (_clientAbortSync)
            {
                _clientAbortRegistration.Dispose();
                _clientAbortRegistration = default;
            }
        }

        public void onError(Exception ex)
        {
            NotifyConnectionError(ex);

            if (_closing || _lifetimeCancellationTokenSource.IsCancellationRequested || _stream.IsCompleted)
            {
                _logger.LogDebug(ex, "HTSP live stream connection closed; suppressing reconnect");
                _firstPacket.TrySetCanceled();
                _stream.Complete();
                return;
            }

            if (_recovering || !_started)
            {
                _logger.LogWarning(ex, "HTSP live stream connection error while {Phase}; retry controller will handle it", _recovering ? "recovering" : "opening");
                return;
            }

            if (Interlocked.CompareExchange(ref _reconnectScheduled, 1, 0) != 0)
            {
                _logger.LogDebug(ex, "HTSP live stream connection error ignored because a reconnect is already scheduled");
                return;
            }

            _ = Task.Run(() => ReconnectLoopAsync(ex));
        }

        private async Task ReconnectLoopAsync(Exception initialException)
        {
            Exception lastException = initialException;
            _recovering = true;

            try
            {
                while (!_closing && !_lifetimeCancellationTokenSource.IsCancellationRequested && !_stream.IsCompleted)
                {
                    var attempt = Interlocked.Increment(ref _liveReconnectAttempts);
                    if (attempt > MaxLiveReconnectAttempts)
                    {
                        break;
                    }

                    var delay = GetRetryDelay(attempt);
                    _logger.LogWarning(
                        lastException,
                        "HTSP live stream socket failed for channel {ChannelId}; reconnect attempt {Attempt}/{MaxAttempts} in {DelayMs}ms",
                        _channelId,
                        attempt,
                        MaxLiveReconnectAttempts,
                        (int)delay.TotalMilliseconds);

                    try
                    {
                        await Task.Delay(delay, _lifetimeCancellationTokenSource.Token).ConfigureAwait(false);

                        var config = Plugin.Instance.Configuration;
                        await ConnectAndSubscribeAsync(
                            config.TVH_ServerName.Trim(),
                            config.HTSP_Port,
                            config.Username.Trim(),
                            config.Password.Trim(),
                            config.Profile?.Trim(),
                            _lifetimeCancellationTokenSource.Token,
                            waitForMuxPacket: true).ConfigureAwait(false);

                        if (_closing || _lifetimeCancellationTokenSource.IsCancellationRequested || _stream.IsCompleted)
                        {
                            CloseCurrentConnection(unsubscribe: true);
                            return;
                        }

                        Interlocked.Exchange(ref _liveReconnectAttempts, 0);
                        _logger.LogInformation("HTSP live stream reconnected for channel {ChannelId} on subscription {SubscriptionId}", _channelId, _subscriptionId);
                        return;
                    }
                    catch (OperationCanceledException) when (_closing || _lifetimeCancellationTokenSource.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        CloseCurrentConnection(unsubscribe: false);
                    }
                }

                if (!_closing && !_stream.IsCompleted)
                {
                    _logger.LogError(lastException, "HTSP live stream reconnect exhausted for channel {ChannelId}; closing Jellyfin stream", _channelId);
                    CompleteWithError(lastException);
                }
            }
            finally
            {
                _recovering = false;
                Interlocked.Exchange(ref _reconnectScheduled, 0);
            }
        }

        public void onMessage(HTSMessage response)
        {
            if (response == null)
            {
                return;
            }

            var method = response.Method;
            if (IsSubscriptionMessage(method) && !BelongsToCurrentSubscription(response))
            {
                return;
            }

            try
            {
                switch (method)
                {
                    case "subscriptionStart":
                        ParseSubscriptionStart(response);
                        break;
                    case "muxpkt":
                        ProcessMuxPacket(response);
                        break;
                    case "subscriptionStop":
                        ProcessSubscriptionStop(response);
                        break;
                    case "subscriptionStatus":
                        ProcessSubscriptionStatus(response);
                        break;
                    case "queueStatus":
                        LogQueueStatus(response);
                        break;
                    case "signalStatus":
                        LogSignalStatus(response);
                        break;
                    case "timeshiftStatus":
                        LogTimeshiftStatus(response);
                        break;
                    case "streamStatus":
                        LogStreamStatus(response);
                        break;
                    case "subscriptionGrace":
                        _logger.LogInformation("HTSP subscription {SubscriptionId} grace timeout: {GraceTimeout}s", _subscriptionId, GetInt(response, "graceTimeout", -1));
                        break;
                    case "subscriptionSpeed":
                        _logger.LogDebug("HTSP subscription {SubscriptionId} speed changed to {Speed}", _subscriptionId, GetInt(response, "speed", 0));
                        break;
                    case "subscriptionSkip":
                        _logger.LogDebug("HTSP subscription {SubscriptionId} skipped: absolute={Absolute}, time={Time}, error={Error}", _subscriptionId, GetInt(response, "absolute", 0), GetLong(response, "time", 0), GetInt(response, "error", 0));
                        break;
                    case "descrambleInfo":
                        LogDescrambleInfo(response);
                        break;
                    default:
                        _logger.LogTrace("Ignoring HTSP live message {Method}", method);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process HTSP live message {Method}", method);
                CompleteWithError(ex);
            }
        }

        private void ParseSubscribeResponse(HTSMessage response)
        {
            if (response == null)
            {
                throw new InvalidOperationException("TVHeadend HTSP subscribe did not return a response.");
            }

            ThrowIfResponseError(response, "subscribe");

            var timestampsAre90Khz = GetInt(response, "90khz", 0) == 1;
            var normts = GetInt(response, "normts", 0) == 1;
            var timeshiftPeriod = GetInt(response, "timeshiftPeriod", 0);
            _muxer.SetTimestampsAre90Khz(timestampsAre90Khz);

            var requestedQueueDepth = GetConfiguredQueueDepth();
            _logger.LogInformation(
                "HTSP subscription {SubscriptionId} accepted: timestamps={TimestampMode}, normts={Normts}, timeshiftPeriod={TimeshiftPeriod}s, requestedQueueDepth={QueueDepth}",
                _subscriptionId,
                timestampsAre90Khz ? "90kHz" : "1MHz",
                normts,
                timeshiftPeriod,
                requestedQueueDepth > 0 ? requestedQueueDepth.ToString(System.Globalization.CultureInfo.InvariantCulture) : "server-default");
        }

        private void ProcessMuxPacket(HTSMessage response)
        {
            if (!response.containsField("payload"))
            {
                return;
            }

            var payload = response.getByteArray("payload");
            if (LooksLikeTransportStream(payload))
            {
                MarkPlayableMuxPacketReceived();
                WriteOutput(payload, randomAccess: false, forceBootstrapReady: true);
                return;
            }

            if (!response.containsField("stream"))
            {
                _logger.LogWarning("Ignoring HTSP mux packet without stream index");
                return;
            }

            if (!_muxer.HasStreams)
            {
                CompleteWithError(new InvalidOperationException("HTSP muxpkt payload is not raw MPEG-TS and no subscriptionStart stream metadata was received for muxing."));
                return;
            }

            var streamIndex = response.getInt("stream");
            if (!_muxer.IsStreamKnown(streamIndex))
            {
                if (_ignoredMuxStreams.Add(streamIndex))
                {
                    _logger.LogDebug(
                        "HTSP dropping mux packets for non-playable or unsupported stream index {StreamIndex}",
                        streamIndex);
                }

                return;
            }

            MarkPlayableMuxPacketReceived();

            var pts = response.containsField("pts") ? response.getLong("pts") : (long?)null;
            var dts = response.containsField("dts") ? response.getLong("dts") : (long?)null;
            var duration = response.containsField("duration") ? response.getLong("duration") : (long?)null;
            var frameType = GetFrameType(response);
            var streamInfo = _muxer.GetStreamInfo(streamIndex);
            var preparedPayload = PrepareVideoPayloadForBootstrap(streamInfo, payload, frameType, out var randomAccess);

            if (ShouldDropDamagedVideo(streamInfo, randomAccess))
            {
                return;
            }

            if (streamInfo != null
                && streamInfo.Kind == HtspTransportStreamMuxer.ElementaryStreamKind.Video
                && randomAccess
                && Interlocked.CompareExchange(ref _awaitingCleanVideoRandomAccess, 0, 1) == 1)
            {
                var damagedForMs = GetElapsedMilliseconds(Interlocked.Exchange(ref _videoDamageSinceUtcTicks, 0));
                _muxer.MarkStreamDiscontinuity(streamIndex, repeatProgramTables: true);
                ResetStartupCacheForNewSubscription(clearParameterSets: false);
                _logger.LogInformation(
                    "HTSP video recovered for channel {ChannelId} at a verified random-access frame after {DamagedForMs}ms; PAT/PMT and a one-shot video discontinuity will be emitted",
                    _channelId,
                    damagedForMs);
            }

            RecordMuxPacket(streamIndex, payload, pts, dts, duration, frameType, randomAccess);

            var chunk = _muxer.WritePacket(
                streamIndex,
                preparedPayload,
                pts,
                dts,
                out var sourceDiscontinuity,
                forceProgramTables: randomAccess,
                randomAccess: randomAccess);

            if (sourceDiscontinuity)
            {
                ResetStartupCacheForNewSubscription(clearParameterSets: false);
                _logger.LogWarning(
                    "HTSP timestamp epoch change confirmed on channel {ChannelId}, stream {StreamIndex}; reset the MPEG-TS timeline, continuity counters, PAT/PMT, and startup cache",
                    _channelId,
                    streamIndex);
            }

            if (chunk.Length > 0)
            {
                WriteOutput(chunk, randomAccess);
            }
        }


        private bool ShouldDropDamagedVideo(HtspTransportStreamMuxer.StreamInfo streamInfo, bool randomAccess)
        {
            if (streamInfo == null
                || streamInfo.Kind != HtspTransportStreamMuxer.ElementaryStreamKind.Video
                || Interlocked.CompareExchange(ref _awaitingCleanVideoRandomAccess, 0, 0) == 0
                || randomAccess)
            {
                return false;
            }

            var damagedSinceTicks = Interlocked.Read(ref _videoDamageSinceUtcTicks);
            var waitSeconds = GetConfiguredSignalIdrWaitSeconds();
            if (damagedSinceTicks > 0
                && waitSeconds > 0
                && DateTime.UtcNow - new DateTime(damagedSinceTicks, DateTimeKind.Utc) >= TimeSpan.FromSeconds(waitSeconds))
            {
                RequestSignalRecoveryReconnect("No verified random-access video frame arrived within the signal recovery timeout.");
            }

            return true;
        }

        private void MarkVideoDamaged(string reason)
        {
            if (Interlocked.CompareExchange(ref _awaitingCleanVideoRandomAccess, 1, 0) == 0)
            {
                Interlocked.Exchange(ref _videoDamageSinceUtcTicks, DateTime.UtcNow.Ticks);
                _logger.LogWarning(
                    "HTSP video marked damaged for channel {ChannelId}: {Reason}. Inter-frame video will be dropped until a verified IDR/IRAP arrives; audio and subtitles remain live",
                    _channelId,
                    reason);
            }
        }

        private void RequestSignalRecoveryReconnect(string reason)
        {
            if (!GetConfiguredSignalRecoveryEnabled()
                || _closing
                || _recovering
                || Interlocked.CompareExchange(ref _signalRecoveryReconnectScheduled, 1, 0) != 0)
            {
                return;
            }

            var nowTicks = DateTime.UtcNow.Ticks;
            var cooldown = TimeSpan.FromSeconds(GetConfiguredSignalRecoveryCooldownSeconds());
            var lastTicks = Interlocked.Read(ref _lastSignalRecoveryUtcTicks);
            if (lastTicks > 0 && DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc) < cooldown)
            {
                Interlocked.Exchange(ref _signalRecoveryReconnectScheduled, 0);
                return;
            }

            var windowStart = Interlocked.Read(ref _signalRecoveryAttemptWindowStartUtcTicks);
            if (windowStart <= 0 || DateTime.UtcNow - new DateTime(windowStart, DateTimeKind.Utc) >= TimeSpan.FromSeconds(SignalRecoveryAttemptWindowSeconds))
            {
                Interlocked.Exchange(ref _signalRecoveryAttemptWindowStartUtcTicks, nowTicks);
                Interlocked.Exchange(ref _signalRecoveryAttempts, 0);
                Interlocked.Exchange(ref _signalRecoverySuppressionLogged, 0);
            }

            var attempt = Interlocked.Increment(ref _signalRecoveryAttempts);
            var maximum = GetConfiguredSignalRecoveryMaxReconnects();
            if (maximum <= 0 || attempt > maximum)
            {
                Interlocked.Exchange(ref _signalRecoveryReconnectScheduled, 0);
                if (Interlocked.CompareExchange(ref _signalRecoverySuppressionLogged, 1, 0) == 0)
                {
                    _logger.LogWarning(
                        "HTSP signal recovery reconnect suppressed for channel {ChannelId}: attempt {Attempt} exceeds configured maximum {Maximum} within {WindowSeconds}s. Waiting for a clean random-access frame instead",
                        _channelId,
                        attempt,
                        maximum,
                        SignalRecoveryAttemptWindowSeconds);
                }

                return;
            }

            Interlocked.Exchange(ref _lastSignalRecoveryUtcTicks, nowTicks);
            var error = new IOException("Signal-triggered HTSP recovery: " + reason);
            _logger.LogWarning(
                error,
                "HTSP signal recovery reconnect requested for channel {ChannelId}: attempt {Attempt}/{Maximum}, cooldown={CooldownSeconds}s",
                _channelId,
                attempt,
                maximum,
                (int)cooldown.TotalSeconds);
            Interlocked.Exchange(ref _signalRecoveryReconnectScheduled, 0);
            onError(error);
        }

        private static long GetElapsedMilliseconds(long utcTicks)
        {
            return utcTicks > 0
                ? Math.Max(0, (long)(DateTime.UtcNow - new DateTime(utcTicks, DateTimeKind.Utc)).TotalMilliseconds)
                : 0;
        }

        private static char GetFrameType(HTSMessage response)
        {
            if (response == null || !response.containsField("frametype"))
            {
                return '\0';
            }

            var value = response.getInt("frametype", 0);
            return value > 0 && value <= char.MaxValue ? char.ToUpperInvariant((char)value) : '\0';
        }

        private byte[] PrepareVideoPayloadForBootstrap(
            HtspTransportStreamMuxer.StreamInfo stream,
            byte[] payload,
            char frameType,
            out bool randomAccess)
        {
            randomAccess = stream != null
                && stream.Kind == HtspTransportStreamMuxer.ElementaryStreamKind.Video
                && frameType == 'I';

            if (stream == null
                || stream.Kind != HtspTransportStreamMuxer.ElementaryStreamKind.Video
                || payload == null
                || payload.Length == 0)
            {
                return payload;
            }

            switch (NormalizeCodec(stream.Codec))
            {
                case "H264":
                case "AVC":
                    return PrepareH264RandomAccessPayload(payload, ref randomAccess);
                case "HEVC":
                case "H265":
                    return PrepareHevcRandomAccessPayload(payload, ref randomAccess);
                default:
                    return payload;
            }
        }

        private byte[] PrepareH264RandomAccessPayload(byte[] payload, ref bool randomAccess)
        {
            var units = ParseAnnexBNalUnits(payload, hevc: false);
            var hasIdr = units.Any(i => i.Type == 5);
            var hasSps = units.Any(i => i.Type == 7);
            var hasPps = units.Any(i => i.Type == 8);

            var sps = units.FirstOrDefault(i => i.Type == 7);
            var pps = units.FirstOrDefault(i => i.Type == 8);
            if (sps != null)
            {
                _cachedH264Sps = CopyNalUnit(payload, sps);
            }

            if (pps != null)
            {
                _cachedH264Pps = CopyNalUnit(payload, pps);
            }

            // TVHeadend's I-frame hint also covers usable recovery-point frames
            // on open-GOP broadcasts, so NAL inspection may promote but not
            // reject it.
            if (units.Count > 0)
            {
                randomAccess = randomAccess
                    || (hasIdr
                        && (hasSps || _cachedH264Sps != null)
                        && (hasPps || _cachedH264Pps != null));
            }

            var parameterSets = randomAccess
                && !(hasSps && hasPps)
                && _cachedH264Sps != null
                && _cachedH264Pps != null
                    ? new[] { _cachedH264Sps, _cachedH264Pps }
                    : Array.Empty<byte[]>();

            return PrepareAnnexBAccessUnit(payload, units, hevc: false, parameterSets);
        }

        private byte[] PrepareHevcRandomAccessPayload(byte[] payload, ref bool randomAccess)
        {
            var units = ParseAnnexBNalUnits(payload, hevc: true);
            var hasRandomAccess = units.Any(i => i.Type >= 16 && i.Type <= 21);
            var hasVps = units.Any(i => i.Type == 32);
            var hasSps = units.Any(i => i.Type == 33);
            var hasPps = units.Any(i => i.Type == 34);

            var vps = units.FirstOrDefault(i => i.Type == 32);
            var sps = units.FirstOrDefault(i => i.Type == 33);
            var pps = units.FirstOrDefault(i => i.Type == 34);
            if (vps != null)
            {
                _cachedHevcVps = CopyNalUnit(payload, vps);
            }

            if (sps != null)
            {
                _cachedHevcSps = CopyNalUnit(payload, sps);
            }

            if (pps != null)
            {
                _cachedHevcPps = CopyNalUnit(payload, pps);
            }

            if (units.Count > 0)
            {
                randomAccess = randomAccess
                    || (hasRandomAccess
                        && (hasVps || _cachedHevcVps != null)
                        && (hasSps || _cachedHevcSps != null)
                        && (hasPps || _cachedHevcPps != null));
            }

            var parameterSets = randomAccess
                && !(hasVps && hasSps && hasPps)
                && _cachedHevcVps != null
                && _cachedHevcSps != null
                && _cachedHevcPps != null
                    ? new[] { _cachedHevcVps, _cachedHevcSps, _cachedHevcPps }
                    : Array.Empty<byte[]>();

            return PrepareAnnexBAccessUnit(payload, units, hevc: true, parameterSets);
        }

        internal static byte[] PrepareAnnexBAccessUnit(byte[] payload, bool hevc, params byte[][] prefixNalUnits)
        {
            return PrepareAnnexBAccessUnit(payload, ParseAnnexBNalUnits(payload, hevc), hevc, prefixNalUnits);
        }

        private static byte[] PrepareAnnexBAccessUnit(
            byte[] payload,
            IReadOnlyList<AnnexBNalUnit> units,
            bool hevc,
            params byte[][] prefixNalUnits)
        {
            if (payload == null || units == null || units.Count == 0)
            {
                return payload;
            }

            var firstVcl = units.FirstOrDefault(i => hevc ? i.Type <= 31 : i.Type >= 1 && i.Type <= 5);
            if (firstVcl == null)
            {
                return payload;
            }

            var audType = hevc ? 35 : 9;
            var hasLeadingAud = units[0].Type == audType;
            var prefixes = prefixNalUnits ?? Array.Empty<byte[]>();
            var prefixLength = prefixes.Where(i => i != null).Sum(i => i.Length);
            if (hasLeadingAud && prefixLength == 0)
            {
                return payload;
            }

            var aud = hasLeadingAud ? Array.Empty<byte>() : BuildAccessUnitDelimiter(payload, firstVcl, hevc);
            if (!hasLeadingAud && aud.Length == 0)
            {
                return payload;
            }

            var insertionOffset = hasLeadingAud ? units[0].Offset + units[0].Length : 0;
            var result = new byte[payload.Length + aud.Length + prefixLength];
            var offset = 0;

            if (insertionOffset > 0)
            {
                Buffer.BlockCopy(payload, 0, result, 0, insertionOffset);
                offset = insertionOffset;
            }

            if (aud.Length > 0)
            {
                Buffer.BlockCopy(aud, 0, result, offset, aud.Length);
                offset += aud.Length;
            }

            foreach (var prefix in prefixes)
            {
                if (prefix == null || prefix.Length == 0)
                {
                    continue;
                }

                Buffer.BlockCopy(prefix, 0, result, offset, prefix.Length);
                offset += prefix.Length;
            }

            Buffer.BlockCopy(payload, insertionOffset, result, offset, payload.Length - insertionOffset);
            return result;
        }

        private static byte[] BuildAccessUnitDelimiter(byte[] payload, AnnexBNalUnit firstVcl, bool hevc)
        {
            if (!hevc)
            {
                // primary_pic_type 7 safely describes any H.264 I/SI/P/SP/B access unit.
                return H264AccessUnitDelimiter;
            }

            var headerOffset = firstVcl.Offset + firstVcl.StartCodeLength;
            if (headerOffset + 1 >= payload.Length)
            {
                return Array.Empty<byte>();
            }

            // Keep the VCL layer and temporal id, as FFmpeg's h265_metadata filter does.
            // pic_type 2 safely describes any HEVC I/P/B access unit.
            return new byte[]
            {
                0x00,
                0x00,
                0x00,
                0x01,
                (byte)(0x46 | (payload[headerOffset] & 0x01)),
                payload[headerOffset + 1],
                0x50
            };
        }

        private static List<AnnexBNalUnit> ParseAnnexBNalUnits(byte[] payload, bool hevc)
        {
            var units = new List<AnnexBNalUnit>();
            if (payload == null || payload.Length < 4)
            {
                return units;
            }

            var searchOffset = 0;
            while (TryFindAnnexBStartCode(payload, searchOffset, out var startOffset, out var startCodeLength))
            {
                var headerOffset = startOffset + startCodeLength;
                if (headerOffset >= payload.Length)
                {
                    break;
                }

                var nextSearchOffset = headerOffset + 1;
                var hasNext = TryFindAnnexBStartCode(payload, nextSearchOffset, out var nextOffset, out _);
                var endOffset = hasNext ? nextOffset : payload.Length;
                var minimumNalBytes = hevc ? 3 : 2; // Header plus at least one RBSP byte.
                if (endOffset - headerOffset < minimumNalBytes)
                {
                    break;
                }

                var type = hevc ? (payload[headerOffset] >> 1) & 0x3F : payload[headerOffset] & 0x1F;
                units.Add(new AnnexBNalUnit(startOffset, endOffset - startOffset, startCodeLength, type));

                if (!hasNext)
                {
                    break;
                }

                searchOffset = nextOffset;
            }

            return units;
        }

        private static bool TryFindAnnexBStartCode(byte[] payload, int start, out int offset, out int length)
        {
            offset = -1;
            length = 0;
            if (payload == null)
            {
                return false;
            }

            for (var i = Math.Max(0, start); i + 3 < payload.Length; i++)
            {
                if (payload[i] != 0x00 || payload[i + 1] != 0x00)
                {
                    continue;
                }

                if (payload[i + 2] == 0x01)
                {
                    offset = i;
                    length = 3;
                    return true;
                }

                if (i + 3 < payload.Length && payload[i + 2] == 0x00 && payload[i + 3] == 0x01)
                {
                    offset = i;
                    length = 4;
                    return true;
                }
            }

            return false;
        }

        private static byte[] CopyNalUnit(byte[] payload, AnnexBNalUnit unit)
        {
            var result = new byte[unit.Length];
            Buffer.BlockCopy(payload, unit.Offset, result, 0, unit.Length);
            return result;
        }

        private void RecordMuxPacket(
            int streamIndex,
            byte[] payload,
            long? pts,
            long? dts,
            long? duration,
            char frameType,
            bool randomAccess)
        {
            if (payload == null)
            {
                return;
            }

            HtspTransportStreamMuxer.StreamInfo streamInfo;
            long packetCount;
            long byteCount;
            bool isFirstPacket;
            string periodicSummary = null;
            var now = DateTime.UtcNow;

            lock (_muxPacketStatsLock)
            {
                _muxedStreamsByIndex.TryGetValue(streamIndex, out streamInfo);
                _muxPacketCounts.TryGetValue(streamIndex, out var previousCount);
                _muxPacketBytes.TryGetValue(streamIndex, out var previousBytes);

                packetCount = previousCount + 1;
                byteCount = previousBytes + payload.Length;
                isFirstPacket = previousCount == 0;
                _muxPacketCounts[streamIndex] = packetCount;
                _muxPacketBytes[streamIndex] = byteCount;
                if (randomAccess)
                {
                    _muxKeyFrameCounts.TryGetValue(streamIndex, out var previousKeyFrames);
                    _muxKeyFrameCounts[streamIndex] = previousKeyFrames + 1;
                }

                var healthIntervalSeconds = GetConfiguredHealthLogIntervalSeconds();
                if (GetConfiguredHealthLoggingEnabled()
                    && healthIntervalSeconds > 0
                    && (now - _lastMuxPacketStatsLogUtc).TotalSeconds >= healthIntervalSeconds)
                {
                    periodicSummary = BuildMuxPacketSummaryLocked();
                    _lastMuxPacketStatsLogUtc = now;
                }
            }

            if (isFirstPacket && GetConfiguredDetailedDiagnostics())
            {
                _logger.LogInformation(
                    "HTSP first mux packet for stream {Stream}: payload={PayloadBytes} bytes, pts={Pts}, dts={Dts}, duration={Duration}, frametype={FrameType}, randomAccess={RandomAccess}, firstBytes={FirstBytes}",
                    DescribeMuxPacketStream(streamIndex, streamInfo),
                    payload.Length,
                    pts.HasValue ? pts.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null",
                    dts.HasValue ? dts.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null",
                    duration.HasValue ? duration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null",
                    frameType == '\0' ? "_" : frameType.ToString(),
                    randomAccess,
                    FormatFirstBytes(payload, 16));

                LogAacTransport(streamIndex, streamInfo, payload);
            }

            if (!string.IsNullOrWhiteSpace(periodicSummary))
            {
                _logger.LogInformation(
                    "HTSP health channel {ChannelId}: {Health}; streams=[{MuxPacketSummary}]",
                    _channelId,
                    BuildHealthSummary(),
                    periodicSummary);
            }
        }

        private void LogAacTransport(int streamIndex, HtspTransportStreamMuxer.StreamInfo streamInfo, byte[] payload)
        {
            if (streamInfo == null || !IsAacCodec(streamInfo.Codec))
            {
                return;
            }

            var declaredCodec = NormalizeCodec(streamInfo.Codec);
            var detectedTransport = DetectAacTransport(payload);
            var declaredLatm = declaredCodec == "AACLATM" || declaredCodec == "LATM";
            var transportMatchesDeclaration = declaredLatm
                ? string.Equals(detectedTransport, "LOAS/LATM", StringComparison.Ordinal)
                : string.Equals(detectedTransport, "ADTS", StringComparison.Ordinal);

            if (transportMatchesDeclaration)
            {
                _logger.LogInformation(
                    "HTSP AAC transport for stream {Stream}: declaredCodec={DeclaredCodec}, detectedTransport={DetectedTransport}, firstBytes={FirstBytes}",
                    DescribeMuxPacketStream(streamIndex, streamInfo),
                    streamInfo.Codec ?? "AAC",
                    detectedTransport,
                    FormatFirstBytes(payload, 16));
                return;
            }

            _logger.LogWarning(
                "HTSP AAC transport mismatch for stream {Stream}: declaredCodec={DeclaredCodec}, detectedTransport={DetectedTransport}, firstBytes={FirstBytes}. Payload is passed through unchanged; framing correction is only needed if playback fails.",
                DescribeMuxPacketStream(streamIndex, streamInfo),
                streamInfo.Codec ?? "AAC",
                detectedTransport,
                FormatFirstBytes(payload, 16));
        }

        private static bool IsAacCodec(string codec)
        {
            switch (NormalizeCodec(codec))
            {
                case "AAC":
                case "AACADTS":
                case "AACLC":
                case "HEAAC":
                case "MPEG4AUDIO":
                case "AACLATM":
                case "LATM":
                    return true;
                default:
                    return false;
            }
        }

        private static string DetectAacTransport(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                return "empty";
            }

            // ADTS: 12-bit 0xFFF sync word and a zero layer field. The MPEG ID
            // and protection-absent bits are allowed to vary.
            if (payload[0] == 0xFF && (payload[1] & 0xF6) == 0xF0)
            {
                if (payload.Length < 7)
                {
                    return "ADTS-truncated";
                }

                var frameLength = ((payload[3] & 0x03) << 11)
                    | (payload[4] << 3)
                    | ((payload[5] & 0xE0) >> 5);

                return frameLength >= 7 && frameLength <= payload.Length
                    ? "ADTS"
                    : "ADTS-invalid-frame-length";
            }

            // LOAS/LATM: 11-bit AudioSyncStream sync word 0x2B7.
            if (payload[0] == 0x56 && (payload[1] & 0xE0) == 0xE0)
            {
                return "LOAS/LATM";
            }

            return "unframed-or-unknown";
        }

        private string BuildMuxPacketSummaryLocked()
        {
            var indexes = _muxedStreamsByIndex.Keys
                .Concat(_muxPacketCounts.Keys)
                .Distinct()
                .OrderBy(i => i)
                .ToList();

            return string.Join("; ", indexes.Select(i =>
            {
                _muxedStreamsByIndex.TryGetValue(i, out var streamInfo);
                _muxPacketCounts.TryGetValue(i, out var packetCount);
                _muxPacketBytes.TryGetValue(i, out var byteCount);
                _muxKeyFrameCounts.TryGetValue(i, out var keyFrameCount);
                var timestampFixes = streamInfo?.TimestampCorrectionCount ?? 0;
                var timestampDiscontinuities = streamInfo?.TimestampDiscontinuityCount ?? 0;
                return DescribeMuxPacketStream(i, streamInfo)
                    + ": packets=" + packetCount
                    + ", bytes=" + byteCount
                    + (keyFrameCount > 0 ? ", randomAccess=" + keyFrameCount : string.Empty)
                    + (timestampFixes > 0 ? ", timestampFixes=" + timestampFixes : string.Empty)
                    + (timestampDiscontinuities > 0 ? ", timestampDiscontinuities=" + timestampDiscontinuities : string.Empty);
            }));
        }

        private static string DescribeMuxPacketStream(int streamIndex, HtspTransportStreamMuxer.StreamInfo streamInfo)
        {
            return streamInfo == null ? streamIndex + ":UNKNOWN" : DescribeHtspStream(streamInfo);
        }

        private static string FormatFirstBytes(byte[] payload, int maxBytes)
        {
            if (payload == null || payload.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(" ", payload.Take(Math.Max(0, maxBytes)).Select(i => i.ToString("X2")));
        }

        private void ProcessSubscriptionStop(HTSMessage response)
        {
            var status = response.getString("status", string.Empty);
            var subscriptionError = response.getString("subscriptionError", string.Empty);
            var details = BuildStatusDetails(status, subscriptionError);

            if (_closing)
            {
                _stream.Complete();
                return;
            }

            if (string.IsNullOrWhiteSpace(details))
            {
                details = "subscription stopped by TVHeadend";
            }

            CompleteWithError(new IOException("HTSP live stream stopped: " + details));
        }

        private void ProcessSubscriptionStatus(HTSMessage response)
        {
            var status = response.getString("status", string.Empty);
            var subscriptionError = response.getString("subscriptionError", string.Empty);
            var details = BuildStatusDetails(status, subscriptionError);

            if (string.IsNullOrWhiteSpace(details))
            {
                _logger.LogTrace("HTSP subscription {SubscriptionId} status OK", _subscriptionId);
                return;
            }

            _logger.LogWarning("HTSP subscription {SubscriptionId} status: {Status}", _subscriptionId, details);
            if (!_started)
            {
                CompleteWithError(new IOException("HTSP subscription failed: " + details));
            }
        }

        private void ParseSubscriptionStart(HTSMessage response)
        {
            if (!response.containsField("streams"))
            {
                _logger.LogWarning("Malformed HTSP subscriptionStart: streams field is missing");
                return;
            }

            var streams = new List<HtspTransportStreamMuxer.StreamInfo>();
            foreach (var item in response.getList("streams"))
            {
                if (item is not HTSMessage stream || !stream.containsField("index"))
                {
                    continue;
                }

                streams.Add(new HtspTransportStreamMuxer.StreamInfo
                {
                    Index = stream.getInt("index"),
                    Codec = stream.getString("type", "UNKNOWN"),
                    Language = stream.getString("language", string.Empty),
                    DisplayName = GetStreamDisplayName(stream),
                    Meta = stream.containsField("meta") ? stream.getByteArray("meta") : null,
                    Width = GetInt(stream, "width", 0),
                    Height = GetInt(stream, "height", 0),
                    Channels = GetInt(stream, "channels", 0),
                    Rate = GetInt(stream, "rate", 0),
                    AudioType = GetInt(stream, "audio_type", 0),
                    CompositionId = GetInt(stream, "composition_id", 0),
                    AncillaryId = GetInt(stream, "ancillary_id", 0)
                });
            }

            var orderedStreams = streams
                .OrderBy(i => i.Index)
                .Select(PrepareHtspStreamForMuxing)
                .ToList();
            var muxStreams = orderedStreams
                .Where(ShouldMuxHtspTransportStream)
                .ToList();
            var droppedStreams = orderedStreams
                .Where(i => !ShouldMuxHtspTransportStream(i))
                .ToList();

            var previousMetadataSubscriptionId = Interlocked.Exchange(ref _lastMetadataSubscriptionId, _subscriptionId);
            var sourceDiscontinuity = previousMetadataSubscriptionId != 0
                && previousMetadataSubscriptionId != _subscriptionId;
            _ignoredMuxStreams.Clear();
            ResetMuxPacketStats(muxStreams);
            var layoutChanged = _muxer.SetStreams(muxStreams, sourceDiscontinuity);
            // SetStreams classifies the shared StreamInfo objects and assigns their
            // MPEG-TS kind/PID. Resolve the primary video only after that step;
            // before configuration every StreamInfo.Kind is still its default.
            _primaryVideoStreamIndex = muxStreams
                .FirstOrDefault(i => i.Kind == HtspTransportStreamMuxer.ElementaryStreamKind.Video)
                ?.Index;
            if (sourceDiscontinuity || layoutChanged)
            {
                // Never inject parameter sets cached from a previous upstream
                // subscription after reconnect. Tvheadend normts should provide a
                // fresh decoder bootstrap; waiting for it is safer than mixing SPS/
                // PPS/VPS from a previous encoder state into the new timeline.
                ResetStartupCacheForNewSubscription(clearParameterSets: true);
            }

            UpdateMediaSourceStreamMetadata(muxStreams);
            LogDvbSubtitlePageIds(muxStreams);
            LogSourceInfo(response);
            _logger.LogInformation(
                "HTSP stream metadata received: {TotalCount} stream(s), muxing {MuxedCount} player-selectable/non-CA stream(s), PMT version {PmtVersion}, layoutChanged={LayoutChanged}, sourceDiscontinuity={SourceDiscontinuity}: {StreamSummary}",
                orderedStreams.Count,
                _muxer.SupportedStreamCount,
                _muxer.PmtVersion,
                layoutChanged,
                sourceDiscontinuity,
                _muxer.StreamSummary);

            if (droppedStreams.Count > 0)
            {
                _logger.LogInformation(
                    "HTSP ignored {DroppedCount} CA/private/data stream(s) from Jellyfin MPEG-TS output: {DroppedStreams}",
                    droppedStreams.Count,
                    string.Join(", ", droppedStreams.Select(DescribeHtspStream)));
            }

            RequestControlStreamFilter(droppedStreams);

            if (muxStreams.Count > 0 && _muxer.SupportedStreamCount != muxStreams.Count)
            {
                _logger.LogWarning(
                    "HTSP stream metadata selected {MuxCandidateCount} stream(s) for MPEG-TS, but only {MuxedCount} could be routed into MPEG-TS.",
                    muxStreams.Count,
                    _muxer.SupportedStreamCount);
            }
        }

        private void RequestControlStreamFilter(IReadOnlyCollection<HtspTransportStreamMuxer.StreamInfo> droppedStreams)
        {
            if (!(Plugin.Instance?.Configuration?.HTSPFilterControlStreams ?? false)
                || droppedStreams == null
                || droppedStreams.Count == 0
                || Volatile.Read(ref _lastFilteredSubscriptionId) == _subscriptionId)
            {
                return;
            }

            var indexes = droppedStreams
                .Where(IsPrivateOrControlHtspStream)
                .Select(i => i.Index)
                .Distinct()
                .OrderBy(i => i)
                .ToList();
            if (indexes.Count == 0)
            {
                return;
            }

            HTSConnectionAsync connection;
            lock (_connectionStateLock)
            {
                connection = _connection;
            }

            if (connection == null || connection.getNegotiatedProtocolVersion() < 12)
            {
                return;
            }

            var request = new HTSMessage { Method = "subscriptionFilterStream" };
            request.putField("subscriptionId", _subscriptionId);
            request.putField("disable", indexes.Select(i => (int?)i).ToList());
            var filteredSubscriptionId = _subscriptionId;
            Interlocked.Exchange(ref _lastFilteredSubscriptionId, filteredSubscriptionId);

            try
            {
                connection.sendMessage(
                    request,
                    new CallbackResponseHandler(response =>
                    {
                        try
                        {
                            ThrowIfResponseError(response, "subscriptionFilterStream");
                            _logger.LogInformation(
                                "HTSP subscription {SubscriptionId} disabled upstream CA/control/data stream indexes: {Indexes}",
                                filteredSubscriptionId,
                                string.Join(",", indexes));
                        }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref _lastFilteredSubscriptionId, 0, filteredSubscriptionId);
                            _logger.LogWarning(
                                ex,
                                "HTSP subscriptionFilterStream failed for subscription {SubscriptionId}; local MPEG-TS filtering remains active",
                                filteredSubscriptionId);
                        }
                    }));
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _lastFilteredSubscriptionId, 0, filteredSubscriptionId);
                _logger.LogWarning(
                    ex,
                    "Could not send HTSP subscriptionFilterStream for subscription {SubscriptionId}; local MPEG-TS filtering remains active",
                    filteredSubscriptionId);
            }
        }

        private static string GetStreamDisplayName(HTSMessage stream)
        {
            if (stream == null)
            {
                return string.Empty;
            }

            // Tvheadend does not guarantee a human-readable stream label, but
            // different versions/backends have used several aliases. Preserve
            // the first non-empty value so Jellyfin clients can display the
            // broadcaster/TVH track name instead of only language + codec.
            foreach (var field in new[]
            {
                "name",
                "title",
                "displayName",
                "displayname",
                "streamName",
                "stream_name",
                "description",
                "comment",
                "label"
            })
            {
                if (!stream.containsField(field))
                {
                    continue;
                }

                var value = stream.getString(field, string.Empty);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static string GetFallbackStreamTitle(HtspTransportStreamMuxer.StreamInfo stream)
        {
            if (stream == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(stream.DisplayName))
            {
                return stream.DisplayName.Trim();
            }

            // ISO-639/DVB audio_type values carried by Tvheadend.
            return stream.AudioType switch
            {
                1 => "Clean effects",
                2 => "Hearing impaired",
                3 => "Audio description",
                _ => null
            };
        }

        private void ResetMuxPacketStats(IReadOnlyList<HtspTransportStreamMuxer.StreamInfo> streams)
        {
            lock (_muxPacketStatsLock)
            {
                _muxedStreamsByIndex.Clear();
                _muxPacketCounts.Clear();
                _muxPacketBytes.Clear();
                _muxKeyFrameCounts.Clear();
                _lastMuxPacketStatsLogUtc = DateTime.UtcNow;

                foreach (var stream in streams ?? Array.Empty<HtspTransportStreamMuxer.StreamInfo>())
                {
                    _muxedStreamsByIndex[stream.Index] = stream;
                }
            }
        }

        private void LogDvbSubtitlePageIds(IReadOnlyList<HtspTransportStreamMuxer.StreamInfo> streams)
        {
            if (streams == null)
            {
                return;
            }

            foreach (var stream in streams.Where(IsDvbSubtitleStream))
            {
                var compositionId = stream.CompositionId & 0xFFFF;
                var ancillaryId = stream.AncillaryId & 0xFFFF;
                if (compositionId == 0)
                {
                    compositionId = 1;
                }

                if (ancillaryId == 0)
                {
                    ancillaryId = compositionId;
                }

                _logger.LogInformation(
                    "HTSP DVB subtitle stream {StreamIndex}: lang={Language}, composition_id={CompositionId}, ancillary_id={AncillaryId}",
                    stream.Index,
                    string.IsNullOrWhiteSpace(stream.Language) ? "und" : stream.Language,
                    compositionId,
                    ancillaryId);
            }
        }

        private void UpdateMediaSourceStreamMetadata(IReadOnlyList<HtspTransportStreamMuxer.StreamInfo> streams)
        {
            if (streams == null || streams.Count == 0)
            {
                return;
            }

            var mediaStreams = new List<MediaStream>();
            MediaStream firstAudioStream = null;
            var ffmpegStreamIndex = 0;

            foreach (var stream in streams.OrderBy(i => i.Index))
            {
                var mediaStream = CreateMediaStream(stream, ffmpegStreamIndex);
                if (mediaStream != null)
                {
                    if (mediaStream.Type == MediaStreamType.Audio && firstAudioStream == null)
                    {
                        mediaStream.IsDefault = true;
                        firstAudioStream = mediaStream;
                    }

                    mediaStreams.Add(mediaStream);
                }

                ffmpegStreamIndex++;
            }

            if (mediaStreams.Count == 0)
            {
                _logger.LogWarning("HTSP subscription {SubscriptionId} did not expose any playable audio/video/subtitle stream metadata to Jellyfin", _subscriptionId);
                return;
            }

            MediaSource.MediaStreams = mediaStreams;
            MediaSource.DefaultAudioStreamIndex = firstAudioStream?.Index;
            MediaSource.DefaultSubtitleStreamIndex = null;

            // We already have the authoritative track list from HTSP subscriptionStart.
            // Letting Jellyfin run its short live probe can replace this complete list
            // with only the tracks that happened to emit packets during the probe window.
            MediaSource.SupportsProbing = false;

            _logger.LogInformation(
                "HTSP exposed {Count} playable stream(s) to Jellyfin metadata: {Streams}; all muxable audio/subtitle streams are carried, CA/private control streams are ignored",
                mediaStreams.Count,
                string.Join(", ", mediaStreams.Select(i => $"#{i.Index}:{i.Type}:{i.Codec}{(string.IsNullOrWhiteSpace(i.Language) ? string.Empty : ":" + i.Language)}")));
        }

        private static HtspTransportStreamMuxer.StreamInfo PrepareHtspStreamForMuxing(HtspTransportStreamMuxer.StreamInfo stream)
        {
            if (stream == null)
            {
                return null;
            }

            stream.MuxAsPrivateData = ShouldMuxAsFallbackPrivateData(stream);
            return stream;
        }

        private static bool ShouldMuxHtspTransportStream(HtspTransportStreamMuxer.StreamInfo stream)
        {
            if (stream == null || IsPrivateOrControlHtspStream(stream))
            {
                return false;
            }

            if (HtspTransportStreamMuxer.CanMuxCodec(stream.Codec))
            {
                return true;
            }

            return ShouldMuxAsFallbackPrivateData(stream);
        }

        private static bool ShouldMuxAsFallbackPrivateData(HtspTransportStreamMuxer.StreamInfo stream)
        {
            if (stream == null || IsPrivateOrControlHtspStream(stream) || HtspTransportStreamMuxer.CanMuxCodec(stream.Codec))
            {
                return false;
            }

            return LooksLikePlayableButUnsupportedStream(stream);
        }

        private static bool LooksLikePlayableButUnsupportedStream(HtspTransportStreamMuxer.StreamInfo stream)
        {
            var codec = NormalizeCodec(stream.Codec);

            if (codec.Contains("AUDIO", StringComparison.Ordinal)
                || codec.Contains("SOUND", StringComparison.Ordinal)
                || codec.Contains("SUB", StringComparison.Ordinal)
                || codec.Contains("CAPTION", StringComparison.Ordinal)
                || codec.Contains("CC", StringComparison.Ordinal)
                || codec.Contains("TTXT", StringComparison.Ordinal)
                || codec.Contains("TELETEXT", StringComparison.Ordinal))
            {
                return true;
            }

            if (stream.Channels > 0 || stream.Rate >= 8000)
            {
                return true;
            }

            return stream.CompositionId != 0 || stream.AncillaryId != 0;
        }

        private static bool IsPrivateOrControlHtspStream(HtspTransportStreamMuxer.StreamInfo stream)
        {
            if (stream == null)
            {
                return true;
            }

            switch (NormalizeCodec(stream.Codec))
            {
                case "CA":
                case "CAT":
                case "ECM":
                case "EMM":
                case "PAT":
                case "PMT":
                case "PCR":
                case "PRIVATE":
                case "DATA":
                case "BINDATA":
                case "BINARY":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsDvbSubtitleStream(HtspTransportStreamMuxer.StreamInfo stream)
        {
            if (stream == null)
            {
                return false;
            }

            switch (NormalizeCodec(stream.Codec))
            {
                case "DVBSUB":
                case "DVBSUBTITLE":
                case "DVB_SUBTITLE":
                    return true;
                default:
                    return false;
            }
        }

        private static string DescribeHtspStream(HtspTransportStreamMuxer.StreamInfo stream)
        {
            if (stream == null)
            {
                return "<null>";
            }

            var codec = string.IsNullOrWhiteSpace(stream.Codec) ? "UNKNOWN" : stream.Codec;
            var language = string.IsNullOrWhiteSpace(stream.Language) ? string.Empty : ":" + stream.Language;
            var title = string.IsNullOrWhiteSpace(stream.DisplayName) ? string.Empty : ":\"" + stream.DisplayName + "\"";
            return stream.Index + ":" + codec + language + title;
        }

        private static MediaStream CreateMediaStream(HtspTransportStreamMuxer.StreamInfo stream, int ffmpegStreamIndex)
        {
            if (stream == null || !TryGetMediaStreamType(stream.Codec, out var mediaStreamType))
            {
                return null;
            }

            var mediaStream = new MediaStream
            {
                Index = ffmpegStreamIndex,
                Type = mediaStreamType,
                Codec = ToJellyfinCodec(stream.Codec),
                Language = NormalizeLanguage(stream.Language),
                TimeBase = "1/90000",
                IsExternal = false
            };

            var streamTitle = GetFallbackStreamTitle(stream);
            if (!string.IsNullOrWhiteSpace(streamTitle))
            {
                mediaStream.Title = streamTitle;
                mediaStream.Comment = streamTitle;
            }

            if (mediaStreamType == MediaStreamType.Video)
            {
                if (stream.Width > 0)
                {
                    mediaStream.Width = stream.Width;
                }

                if (stream.Height > 0)
                {
                    mediaStream.Height = stream.Height;
                }

                mediaStream.IsInterlaced = true;
                mediaStream.RealFrameRate = 50.0F;
            }
            else if (mediaStreamType == MediaStreamType.Audio)
            {
                if (stream.Channels > 0)
                {
                    mediaStream.Channels = stream.Channels;
                }

                var sampleRate = GetAudioSampleRate(stream);
                if (sampleRate.HasValue)
                {
                    mediaStream.SampleRate = sampleRate.Value;
                }

                mediaStream.IsHearingImpaired = stream.AudioType == 2;
            }
            else if (mediaStreamType == MediaStreamType.Subtitle)
            {
                // The Android TV direct-play path receives the raw MPEG-TS and
                // must select/render embedded subtitle PIDs locally. Mark DVB
                // subtitles explicitly as embedded so the app/profile path does
                // not treat the stream like an external/no-delivery subtitle.
                mediaStream.DeliveryMethod = SubtitleDeliveryMethod.Embed;
                mediaStream.IsExternal = false;
                mediaStream.SupportsExternalStream = false;
            }

            return mediaStream;
        }

        private static int? GetAudioSampleRate(HtspTransportStreamMuxer.StreamInfo stream)
        {
            if (stream == null)
            {
                return null;
            }

            // Tvheadend does not always provide an audio sample rate in the HTSP
            // subscriptionStart stream map.  Avoid exposing small control/enumeration
            // values such as 3 as Jellyfin sample rates.
            if (stream.Rate >= 8000 && stream.Rate <= 384000)
            {
                return stream.Rate;
            }

            switch (NormalizeCodec(stream.Codec))
            {
                case "AAC":
                case "AACADTS":
                case "AACLC":
                case "HEAAC":
                case "MPEG4AUDIO":
                case "AACLATM":
                case "LATM":
                case "MPEG1AUDIO":
                case "MPEG2AUDIO":
                case "MP2":
                case "MP3":
                case "MPA":
                case "AC3":
                case "EAC3":
                case "DTS":
                case "DCA":
                case "TRUEHD":
                    return 48000;
                default:
                    return null;
            }
        }

        private static bool TryGetMediaStreamType(string codec, out MediaStreamType mediaStreamType)
        {
            switch (NormalizeCodec(codec))
            {
                case "MPEG1VIDEO":
                case "MPEGTS":
                case "MPEG2VIDEO":
                case "MPEGVIDEO":
                case "H264":
                case "AVC":
                case "HEVC":
                case "H265":
                case "MPEG4VIDEO":
                case "MPEG4PART2":
                case "VC1":
                    mediaStreamType = MediaStreamType.Video;
                    return true;
                case "AAC":
                case "AACADTS":
                case "AACLC":
                case "HEAAC":
                case "MPEG4AUDIO":
                case "AACLATM":
                case "LATM":
                case "MPEG1AUDIO":
                case "MPEG2AUDIO":
                case "MP2":
                case "MP3":
                case "MPA":
                case "AC3":
                case "EAC3":
                case "DTS":
                case "DCA":
                case "VORBIS":
                case "OPUS":
                case "TRUEHD":
                    mediaStreamType = MediaStreamType.Audio;
                    return true;
                case "DVBSUB":
                case "DVBSUBTITLE":
                case "DVB_SUBTITLE":
                case "DVDSUB":
                case "DVDSUBTITLE":
                case "DVD_SUBTITLE":
                case "PGS":
                case "PGSSUB":
                case "PGSSUBTITLE":
                case "HDMVPGSSUBTITLE":
                case "TELETEXT":
                case "TTXT":
                case "TEXTSUB":
                case "TEXTSUBTITLE":
                case "SRT":
                case "SUBRIP":
                case "SSA":
                case "ASS":
                    mediaStreamType = MediaStreamType.Subtitle;
                    return true;
                default:
                    mediaStreamType = default;
                    return false;
            }
        }

        private static string ToJellyfinCodec(string codec)
        {
            switch (NormalizeCodec(codec))
            {
                case "MPEG1VIDEO":
                    return "mpeg1video";
                case "MPEGTS":
                case "MPEG2VIDEO":
                case "MPEGVIDEO":
                    return "mpeg2video";
                case "H264":
                case "AVC":
                    return "h264";
                case "HEVC":
                case "H265":
                    return "hevc";
                case "MPEG4VIDEO":
                case "MPEG4PART2":
                    return "mpeg4";
                case "VC1":
                    return "vc1";
                case "AAC":
                case "AACADTS":
                case "AACLC":
                case "HEAAC":
                case "MPEG4AUDIO":
                    return "aac";
                case "AACLATM":
                case "LATM":
                    return "aac_latm";
                case "MPEG1AUDIO":
                case "MPEG2AUDIO":
                case "MP2":
                case "MPA":
                    return "mp2";
                case "MP3":
                    return "mp3";
                case "AC3":
                    return "ac3";
                case "EAC3":
                    return "eac3";
                case "DTS":
                case "DCA":
                    return "dts";
                case "VORBIS":
                    return "vorbis";
                case "OPUS":
                    return "opus";
                case "TRUEHD":
                    return "truehd";
                case "DVBSUB":
                case "DVBSUBTITLE":
                case "DVB_SUBTITLE":
                    // Jellyfin classifies subtitle codecs using substring checks;
                    // "dvb_subtitle" is mistakenly treated as text, while "dvbsub"
                    // is correctly treated as non-text / non-extractable.
                    return "dvbsub";
                case "DVDSUB":
                case "DVDSUBTITLE":
                case "DVD_SUBTITLE":
                    return "dvdsub";
                case "PGS":
                case "PGSSUB":
                case "PGSSUBTITLE":
                case "HDMVPGSSUBTITLE":
                    return "pgssub";
                case "TELETEXT":
                case "TTXT":
                    // Keep this non-text too. Embedded live-TV teletext is carried
                    // inside the MPEG-TS stream, not as an external text attachment.
                    return "dvbsub_teletext";
                case "SRT":
                case "SUBRIP":
                    return "subrip";
                case "SSA":
                case "ASS":
                    return "ass";
                case "TEXTSUB":
                case "TEXTSUBTITLE":
                    return "text";
                default:
                    return string.IsNullOrWhiteSpace(codec) ? "unknown" : codec.Trim().ToLowerInvariant();
            }
        }

        private static string NormalizeCodec(string codec)
        {
            return (codec ?? string.Empty)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToUpperInvariant();
        }

        private static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            var normalized = new string(language.Trim().ToLowerInvariant().Where(char.IsLetter).Take(3).ToArray());
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private void ResetQueueDiagnostics()
        {
            Interlocked.Exchange(ref _lastQueueIdrops, 0);
            Interlocked.Exchange(ref _lastQueuePdrops, 0);
            Interlocked.Exchange(ref _lastQueueBdrops, 0);
            Interlocked.Exchange(ref _lastQueuePackets, 0);
            Interlocked.Exchange(ref _lastQueueBytes, 0);
            Interlocked.Exchange(ref _lastQueueDelayUs, 0);
        }

        private void LogQueueStatus(HTSMessage response)
        {
            var idrops = GetLong(response, "Idrops", 0);
            var pdrops = GetLong(response, "Pdrops", 0);
            var bdrops = GetLong(response, "Bdrops", 0);
            var previousIdrops = Interlocked.Exchange(ref _lastQueueIdrops, idrops);
            var previousPdrops = Interlocked.Exchange(ref _lastQueuePdrops, pdrops);
            var previousBdrops = Interlocked.Exchange(ref _lastQueueBdrops, bdrops);
            var deltaIdrops = idrops >= previousIdrops ? idrops - previousIdrops : idrops;
            var deltaPdrops = pdrops >= previousPdrops ? pdrops - previousPdrops : pdrops;
            var deltaBdrops = bdrops >= previousBdrops ? bdrops - previousBdrops : bdrops;
            var packets = GetLong(response, "packets", 0);
            var bytes = GetLong(response, "bytes", 0);
            var delay = GetLong(response, "delay", 0);
            var queueDepth = GetConfiguredQueueDepth();
            Interlocked.Exchange(ref _lastQueuePackets, packets);
            Interlocked.Exchange(ref _lastQueueBytes, bytes);
            Interlocked.Exchange(ref _lastQueueDelayUs, delay);

            if (deltaIdrops > 0 || deltaPdrops > 0 || deltaBdrops > 0)
            {
                _logger.LogWarning(
                    "HTSP queue {SubscriptionId} dropped new frames I/P/B={DeltaIdrops}/{DeltaPdrops}/{DeltaBdrops}; totals={Idrops}/{Pdrops}/{Bdrops}, packets={Packets}, bytes={Bytes}, delay={Delay}us, requestedQueueDepth={QueueDepth}",
                    _subscriptionId,
                    deltaIdrops,
                    deltaPdrops,
                    deltaBdrops,
                    idrops,
                    pdrops,
                    bdrops,
                    packets,
                    bytes,
                    delay,
                    queueDepth);
                return;
            }

            _logger.LogTrace(
                "HTSP queue {SubscriptionId}: packets={Packets}, bytes={Bytes}, delay={Delay}us, drops I/P/B={Idrops}/{Pdrops}/{Bdrops}, requestedQueueDepth={QueueDepth}",
                _subscriptionId,
                packets,
                bytes,
                delay,
                idrops,
                pdrops,
                bdrops,
                queueDepth);
        }

        private void LogSignalStatus(HTSMessage response)
        {
            var current = new SignalSnapshot
            {
                UpdatedUtc = DateTime.UtcNow,
                Status = response.getString("feStatus", string.Empty),
                SnrRaw = GetNullableInt(response, "feSNR"),
                SnrAbsolute = GetNullableLong(response, "feAbsoluteSNR"),
                SignalRaw = GetNullableInt(response, "feSignal"),
                SignalAbsolute = GetNullableLong(response, "feAbsoluteSignal"),
                Ber = GetNullableLong(response, "feBER"),
                Unc = GetNullableLong(response, "feUNC")
            };

            SignalSnapshot previous;
            lock (_signalStateLock)
            {
                previous = _signalSnapshot;
                _signalSnapshot = current;
            }

            var firstValidStatus = previous == null || previous.UpdatedUtc == default;
            var lockWasPresent = HasFrontendLock(previous?.Status);
            var lockIsPresent = HasFrontendLock(current.Status);
            var lockChanged = !firstValidStatus && lockWasPresent != lockIsPresent;
            var uncIncrease = GetCounterIncrease(previous?.Unc, current.Unc);
            var berIncrease = GetCounterIncrease(previous?.Ber, current.Ber);
            var signalChanged = HasMeaningfulFrontendChange(previous?.SignalRaw, current.SignalRaw, previous?.SignalAbsolute, current.SignalAbsolute);
            var snrChanged = HasMeaningfulFrontendChange(previous?.SnrRaw, current.SnrRaw, previous?.SnrAbsolute, current.SnrAbsolute);
            var summary = FormatSignalSummary(current);

            EvaluateSignalRecovery(current, lockIsPresent, uncIncrease);

            if (lockChanged && !lockIsPresent)
            {
                _logger.LogWarning(
                    "HTSP signal lock lost for channel {ChannelId}: adapter={Adapter}, service={Service}, {SignalSummary}",
                    _channelId,
                    _sourceAdapter ?? string.Empty,
                    _sourceService ?? string.Empty,
                    summary);
            }
            else if (uncIncrease > 0 || berIncrease > 0)
            {
                _logger.LogWarning(
                    "HTSP signal errors increased for channel {ChannelId}: adapter={Adapter}, service={Service}, newBER={NewBer}, newUNC={NewUnc}, {SignalSummary}, queueDrops I/P/B={IDrops}/{PDrops}/{BDrops}",
                    _channelId,
                    _sourceAdapter ?? string.Empty,
                    _sourceService ?? string.Empty,
                    berIncrease,
                    uncIncrease,
                    summary,
                    Interlocked.Read(ref _lastQueueIdrops),
                    Interlocked.Read(ref _lastQueuePdrops),
                    Interlocked.Read(ref _lastQueueBdrops));
            }
            else if (GetConfiguredSignalHealthLoggingEnabled() && (firstValidStatus || lockChanged || signalChanged || snrChanged))
            {
                _logger.LogInformation(
                    "HTSP signal channel {ChannelId}: adapter={Adapter}, service={Service}, {SignalSummary}",
                    _channelId,
                    _sourceAdapter ?? string.Empty,
                    _sourceService ?? string.Empty,
                    summary);
            }
            else if (GetConfiguredSignalHealthLoggingEnabled() && GetConfiguredDetailedDiagnostics())
            {
                _logger.LogTrace("HTSP signal {SubscriptionId}: {SignalSummary}", _subscriptionId, summary);
            }
        }

        private void EvaluateSignalRecovery(SignalSnapshot current, bool hasLock, long uncIncrease)
        {
            if (!GetConfiguredSignalRecoveryEnabled())
            {
                return;
            }

            var nowTicks = current.UpdatedUtc.Ticks;
            if (hasLock)
            {
                Interlocked.Exchange(ref _frontendUnlockSinceUtcTicks, 0);
            }
            else
            {
                var unlockStart = Interlocked.Read(ref _frontendUnlockSinceUtcTicks);
                if (unlockStart <= 0)
                {
                    Interlocked.CompareExchange(ref _frontendUnlockSinceUtcTicks, nowTicks, 0);
                    unlockStart = Interlocked.Read(ref _frontendUnlockSinceUtcTicks);
                }

                var threshold = GetConfiguredSignalLockLossSeconds();
                if (threshold > 0
                    && current.UpdatedUtc - new DateTime(unlockStart, DateTimeKind.Utc) >= TimeSpan.FromSeconds(threshold))
                {
                    MarkVideoDamaged("Frontend lock has been absent for at least " + threshold + " seconds.");
                    RequestSignalRecoveryReconnect("Sustained frontend lock loss.");
                }
            }

            if (uncIncrease <= 0)
            {
                return;
            }

            var windowStart = Interlocked.Read(ref _signalErrorWindowStartUtcTicks);
            if (windowStart <= 0
                || current.UpdatedUtc - new DateTime(windowStart, DateTimeKind.Utc) >= TimeSpan.FromSeconds(SignalErrorWindowSeconds))
            {
                Interlocked.Exchange(ref _signalErrorWindowStartUtcTicks, nowTicks);
                Interlocked.Exchange(ref _signalErrorWindowUncIncrease, 0);
            }

            var burst = Interlocked.Add(ref _signalErrorWindowUncIncrease, uncIncrease);
            var thresholdUnc = GetConfiguredSignalUncBurstThreshold();
            if (thresholdUnc > 0 && burst >= thresholdUnc)
            {
                MarkVideoDamaged("Uncorrected block count increased by " + burst + " within " + SignalErrorWindowSeconds + " seconds.");
                RequestSignalRecoveryReconnect("Rapid increase in uncorrected transport blocks.");
                Interlocked.Exchange(ref _signalErrorWindowStartUtcTicks, nowTicks);
                Interlocked.Exchange(ref _signalErrorWindowUncIncrease, 0);
            }
        }

        private string BuildHealthSummary()
        {
            SignalSnapshot signal;
            lock (_signalStateLock)
            {
                signal = _signalSnapshot?.Clone();
            }

            List<BlockingByteStream> consumerQueues;
            int pendingBootstrapReaders;
            lock (_broadcastLock)
            {
                consumerQueues = _consumerQueuesByOwner.Values.SelectMany(i => i).ToList();
                pendingBootstrapReaders = _pendingBootstrapQueues.Count;
            }

            var consumerBufferBytes = consumerQueues.Select(i => i.BufferedBytes).ToList();
            var totalConsumerBufferBytes = consumerBufferBytes.Sum();
            var maxConsumerBufferBytes = consumerBufferBytes.Count > 0 ? consumerBufferBytes.Max() : 0;

            var lastPacketTicks = Interlocked.Read(ref _lastPlayableMuxPacketUtcTicks);
            var lastPacketAgeMs = lastPacketTicks > 0
                ? Math.Max(0, (long)(DateTime.UtcNow - new DateTime(lastPacketTicks, DateTimeKind.Utc)).TotalMilliseconds)
                : -1;

            return FormatSignalSummary(signal)
                + ", queuePackets=" + Interlocked.Read(ref _lastQueuePackets)
                + ", queueBytes=" + Interlocked.Read(ref _lastQueueBytes)
                + ", queueDelayUs=" + Interlocked.Read(ref _lastQueueDelayUs)
                + ", queueDrops=" + Interlocked.Read(ref _lastQueueIdrops)
                + "/" + Interlocked.Read(ref _lastQueuePdrops)
                + "/" + Interlocked.Read(ref _lastQueueBdrops)
                + ", lastMuxPacketMs=" + lastPacketAgeMs
                + ", outputConsumers=" + consumerQueues.Count
                + ", outputBufferedBytes=" + totalConsumerBufferBytes + "/" + maxConsumerBufferBytes
                + ", pendingBootstrapReaders=" + pendingBootstrapReaders
                + ", reconnectAttempts=" + Interlocked.CompareExchange(ref _liveReconnectAttempts, 0, 0)
                + ", signalRecoveryAttempts=" + Interlocked.CompareExchange(ref _signalRecoveryAttempts, 0, 0)
                + ", awaitingCleanVideo=" + (Interlocked.CompareExchange(ref _awaitingCleanVideoRandomAccess, 0, 0) != 0);
        }

        private static string FormatSignalSummary(SignalSnapshot signal)
        {
            if (signal == null || signal.UpdatedUtc == default)
            {
                return "signal=unavailable";
            }

            return "status=" + (string.IsNullOrWhiteSpace(signal.Status) ? "unknown" : signal.Status)
                + ", signal=" + FormatFrontendValue(signal.SignalRaw, signal.SignalAbsolute, "dBm")
                + ", snr=" + FormatFrontendValue(signal.SnrRaw, signal.SnrAbsolute, "dB")
                + ", ber=" + (signal.Ber.HasValue ? signal.Ber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "n/a")
                + ", unc=" + (signal.Unc.HasValue ? signal.Unc.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "n/a")
                + ", ageMs=" + Math.Max(0, (long)(DateTime.UtcNow - signal.UpdatedUtc).TotalMilliseconds);
        }

        private static string FormatFrontendValue(int? rawValue, long? absoluteValue, string absoluteUnit)
        {
            if (absoluteValue.HasValue)
            {
                return (absoluteValue.Value / 1000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " " + absoluteUnit;
            }

            var percent = NormalizeFrontendValue(rawValue);
            if (!rawValue.HasValue)
            {
                return "n/a";
            }

            return percent.HasValue
                ? percent.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "% (raw=" + rawValue.Value + ")"
                : "raw=" + rawValue.Value;
        }

        private static double? NormalizeFrontendValue(int? value)
        {
            if (!value.HasValue || value.Value < 0)
            {
                return null;
            }

            if (value.Value <= 100)
            {
                return value.Value;
            }

            if (value.Value <= 65535)
            {
                return value.Value * 100.0 / 65535.0;
            }

            return null;
        }

        private static double? NormalizeAbsoluteFrontendValue(long? value, double minimum, double maximum)
        {
            return value.HasValue
                ? Math.Max(0, Math.Min(100, ((value.Value / 1000.0) - minimum) * 100 / (maximum - minimum)))
                : (double?)null;
        }

        private static bool HasMeaningfulFrontendChange(int? previous, int? current, long? previousAbsolute, long? currentAbsolute)
        {
            if (previousAbsolute.HasValue && currentAbsolute.HasValue)
            {
                return Math.Abs(currentAbsolute.Value - previousAbsolute.Value) >= 1000;
            }

            if (previousAbsolute.HasValue != currentAbsolute.HasValue)
            {
                return true;
            }

            var previousPercent = NormalizeFrontendValue(previous);
            var currentPercent = NormalizeFrontendValue(current);
            return previousPercent.HasValue
                && currentPercent.HasValue
                && Math.Abs(previousPercent.Value - currentPercent.Value) >= 5.0;
        }

        private static bool HasFrontendLock(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            return status.Split(new[] { ' ', '|', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(token => string.Equals(token.Trim(), "LOCK", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(token.Trim(), "GOOD", StringComparison.OrdinalIgnoreCase));
        }

        private static long GetCounterIncrease(long? previous, long? current)
        {
            if (!current.HasValue || current.Value <= 0)
            {
                return 0;
            }

            if (!previous.HasValue || current.Value < previous.Value)
            {
                return current.Value;
            }

            return current.Value - previous.Value;
        }

        private void LogTimeshiftStatus(HTSMessage response)
        {
            _logger.LogTrace(
                "HTSP timeshift {SubscriptionId}: full={Full}, shift={Shift}, start={Start}, end={End}",
                _subscriptionId,
                GetInt(response, "full", 0),
                GetLong(response, "shift", 0),
                GetLong(response, "start", 0),
                GetLong(response, "end", 0));
        }

        private void LogStreamStatus(HTSMessage response)
        {
            _logger.LogDebug(
                "HTSP stream status {SubscriptionId}: stream={Stream}, status={Status}, errors={Errors}",
                _subscriptionId,
                GetInt(response, "stream", -1),
                response.getString("status", string.Empty),
                response.getString("errors", string.Empty));
        }

        private void LogDescrambleInfo(HTSMessage response)
        {
            _logger.LogTrace(
                "HTSP descramble {SubscriptionId}: pid={Pid}, caid={Caid}, provid={Provid}, cardsystem={CardSystem}, reader={Reader}",
                _subscriptionId,
                GetInt(response, "pid", 0),
                GetInt(response, "caid", 0),
                GetInt(response, "provid", 0),
                response.getString("cardsystem", string.Empty),
                response.getString("reader", string.Empty));
        }

        private void LogSourceInfo(HTSMessage response)
        {
            if (!response.containsField("sourceinfo"))
            {
                return;
            }

            try
            {
                var source = response.GetField("sourceinfo") as HTSMessage;
                if (source == null)
                {
                    return;
                }

                _sourceAdapter = source.getString("adapter", string.Empty);
                _sourceService = source.getString("service", string.Empty);
                _sourceNetwork = source.getString("network", string.Empty);
                _sourceMux = source.getString("mux", string.Empty);
                _sourceProvider = source.getString("provider", string.Empty);
                _logger.LogInformation(
                    "HTSP source: adapter={Adapter}, network={Network}, mux={Mux}, provider={Provider}, service={Service}, satpos={SatPos}",
                    _sourceAdapter,
                    _sourceNetwork,
                    _sourceMux,
                    _sourceProvider,
                    _sourceService,
                    source.getString("satpos", string.Empty));
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Could not parse HTSP sourceinfo");
            }
        }

        private void WriteOutput(byte[] chunk, bool randomAccess = false, bool forceBootstrapReady = false)
        {
            if (_closing || _lifetimeCancellationTokenSource.IsCancellationRequested || _stream.IsCompleted)
            {
                return;
            }

            _started = true;
            var bootstrapReady = BroadcastOutput(chunk, randomAccess, forceBootstrapReady);
            if (forceBootstrapReady || !_primaryVideoStreamIndex.HasValue || bootstrapReady)
            {
                _firstPacket.TrySetResult(true);
            }
        }

        private void CompleteWithError(Exception ex)
        {
            if (!_closing)
            {
                _firstPacket.TrySetException(ex);
            }

            _closing = true;
            _lifetimeCancellationTokenSource.Cancel();
            CancelStallWatchdog();
            CompleteAllConsumerQueues();
            _stream.Complete(ex);
            CloseCurrentConnection(unsubscribe: true);
            ActiveProducersByUniqueId.TryRemove(UniqueId, out _);
            if (_registeredAsSharedHub)
            {
                SharedHubsByChannelId.TryRemove(_channelId, out _);
                _registeredAsSharedHub = false;
            }
        }

        private static bool GetConfiguredStreamSharingEnabled()
        {
            return Plugin.Instance?.Configuration?.HTSPEnableStreamSharing ?? true;
        }

        private static bool GetConfiguredKeyframeStartupEnabled()
        {
            return Plugin.Instance?.Configuration?.HTSPKeyframeStartupEnabled ?? true;
        }

        private static bool GetConfiguredHealthLoggingEnabled()
        {
            return Plugin.Instance?.Configuration?.HTSPHealthLoggingEnabled ?? true;
        }

        private static int GetConfiguredHealthLogIntervalSeconds()
        {
            return Math.Max(0, Math.Min(600, Plugin.Instance?.Configuration?.HTSPHealthLogIntervalSeconds ?? 30));
        }

        private static bool GetConfiguredSignalHealthLoggingEnabled()
        {
            return Plugin.Instance?.Configuration?.HTSPSignalHealthLoggingEnabled ?? true;
        }

        private static bool GetConfiguredDetailedDiagnostics()
        {
            return Plugin.Instance?.Configuration?.HTSPDetailedDiagnostics ?? false;
        }

        public static IReadOnlyList<HtspProducerStatus> GetActiveProducerStatuses()
        {
            return ActiveProducersByUniqueId.Values
                .Distinct()
                .Select(producer => producer.CreateProducerStatus())
                .OrderBy(status => status.Service ?? status.ChannelId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private HtspProducerStatus CreateProducerStatus()
        {
            SignalSnapshot signal;
            lock (_signalStateLock)
            {
                signal = _signalSnapshot?.Clone();
            }

            List<HtspStreamStatus> streams;
            lock (_muxPacketStatsLock)
            {
                streams = _muxedStreamsByIndex
                    .OrderBy(item => item.Key)
                    .Select(item => new HtspStreamStatus
                    {
                        Index = item.Key,
                        Codec = item.Value?.Codec,
                        Language = item.Value?.Language,
                        Title = item.Value?.DisplayName,
                        Pid = item.Value?.Pid ?? 0,
                        Packets = _muxPacketCounts.TryGetValue(item.Key, out var packetCount) ? packetCount : 0,
                        Bytes = _muxPacketBytes.TryGetValue(item.Key, out var byteCount) ? byteCount : 0,
                        RandomAccessFrames = _muxKeyFrameCounts.TryGetValue(item.Key, out var keyFrames) ? keyFrames : 0
                    })
                    .ToList();
            }

            var lastPacketTicks = Interlocked.Read(ref _lastPlayableMuxPacketUtcTicks);
            return new HtspProducerStatus
            {
                ChannelId = _channelId,
                PlaybackId = UniqueId,
                SubscriptionId = _subscriptionId,
                State = _closing ? "closing" : _recovering ? "recovering" : _started ? "streaming" : "opening",
                OpenedUtc = _producerOpenedUtc == DateTime.MinValue ? null : _producerOpenedUtc,
                Adapter = _sourceAdapter,
                Service = _sourceService,
                Network = _sourceNetwork,
                Mux = _sourceMux,
                Provider = _sourceProvider,
                SharedPlaybackCount = GetSharedPlaybackReferenceCount(),
                ActiveReaderCount = Interlocked.CompareExchange(ref _activeStreamReaders, 0, 0),
                SignalStatus = signal?.Status,
                HasLock = HasFrontendLock(signal?.Status),
                SignalRaw = signal?.SignalRaw,
                SignalPercent = NormalizeFrontendValue(signal?.SignalRaw) ?? NormalizeAbsoluteFrontendValue(signal?.SignalAbsolute, -100, -20),
                SignalDbm = signal?.SignalAbsolute / 1000.0,
                SnrRaw = signal?.SnrRaw,
                SnrPercent = NormalizeFrontendValue(signal?.SnrRaw) ?? NormalizeAbsoluteFrontendValue(signal?.SnrAbsolute, 0, 40),
                SnrDb = signal?.SnrAbsolute / 1000.0,
                Ber = signal?.Ber,
                Unc = signal?.Unc,
                SignalAgeMs = signal == null || signal.UpdatedUtc == default ? null : Math.Max(0, (long)(DateTime.UtcNow - signal.UpdatedUtc).TotalMilliseconds),
                QueuePackets = Interlocked.Read(ref _lastQueuePackets),
                QueueBytes = Interlocked.Read(ref _lastQueueBytes),
                QueueDelayUs = Interlocked.Read(ref _lastQueueDelayUs),
                QueueIDrops = Interlocked.Read(ref _lastQueueIdrops),
                QueuePDrops = Interlocked.Read(ref _lastQueuePdrops),
                QueueBDrops = Interlocked.Read(ref _lastQueueBdrops),
                LastMuxPacketAgeMs = lastPacketTicks <= 0 ? null : Math.Max(0, (long)(DateTime.UtcNow - new DateTime(lastPacketTicks, DateTimeKind.Utc)).TotalMilliseconds),
                ReconnectAttempts = Interlocked.CompareExchange(ref _liveReconnectAttempts, 0, 0),
                SignalRecoveryAttempts = Interlocked.CompareExchange(ref _signalRecoveryAttempts, 0, 0),
                AwaitingCleanVideo = Interlocked.CompareExchange(ref _awaitingCleanVideoRandomAccess, 0, 0) != 0,
                KeyframeStartupReady = _startupCacheKeyframeAligned,
                StartupCacheBytes = Interlocked.Read(ref _startupCacheBytes),
                Streams = streams
            };
        }

        private static bool GetConfiguredSignalRecoveryEnabled()
        {
            return Plugin.Instance?.Configuration?.HTSPSignalRecoveryEnabled ?? true;
        }

        private static int GetConfiguredSignalLockLossSeconds()
        {
            return Math.Max(1, Math.Min(30, Plugin.Instance?.Configuration?.HTSPSignalLockLossSeconds ?? 3));
        }

        private static int GetConfiguredSignalUncBurstThreshold()
        {
            return Math.Max(1, Math.Min(1000, Plugin.Instance?.Configuration?.HTSPSignalUncBurstThreshold ?? 5));
        }

        private static int GetConfiguredSignalIdrWaitSeconds()
        {
            return Math.Max(1, Math.Min(15, Plugin.Instance?.Configuration?.HTSPSignalIdrWaitSeconds ?? 3));
        }

        private static int GetConfiguredSignalRecoveryMaxReconnects()
        {
            return Math.Max(0, Math.Min(10, Plugin.Instance?.Configuration?.HTSPSignalRecoveryMaxReconnects ?? 2));
        }

        private static int GetConfiguredSignalRecoveryCooldownSeconds()
        {
            return Math.Max(1, Math.Min(300, Plugin.Instance?.Configuration?.HTSPSignalRecoveryCooldownSeconds ?? 15));
        }

        private void ResetConnectionAttemptSignals()
        {
            lock (_connectionStateLock)
            {
                _connectionFirstPacket = CreateFirstPacketSource();
                _connectionError = CreateConnectionErrorSource();
            }
        }

        private Task<bool> GetConnectionFirstPacketTask()
        {
            lock (_connectionStateLock)
            {
                return _connectionFirstPacket.Task;
            }
        }

        private TaskCompletionSource<bool> GetConnectionFirstPacketTaskSource()
        {
            lock (_connectionStateLock)
            {
                return _connectionFirstPacket;
            }
        }

        private Task<Exception> GetConnectionErrorTask()
        {
            lock (_connectionStateLock)
            {
                return _connectionError.Task;
            }
        }

        private void NotifyConnectionError(Exception ex)
        {
            TaskCompletionSource<Exception> source;
            lock (_connectionStateLock)
            {
                source = _connectionError;
            }

            source.TrySetResult(ex);
        }

        private void CloseCurrentConnection(bool unsubscribe)
        {
            HTSConnectionAsync connection;
            lock (_connectionStateLock)
            {
                connection = _connection;
                _connection = null;
            }

            if (connection == null)
            {
                return;
            }

            connection.BeginExpectedClose();

            if (unsubscribe && _subscriptionId > 0)
            {
                try
                {
                    var unsubscribeMessage = new HTSMessage { Method = "unsubscribe" };
                    unsubscribeMessage.putField("subscriptionId", _subscriptionId);
                    var response = new TaskResponseHandler();
                    connection.sendMessage(unsubscribeMessage, response);
                    response.Task.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                }
            }

            try
            {
                connection.Dispose();
            }
            catch
            {
            }
        }

        private static TimeSpan GetRetryDelay(int attempt)
        {
            if (attempt <= 1)
            {
                return InitialReconnectDelay;
            }

            var multiplier = Math.Min(1 << Math.Min(attempt - 1, 4), 16);
            var delay = TimeSpan.FromMilliseconds(InitialReconnectDelay.TotalMilliseconds * multiplier);
            return delay > MaxReconnectDelay ? MaxReconnectDelay : delay;
        }

        private bool BelongsToCurrentSubscription(HTSMessage response)
        {
            return !response.containsField("subscriptionId") || response.getInt("subscriptionId") == _subscriptionId;
        }

        private static bool IsSubscriptionMessage(string method)
        {
            switch (method)
            {
                case "subscriptionStart":
                case "subscriptionStop":
                case "subscriptionStatus":
                case "subscriptionGrace":
                case "subscriptionSkip":
                case "subscriptionSpeed":
                case "queueStatus":
                case "signalStatus":
                case "timeshiftStatus":
                case "streamStatus":
                case "descrambleInfo":
                case "muxpkt":
                    return true;
                default:
                    return false;
            }
        }

        private static void ThrowIfResponseError(HTSMessage response, string operation)
        {
            if (response.getInt("noaccess", 0) == 1)
            {
                throw new UnauthorizedAccessException("TVHeadend HTSP " + operation + " denied access.");
            }

            if (response.containsField("error"))
            {
                throw new InvalidOperationException("TVHeadend HTSP " + operation + " failed: " + response.getString("error"));
            }
        }

        private static string BuildStatusDetails(string status, string subscriptionError)
        {
            if (!string.IsNullOrWhiteSpace(status) && !string.IsNullOrWhiteSpace(subscriptionError))
            {
                return status + " (" + subscriptionError + ")";
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                return status;
            }

            return subscriptionError ?? string.Empty;
        }

        private static int GetInt(HTSMessage message, string field, int defaultValue)
        {
            return message.containsField(field) ? message.getInt(field) : defaultValue;
        }

        private static long GetLong(HTSMessage message, string field, long defaultValue)
        {
            return message.containsField(field) ? message.getLong(field) : defaultValue;
        }

        private static int? GetNullableInt(HTSMessage message, string field)
        {
            return message.containsField(field) ? message.getInt(field) : null;
        }

        private static long? GetNullableLong(HTSMessage message, string field)
        {
            return message.containsField(field) ? message.getLong(field) : null;
        }

        public void Dispose()
        {
            Close().GetAwaiter().GetResult();

            if (!IsSharedProxy && _registeredAsSharedHub && GetSharedPlaybackReferenceCount() > 0)
            {
                // Jellyfin may dispose the first playback object while other
                // devices are still attached to the shared upstream channel hub.
                // The static hub table still owns this object, so keep producer
                // resources alive until the last playback reference leaves.
                return;
            }

            DisposeClientAbortRegistration();
            CancelReaderIdleDisconnect();
            CancelSharedHubIdleClose();
            CancelAllOwnerIdleDisconnects();
            _stream.Dispose();
            _lifetimeCancellationTokenSource.Dispose();
            _connectionSemaphore.Dispose();
        }

        internal static bool LooksLikeTransportStream(byte[] payload)
        {
            if (payload == null || payload.Length < 188 || payload.Length % 188 != 0)
            {
                return false;
            }

            var packetsToCheck = Math.Min(payload.Length / 188, 8);
            for (var i = 0; i < packetsToCheck; i++)
            {
                if (payload[i * 188] != 0x47)
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class AnnexBNalUnit
        {
            public AnnexBNalUnit(int offset, int length, int startCodeLength, int type)
            {
                Offset = offset;
                Length = length;
                StartCodeLength = startCodeLength;
                Type = type;
            }

            public int Offset { get; }

            public int Length { get; }

            public int StartCodeLength { get; }

            public int Type { get; }
        }

        private sealed class CallbackResponseHandler : HTSResponseHandler
        {
            private readonly Action<HTSMessage> _callback;

            public CallbackResponseHandler(Action<HTSMessage> callback)
            {
                _callback = callback;
            }

            public void handleResponse(HTSMessage response)
            {
                _callback?.Invoke(response);
            }
        }

        private sealed class TaskResponseHandler : HTSResponseHandler
        {
            private readonly TaskCompletionSource<HTSMessage> _task = new TaskCompletionSource<HTSMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<HTSMessage> Task => _task.Task;

            public void handleResponse(HTSMessage response)
            {
                _task.TrySetResult(response);
            }
        }

        private sealed class ConsumerReadStream : Stream
        {
            private readonly BlockingByteStream _inner;
            private readonly Action<bool> _onClosed;
            private readonly ILogger _logger;
            private readonly string _channelId;
            private readonly string _readerId;
            private int _closed;
            private bool _consumedBytes;

            public ConsumerReadStream(BlockingByteStream inner, Action<bool> onClosed, ILogger logger, string channelId, string readerId)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _onClosed = onClosed;
                _logger = logger;
                _channelId = channelId;
                _readerId = readerId;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, count);
                if (read > 0)
                {
                    _consumedBytes = true;
                }

                return read;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
                {
                    try
                    {
                        _onClosed?.Invoke(_consumedBytes);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(
                            ex,
                            "Error while closing HTSP direct stream reader {ReaderId} for channel {ChannelId}",
                            _readerId,
                            _channelId);
                    }
                }

                base.Dispose(disposing);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class BlockingByteStream : Stream
        {
            private const long MaxBufferedBytes = 128L * 1024L * 1024L;

            private readonly Queue<byte[]> _chunks = new Queue<byte[]>();
            private readonly Action<string> _onConsumerClosed;
            private byte[] _current;
            private int _currentOffset;
            private bool _completed;
            private bool _disposed;
            private long _bufferedBytes;
            private Exception _error;
            private int _consumerClosedNotified;

            public BlockingByteStream(Action<string> onConsumerClosed)
            {
                _onConsumerClosed = onConsumerClosed;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public bool IsCompleted
            {
                get
                {
                    lock (_chunks)
                    {
                        return _completed || _disposed;
                    }
                }
            }

            public long BufferedBytes
            {
                get
                {
                    lock (_chunks)
                    {
                        return _bufferedBytes;
                    }
                }
            }

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public void WriteChunk(byte[] chunk)
            {
                if (chunk == null || chunk.Length == 0)
                {
                    return;
                }

                var consumerTooSlow = false;
                lock (_chunks)
                {
                    if (_completed || _disposed)
                    {
                        return;
                    }

                    _chunks.Enqueue(chunk);
                    _bufferedBytes += chunk.Length;
                    if (_bufferedBytes > MaxBufferedBytes)
                    {
                        _completed = true;
                        _chunks.Clear();
                        _bufferedBytes = 0;
                        consumerTooSlow = true;
                    }

                    Monitor.PulseAll(_chunks);
                }

                if (consumerTooSlow)
                {
                    SignalConsumerClosed("Jellyfin stopped draining the direct stream buffer");
                }
            }

            public void Complete(Exception error = null)
            {
                lock (_chunks)
                {
                    _error = error;
                    _completed = true;
                    Monitor.PulseAll(_chunks);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null)
                {
                    throw new ArgumentNullException(nameof(buffer));
                }

                if (offset < 0 || count < 0 || offset + count > buffer.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset));
                }

                lock (_chunks)
                {
                    while (_current == null || _currentOffset >= _current.Length)
                    {
                        if (_disposed)
                        {
                            return 0;
                        }

                        if (_chunks.Count > 0)
                        {
                            _current = _chunks.Dequeue();
                            _bufferedBytes -= _current.Length;
                            if (_bufferedBytes < 0)
                            {
                                _bufferedBytes = 0;
                            }

                            _currentOffset = 0;
                            break;
                        }

                        if (_error != null)
                        {
                            throw new IOException(_error.Message, _error);
                        }

                        if (_completed)
                        {
                            return 0;
                        }

                        Monitor.Wait(_chunks, TimeSpan.FromSeconds(30));
                    }

                    var bytesToCopy = Math.Min(count, _current.Length - _currentOffset);
                    Array.Copy(_current, _currentOffset, buffer, offset, bytesToCopy);
                    _currentOffset += bytesToCopy;
                    return bytesToCopy;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    lock (_chunks)
                    {
                        _disposed = true;
                        _completed = true;
                        _chunks.Clear();
                        _bufferedBytes = 0;
                        _current = null;
                        _currentOffset = 0;
                        Monitor.PulseAll(_chunks);
                    }
                }

                base.Dispose(disposing);
            }

            private void SignalConsumerClosed(string reason)
            {
                if (Interlocked.Exchange(ref _consumerClosedNotified, 1) == 0)
                {
                    _onConsumerClosed?.Invoke(reason);
                }
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
