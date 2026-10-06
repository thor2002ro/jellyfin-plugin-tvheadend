using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using TVHeadEnd;
using TVHeadEnd.HTSP;
using Xunit;

public class RecordingArtworkTests
{
    [Theory]
    [InlineData("generated")]
    [InlineData("broadcaster")]
    [InlineData("direct")]
    [InlineData("disabled")]
    [InlineData("wrong-event-time")]
    [InlineData("changed-server")]
    [InlineData("cancelled")]
    [InlineData("invalid")]
    [InlineData("missing")]
    [InlineData("slow")]
    [InlineData("changed-source")]
    public async Task RecordingArtworkPersistsAfterEpgExpiryAndKeepsBroadcasterPriority(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-recording-art-" + Guid.NewGuid().ToString("N"));
        var (plugin, validator) = PluginTests.ConfigureImageCache(root);
        plugin.Configuration.GenerateMissingProgrammeImages = scenario != "disabled";
        var factory = new HttpFactory();
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance, factory, validator);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(HTSConnectionHandler).GetField("_configured", flags)!.SetValue(handler, true);
        typeof(HTSConnectionHandler).GetField("_tvhServerName", flags)!.SetValue(handler, plugin.Configuration.TVH_ServerName);
        typeof(HTSConnectionHandler).GetField("_htspPort", flags)!.SetValue(handler, plugin.Configuration.HTSP_Port);
        typeof(HTSConnectionHandler).GetField("_userName", flags)!.SetValue(handler, plugin.Configuration.Username);
        typeof(HTSConnectionHandler).GetField("_httpBaseUrl", flags)!.SetValue(handler, "http://tvh:9981");
        var start = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var epg = new HTSMessage { Method = "eventAdd" };
        epg.putField("eventId", new BigInteger(123));
        epg.putField("channelId", new BigInteger(42));
        epg.putField("start", new BigInteger(start));
        epg.putField("stop", new BigInteger(start + 1800));
        if (scenario == "broadcaster") epg.putField("image", "https://broadcaster/preferred.jpg");
        if (scenario is "slow" or "changed-source") epg.putField("image", "http://tvh:9981/artwork/old.jpg");
        handler.onMessage(epg);
        factory.Response.Reply = async token =>
        {
            if (scenario == "slow") await Task.Delay(Timeout.Infinite, token);
            if (scenario == "changed-source")
            {
                var update = new HTSMessage { Method = "eventUpdate" };
                update.putField("eventId", new BigInteger(123));
                update.putField("image", "https://broadcaster/new.jpg");
                handler.onMessage(update);
                await handler.BuildDvrInfos(CancellationToken.None);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[] { 0xff, 0xd8, 0xff, 0xd9 }) };
        };
        var dvr = new HTSMessage { Method = "dvrEntryAdd" };
        dvr.putField("id", new BigInteger(7));
        dvr.putField("channel", new BigInteger(42));
        dvr.putField("eventId", new BigInteger(123));
        dvr.putField("start", new BigInteger(scenario == "wrong-event-time" ? start - 86400 : start - 120));
        dvr.putField("stop", new BigInteger(scenario == "wrong-event-time" ? start - 84600 : start + 1920));
        dvr.putField("state", "completed");
        if (scenario == "direct") dvr.putField("image", "https://broadcaster/direct.jpg");
        if (scenario == "invalid") dvr.putField("image", "file:///private/artwork.jpg");
        dvr.putField("title", "Programme");
        handler.onMessage(dvr);
        if (scenario == "slow")
        {
            for (var id = 100; id < 130; id++)
            {
                var extra = new HTSMessage();
                foreach (var field in dvr) extra.putField(field.Key, field.Value);
                extra.putField("id", new BigInteger(id));
                if (id >= 105) extra.putField("image", "file:///private/artwork.jpg");
                handler.onMessage(extra);
            }
        }
        var generated = (string)typeof(ProgrammeImageService).GetMethod("GetImagePath", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { 42L, "123", DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime })!;
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        if (scenario != "missing") await File.WriteAllBytesAsync(generated, new byte[] { 0xff, 0xd8, 0xff, 0xd9 });
        if (scenario == "changed-server") plugin.Configuration.TVH_ServerName = "other-server";
        using var cancellation = new CancellationTokenSource();
        try
        {
            if (scenario == "cancelled")
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.BuildDvrInfos(cancellation.Token));
                return;
            }
            var recordings = (await handler.BuildDvrInfos(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12))).ToArray();
            Assert.Equal(scenario == "slow" ? 31 : 1, recordings.Length);
            if (scenario == "slow")
            {
                Assert.InRange(factory.Response.RequestCount, 1, 4);
                Assert.All(recordings, recording => { Assert.Null(recording.ImageUrl); Assert.False(recording.HasImage); });
            }
            var item = Assert.Single(recordings, recording => recording.Id == "7");
            var hasImage = scenario is "generated" or "broadcaster" or "direct" or "invalid";
            Assert.Equal(hasImage, item.HasImage);
            if (scenario == "broadcaster") Assert.Equal("https://broadcaster/preferred.jpg", item.ImageUrl);
            if (scenario == "direct") Assert.Equal("https://broadcaster/direct.jpg", item.ImageUrl);
            if (scenario == "generated")
            {
                Assert.NotEqual(generated, item.ImageUrl);
                Assert.True(File.Exists(item.ImageUrl));
            }
            var delete = new HTSMessage { Method = "eventDelete" };
            delete.putField("eventId", new BigInteger(123));
            handler.onMessage(delete);
            File.Delete(generated);
            var expired = Assert.Single(await handler.BuildDvrInfos(CancellationToken.None), recording => recording.Id == "7");
            if (scenario == "changed-source")
            {
                Assert.Equal("https://broadcaster/new.jpg", expired.ImageUrl);
                Assert.Equal("https://broadcaster/new.jpg", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(plugin.ImageCachePath, "*.recording"))));
            }
            else
            {
                Assert.Equal(item.ImageUrl, expired.ImageUrl);
                Assert.Equal(item.HasImage, expired.HasImage);
            }
            var host = PluginTests.CreateProxy<IServerApplicationHost>((method, _) => method.ReturnType == typeof(string) ? "http://jellyfin" : null);
            var liveTv = new LiveTvService(NullLoggerFactory.Instance, null, handler, host, null, null);
            var channel = new RecordingsChannel(NullLoggerFactory.Instance, handler, liveTv);
            var mapped = (ChannelItemInfo)typeof(RecordingsChannel).GetMethod("ConvertToChannelItem", flags)!.Invoke(channel, new object[] { expired })!;
            Assert.Equal(expired.ImageUrl, mapped.ImageUrl);
            if (hasImage) Assert.False(string.IsNullOrEmpty(expired.ImageUrl));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class HttpFactory : IHttpClientFactory
    {
        public ResponseHandler Response { get; } = new();
        public HttpClient CreateClient(string name) => new(Response, false);
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);
        public Func<CancellationToken, Task<HttpResponseMessage>> Reply { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Reply(cancellationToken);
        }
    }
}
