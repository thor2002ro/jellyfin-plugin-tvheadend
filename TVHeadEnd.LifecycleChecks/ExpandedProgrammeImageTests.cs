using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.Configuration;
using TVHeadEnd.HTSP;
using Xunit;

public class ExpandedProgrammeImageTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, true, false)]
    public async Task BackgroundCaptureNeedsOptInUsesWeightOneAndClosesImmediately(bool enabled, bool background, bool radio, bool expected)
    {
        Assert.False(new PluginConfiguration().CaptureUnwatchedProgrammeImages);
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-background-frame-" + Guid.NewGuid().ToString("N"));
        var (plugin, validator) = PluginTests.ConfigureImageCache(root);
        await using var server = new ChannelSwitchingTests.Server(emitFrame: true);
        plugin.Configuration.GenerateMissingProgrammeImages = enabled;
        plugin.Configuration.CaptureUnwatchedProgrammeImages = background;
        plugin.Configuration.TVH_ServerName = "127.0.0.1";
        plugin.Configuration.HTSP_Port = server.Port;
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance, new HttpFactory(), validator);
        typeof(HTSConnectionHandler).GetField("_configured", Private)!.SetValue(handler, true);
        typeof(HTSConnectionHandler).GetField("_tvhServerName", Private)!.SetValue(handler, "127.0.0.1");
        typeof(HTSConnectionHandler).GetField("_htspPort", Private)!.SetValue(handler, server.Port);
        typeof(HTSConnectionHandler).GetField("_userName", Private)!.SetValue(handler, plugin.Configuration.Username);
        var start = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();
        var epg = new HTSMessage { Method = "eventAdd" };
        epg.putField("eventId", new BigInteger(123));
        epg.putField("channelId", new BigInteger(42));
        epg.putField("start", new BigInteger(start));
        epg.putField("stop", new BigInteger(start + 3600));
        handler.onMessage(epg);
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "42", ServiceName = "TVHclient LiveTvService", ChannelType = radio ? ChannelType.Radio : ChannelType.TV };
        var programme = new LiveTvProgram { Id = Guid.NewGuid(), StartDate = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime };
        var updates = 0;
        var library = PluginTests.CreateProxy<ILibraryManager>((method, arguments) =>
        {
            if (method.Name == nameof(ILibraryManager.GetItemList))
                return ((InternalItemsQuery)arguments[0]).IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.LiveTvChannel)
                    ? new BaseItem[] { channel } : new BaseItem[] { programme };
            if (method.Name == nameof(ILibraryManager.UpdateItemAsync)) { updates++; return Task.CompletedTask; }
            return null;
        });
        var extractions = 0;
        var encoder = PluginTests.CreateProxy<IMediaEncoder>((method, arguments) =>
        {
            Assert.Equal(nameof(IMediaEncoder.ExtractVideoImage), method.Name);
            Assert.Empty((long[])typeof(HtspLiveStream).GetMethod("GetWatchedChannelIds", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!);
            var identity = (string)typeof(ProgrammeImageService).GetProperty("CurrentConnectionIdentity", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.Equal("uuid:loopback-mux", typeof(HtspLiveStream).GetMethod("GetKnownMux", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { identity, 42L }));
            extractions++;
            Assert.Equal((byte)0x47, File.ReadAllBytes((string)arguments[0])[0]);
            var path = Path.Combine(root, "extracted.jpg");
            File.WriteAllBytes(path, new byte[] { 0xff, 0xd8, 0xff, 0xd9 });
            return Task.FromResult(path);
        });
        var liveTv = new LiveTvService(NullLoggerFactory.Instance, null, handler, null, null, library);
        using var worker = new ProgrammeImageService(handler, liveTv, encoder, library, NullLogger<ProgrammeImageService>.Instance);
        try
        {
            var capture = typeof(ProgrammeImageService).GetMethod("CaptureMissingImagesAsync", Private)!;
            await ((Task)capture.Invoke(worker, new object[] { CancellationToken.None })!).WaitAsync(TimeSpan.FromSeconds(5));
            if (expected) File.SetLastWriteTimeUtc(programme.GetImageInfo(ImageType.Primary, 0).Path, DateTime.UtcNow.AddMinutes(-6));
            await (Task)capture.Invoke(worker, new object[] { CancellationToken.None })!;
            Assert.Equal(expected ? 1 : 0, extractions);
            Assert.Equal(expected ? 1 : 0, updates);
            Assert.Equal(expected, programme.HasImage(ImageType.Primary));
            if (expected)
            {
                Assert.Equal(1, Assert.Single(server.Requests, item => item.Method == "subscribe").getInt("weight"));
                await server.WaitFor("unsubscribe");
                Assert.Empty((long[])typeof(HtspLiveStream).GetMethod("GetWatchedChannelIds", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!);
            }
            else Assert.Empty(server.Requests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LogoOverlayFallsBackAndCanUseTheRealConfiguredFfmpeg()
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-frame-logo-" + Guid.NewGuid().ToString("N"));
        var (plugin, _) = PluginTests.ConfigureImageCache(root);
        var validator = PluginTests.CreateProxy<IImageEncoder>((_, _) => new ImageDimensions(400, 200));
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance, new HttpFactory(), validator);
        var executable = Environment.GetEnvironmentVariable("TVHEADEND_TEST_FFMPEG");
        var configured = "missing-ffmpeg";
        var encoder = PluginTests.CreateProxy<IMediaEncoder>((method, _) => method.Name == "get_EncoderPath" ? configured : null);
        var liveTv = new LiveTvService(NullLoggerFactory.Instance, null, handler, null, null, null);
        using var worker = new ProgrammeImageService(handler, liveTv, encoder, null, NullLogger<ProgrammeImageService>.Instance);
        var stamp = typeof(ProgrammeImageService).GetMethod("StampLogoAsync", Private)!;
        var frame = Path.Combine(root, "frame.png");
        var logo = Path.Combine(root, "logo.png");
        var output = Path.Combine(root, "stamped.jpg");
        try
        {
            Assert.False(await (Task<bool>)stamp.Invoke(worker, new object[] { frame, logo, output, CancellationToken.None })!);
            if (string.IsNullOrEmpty(executable)) return;
            configured = executable;
            async Task Run(params string[] args)
            {
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var arg in args) info.ArgumentList.Add(arg);
                using var process = Process.Start(info)!;
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(process.ExitCode == 0, await error);
            }
            await Run("-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=400x200", "-frames:v", "1", frame);
            await Run("-v", "error", "-f", "lavfi", "-i", "color=c=red:s=100x50", "-frames:v", "1", logo);
            Assert.True(await (Task<bool>)stamp.Invoke(worker, new object[] { frame, logo, output, CancellationToken.None })!);
            var pixels = Path.Combine(root, "pixels.rgb");
            await Run("-v", "error", "-i", output, "-f", "rawvideo", "-pix_fmt", "rgb24", pixels);
            var bytes = await File.ReadAllBytesAsync(pixels);
            var red = (180 * 400 + 370) * 3;
            Assert.True(bytes[red] > 180 && bytes[red + 2] < 80, "Bottom-right logo must be red");
            Assert.True(bytes[2] > 180 && bytes[0] < 80, "Frame outside the logo must stay blue");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class HttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
