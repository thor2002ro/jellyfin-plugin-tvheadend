using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;

namespace TVHeadEnd
{
    public sealed class HtspStreamStatus
    {
        public int Index { get; set; }
        public int Pid { get; set; }
        public string Codec { get; set; }
        public string Language { get; set; }
        public string Title { get; set; }
        public long Packets { get; set; }
        public long Bytes { get; set; }
        public long RandomAccessFrames { get; set; }
        public long TimestampCorrections { get; set; }
        public long TimestampDiscontinuities { get; set; }
        public long TimestampAnomalyDrops { get; set; }
        public long AudInsertions { get; set; }
    }

    public sealed class HtspProducerStatus
    {
        public string ChannelId { get; set; }
        public string PlaybackId { get; set; }
        public int SubscriptionId { get; set; }
        public string State { get; set; }
        public DateTime? OpenedUtc { get; set; }
        public string Adapter { get; set; }
        public string Service { get; set; }
        public string Network { get; set; }
        public string Mux { get; set; }
        public string Provider { get; set; }
        public int SharedPlaybackCount { get; set; }
        public int ActiveReaderCount { get; set; }
        public string SignalStatus { get; set; }
        public bool HasLock { get; set; }
        public int? SignalRaw { get; set; }
        public double? SignalPercent { get; set; }
        public double? SignalDbm { get; set; }
        public int? SnrRaw { get; set; }
        public double? SnrPercent { get; set; }
        public double? SnrDb { get; set; }
        public long? Ber { get; set; }
        public long? Unc { get; set; }
        public long? SignalAgeMs { get; set; }
        public long QueuePackets { get; set; }
        public long QueueBytes { get; set; }
        public long QueueDelayUs { get; set; }
        public long QueueIDrops { get; set; }
        public long QueuePDrops { get; set; }
        public long QueueBDrops { get; set; }
        public long? LastMuxPacketAgeMs { get; set; }
        public int ReconnectAttempts { get; set; }
        public int SignalRecoveryAttempts { get; set; }
        public bool AwaitingCleanVideo { get; set; }
        public long VideoDamageEvents { get; set; }
        public long DamagedVideoDrops { get; set; }
        public long? VideoDamageAgeMs { get; set; }
        public string LastVideoDamageReason { get; set; }
        public bool KeyframeStartupReady { get; set; }
        public long StartupCacheBytes { get; set; }
        public IReadOnlyList<HtspStreamStatus> Streams { get; set; }
    }

    public sealed class PluginRuntimeStatus
    {
        public DateTime GeneratedUtc { get; set; }
        public string PluginVersion { get; set; }
        public string StreamingMethod { get; set; }
        public string Server { get; set; }
        public bool Connected { get; set; }
        public string ServerVersion { get; set; }
        public int? HtspProtocolVersion { get; set; }
        public int ActiveProducerCount { get; set; }
        public IReadOnlyList<HtspProducerStatus> Producers { get; set; }
    }

    [ApiController]
    [Authorize(Policy = "RequiresElevation")]
    [Route("TVHeadEnd/Status")]
    public sealed class PluginStatusController : ControllerBase
    {
        private readonly HTSConnectionHandler _connectionHandler;

        public PluginStatusController(HTSConnectionHandler connectionHandler)
        {
            _connectionHandler = connectionHandler;
        }

        [HttpGet]
        public ActionResult<PluginRuntimeStatus> GetStatus()
        {
            var configuration = Plugin.Instance?.Configuration;
            var producers = HtspLiveStream.GetActiveProducerStatuses();
            var connection = _connectionHandler.GetConnectionStatus();
            return Ok(new PluginRuntimeStatus
            {
                GeneratedUtc = DateTime.UtcNow,
                PluginVersion = typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(Plugin).Assembly.GetName().Version?.ToString()
                    ?? "unknown",
                StreamingMethod = configuration?.StreamingMethod ?? string.Empty,
                Server = configuration == null ? string.Empty : configuration.TVH_ServerName + ":" + configuration.HTSP_Port,
                Connected = connection.Connected,
                ServerVersion = connection.ServerVersion,
                HtspProtocolVersion = connection.ProtocolVersion,
                ActiveProducerCount = producers.Count,
                Producers = producers
            });
        }
    }

    [ApiController]
    [Route("TVHeadEnd/Recordings")]
    public sealed class PluginRecordingStreamController : ControllerBase
    {
        private readonly LiveTvService _liveTvService;

        public PluginRecordingStreamController(LiveTvService liveTvService)
        {
            _liveTvService = liveTvService;
        }

        [HttpGet("{recordingId}/{token}/Stream")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> GetStream(string recordingId, string token, CancellationToken cancellationToken)
        {
            // Jellyfin fetches remote channel media server-side without forwarding the user's authorization header.
            if (!_liveTvService.IsRecordingStreamTokenValid(recordingId, token))
            {
                return NotFound();
            }

            var streamUrl = await _liveTvService.GetRecordingStreamUrl(recordingId, cancellationToken).ConfigureAwait(false);
            return Redirect(streamUrl);
        }
    }

    [ApiController]
    [Authorize(Policy = "RequiresElevation")]
    [Route("TVHeadEnd/Profiles")]
    public sealed class PluginProfilesController : ControllerBase
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private readonly HTSConnectionHandler _connectionHandler;

        public PluginProfilesController(HTSConnectionHandler connectionHandler)
        {
            _connectionHandler = connectionHandler;
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyList<RecordingProfileInfo>>> GetProfiles(CancellationToken cancellationToken)
        {
            if (_connectionHandler.GetServerProtocolVersion() < 16)
            {
                return Ok(Array.Empty<RecordingProfileInfo>());
            }

            var request = new HTSMessage { Method = "getDvrConfigs" };
            var handler = new LoopBackResponseHandler();
            var sequence = _connectionHandler.SendMessage(request, handler);
            HTSMessage response;
            try
            {
                response = await handler.GetResponseAsync(cancellationToken, RequestTimeout).ConfigureAwait(false);
            }
            finally
            {
                _connectionHandler.RemoveResponseHandler(sequence);
            }

            if (response == null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, "TVHeadend returned no DVR configuration response.");
            }

            if (response.getInt("noaccess", 0) != 0)
            {
                return StatusCode(
                    StatusCodes.Status403Forbidden,
                    "TVHeadend denied access to DVR configurations. Enable Basic recorder access and allow the required DVR configuration for this user.");
            }

            if (!response.containsField("dvrconfigs"))
            {
                return Ok(Array.Empty<RecordingProfileInfo>());
            }

            return Ok(response.getList("dvrconfigs")
                .OfType<HTSMessage>()
                .Select(config => new RecordingProfileInfo
                {
                    Id = config.getString("uuid", string.Empty),
                    Name = config.getString("name", string.Empty)
                })
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Name))
                .GroupBy(profile => profile.Id ?? profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }
    }

    public sealed class RecordingProfileInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }
}
