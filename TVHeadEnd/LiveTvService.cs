using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Configuration;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;
using static TVHeadEnd.AccessTicketHandler.TicketType;

namespace TVHeadEnd
{
    public class LiveTvService : ILiveTvService, ISupportsDirectStreamProvider
    {
        private readonly IMediaEncoder _mediaEncoder;

        private readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);

        private readonly ILoggerFactory _loggerFactory;
        private readonly IServerApplicationHost _appHost;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly HTSConnectionHandler _htsConnectionHandler;
        private readonly AccessTicketHandler _channelTicketHandler;
        private readonly AccessTicketHandler _recordingTicketHandler;

        private readonly ILogger<LiveTvService> _logger;
        public DateTime _lastRecordingChange = DateTime.MinValue;

        public LiveTvService(ILoggerFactory loggerFactory, IMediaEncoder mediaEncoder, HTSConnectionHandler connectionHandler, IServerApplicationHost appHost, IHttpContextAccessor httpContextAccessor)
        {
            //System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _loggerFactory = loggerFactory;
            _appHost = appHost;
            _httpContextAccessor = httpContextAccessor;
            _logger = loggerFactory.CreateLogger<LiveTvService>();
            _logger.LogDebug("LiveTvService()");

            _htsConnectionHandler = connectionHandler;

            {
                var lifeSpan = TimeSpan.FromSeconds(15);       // Revalidate tickets every 15 seconds
                var requestTimeout = TimeSpan.FromSeconds(10); // First request retry after 10 seconds
                var retries = 2;                               // Number of times to retry getting tickets
                _channelTicketHandler = new AccessTicketHandler(loggerFactory, _htsConnectionHandler, requestTimeout, retries, lifeSpan, Channel);
                _recordingTicketHandler = new AccessTicketHandler(loggerFactory, _htsConnectionHandler, requestTimeout, retries, lifeSpan, Recording);
            }

            //Added for stream probing
            _mediaEncoder = mediaEncoder;
        }

        public string HomePageUrl { get { return "http://tvheadend.org/"; } }

        public string Name { get { return "TVHclient LiveTvService"; } }

        public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            int timeOut = await _htsConnectionHandler.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.CancelSeriesTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage deleteAutorecMessage = new HTSMessage();
            deleteAutorecMessage.Method = "deleteAutorecEntry";
            deleteAutorecMessage.putField("id", timerId);

            HTSMessage deleteAutorecResponse;
            try
            {
                deleteAutorecResponse = await SendMessageAsync(deleteAutorecMessage, cancellationToken).ConfigureAwait(false);
                _lastRecordingChange = DateTime.UtcNow;
            }
            catch (TimeoutException)
            {
                _logger.LogError("LiveTvService.CancelSeriesTimerAsync: can't delete recording because the timeout was reached");
                return;
            }

            Boolean success = deleteAutorecResponse.getInt("success", 0) == 1;
            if (!success)
            {
                if (deleteAutorecResponse.containsField("error"))
                {
                    _logger.LogError("LiveTvService.CancelSeriesTimerAsync: can't delete recording: '{why}'", deleteAutorecResponse.getString("error"));
                }
                else if (deleteAutorecResponse.containsField("noaccess"))
                {
                    _logger.LogError("LiveTvService.CancelSeriesTimerAsync: can't delete recording: '{why}'", deleteAutorecResponse.getString("noaccess"));
                }
            }
        }

        public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            int timeOut = await _htsConnectionHandler.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.CancelTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage cancelTimerMessage = new HTSMessage();
            cancelTimerMessage.Method = "cancelDvrEntry";
            cancelTimerMessage.putField("id", _htsConnectionHandler.ResolveDvrId(timerId));

            HTSMessage cancelTimerResponse;
            try
            {
                cancelTimerResponse = await SendMessageAsync(cancelTimerMessage, cancellationToken).ConfigureAwait(false);
                _lastRecordingChange = DateTime.UtcNow;
            }
            catch (TimeoutException)
            {
                _logger.LogError("LiveTvService.CancelTimerAsync: can't cancel timer because the timeout was reached");
                return;
            }

            Boolean success = cancelTimerResponse.getInt("success", 0) == 1;
            if (!success)
            {
                if (cancelTimerResponse.containsField("error"))
                {
                    _logger.LogError("LiveTvService.CancelTimerAsync: can't cancel timer: '{why}'", cancelTimerResponse.getString("error"));
                }
                else if (cancelTimerResponse.containsField("noaccess"))
                {
                    _logger.LogError("LiveTvService.CancelTimerAsync: can't cancel timer: '{why}'", cancelTimerResponse.getString("noaccess"));
                }
            }
        }

        public async Task CloseLiveStream(string subscriptionId, CancellationToken cancellationToken)
        {
            await Task.Factory.StartNew(() =>
            {
                _logger.LogDebug("LiveTvService.CloseLiveStream: closed stream for subscriptionId: {id}", subscriptionId);
                return subscriptionId;
            }, cancellationToken);
        }

        public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            await SaveSeriesTimerAsync("addAutorecEntry", info, cancellationToken).ConfigureAwait(false);
        }

        public async Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        {
            int timeOut = await _htsConnectionHandler.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.CreateTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage createTimerMessage = new HTSMessage();
            createTimerMessage.Method = "addDvrEntry";
            createTimerMessage.putField("channelId", _htsConnectionHandler.ResolveChannelId(info.ChannelId));
            createTimerMessage.putField("start", new DateTimeOffset(info.StartDate.ToUniversalTime()).ToUnixTimeSeconds());
            createTimerMessage.putField("stop", new DateTimeOffset(info.EndDate.ToUniversalTime()).ToUnixTimeSeconds());
            createTimerMessage.putField("startExtra", (long)(info.PrePaddingSeconds / 60));
            createTimerMessage.putField("stopExtra", (long)(info.PostPaddingSeconds / 60));
            createTimerMessage.putField("priority", _htsConnectionHandler.GetPriority()); // info.Priority delivers always 0 - no GUI
            createTimerMessage.putField("configName", _htsConnectionHandler.GetProfile());
            createTimerMessage.putField("description", info.Overview);
            createTimerMessage.putField("title", info.Name);
            createTimerMessage.putField("creator", Plugin.Instance.Configuration.Username);

            HTSMessage createTimerResponse;
            try
            {
                createTimerResponse = await SendMessageAsync(createTimerMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("LiveTvService.CreateTimerAsync: can't create timer because the timeout was reached");
                return;
            }

            Boolean success = createTimerResponse.getInt("success", 0) == 1;
            if (!success)
            {
                if (createTimerResponse.containsField("error"))
                {
                    _logger.LogError("LiveTvService.CreateTimerAsync: can't create timer: '{why}'", createTimerResponse.getString("error"));
                }
                else if (createTimerResponse.containsField("noaccess"))
                {
                    _logger.LogError("LiveTvService.CreateTimerAsync: can't create timer: '{why}'", createTimerResponse.getString("noaccess"));
                }
            }
        }

        public async Task DeleteRecordingAsync(string recordingId, CancellationToken cancellationToken)
        {
            int timeOut = await _htsConnectionHandler.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("LiveTvService.DeleteRecordingAsync: call cancelled or timed out");
                return;
            }

            HTSMessage deleteRecordingMessage = new HTSMessage();
            deleteRecordingMessage.Method = "deleteDvrEntry";
            deleteRecordingMessage.putField("id", _htsConnectionHandler.ResolveDvrId(recordingId));

            HTSMessage deleteRecordingResponse;
            try
            {
                deleteRecordingResponse = await SendMessageAsync(deleteRecordingMessage, cancellationToken).ConfigureAwait(false);
                _lastRecordingChange = DateTime.UtcNow;
            }
            catch (TimeoutException)
            {
                _logger.LogError("LiveTvService.DeleteRecordingAsync: can't delete recording because the timeout was reached");
                return;
            }

            Boolean success = deleteRecordingResponse.getInt("success", 0) == 1;
            if (!success)
            {
                if (deleteRecordingResponse.containsField("error"))
                {
                    _logger.LogError("LiveTvService.DeleteRecordingAsync: can't delete recording: '{why}'", deleteRecordingResponse.getString("error"));
                }
                else if (deleteRecordingResponse.containsField("noaccess"))
                {
                    _logger.LogError("LiveTvService.DeleteRecordingAsync: can't delete recording: '{why}'", deleteRecordingResponse.getString("noaccess"));
                }
            }
        }

        public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        {
            int timeOut = await _htsConnectionHandler.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("LiveTvService.GetChannelsAsync: call cancelled or timed out - returning empty list");
                return new List<ChannelInfo>();
            }

            IEnumerable<ChannelInfo> channels;
            try
            {
                channels = await _htsConnectionHandler.BuildChannelInfos(cancellationToken).WaitAsync(_timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return [];
            }

            var list = channels.ToList();

            foreach (var channel in list)
            {
                if (string.IsNullOrEmpty(channel.ImageUrl))
                {
                    channel.ImageUrl = _htsConnectionHandler.GetChannelImageUrl(channel.Id);
                }
            }

            return list;
        }

        public async Task<MediaSourceInfo> GetChannelStream(string channelId, string mediaSourceId, CancellationToken cancellationToken)
        {
            var streamingMethod = _htsConnectionHandler.GetStreamingMethod();
            if (streamingMethod == StreamingMethods.Htsp)
            {
                _logger.LogInformation(
                    "LiveTvService.GetChannelStream: HTSP streaming is selected for channel {ChannelId}; TVHeadend HTTP fallback is disabled",
                    channelId);
                return CreateHtspMediaSource(channelId);
            }

            var ticket = await _channelTicketHandler.GetTicket(channelId, cancellationToken);
            bool useBasicAuthentication = streamingMethod == StreamingMethods.HttpBasic;

            _logger.LogInformation(
                "LiveTvService.GetChannelStream: {StreamingMethod} streaming is selected; probing audio and subtitle tracks",
                streamingMethod);

            MediaSourceInfo livetvasset = new MediaSourceInfo
            {
                Id = channelId,
                Path = _htsConnectionHandler.GetHttpBaseUrl() + (useBasicAuthentication ? ticket.Path : ticket.Url),
                Protocol = MediaProtocol.Http,
                AnalyzeDurationMs = 2000,
                SupportsDirectStream = false,
                RequiresClosing = true,
                SupportsProbing = false,
                Container = "ts",
                RequiresOpening = true,
                IsInfiniteStream = true
            };

            if (useBasicAuthentication)
            {
                livetvasset.RequiredHttpHeaders = _htsConnectionHandler.GetHeaders();
            }

            await ProbeStream(livetvasset, "LiveTV", cancellationToken);

            if (_htsConnectionHandler.GetForceDeinterlace() && livetvasset.MediaStreams != null)
            {
                _logger.LogInformation("LiveTvService.GetChannelStream: force video deinterlacing for all channels and recordings is enabled");

                foreach (MediaStream stream in livetvasset.MediaStreams)
                {
                    if (stream.Type == MediaStreamType.Video && !stream.IsInterlaced)
                    {
                        stream.IsInterlaced = true;
                    }

                    stream.RealFrameRate = 50.0F;
                }
            }

            return livetvasset;
        }

        public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(string channelId, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            if (_htsConnectionHandler.GetStreamingMethod() == StreamingMethods.Htsp)
            {
                try
                {
                    var stream = new HtspLiveStream(CreateHtspMediaSource(channelId), _htsConnectionHandler.ResolveChannelId(channelId).ToString(), _loggerFactory, _appHost, _httpContextAccessor);
                    await stream.Open(cancellationToken).ConfigureAwait(false);
                    return stream;
                }
                catch (HtspLiveStreamException)
                {
                    throw;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    var htspException = HtspLiveStreamException.Create(channelId, ex);
                    _logger.LogError(htspException, "HTSP direct stream provider failed for channel {ChannelId}; TVHeadend HTTP fallback is disabled", channelId);
                    throw htspException;
                }
            }

            var mediaSource = await GetChannelStream(channelId, streamId, cancellationToken).ConfigureAwait(false);
            return new MediaSourceLiveStream(mediaSource, () => CloseLiveStream(mediaSource.Id, CancellationToken.None));
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
                _logger.LogWarning(ex, "Unable to resolve a client-facing Jellyfin URL; falling back to the local API URL for HTSP live stream metadata");
                return _appHost.GetApiUrlForLocalAccess().TrimEnd('/');
            }
        }

        internal async Task<string> GetRecordingStreamUrl(string recordingId, CancellationToken cancellationToken)
        {
            var ticket = await _recordingTicketHandler.GetTicket(recordingId, cancellationToken).ConfigureAwait(false);
            return _htsConnectionHandler.GetHttpBaseUrl() + ticket.Url;
        }

        internal string GetRecordingProxyUrl(string recordingId)
        {
            return _appHost.GetApiUrlForLocalAccess().TrimEnd('/') + "/TVHeadEnd/Recordings/" + Uri.EscapeDataString(recordingId)
                + "/" + GetRecordingStreamToken(recordingId) + "/Stream";
        }

        internal bool IsRecordingStreamTokenValid(string recordingId, string token)
        {
            var expected = Encoding.ASCII.GetBytes(GetRecordingStreamToken(recordingId));
            var actual = Encoding.ASCII.GetBytes(token ?? string.Empty);
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        private static string GetRecordingStreamToken(string recordingId)
        {
            var secret = Encoding.UTF8.GetBytes(Plugin.Instance.Configuration.RecordingStreamSecret);
            return Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(recordingId)));
        }

        private static string GetStableHtspMediaSourceId(string channelId)
        {
            var normalizedChannelId = string.IsNullOrWhiteSpace(channelId) ? string.Empty : channelId.Trim();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("tvheadend-htsp:" + normalizedChannelId));
            var guidBytes = hash.Take(16).ToArray();

            // Make the deterministic value GUID-shaped for Jellyfin code paths that
            // parse MediaSourceInfo.Id as a Guid, while keeping it stable across
            // repeated GetChannelStreamMediaSources/GetChannelStream calls.
            guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x40);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
            return new Guid(guidBytes).ToString("N");
        }

        private MediaSourceInfo CreateHtspMediaSource(string channelId)
        {
            return new MediaSourceInfo
            {
                // This ID is used by Jellyfin's HLS/subtitle paths as a media source ID.
                // Keep the TVHeadend channel ID separate and expose a GUID-shaped media
                // source ID so subtitle filter/attachment code paths do not try to
                // Guid.Parse() a numeric TVH channel ID.
                Id = GetStableHtspMediaSourceId(channelId),
                Path = GetClientApiBaseUrl(),
                EncoderPath = _appHost.GetApiUrlForLocalAccess(),
                EncoderProtocol = MediaProtocol.Http,
                Protocol = MediaProtocol.Http,
                AnalyzeDurationMs = 2000,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = true,
                // Live HTSP is rebuilt MPEG-TS. Prefer Jellyfin's most-compatible
                // live transcoding profile so web/remote clients do not choose
                // fMP4/AV1 profiles that the server hardware encoder may not
                // actually support.
                UseMostCompatibleTranscodingProfile = true,
                Container = "ts",
                GenPtsInput = true,
                RequiresOpening = true,
                RequiresClosing = true,
                IsInfiniteStream = true,
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = -1,
                        IsInterlaced = true,
                        RealFrameRate = 50.0F
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = -1
                    }
                }
            };
        }

        private async Task ProbeStream(MediaSourceInfo mediaSourceInfo, string source, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Probe stream for {source}", source);

            MediaInfoRequest req = new MediaInfoRequest
            {
                MediaType = MediaBrowser.Model.Dlna.DlnaProfileType.Video,
                MediaSource = mediaSourceInfo,
                ExtractChapters = false,
            };

            var originalRuntime = mediaSourceInfo.RunTimeTicks;
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();
            MediaInfo info = await _mediaEncoder.GetMediaInfo(req, cancellationToken).ConfigureAwait(false);
            stopWatch.Stop();
            TimeSpan ts = stopWatch.Elapsed;
            string elapsedTime = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
            _logger.LogDebug("Probe RunTime {ElapsedTime}", elapsedTime);

            if (info != null)
            {
                _logger.LogDebug("Probe returned:");

                mediaSourceInfo.Bitrate = info.Bitrate;
                _logger.LogDebug("        BitRate:                    {BitRate}", info.Bitrate);

                mediaSourceInfo.Container = info.Container;
                _logger.LogDebug("        Container:                  {Container}", info.Container);

                mediaSourceInfo.MediaStreams = info.MediaStreams;
                _logger.LogDebug("        MediaStreams:               ");
                LogMediaStreamList(info.MediaStreams, "                       ");

                mediaSourceInfo.RunTimeTicks = info.RunTimeTicks;
                _logger.LogDebug("        RunTimeTicks:               {RunTimeTicks}", info.RunTimeTicks);

                mediaSourceInfo.Size = info.Size;
                _logger.LogDebug("        Size:                       {Size}", info.Size);

                mediaSourceInfo.Timestamp = info.Timestamp;
                _logger.LogDebug("        Timestamp:                  {Timestamp}", info.Timestamp);

                mediaSourceInfo.Video3DFormat = info.Video3DFormat;
                _logger.LogDebug("        Video3DFormat:              {Video3DFormat}", info.Video3DFormat);

                mediaSourceInfo.VideoType = info.VideoType;
                _logger.LogDebug("        VideoType:                  {VideoType}", info.VideoType);

                mediaSourceInfo.RequiresClosing = true;
                _logger.LogDebug("        RequiresClosing:            {RequiresClosing}", info.RequiresClosing);

                mediaSourceInfo.RequiresOpening = true;
                _logger.LogDebug("        RequiresOpening:            {RequiresOpening}", info.RequiresOpening);

                mediaSourceInfo.SupportsDirectPlay = true;
                _logger.LogDebug("        SupportsDirectPlay:         {SupportsDirectPlay}", info.SupportsDirectPlay);

                mediaSourceInfo.SupportsDirectStream = true;
                _logger.LogDebug("        SupportsDirectStream:       {SupportsDirectStream}", info.SupportsDirectStream);

                mediaSourceInfo.SupportsTranscoding = true;
                _logger.LogDebug("        SupportsTranscoding:        {SupportsTranscoding}", info.SupportsTranscoding);

                mediaSourceInfo.DefaultSubtitleStreamIndex = null;
                _logger.LogDebug("        DefaultSubtitleStreamIndex: n/a");

                if (!originalRuntime.HasValue)
                {
                    mediaSourceInfo.RunTimeTicks = null;
                    _logger.LogDebug("        Original runtime:           n/a");
                }

                var audioStream = mediaSourceInfo.MediaStreams.FirstOrDefault(i => i.Type == MediaStreamType.Audio);
                if (audioStream == null || audioStream.Index == -1)
                {
                    mediaSourceInfo.DefaultAudioStreamIndex = null;
                    _logger.LogDebug("        DefaultAudioStreamIndex:    n/a");
                }
                else
                {
                    mediaSourceInfo.DefaultAudioStreamIndex = audioStream.Index;
                    _logger.LogDebug("        DefaultAudioStreamIndex:    '{DefaultAudioStreamIndex}'", info.DefaultAudioStreamIndex);
                }
            }
            else
            {
                _logger.LogError("Cannot probe {source} stream", source);
            }
        }

        private void LogMediaStreamList(IReadOnlyList<MediaStream> theList, string prefix)
        {
            foreach (MediaStream i in theList)
                LogMediaStream(i, prefix);
        }

        private void LogMediaStream(MediaStream ms, string prefix)
        {
            _logger.LogDebug("{Prefix}AspectRatio             {AspectRatio}", prefix, ms.AspectRatio);
            _logger.LogDebug("{Prefix}AverageFrameRate        {AverageFrameRate}", prefix, ms.AverageFrameRate);
            _logger.LogDebug("{Prefix}BitDepth                {BitDepth}", prefix, ms.BitDepth);
            _logger.LogDebug("{Prefix}BitRate                 {BitRate}", prefix, ms.BitRate);
            _logger.LogDebug("{Prefix}ChannelLayout           {ChannelLayout}", prefix, ms.ChannelLayout); // Object
            _logger.LogDebug("{Prefix}Channels                {Channels}", prefix, ms.Channels);
            _logger.LogDebug("{Prefix}Codec                   {Codec}", prefix, ms.Codec); // Object
            _logger.LogDebug("{Prefix}CodecTag                {CodecTag}", prefix, ms.CodecTag); // Object
            _logger.LogDebug("{Prefix}Comment                 {Comment}", prefix, ms.Comment);
            _logger.LogDebug("{Prefix}DeliveryMethod          {DeliveryMethod}", prefix, ms.DeliveryMethod); // Object
            //_logger.LogDebug("{Prefix}ExternalId              {ExternalId}", prefix, ms.ExternalId);
            _logger.LogDebug("{Prefix}Height                  {Height}", prefix, ms.Height);
            _logger.LogDebug("{Prefix}Index                   {Index}", prefix, ms.Index);
            _logger.LogDebug("{Prefix}IsAnamorphic            {IsAnamorphic}", prefix, ms.IsAnamorphic);
            _logger.LogDebug("{Prefix}IsDefault               {IsDefault}", prefix, ms.IsDefault);
            _logger.LogDebug("{Prefix}IsExternal              {IsExternal}", prefix, ms.IsExternal);
            _logger.LogDebug("{Prefix}IsExternalUrl           {IsExternalUrl}", prefix, ms.IsExternalUrl);
            _logger.LogDebug("{Prefix}IsForced                {IsForced}", prefix, ms.IsForced);
            _logger.LogDebug("{Prefix}IsInterlaced            {IsInterlaced}", prefix, ms.IsInterlaced);
            _logger.LogDebug("{Prefix}IsTextSubtitleStream    {IsTextSubtitleStream}", prefix, ms.IsTextSubtitleStream);
            _logger.LogDebug("{Prefix}Language                {Language}", prefix, ms.Language);
            _logger.LogDebug("{Prefix}Level                   {Level}", prefix, ms.Level);
            _logger.LogDebug("{Prefix}PacketLength            {PacketLength}", prefix, ms.PacketLength);
            _logger.LogDebug("{Prefix}PixelFormat             {PixelFormat}", prefix, ms.PixelFormat);
            _logger.LogDebug("{Prefix}Profile                 {Profile}", prefix, ms.Profile);
            _logger.LogDebug("{Prefix}RealFrameRate           {RealFrameRate}", prefix, ms.RealFrameRate);
            _logger.LogDebug("{Prefix}RefFrames               {RefFrames}", prefix, ms.RefFrames);
            _logger.LogDebug("{Prefix}SampleRate              {SampleRate}", prefix, ms.SampleRate);
            _logger.LogDebug("{Prefix}Score                   {Score}", prefix, ms.Score);
            _logger.LogDebug("{Prefix}SupportsExternalStream  {SupportsExternalStream}", prefix, ms.SupportsExternalStream);
            _logger.LogDebug("{Prefix}Type                    {Type}", prefix, ms.Type); // Object
            _logger.LogDebug("{Prefix}Width                   {Width}", prefix, ms.Width);
            _logger.LogDebug("{Prefix}========================", prefix);
        }

        public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            var source = await GetChannelStream(channelId, string.Empty, cancellationToken);
            return [source];
        }

        public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo program = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SeriesTimerInfo
            {
                PostPaddingSeconds = Plugin.Instance.Configuration.Post_Padding,
                PrePaddingSeconds = Plugin.Instance.Configuration.Pre_Padding,
                Priority = _htsConnectionHandler.GetPriority(),
                RecordAnyChannel = true,
                RecordAnyTime = true,
                RecordNewOnly = false
            });
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            int timeOut = await _htsConnectionHandler.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetProgramsAsync: call cancelled or timed out - returning empty list");
                return new List<ProgramInfo>();
            }

            GetEventsResponseHandler currGetEventsResponseHandler = new GetEventsResponseHandler(startDateUtc, endDateUtc, _logger);

            HTSMessage queryEvents = new HTSMessage();
            queryEvents.Method = "getEvents";
            queryEvents.putField("channelId", _htsConnectionHandler.ResolveChannelId(channelId));
            queryEvents.putField("maxTime", ((DateTimeOffset)endDateUtc).ToUnixTimeSeconds());
            int sequence = _htsConnectionHandler.SendMessage(queryEvents, currGetEventsResponseHandler);

            _logger.LogDebug("LiveTvService.GetProgramsAsync: ask TVH for events of channel '{chanid}'", channelId);

            IEnumerable<ProgramInfo> programs;
            try
            {
                programs = await currGetEventsResponseHandler.GetEvents(cancellationToken).WaitAsync(_timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("LiveTvService.GetProgramsAsync: timeout reached while calling for events of channel '{chanid}'", channelId);
                return [];
            }
            finally
            {
                _htsConnectionHandler.RemoveResponseHandler(sequence);
            }

            foreach (var program in programs)
            {
                program.ChannelId = channelId;
            }

            return programs;
        }

        public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        {
            int timeOut = await _htsConnectionHandler.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetSeriesTimersAsync: call cancelled ot timed out - returning empty list");
                return new List<SeriesTimerInfo>();
            }

            try
            {
                return await _htsConnectionHandler.BuildAutorecInfos(cancellationToken).WaitAsync(_timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return [];
            }
        }

        public async Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        {
            //  retrieve the 'Pending' recordings");

            int timeOut = await _htsConnectionHandler.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetTimersAsync: call cancelled or timed out - returning empty list");
                return new List<TimerInfo>();
            }

            try
            {
                return await _htsConnectionHandler.BuildPendingTimersInfos(cancellationToken).WaitAsync(_timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return [];
            }
        }
        public Task ResetTuner(string id, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            await SaveSeriesTimerAsync("updateAutorecEntry", info, cancellationToken).ConfigureAwait(false);
        }

        public async Task UpdateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        {
            int timeOut = await _htsConnectionHandler.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.UpdateTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage updateTimerMessage = new HTSMessage();
            updateTimerMessage.Method = "updateDvrEntry";
            updateTimerMessage.putField("id", _htsConnectionHandler.ResolveDvrId(info.Id));
            updateTimerMessage.putField("startExtra", (long)(info.PrePaddingSeconds / 60));
            updateTimerMessage.putField("stopExtra", (long)(info.PostPaddingSeconds / 60));

            HTSMessage updateTimerResponse;
            try
            {
                updateTimerResponse = await SendMessageAsync(updateTimerMessage, cancellationToken).ConfigureAwait(false);
                _lastRecordingChange = DateTime.UtcNow;
            }
            catch (TimeoutException)
            {
                _logger.LogError("LiveTvService.UpdateTimerAsync: can't update timer because the timeout was reached");
                return;
            }

            Boolean success = updateTimerResponse.getInt("success", 0) == 1;
            if (!success)
            {
                if (updateTimerResponse.containsField("error"))
                {
                    _logger.LogError("LiveTvService.UpdateTimerAsync: can't update timer: '{why}'", updateTimerResponse.getString("error"));
                }
                else if (updateTimerResponse.containsField("noaccess"))
                {
                    _logger.LogError("LiveTvService.UpdateTimerAsync: can't update timer: '{why}'", updateTimerResponse.getString("noaccess"));
                }
            }
        }

        /***********/
        /* Helpers */
        /***********/

        private async Task SaveSeriesTimerAsync(string method, SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            int timeOut = await _htsConnectionHandler.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1)
            {
                _logger.LogDebug("LiveTvService.{Method}: call timed out", method);
                return;
            }

            HTSMessage response;
            try
            {
                response = await SendMessageAsync(BuildAutorecMessage(method, info), cancellationToken).ConfigureAwait(false);
                _lastRecordingChange = DateTime.UtcNow;
            }
            catch (TimeoutException)
            {
                _logger.LogError("LiveTvService.{Method}: TVHeadend did not respond before the timeout", method);
                return;
            }

            if (response.containsField("error"))
            {
                _logger.LogError("LiveTvService.{Method}: TVHeadend rejected the series timer: '{Why}'", method, response.getString("error"));
            }
            else if (response.containsField("noaccess"))
            {
                _logger.LogError("LiveTvService.{Method}: TVHeadend denied access", method);
            }
            else if (method == "addAutorecEntry" && response.getInt("success", 0) != 1)
            {
                _logger.LogError("LiveTvService.{Method}: TVHeadend did not create the series timer", method);
            }
        }

        private HTSMessage BuildAutorecMessage(string method, SeriesTimerInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);

            var message = new HTSMessage { Method = method };
            if (method == "updateAutorecEntry")
            {
                if (string.IsNullOrWhiteSpace(info.Id))
                {
                    throw new ArgumentException("A TVHeadend autorecording ID is required for updates.", nameof(info));
                }

                message.putField("id", info.Id);
            }

            if (!string.IsNullOrWhiteSpace(info.Name))
            {
                string title = method == "updateAutorecEntry" ? _htsConnectionHandler.GetAutorecTitle(info.Id) : null;
                message.putField("title", string.IsNullOrWhiteSpace(title) ? info.Name : title);
                message.putField("name", info.Name);
            }

            message.putField("enabled", 1);
            message.putField("channelId", info.RecordAnyChannel || string.IsNullOrWhiteSpace(info.ChannelId) ? -1L : _htsConnectionHandler.ResolveChannelId(info.ChannelId));
            message.putField("daysOfWeek", info.Days == null ? 0 : AutorecDataHelper.getDaysOfWeekFromList(info.Days));
            message.putField("priority", info.Priority is >= 0 and <= 4 or 6 ? info.Priority : _htsConnectionHandler.GetPriority());
            message.putField("startExtra", (long)(info.PrePaddingSeconds / 60));
            message.putField("stopExtra", (long)(info.PostPaddingSeconds / 60));
            message.putField("broadcastType", info.RecordNewOnly ? 1 : 0);
            message.putField("configName", _htsConnectionHandler.GetProfile());

            if (info.RecordAnyTime)
            {
                message.putField("start", -1);
                message.putField("startWindow", -1);
            }
            else
            {
                int startUtcOffsetMinutes = _htsConnectionHandler.GetServerUtcOffsetMinutes(info.StartDate);
                int endUtcOffsetMinutes = _htsConnectionHandler.GetServerUtcOffsetMinutes(info.EndDate);
                message.putField("start", AutorecDataHelper.getMinutesFromMidnight(info.StartDate, startUtcOffsetMinutes));
                message.putField("startWindow", AutorecDataHelper.getMinutesFromMidnight(info.EndDate, endUtcOffsetMinutes));
            }

            return message;
        }

        private async Task<HTSMessage> SendMessageAsync(HTSMessage message, CancellationToken cancellationToken)
        {
            var responseHandler = new LoopBackResponseHandler();
            int sequence = _htsConnectionHandler.SendMessage(message, responseHandler);
            try
            {
                return await responseHandler.GetResponseAsync(cancellationToken, _timeout).ConfigureAwait(false);
            }
            finally
            {
                _htsConnectionHandler.RemoveResponseHandler(sequence);
            }
        }
    }

}
