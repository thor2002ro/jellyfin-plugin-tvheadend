using System;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.Configuration;
using TVHeadEnd.HTSP;
using Xunit;

public class ProgrammeImageTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static string ConfigureIdentity(HTSConnectionHandler handler, PluginConfiguration config)
    {
        typeof(HTSConnectionHandler).GetField("_configured", Private)!.SetValue(handler, true);
        typeof(HTSConnectionHandler).GetField("_tvhServerName", Private)!.SetValue(handler, config.TVH_ServerName);
        typeof(HTSConnectionHandler).GetField("_htspPort", Private)!.SetValue(handler, config.HTSP_Port);
        typeof(HTSConnectionHandler).GetField("_userName", Private)!.SetValue(handler, config.Username);
        return (string)typeof(ProgrammeImageService).GetProperty("CurrentConnectionIdentity", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    }

    private static string ImagePath(long channel, string id, DateTime start) =>
        (string)typeof(ProgrammeImageService).GetMethod("GetImagePath", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { channel, id, start })!;

    [Fact]
    public void CheckboxDefaultsOffAndCacheIdentitySeparatesServerChannelAndProgrammeStart()
    {
        Assert.False(new PluginConfiguration().GenerateMissingProgrammeImages);
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-frames-key-" + Guid.NewGuid().ToString("N"));
        var (plugin, _) = PluginTests.ConfigureImageCache(root);
        try
        {
            var time = DateTime.UtcNow;
            var path = ImagePath(42, "123", time);
            Assert.Equal(path, ImagePath(42, "123", time));
            Assert.NotEqual(path, ImagePath(43, "123", time));
            Assert.NotEqual(path, ImagePath(42, "123", time.AddHours(1)));
            plugin.Configuration.TVH_ServerName = "another-server";
            Assert.NotEqual(path, ImagePath(42, "123", time));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("cached", true)]
    [InlineData("off", false)]
    [InlineData("broadcaster", false)]
    [InlineData("uncached", false)]
    [InlineData("changed-server", false)]
    public async Task GuideImportUsesGeneratedImagesOnlyWhenEnabledAndBroadcasterArtworkIsMissing(string scenario, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-frames-guide-" + Guid.NewGuid().ToString("N"));
        var (plugin, validator) = PluginTests.ConfigureImageCache(root);
        plugin.Configuration.GenerateMissingProgrammeImages = scenario != "off";
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance, new HttpFactory(), validator);
        ConfigureIdentity(handler, plugin.Configuration);
        var connection = new HTSConnectionAsync(handler, "test", "1", NullLoggerFactory.Instance);
        typeof(HTSConnectionHandler).GetField("_configured", Private)!.SetValue(handler, true);
        typeof(HTSConnectionHandler).GetField("_connected", Private)!.SetValue(handler, true);
        typeof(HTSConnectionHandler).GetField("_httpBaseUrl", Private)!.SetValue(handler, "http://tvh:9981");
        typeof(HTSConnectionHandler).GetField("_htsConnection", Private)!.SetValue(handler, connection);
        var start = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();
        var epg = new HTSMessage { Method = "eventAdd" };
        epg.putField("channelId", new BigInteger(42));
        epg.putField("eventId", new BigInteger(123));
        epg.putField("start", new BigInteger(start));
        epg.putField("stop", new BigInteger(start + 3600));
        if (scenario == "broadcaster") epg.putField("image", "https://broadcaster/preferred.jpg");
        handler.onMessage(epg);
        handler.onMessage(new HTSMessage { Method = "initialSyncCompleted" });
        var path = ImagePath(42, "123", DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (scenario != "uncached") await File.WriteAllBytesAsync(path, new byte[] { 0xff, 0xd8, 0xff, 0xd9 });
        if (scenario == "changed-server") plugin.Configuration.TVH_ServerName = "other-server";
        try
        {
            var service = new LiveTvService(NullLoggerFactory.Instance, null, handler, null, null, null);
            var programme = Assert.Single(await service.GetProgramsAsync("42", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1), CancellationToken.None));
            Assert.Equal(expected ? path : null, programme.ImagePath);
            if (scenario == "broadcaster") Assert.Equal("https://broadcaster/preferred.jpg", programme.ImageUrl);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("capture", 1)]
    [InlineData("off", 0)]
    [InlineData("unwatched", 0)]
    [InlineData("radio", 0)]
    [InlineData("old-keyframe", 0)]
    [InlineData("broadcaster", 0)]
    [InlineData("existing-image", 0)]
    [InlineData("invalid-output", 1)]
    [InlineData("ended-during-capture", 1)]
    [InlineData("broadcaster-during-capture", 1)]
    [InlineData("publish-failure", 1)]
    [InlineData("foreign-channel", 0)]
    [InlineData("foreign-programme", 0)]
    [InlineData("cancelled", 1)]
    [InlineData("disabled-during-capture", 1)]
    [InlineData("foreign-server", 0)]
    [InlineData("changed-server", 0)]
    [InlineData("changed-server-during-capture", 1)]
    [InlineData("late-cancellation", 1)]
    public async Task CaptureUsesOnlyWatchedBuffersAndKeepsExistingArtwork(string scenario, int expectedExtractions)
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-frames-" + Guid.NewGuid().ToString("N"));
        var (plugin, normalValidator) = PluginTests.ConfigureImageCache(root);
        plugin.Configuration.GenerateMissingProgrammeImages = scenario != "off";
        plugin.Configuration.HTSPKeyframeStartupEnabled = false;
        var validator = scenario == "invalid-output"
            ? PluginTests.CreateProxy<IImageEncoder>((_, _) => new ImageDimensions(0, 0)) : normalValidator;
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance, new HttpFactory(), validator);
        var identity = ConfigureIdentity(handler, plugin.Configuration);
        var startUnix = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();
        var start = DateTimeOffset.FromUnixTimeSeconds(startUnix).UtcDateTime;
        var epg = new HTSMessage { Method = "eventAdd" };
        epg.putField("eventId", new BigInteger(123));
        epg.putField("channelId", new BigInteger(42));
        epg.putField("start", new BigInteger(startUnix));
        epg.putField("stop", new BigInteger(startUnix + 3600));
        if (scenario == "broadcaster") epg.putField("image", "https://broadcaster/artwork.jpg");
        handler.onMessage(epg);
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "42", ServiceName = "TVHclient LiveTvService" };
        var programme = new LiveTvProgram { Id = Guid.NewGuid(), ExternalId = "123", StartDate = start };
        if (scenario == "foreign-channel") channel.ServiceName = "Other service";
        if (scenario == "foreign-programme") programme.StartDate = start.AddHours(1);
        if (scenario == "existing-image") programme.SetImagePath(ImageType.Primary, "https://existing/artwork.jpg");
        var updates = 0;
        var library = PluginTests.CreateProxy<ILibraryManager>((method, arguments) =>
        {
            if (method.Name == nameof(ILibraryManager.GetItemList))
            {
                var query = (InternalItemsQuery)arguments[0];
                if (query.IncludeItemTypes.Contains(BaseItemKind.LiveTvChannel))
                {
                    Assert.Equal("42", query.ExternalId);
                    return new BaseItem[] { channel };
                }
                Assert.Equal(channel.Id, Assert.Single(query.ChannelIds));
                Assert.Equal("123", query.ExternalId);
                return new BaseItem[] { programme };
            }
            if (method.Name == nameof(ILibraryManager.UpdateItemAsync))
            {
                updates++;
                Assert.Same(programme, arguments[0]);
                if (scenario == "publish-failure") return Task.FromException(new IOException("Database unavailable"));
                return Task.CompletedTask;
            }
            return null;
        });
        var extracts = 0;
        using var callerCancellation = new CancellationTokenSource();
        var lateExtraction = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string samplePath = null, extractedPath = null;
        var encoder = PluginTests.CreateProxy<IMediaEncoder>((method, arguments) =>
        {
            Assert.Equal(nameof(IMediaEncoder.ExtractVideoImage), method.Name);
            extracts++;
            samplePath = (string)arguments[0];
            var bytes = File.ReadAllBytes(samplePath);
            Assert.InRange(bytes.Length, 188, 188 * 8000);
            Assert.Equal((byte)0x47, bytes[0]);
            Assert.Equal((byte)2, bytes[1]);
            if (scenario == "late-cancellation") return lateExtraction.Task;
            if (scenario == "cancelled")
            {
                callerCancellation.Cancel();
                return Task.FromCanceled<string>(callerCancellation.Token);
            }
            extractedPath = Path.Combine(root, "extracted.jpg");
            File.WriteAllBytes(extractedPath, new byte[] { 0xff, 0xd8, 0xff, 0xd9 });
            if (scenario == "disabled-during-capture") plugin.Configuration.GenerateMissingProgrammeImages = false;
            if (scenario == "changed-server-during-capture") plugin.Configuration.TVH_ServerName = "other-server";
            if (scenario == "ended-during-capture")
            {
                var delete = new HTSMessage { Method = "eventDelete" };
                delete.putField("eventId", new BigInteger(123));
                handler.onMessage(delete);
            }
            if (scenario == "broadcaster-during-capture")
            {
                var update = new HTSMessage { Method = "eventUpdate" };
                update.putField("eventId", new BigInteger(123));
                update.putField("image", "https://broadcaster/preferred.jpg");
                handler.onMessage(update);
            }
            return Task.FromResult(extractedPath);
        });
        var source = new MediaSourceInfo { MediaStreams = scenario == "radio" ? []
            : [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Index = 0 }] };
        using var stream = new HtspLiveStream(source, "42", NullLoggerFactory.Instance, null, null);
        typeof(HtspLiveStream).GetField("_serverIdentity", Private)!.SetValue(stream, scenario == "foreign-server" ? "other-server" : identity);
        if (scenario == "changed-server") plugin.Configuration.TVH_ServerName = "other-server";
        using var reader = stream.GetStream();
        var producers = (ConcurrentDictionary<string, HtspLiveStream>)typeof(HtspLiveStream)
            .GetField("RunningChannelsByUniqueId", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        producers[stream.UniqueId] = stream;
        var addChunk = typeof(HtspLiveStream).GetMethod("AddStartupCacheChunkLocked", Private)!;
        byte[] Chunk(byte marker) { var bytes = new byte[188]; bytes[0] = 0x47; bytes[1] = marker; return bytes; }
        addChunk.Invoke(stream, new object[] { Chunk(1), false, false, false });
        addChunk.Invoke(stream, new object[] { Chunk(2), true, false, false });
        if (scenario == "unwatched") typeof(HtspLiveStream).GetField("_activeStreamReaders", Private)!.SetValue(stream, 0);
        if (scenario == "old-keyframe") typeof(HtspLiveStream).GetField("_lastKeyframeUtcTicks", Private)!.SetValue(stream, start.AddSeconds(-1).Ticks);
        var liveTv = new LiveTvService(NullLoggerFactory.Instance, null, handler, null, null, library);
        using var worker = new ProgrammeImageService(handler, liveTv, encoder, library, NullLogger<ProgrammeImageService>.Instance);
        try
        {
            var capture = typeof(ProgrammeImageService).GetMethod("CaptureMissingImagesAsync", Private)!;
            var operation = (Task)capture.Invoke(worker, new object[] { callerCancellation.Token })!;
            if (scenario == "late-cancellation")
            {
                await callerCancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.True(File.Exists(samplePath));
                await (Task)capture.Invoke(worker, new object[] { CancellationToken.None })!;
                Assert.Equal(1, extracts);
                extractedPath = Path.Combine(root, "late.jpg");
                await File.WriteAllBytesAsync(extractedPath, new byte[] { 0xff, 0xd8, 0xff, 0xd9 });
                lateExtraction.SetResult(extractedPath);
                for (var attempt = 0; attempt < 100 && (File.Exists(samplePath) || File.Exists(extractedPath)); attempt++)
                    await Task.Delay(10);
            }
            else if (scenario == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            else
            {
                await operation;
                await (Task)capture.Invoke(worker, new object[] { callerCancellation.Token })!;
            }
            var success = scenario == "capture";
            Assert.Equal(scenario == "invalid-output" ? 2 : expectedExtractions, extracts);
            Assert.Equal(success ? 1 : scenario == "publish-failure" ? 2 : 0, updates);
            Assert.Equal(success || scenario == "existing-image", programme.HasImage(ImageType.Primary));
            if (success) Assert.True(File.Exists(programme.GetImageInfo(ImageType.Primary, 0).Path));
            Assert.False(File.Exists(samplePath));
            Assert.False(File.Exists(extractedPath));
            Assert.Equal(scenario == "unwatched" ? 0 : 1, typeof(HtspLiveStream).GetField("_activeStreamReaders", Private)!.GetValue(stream));
        }
        finally
        {
            producers.TryRemove(stream.UniqueId, out _);
            Directory.Delete(root, true);
        }
    }

    private sealed class HttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
