using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;
using Xunit;

public class RecordingTimestampTests
{
    private static HTSMessage Message(string method, int id = 42, string title = null)
    {
        var message = new HTSMessage { Method = method };
        message.putField("id", new BigInteger(id));
        if (method == "dvrEntryAdd") message.putField("state", "completed");
        if (title != null) message.putField("title", title);
        return message;
    }

    [Fact]
    public async Task TimestampsTrackActualChangesPerRecordingAndIgnoreDuplicatePushes()
    {
        var helper = new DvrDataHelper(NullLogger<DvrDataHelper>.Instance);
        var before = DateTime.UtcNow;
        helper.dvrEntryAdd(Message("dvrEntryAdd", title: "Original"));
        helper.dvrEntryAdd(Message("dvrEntryAdd", 43, "Other"));
        var initial = (await helper.buildDvrInfos(CancellationToken.None)).ToArray();
        var first = Assert.Single(initial, item => item.Id == "42");
        var other = Assert.Single(initial, item => item.Id == "43");
        Assert.Equal(DateTimeKind.Utc, first.DateLastUpdated.Kind);
        Assert.InRange(first.DateLastUpdated, before, DateTime.UtcNow);
        var duplicate = Message("dvrEntryUpdate", title: "Original");
        duplicate.putField("seq", new BigInteger(99));
        helper.dvrEntryUpdate(duplicate);
        helper.dvrEntryAdd(Message("dvrEntryAdd", title: "Original"));
        Assert.Equal(first.DateLastUpdated, Assert.Single(await helper.buildDvrInfos(CancellationToken.None), item => item.Id == "42").DateLastUpdated);
        helper.dvrEntryUpdate(Message("dvrEntryUpdate", title: "Changed"));
        var updated = (await helper.buildDvrInfos(CancellationToken.None)).ToArray();
        Assert.True(Assert.Single(updated, item => item.Id == "42").DateLastUpdated > first.DateLastUpdated);
        Assert.Equal(other.DateLastUpdated, Assert.Single(updated, item => item.Id == "43").DateLastUpdated);
        HTSMessage Files()
        {
            var message = Message("dvrEntryUpdate");
            var file = new HTSMessage();
            file.putField("filename", "recording.ts");
            message.putField("files", new ArrayList { file });
            return message;
        }
        helper.dvrEntryUpdate(Files());
        var nested = Assert.Single(await helper.buildDvrInfos(CancellationToken.None), item => item.Id == "42");
        Assert.True(nested.DateLastUpdated > Assert.Single(updated, item => item.Id == "42").DateLastUpdated);
        Assert.Equal("Changed", nested.Name);
        helper.dvrEntryUpdate(Files());
        Assert.Equal(nested.DateLastUpdated, Assert.Single(await helper.buildDvrInfos(CancellationToken.None), item => item.Id == "42").DateLastUpdated);
    }

    [Fact]
    public async Task ClockRollbackIsMonotonicAndDeletionOrReconnectDropsOldTimestamps()
    {
        var helper = new DvrDataHelper(NullLogger<DvrDataHelper>.Instance);
        helper.dvrEntryAdd(Message("dvrEntryAdd", title: "Original"));
        var timestamps = (Dictionary<string, DateTime>)typeof(DvrDataHelper)
            .GetField("_lastUpdated", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(helper)!;
        var future = DateTime.UtcNow.AddHours(1);
        timestamps["42"] = future;
        helper.dvrEntryUpdate(Message("dvrEntryUpdate", title: "Changed"));
        Assert.Equal(future.AddTicks(1), Assert.Single(await helper.buildDvrInfos(CancellationToken.None)).DateLastUpdated);
        helper.dvrEntryDelete(Message("dvrEntryDelete"));
        Assert.Empty(timestamps);
        helper.dvrEntryUpdate(Message("dvrEntryUpdate", title: "Missing"));
        Assert.Empty(timestamps);
        helper.dvrEntryAdd(Message("dvrEntryAdd", title: "Reused ID"));
        Assert.True(Assert.Single(await helper.buildDvrInfos(CancellationToken.None)).DateLastUpdated < future);
        helper.clean();
        Assert.Empty(timestamps);
        var reconnect = DateTime.UtcNow;
        helper.dvrEntryAdd(Message("dvrEntryAdd", title: "Reconnect"));
        Assert.InRange(Assert.Single(await helper.buildDvrInfos(CancellationToken.None)).DateLastUpdated, reconnect, DateTime.UtcNow);
    }

    [Fact]
    public async Task NativeChannelMappingReceivesUpdatedTimestampAndPreservesCompatibilityFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-recording-date-" + Guid.NewGuid().ToString("N"));
        PluginTests.ConfigureImageCache(root);
        try
        {
            using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance, new HttpFactory(), null);
            var helper = (DvrDataHelper)typeof(HTSConnectionHandler).GetField("_dvrDataHelper", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
            helper.dvrEntryAdd(Message("dvrEntryAdd", title: "Programme"));
            var recording = Assert.Single(await helper.buildDvrInfos(CancellationToken.None));
            var host = PluginTests.CreateProxy<IServerApplicationHost>((method, _) => method.ReturnType == typeof(string) ? "http://jellyfin" : null);
            var liveTv = new LiveTvService(NullLoggerFactory.Instance, null, handler, host, null, null);
            var channel = new RecordingsChannel(NullLoggerFactory.Instance, handler, liveTv);
            ChannelItemInfo Map() => (ChannelItemInfo)typeof(RecordingsChannel).GetMethod("ConvertToChannelItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(channel, new object[] { recording })!;
            Assert.Equal(recording.DateLastUpdated, Map().DateModified);
            var originalModified = Map().DateModified;
            helper.dvrEntryUpdate(Message("dvrEntryUpdate", title: "Updated programme"));
            recording = Assert.Single(await helper.buildDvrInfos(CancellationToken.None));
            Assert.Equal(recording.DateLastUpdated, Map().DateModified);
            Assert.True(Map().DateModified > originalModified);
            recording.DateLastUpdated = DateTime.MinValue;
            Assert.Equal(new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc), Map().DateModified);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class HttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
