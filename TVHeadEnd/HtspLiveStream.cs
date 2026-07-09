using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
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
        private static int _nextSubscriptionId;

        private readonly string _channelId;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServerApplicationHost _appHost;
        private readonly ILogger<HtspLiveStream> _logger;
        private readonly BlockingByteStream _stream = new BlockingByteStream();
        private readonly HtspTransportStreamMuxer _muxer = new HtspTransportStreamMuxer();
        private readonly TaskCompletionSource<bool> _firstPacket = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private HTSConnectionAsync _connection;
        private int _subscriptionId;
        private bool _closing;
        private bool _started;

        public HtspLiveStream(MediaSourceInfo mediaSource, string channelId, ILoggerFactory loggerFactory, IServerApplicationHost appHost)
        {
            MediaSource = mediaSource;
            _channelId = channelId;
            _loggerFactory = loggerFactory;
            _appHost = appHost;
            _logger = loggerFactory.CreateLogger<HtspLiveStream>();
            UniqueId = Guid.NewGuid().ToString("N");
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
            var config = Plugin.Instance.Configuration;
            _subscriptionId = Interlocked.Increment(ref _nextSubscriptionId);
            _connection = new HTSConnectionAsync(this, "TVHclient4Jellyfin-HTSP", "" + HTSMessage.HTSP_VERSION, _loggerFactory);

            try
            {
                _connection.open(config.TVH_ServerName.Trim(), config.HTSP_Port);
                if (!_connection.authenticate(config.Username.Trim(), config.Password.Trim(), false))
                {
                    throw new InvalidOperationException("TVHeadend HTSP authentication failed.");
                }

                var subscribe = new HTSMessage { Method = "subscribe" };
                subscribe.putField("channelId", HtspFieldHelper.ParseUInt32Id(_channelId, "channelId"));
                subscribe.putField("subscriptionId", _subscriptionId);
                subscribe.putField("weight", 100);
                subscribe.putField("90khz", 1);
                subscribe.putField("normts", 1);

                if (!string.IsNullOrWhiteSpace(config.Profile))
                {
                    subscribe.putField("profile", config.Profile.Trim());
                }

                var response = new TaskResponseHandler();
                _connection.sendMessage(subscribe, response);
                var subscribeResponse = await response.Task.WaitAsync(openCancellationToken).ConfigureAwait(false);
                ParseSubscribeResponse(subscribeResponse);

                var firstPacket = await Task.WhenAny(_firstPacket.Task, Task.Delay(TimeSpan.FromSeconds(10), openCancellationToken)).ConfigureAwait(false);
                if (openCancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("HTSP live stream open cancelled.", openCancellationToken);
                }

                if (firstPacket != _firstPacket.Task)
                {
                    throw new TimeoutException("Timed out waiting for the first HTSP mux packet.");
                }

                await _firstPacket.Task.ConfigureAwait(false);
                MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
                MediaSource.Protocol = MediaProtocol.Http;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && openCancellationToken.IsCancellationRequested))
            {
                await Close().ConfigureAwait(false);

                if (ex is HtspLiveStreamException)
                {
                    throw;
                }

                var htspException = HtspLiveStreamException.Create(_channelId, ex);
                _logger.LogError(htspException, "HTSP live stream open failed for channel {ChannelId}; TVHeadend HTTP fallback is disabled", _channelId);
                throw htspException;
            }
            catch
            {
                await Close().ConfigureAwait(false);
                throw;
            }
        }

        public Task Close()
        {
            _closing = true;
            EnableStreamSharing = false;
            _stream.Complete();

            if (_connection != null)
            {
                try
                {
                    var unsubscribe = new HTSMessage { Method = "unsubscribe" };
                    unsubscribe.putField("subscriptionId", _subscriptionId);
                    var response = new TaskResponseHandler();
                    _connection.sendMessage(unsubscribe, response);
                    response.Task.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                }

                _connection.stop();
                _connection = null;
            }

            return Task.CompletedTask;
        }

        public Stream GetStream()
        {
            return _stream;
        }

        public void onError(Exception ex)
        {
            if (_closing)
            {
                _logger.LogDebug(ex, "HTSP live stream connection closed");
                _firstPacket.TrySetCanceled();
                _stream.Complete();
                return;
            }

            _logger.LogError(ex, "HTSP live stream error");
            CompleteWithError(ex);
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

            _logger.LogInformation(
                "HTSP subscription {SubscriptionId} accepted: timestamps={TimestampMode}, normts={Normts}, timeshiftPeriod={TimeshiftPeriod}s",
                _subscriptionId,
                timestampsAre90Khz ? "90kHz" : "1MHz",
                normts,
                timeshiftPeriod);
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
                WriteOutput(payload);
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

            var chunk = _muxer.WritePacket(
                response.getInt("stream"),
                payload,
                response.containsField("pts") ? response.getLong("pts") : null,
                response.containsField("dts") ? response.getLong("dts") : null);

            if (chunk.Length > 0)
            {
                WriteOutput(chunk);
            }
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
                if (item is not HTSMessage stream || !stream.containsField("index") || !stream.containsField("type"))
                {
                    continue;
                }

                streams.Add(new HtspTransportStreamMuxer.StreamInfo
                {
                    Index = stream.getInt("index"),
                    Codec = stream.getString("type"),
                    Language = stream.getString("language", string.Empty),
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

            _muxer.SetStreams(streams);
            LogSourceInfo(response);
            _logger.LogInformation(
                "HTSP stream metadata received: {TotalCount} stream(s), muxing {SupportedCount} supported stream(s)",
                streams.Count,
                _muxer.SupportedStreamCount);

            if (streams.Count > 0 && _muxer.SupportedStreamCount == 0)
            {
                _logger.LogWarning("TVHeadend reported streams, but none of their codecs can be remuxed into MPEG-TS by the HTSP muxer.");
            }
        }

        private void LogQueueStatus(HTSMessage response)
        {
            var idrops = GetInt(response, "Idrops", 0);
            var pdrops = GetInt(response, "Pdrops", 0);
            var bdrops = GetInt(response, "Bdrops", 0);
            var logLevel = idrops > 0 || pdrops > 0 || bdrops > 0 ? LogLevel.Warning : LogLevel.Trace;

            _logger.Log(
                logLevel,
                "HTSP queue {SubscriptionId}: packets={Packets}, bytes={Bytes}, delay={Delay}us, drops I/P/B={Idrops}/{Pdrops}/{Bdrops}",
                _subscriptionId,
                GetInt(response, "packets", 0),
                GetInt(response, "bytes", 0),
                GetLong(response, "delay", 0),
                idrops,
                pdrops,
                bdrops);
        }

        private void LogSignalStatus(HTSMessage response)
        {
            _logger.LogTrace(
                "HTSP signal {SubscriptionId}: status={Status}, snr={Snr}, signal={Signal}, ber={Ber}, unc={Unc}",
                _subscriptionId,
                response.getString("feStatus", string.Empty),
                GetInt(response, "feSNR", 0),
                GetInt(response, "feSignal", 0),
                GetInt(response, "feBER", 0),
                GetInt(response, "feUNC", 0));
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

                _logger.LogInformation(
                    "HTSP source: adapter={Adapter}, network={Network}, mux={Mux}, provider={Provider}, service={Service}, satpos={SatPos}",
                    source.getString("adapter", string.Empty),
                    source.getString("network", string.Empty),
                    source.getString("mux", string.Empty),
                    source.getString("provider", string.Empty),
                    source.getString("service", string.Empty),
                    source.getString("satpos", string.Empty));
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Could not parse HTSP sourceinfo");
            }
        }

        private void WriteOutput(byte[] chunk)
        {
            _started = true;
            _stream.WriteChunk(chunk);
            _firstPacket.TrySetResult(true);
        }

        private void CompleteWithError(Exception ex)
        {
            if (!_closing)
            {
                _firstPacket.TrySetException(ex);
            }

            _stream.Complete(ex);
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

        public void Dispose()
        {
            Close().GetAwaiter().GetResult();
            _stream.Dispose();
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

        private sealed class TaskResponseHandler : HTSResponseHandler
        {
            private readonly TaskCompletionSource<HTSMessage> _task = new TaskCompletionSource<HTSMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<HTSMessage> Task => _task.Task;

            public void handleResponse(HTSMessage response)
            {
                _task.TrySetResult(response);
            }
        }

        private sealed class BlockingByteStream : Stream
        {
            private readonly Queue<byte[]> _chunks = new Queue<byte[]>();
            private byte[] _current;
            private int _currentOffset;
            private bool _completed;
            private Exception _error;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

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

                lock (_chunks)
                {
                    if (_completed)
                    {
                        return;
                    }

                    _chunks.Enqueue(chunk);
                    Monitor.PulseAll(_chunks);
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
                lock (_chunks)
                {
                    while (_current == null || _currentOffset >= _current.Length)
                    {
                        if (_chunks.Count > 0)
                        {
                            _current = _chunks.Dequeue();
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
