using System;
using System.Collections;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;
using Xunit;

public class RecordingCacheTests
{
    private static HTSMessage Message(string method, string state = null)
    {
        var message = new HTSMessage { Method = method };
        message.putField("id", new BigInteger(42));
        if (state != null) message.putField("state", state);
        return message;
    }

    private static string Key(RecordingsChannel channel) => string.Join("-", channel.GetCacheKey("user").Split('-').Skip(3));

    [Fact]
    public async Task DvrPushesInvalidateKeyOnlyForRealChangesAndPreservePartialFields()
    {
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance, new HttpFactory(), null);
        var liveTv = new LiveTvService(NullLoggerFactory.Instance, null, handler, null, null, null);
        var channel = new RecordingsChannel(NullLoggerFactory.Instance, handler, liveTv);
        var initial = Key(channel);
        var add = Message("dvrEntryAdd", "recording");
        add.putField("title", "Programme");
        handler.onMessage(add);
        var added = Key(channel);
        Assert.NotEqual(initial, added);
        handler.onMessage(Message("dvrEntryAdd", "recording"));
        Assert.Equal(added, Key(channel));

        var duplicate = Message("dvrEntryUpdate", "recording");
        duplicate.putField("seq", new BigInteger(7));
        handler.onMessage(duplicate);
        Assert.Equal(added, Key(channel));
        handler.onMessage(Message("dvrEntryUpdate", "completed"));
        var completed = Key(channel);
        Assert.NotEqual(added, completed);
        var helper = (DvrDataHelper)typeof(HTSConnectionHandler).GetField("_dvrDataHelper", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
        var recording = Assert.Single(await helper.buildDvrInfos(CancellationToken.None));
        Assert.Equal("Programme", recording.Name);

        handler.onMessage(Message("dvrEntryDelete"));
        var deleted = Key(channel);
        Assert.NotEqual(completed, deleted);
        handler.onMessage(Message("dvrEntryDelete"));
        handler.onMessage(Message("dvrEntryUpdate", "completed"));
        Assert.Equal(deleted, Key(channel));
        liveTv._lastRecordingChange = DateTime.UtcNow;
        Assert.NotEqual(deleted, Key(channel));
    }

    [Fact]
    public void EqualNestedPushesDoNotInvalidateAndReconnectClearDoes()
    {
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance, new HttpFactory(), null);
        var liveTv = new LiveTvService(NullLoggerFactory.Instance, null, handler, null, null, null);
        var channel = new RecordingsChannel(NullLoggerFactory.Instance, handler, liveTv);
        HTSMessage Files(string path)
        {
            var message = Message("dvrEntryUpdate");
            var file = new HTSMessage();
            file.putField("filename", path);
            message.putField("files", new ArrayList { file });
            return message;
        }
        handler.onMessage(Message("dvrEntryAdd", "completed"));
        handler.onMessage(Files("programme.ts"));
        var first = Key(channel);
        handler.onMessage(Files("programme.ts"));
        Assert.Equal(first, Key(channel));
        handler.onMessage(Files("renamed.ts"));
        var changed = Key(channel);
        Assert.NotEqual(first, changed);
        var helper = (DvrDataHelper)typeof(HTSConnectionHandler).GetField("_dvrDataHelper", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
        helper.clean();
        var cleared = Key(channel);
        Assert.NotEqual(changed, cleared);
        helper.clean();
        Assert.Equal(cleared, Key(channel));
        handler.onMessage(Message("initialSyncCompleted"));
        Assert.Equal(cleared, Key(channel));
        handler.onMessage(Message("dvrEntryAdd", "completed"));
        Assert.NotEqual(cleared, Key(channel));
    }

    private sealed class HttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
