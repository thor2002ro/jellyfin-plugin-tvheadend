using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
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
        private static int _nextSubscriptionId;

        private readonly string _channelId;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServerApplicationHost _appHost;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<HtspLiveStream> _logger;
        private readonly BlockingByteStream _stream = new BlockingByteStream();
        private readonly HtspTransportStreamMuxer _muxer = new HtspTransportStreamMuxer();
        private readonly TaskCompletionSource<bool> _firstPacket = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<int> _ignoredMuxStreams = new HashSet<int>();

        private HTSConnectionAsync _connection;
        private int _subscriptionId;
        private bool _closing;
        private bool _started;

        public HtspLiveStream(MediaSourceInfo mediaSource, string channelId, ILoggerFactory loggerFactory, IServerApplicationHost appHost, IHttpContextAccessor httpContextAccessor)
        {
            MediaSource = mediaSource;
            _channelId = channelId;
            _loggerFactory = loggerFactory;
            _appHost = appHost;
            _httpContextAccessor = httpContextAccessor;
            _logger = loggerFactory.CreateLogger<HtspLiveStream>();
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
                var liveStreamPath = "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
                MediaSource.Path = GetClientApiBaseUrl() + liveStreamPath;
                MediaSource.EncoderPath = _appHost.GetApiUrlForLocalAccess() + liveStreamPath;
                MediaSource.EncoderProtocol = MediaProtocol.Http;
                MediaSource.Protocol = MediaProtocol.Http;
                MediaSource.Container = "ts";
                MediaSource.IgnoreDts = true;
                MediaSource.SupportsDirectPlay = false;
                MediaSource.SupportsDirectStream = true;
                MediaSource.SupportsTranscoding = true;
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

            var streamIndex = response.getInt("stream");
            if (!_muxer.IsStreamKnown(streamIndex))
            {
                if (_ignoredMuxStreams.Add(streamIndex))
                {
                    _logger.LogDebug(
                        "HTSP dropping mux packets for non-playable or unsupported stream index {StreamIndex}; this mirrors Kodi pvr.hts demux behavior for unknown/private streams",
                        streamIndex);
                }

                return;
            }

            var chunk = _muxer.WritePacket(
                streamIndex,
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
                if (item is not HTSMessage stream || !stream.containsField("index"))
                {
                    continue;
                }

                streams.Add(new HtspTransportStreamMuxer.StreamInfo
                {
                    Index = stream.getInt("index"),
                    Codec = stream.getString("type", "UNKNOWN"),
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

            var orderedStreams = streams.OrderBy(i => i.Index).ToList();
            var playableStreams = orderedStreams
                .Where(IsPlayableForJellyfinTransportStream)
                .ToList();
            var droppedStreams = orderedStreams
                .Where(i => !IsPlayableForJellyfinTransportStream(i))
                .ToList();

            _ignoredMuxStreams.Clear();
            _muxer.SetStreams(playableStreams);
            UpdateMediaSourceStreamMetadata(playableStreams);
            LogSourceInfo(response);
            _logger.LogInformation(
                "HTSP stream metadata received: {TotalCount} stream(s), muxing {MuxedCount} Kodi-style playable stream(s): {StreamSummary}",
                orderedStreams.Count,
                _muxer.SupportedStreamCount,
                _muxer.StreamSummary);

            if (droppedStreams.Count > 0)
            {
                _logger.LogInformation(
                    "HTSP ignored {DroppedCount} non-playable/private stream(s) from Jellyfin MPEG-TS output: {DroppedStreams}",
                    droppedStreams.Count,
                    string.Join(", ", droppedStreams.Select(DescribeHtspStream)));
            }

            if (playableStreams.Count > 0 && _muxer.SupportedStreamCount != playableStreams.Count)
            {
                _logger.LogWarning(
                    "HTSP stream metadata contained {PlayableCount} Jellyfin-playable stream(s), but only {MuxedCount} could be routed into MPEG-TS.",
                    playableStreams.Count,
                    _muxer.SupportedStreamCount);
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
                "HTSP exposed {Count} playable stream(s) to Jellyfin metadata: {Streams}; private/data streams are ignored in the MPEG-TS output",
                mediaStreams.Count,
                string.Join(", ", mediaStreams.Select(i => $"#{i.Index}:{i.Type}:{i.Codec}{(string.IsNullOrWhiteSpace(i.Language) ? string.Empty : ":" + i.Language)}")));
        }

        private static bool IsPlayableForJellyfinTransportStream(HtspTransportStreamMuxer.StreamInfo stream)
        {
            return stream != null
                && HtspTransportStreamMuxer.CanMuxCodec(stream.Codec)
                && TryGetMediaStreamType(stream.Codec, out _);
        }

        private static string DescribeHtspStream(HtspTransportStreamMuxer.StreamInfo stream)
        {
            if (stream == null)
            {
                return "<null>";
            }

            var codec = string.IsNullOrWhiteSpace(stream.Codec) ? "UNKNOWN" : stream.Codec;
            return stream.Index + ":" + codec + (string.IsNullOrWhiteSpace(stream.Language) ? string.Empty : ":" + stream.Language);
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
                case "TELETEXT":
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
                case "TELETEXT":
                    // Keep this non-text too.  Embedded live-TV teletext is carried
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
