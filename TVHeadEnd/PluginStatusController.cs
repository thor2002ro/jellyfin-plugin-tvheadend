using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
        public int? SnrRaw { get; set; }
        public double? SnrPercent { get; set; }
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
        public int ActiveProducerCount { get; set; }
        public IReadOnlyList<HtspProducerStatus> Producers { get; set; }
    }

    [ApiController]
    [Authorize]
    [Route("TVHeadEnd/Status")]
    public sealed class PluginStatusController : ControllerBase
    {
        [HttpGet]
        public ActionResult<PluginRuntimeStatus> GetStatus()
        {
            var configuration = Plugin.Instance?.Configuration;
            var producers = HtspLiveStream.GetActiveProducerStatuses();
            return Ok(new PluginRuntimeStatus
            {
                GeneratedUtc = DateTime.UtcNow,
                PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
                StreamingMethod = configuration?.StreamingMethod ?? string.Empty,
                Server = configuration == null ? string.Empty : configuration.TVH_ServerName + ":" + configuration.HTSP_Port,
                ActiveProducerCount = producers.Count,
                Producers = producers
            });
        }
    }
}
