using System;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;
using Xunit;

public class ChannelUpdateTests
{
    [Fact]
    public void BatchResolutionMatchesScalarIdsAndSkipsStaleExternalChannels()
    {
        var helper = new ChannelDataHelper(NullLogger<ChannelDataHelper>.Instance);
        helper.Add(Channel(42));
        helper.Add(Channel(43));
        var resolve = typeof(ChannelDataHelper).GetMethod("ResolveChannelIds", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long[] Batch(params string[] ids) => (long[])resolve.Invoke(helper, new object[] { ids })!;
        Assert.Equal(new long[] { 42, 43, 43 }, Batch("UUID-42", "43", "missing", "uuid-43", null));
        helper.Remove(42);
        Assert.Equal(new long[] { 43 }, Batch("uuid-42", "UUID-43"));
        var update = Message("channelUpdate", "channelId", 43);
        update.putField("channelIdStr", "replacement");
        helper.Add(update);
        Assert.Equal(new long[] { 43 }, Batch("uuid-43", "replacement"));
    }

    private static HTSMessage Channel(long id, string name = "Channel")
    {
        var message = Message("channelAdd", "channelId", id);
        message.putField("channelNumber", new BigInteger(1));
        message.putField("channelName", name);
        message.putField("channelIdStr", "uuid-" + id);
        var service = new HTSMessage();
        service.putField("type", "hdtv");
        message.putField("services", new[] { service });
        message.putField("tags", new[] { new BigInteger(7) });
        return message;
    }

    private static HTSMessage Message(string method, string key, long id)
    {
        var message = new HTSMessage { Method = method };
        message.putField(key, new BigInteger(id));
        return message;
    }

    [Fact]
    public async Task ChannelChangesMergePartialUpdatesAndIgnoreDuplicateListsAndProgrammePointers()
    {
        var helper = new ChannelDataHelper(NullLogger<ChannelDataHelper>.Instance);
        Assert.True(helper.Add(Channel(1)));
        Assert.False(helper.Add(Channel(1)));
        var update = Message("channelUpdate", "channelId", 1);
        update.putField("eventId", new BigInteger(200));
        update.putField("nextEventId", new BigInteger(201));
        Assert.False(helper.Add(update));
        var membership = Message("channelUpdate", "channelId", 1);
        membership.putField("tags", new[] { new BigInteger(7), new BigInteger(8) });
        Assert.True(helper.Add(membership));
        membership.putField("tags", new[] { new BigInteger(8), new BigInteger(7), new BigInteger(7) });
        Assert.False(helper.Add(membership));
        update.putField("channelName", "Renamed");
        Assert.True(helper.Add(update));
        Assert.False(helper.Add(update));
        Assert.Equal("Renamed", Assert.Single(await helper.BuildChannelInfos(CancellationToken.None)).Name);
        Assert.Equal(1, helper.ResolveChannelId("uuid-1"));
        Assert.True(helper.Remove(1));
        Assert.False(helper.Remove(1));
        Assert.Throws<ArgumentException>(() => helper.ResolveChannelId("uuid-1"));
        Assert.Empty(await helper.BuildChannelInfos(CancellationToken.None));
    }

    [Fact]
    public async Task MetadataPushesShareTheSchedulerAndDeletedChannelsLoseOnlyTheirOwnGuide()
    {
        var manager = PluginTests.CreateProxy<ITaskManager>((_, _) => null);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance, new HttpFactory(), null, manager);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var pending = typeof(HTSConnectionHandler).GetField("_guideRefreshPending", flags)!;
        var helper = (ChannelDataHelper)typeof(HTSConnectionHandler).GetField("_channelDataHelper", flags)!.GetValue(handler)!;
        var tag = Message("tagAdd", "tagId", 7);
        tag.putField("tagName", "Sports");
        handler.onMessage(tag);
        handler.onMessage(Channel(1));
        handler.onMessage(Channel(2));
        foreach (var id in new[] { 1, 2 })
        {
            var epg = Message("eventAdd", "eventId", id);
            epg.putField("channelId", new BigInteger(id));
            epg.putField("start", new BigInteger(1000));
            epg.putField("stop", new BigInteger(2000));
            handler.onMessage(epg);
        }
        Assert.False((bool)pending.GetValue(handler)!);
        handler.onMessage(new HTSMessage { Method = "initialSyncCompleted" });
        handler.onMessage(Channel(1));
        Assert.False((bool)pending.GetValue(handler)!);
        handler.onMessage(Channel(1, "Renamed"));
        Assert.True((bool)pending.GetValue(handler)!);
        pending.SetValue(handler, false);
        tag.Method = "tagUpdate";
        tag.putField("tagName", "News");
        handler.onMessage(tag);
        Assert.True((bool)pending.GetValue(handler)!);
        pending.SetValue(handler, false);
        handler.onMessage(Message("channelDelete", "channelId", 999));
        Assert.False((bool)pending.GetValue(handler)!);
        handler.onMessage(new HTSMessage { Method = "channelDelete" });
        Assert.False((bool)pending.GetValue(handler)!);
        handler.onMessage(Message("channelDelete", "channelId", 1));
        Assert.True((bool)pending.GetValue(handler)!);
        Assert.Empty(handler.GetCachedEvents(1));
        Assert.Single(handler.GetCachedEvents(2));
        Assert.Equal("uuid-2", Assert.Single(await helper.BuildChannelInfos(CancellationToken.None)).Id);
        pending.SetValue(handler, false);
        handler.onMessage(Message("channelDelete", "channelId", 1));
        Assert.False((bool)pending.GetValue(handler)!);
        handler.onError(new System.IO.IOException("Disconnected"));
        handler.onMessage(Channel(3));
        Assert.False((bool)pending.GetValue(handler)!);
    }

    private sealed class HttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
