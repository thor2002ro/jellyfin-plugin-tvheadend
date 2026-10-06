using System;
using System.Linq;
using System.Numerics;
using System.Net.Http;
using System.Reflection;
using MediaBrowser.Model.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;
using Xunit;

public class EpgTests
{
    [Fact]
    public async Task PushRefreshSuppressesInitialDumpAndCoalescesChangesAcrossBusyTasksAndReconnect()
    {
        var queued = 0;
        var queuedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = TaskState.Idle;
        TaskResult lastExecution = null;
        var task = PluginTests.CreateProxy<IScheduledTask>((method, _) => method.Name == "get_Key" ? "RefreshGuide" : null);
        var worker = PluginTests.CreateProxy<IScheduledTaskWorker>((method, _) => method.Name switch
        {
            "get_ScheduledTask" => task, "get_State" => state,
            "get_LastExecutionResult" => lastExecution, _ => null
        });
        var manager = PluginTests.CreateProxy<ITaskManager>((method, _) =>
        {
            if (method.Name == "get_ScheduledTasks") return new[] { worker };
            if (method.Name == "QueueScheduledTask")
            {
                queued++;
                queuedSignal.TrySetResult();
            }
            return null;
        });
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance, new EpgHttpFactory(), null, manager);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var refresh = typeof(HTSConnectionHandler).GetMethod("RefreshGuide", flags)!;
        var pending = typeof(HTSConnectionHandler).GetField("_guideRefreshPending", flags)!;
        var last = typeof(HTSConnectionHandler).GetField("_lastGuideRefreshUtc", flags)!;
        handler.onMessage(Event(1, 10));
        Assert.False((bool)pending.GetValue(handler)!);
        handler.onMessage(new HTSMessage { Method = "initialSyncCompleted" });
        for (var id = 2; id < 100; id++) handler.onMessage(Event(id, 10));
        Assert.True((bool)pending.GetValue(handler)!);
        state = TaskState.Running;
        refresh.Invoke(handler, null);
        Assert.Equal(0, queued);
        Assert.True((bool)pending.GetValue(handler)!);
        state = TaskState.Idle;
        var timer = (Timer)typeof(HTSConnectionHandler).GetField("_guideRefreshTimer", flags)!.GetValue(handler)!;
        timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        await queuedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var refreshLock = typeof(HTSConnectionHandler).GetField("_guideRefreshLock", flags)!.GetValue(handler)!;
        lock (refreshLock)
        {
            Assert.Equal(1, queued);
            Assert.False((bool)pending.GetValue(handler)!);
        }
        handler.onMessage(Event(100, 10));
        refresh.Invoke(handler, null);
        Assert.Equal(1, queued);
        Assert.True((bool)pending.GetValue(handler)!);
        last.SetValue(handler, DateTime.UtcNow.AddHours(-13));
        lastExecution = new TaskResult { EndTimeUtc = DateTime.UtcNow };
        refresh.Invoke(handler, null);
        Assert.Equal(1, queued);
        lastExecution.EndTimeUtc = DateTime.UtcNow.AddHours(-13);
        refresh.Invoke(handler, null);
        Assert.Equal(2, queued);
        handler.onMessage(Event(101, 10));
        handler.onError(new System.IO.IOException("Disconnected"));
        refresh.Invoke(handler, null);
        Assert.Equal(2, queued);
        Assert.False((bool)pending.GetValue(handler)!);
        Assert.Empty(handler.GetCachedEvents(10));
    }

    private sealed class EpgHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static HTSMessage Event(long id, long channel, string title = "Original")
    {
        var message = new HTSMessage { Method = "eventAdd" };
        message.putField("eventId", new BigInteger(id));
        message.putField("channelId", new BigInteger(channel));
        message.putField("start", new BigInteger(1000));
        message.putField("stop", new BigInteger(2000));
        message.putField("title", title);
        return message;
    }

    [Fact]
    public void PartialUpdatesMovesDeletesAndReconnectKeepTheChannelIndexConsistent()
    {
        var cache = new EpgDataHelper();
        cache.Update(Event(1, 10));
        var update = new HTSMessage { Method = "eventUpdate" };
        update.putField("eventId", new BigInteger(1));
        update.putField("title", "Updated");
        Assert.True(cache.Update(update));
        Assert.False(cache.Update(update));
        var snapshot = cache.GetEvents(10).Single();
        Assert.Equal(1000L, snapshot.getLong("start"));
        Assert.Equal("Updated", snapshot.getString("title"));
        snapshot.putField("title", "Caller mutation");
        Assert.Equal("Updated", cache.GetEvents(10).Single().getString("title"));
        update.putField("channelId", new BigInteger(20));
        cache.Update(update);
        Assert.Empty(cache.GetEvents(10));
        Assert.Single(cache.GetEvents(20));
        var delete = new HTSMessage { Method = "eventDelete" };
        delete.putField("eventId", new BigInteger(1));
        Assert.True(cache.Update(delete));
        Assert.False(cache.Update(delete));
        Assert.Empty(cache.GetEvents(20));
        cache.Update(Event(2, 20));
        cache.Clean();
        Assert.Empty(cache.GetEvents(20));
    }

    [Fact]
    public async Task CachedEventsReuseProgrammeMappingAndRangeFiltering()
    {
        var cache = new EpgDataHelper();
        cache.Update(Event(1, 10));
        cache.Update(Event(2, 20));
        Assert.Single(cache.GetEvents(10, 1200, 1500));
        Assert.Empty(cache.GetEvents(10, 3000, 4000));
        var incomplete = new HTSMessage { Method = "eventUpdate" };
        incomplete.putField("eventId", new BigInteger(3));
        incomplete.putField("channelId", new BigInteger(10));
        cache.Update(incomplete);
        var epoch = DateTime.UnixEpoch;
        var mapper = new GetEventsResponseHandler(epoch.AddSeconds(1200), epoch.AddSeconds(1500), NullLogger<LiveTvService>.Instance);
        var response = new HTSMessage();
        response.putField("events", cache.GetEvents(10));
        mapper.handleResponse(response);
        var program = Assert.Single(await mapper.GetEvents(CancellationToken.None));
        Assert.Equal("Original", program.Name);
        Assert.Equal(epoch.AddSeconds(1000), program.StartDate);
        mapper = new GetEventsResponseHandler(epoch.AddSeconds(3000), epoch.AddSeconds(4000), NullLogger<LiveTvService>.Instance);
        mapper.handleResponse(response);
        Assert.Empty(await mapper.GetEvents(CancellationToken.None));
    }
}

