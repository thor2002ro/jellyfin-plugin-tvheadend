using System;
using System.IO;
using System.Reflection;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Model.Dto;
using TVHeadEnd;
using TVHeadEnd.HTSP;
using Xunit;

public class MuxOrderingTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    private static HTSMessage Source(string network, string mux, string uuid = null, string satellite = null)
    {
        var source = new HTSMessage();
        source.putField("network", network);
        source.putField("mux", mux);
        source.putField("mux_uuid", uuid);
        source.putField("satpos", satellite);
        return source;
    }

    private static string Key(HTSMessage source) => (string)typeof(HtspLiveStream).GetMethod("GetMuxIdentity", Static)!.Invoke(null, new object[] { source });

    [Fact]
    public void MuxIdentityPrefersUuidAndScopesNamesToNetworkAndSatellite()
    {
        Assert.Equal(Key(Source("A", "name", "uuid")), Key(Source("B", "renamed", "uuid")));
        Assert.NotEqual(Key(Source("A", "name", "uuid")), Key(Source("A", "name", "other")));
        Assert.NotEqual(Key(Source("A", "12130H")), Key(Source("B", "12130H")));
        Assert.NotEqual(Key(Source("A", "12130H", satellite: "30W")), Key(Source("A", "12130H", satellite: "19E")));
        Assert.Null(Key(null));
        Assert.Null(Key(Source(null, "12130H")));
        Assert.Null(Key(Source("A", null)));
    }

    [Fact]
    public void KnownPipeNetworkUsesTransponderButDoesNotGuessForOtherNetworks()
    {
        var first = Key(Source("abertpy: Abertis", "abertpy: MUX 11347H pPID 2308", "one"));
        Assert.Equal(first, Key(Source("abertpy: Abertis", "abertpy: pPID 2309 MUX 11347h", "two")));
        Assert.NotEqual(first, Key(Source("abertpy: Abertis", "MUX 12476V pPID 2309", "three")));
        Assert.NotEqual(first, Key(Source("abertpy: Other", "MUX 11347H", "four")));
        Assert.NotEqual(Key(Source("IPTV", "MUX 11347H pPID 2308", "one")), Key(Source("IPTV", "MUX 11347H pPID 2309", "two")));
        Assert.Equal(Key(Source("abertpy", "pPID 2308", "one")), Key(Source("Other", null, "one")));
        Assert.Equal(Key(Source("abertpy", "MUX 1234567H", "one")), Key(Source("Other", null, "one")));
    }

    [Fact]
    public void FiniteCapturePassGroupsMuxesWithoutStarvingOtherChannelsAndResetsForServerChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-mux-order-" + Guid.NewGuid().ToString("N"));
        var (plugin, _) = PluginTests.ConfigureImageCache(root);
        var identity = (string)typeof(ProgrammeImageService).GetProperty("CurrentConnectionIdentity", Static)!.GetValue(null)!;
        void Learn(long channel, string mux)
        {
            using var stream = new HtspLiveStream(new MediaSourceInfo(), channel.ToString(CultureInfo.InvariantCulture), NullLoggerFactory.Instance, null, null);
            typeof(HtspLiveStream).GetField("_serverIdentity", Instance)!.SetValue(stream, identity);
            var message = new HTSMessage { Method = "subscriptionStart" };
            message.putField("sourceinfo", Source("A", mux, mux));
            typeof(HtspLiveStream).GetMethod("LogSourceInfo", Instance)!.Invoke(stream, new object[] { message });
        }
        using var worker = new ProgrammeImageService(null, null, null, null, NullLogger<ProgrammeImageService>.Instance);
        var next = typeof(ProgrammeImageService).GetMethod("GetNextBackgroundChannel", Instance)!;
        long? Pick(long[] channels, string server) => (long?)next.Invoke(worker, new object[] { channels, server });
        try
        {
            Learn(1, "B"); Learn(2, "A"); Learn(3, "B");
            Assert.Equal(2L, Pick([1, 2, 3, 4], identity));
            Assert.Equal(1L, Pick([1, 2, 3, 4], identity));
            Assert.Equal(3L, Pick([1, 2, 3, 4], identity));
            Assert.Equal(4L, Pick([1, 2, 3, 4], identity));
            typeof(ProgrammeImageService).GetField("_lastBackgroundMux", Instance)!.SetValue(worker, Key(Source("A", "B", "B")));
            Assert.Equal(1L, Pick([1, 2, 3, 4], identity));
            Assert.Equal(4L, Pick([4], identity));
            Assert.Null(typeof(HtspLiveStream).GetMethod("GetKnownMux", Static)!.Invoke(null, new object[] { identity, 1L }));
            Assert.Equal(5L, Pick([5], identity));
            plugin.Configuration.TVH_ServerName = "another-server";
            var other = (string)typeof(ProgrammeImageService).GetProperty("CurrentConnectionIdentity", Static)!.GetValue(null)!;
            Assert.Equal(1L, Pick([3, 1], other));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ChannelRetuneReplacesMuxHintAndUnknownSourceRemovesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-mux-retune-" + Guid.NewGuid().ToString("N"));
        var (plugin, _) = PluginTests.ConfigureImageCache(root);
        var identity = (string)typeof(ProgrammeImageService).GetProperty("CurrentConnectionIdentity", Static)!.GetValue(null)!;
        using var stream = new HtspLiveStream(new MediaSourceInfo(), "42", NullLoggerFactory.Instance, null, null);
        typeof(HtspLiveStream).GetField("_serverIdentity", Instance)!.SetValue(stream, identity);
        var known = typeof(HtspLiveStream).GetMethod("GetKnownMux", Static)!;
        void Learn(HTSMessage source)
        {
            var message = new HTSMessage();
            message.putField("sourceinfo", source);
            typeof(HtspLiveStream).GetMethod("LogSourceInfo", Instance)!.Invoke(stream, new object[] { message });
        }
        try
        {
            Learn(Source("A", "one", "one"));
            Assert.Equal(Key(Source("A", "one", "one")), known.Invoke(null, new object[] { identity, 42L }));
            Learn(Source("A", "two", "two"));
            Assert.Equal(Key(Source("A", "two", "two")), known.Invoke(null, new object[] { identity, 42L }));
            Learn(Source(null, null));
            Assert.Null(known.Invoke(null, new object[] { identity, 42L }));
            plugin.Configuration.TVH_ServerName = "changed-server";
            Learn(Source("A", "one", "one"));
            Assert.Null(known.Invoke(null, new object[] { identity, 42L }));
        }
        finally { Directory.Delete(root, true); }
    }
}
