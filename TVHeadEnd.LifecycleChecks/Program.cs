using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;

const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

var sharedHubsField = typeof(HtspLiveStream).GetField("SharedHubsByChannelId", PrivateStatic)!;
var sharedHubs = (ConcurrentDictionary<string, HtspLiveStream>)sharedHubsField.GetValue(null)!;
var removeSharedHub = typeof(HtspLiveStream).GetMethod("RemoveSharedHub", PrivateStatic)!;
var releasePlayback = typeof(HtspLiveStream).GetMethod("ReleaseSharedPlaybackReference", PrivateInstance)!;
var attachPlayback = typeof(HtspLiveStream).GetMethod("TryAttachPlaybackToProducer", PrivateInstance)!;
var channelId = Guid.NewGuid().ToString("N");
using var staleHub = CreateStream(channelId);
using var replacementHub = CreateStream(channelId);
sharedHubs[channelId] = replacementHub;

Assert(!(bool)removeSharedHub.Invoke(null, new object[] { channelId, staleHub })!, "A stale hub removed its replacement.");
Assert(ReferenceEquals(sharedHubs[channelId], replacementHub), "The replacement hub was not preserved.");
Assert((bool)removeSharedHub.Invoke(null, new object[] { channelId, replacementHub })!, "The current hub could not remove itself.");

using (var stream = CreateStream(Guid.NewGuid().ToString("N")))
{
    using var reader = stream.GetStream();
    Assert(GetInt(stream, "_activeStreamReaders") == 1, "Creating a reader did not increment its count.");
    Assert(GetPlaybackReferenceCount(stream) == 1, "Creating a reader did not retain playback ownership.");

    stream.Close().GetAwaiter().GetResult();
    Assert(GetInt(stream, "_activeStreamReaders") == 0, "Closing playback did not remove owned readers.");
    Assert(GetPlaybackReferenceCount(stream) == 0, "Closing playback leaked its shared reference.");

    using var lateReader = stream.GetStream();
    Assert(ReferenceEquals(lateReader, Stream.Null), "Closed playback created a late reader.");
    Assert(GetPlaybackReferenceCount(stream) == 0, "A late reader restored closed playback ownership.");
}

var registeredChannelId = Guid.NewGuid().ToString("N");
var registeredHub = CreateStream(registeredChannelId);
sharedHubs[registeredChannelId] = registeredHub;
SetField(registeredHub, "_registeredAsSharedHub", true);
using (registeredHub.GetStream())
{
    registeredHub.Close().GetAwaiter().GetResult();
    var idleClose = GetField(registeredHub, "_sharedHubIdleCloseCancellationTokenSource");
    releasePlayback.Invoke(registeredHub, new object[] { registeredHub.UniqueId, "duplicate close" });
    Assert(ReferenceEquals(idleClose, GetField(registeredHub, "_sharedHubIdleCloseCancellationTokenSource")), "Duplicate release reset the idle-close timer.");
}

registeredHub.Dispose();
Assert(GetInt(registeredHub, "_closeStarted") == 1, "Dispose did not close the unused producer.");
Assert(!sharedHubs.ContainsKey(registeredChannelId), "Dispose left the shared hub registered.");

using (var stream = CreateStream(Guid.NewGuid().ToString("N")))
{
    var reader = stream.GetStream();
    Assert(GetInt(stream, "_activeStreamReaders") == 1, "Creating a reader did not increment its count.");
    reader.Dispose();
    Assert(GetInt(stream, "_activeStreamReaders") == 0, "Disposing a reader leaked its count.");
    stream.Close().GetAwaiter().GetResult();
}

for (var i = 0; i < 100; i++)
{
    using var stream = CreateStream(Guid.NewGuid().ToString("N"));
    Stream reader = null;
    Parallel.Invoke(
        () => reader = stream.GetStream(),
        () => stream.Close().GetAwaiter().GetResult());
    reader.Dispose();

    Assert(GetInt(stream, "_activeStreamReaders") == 0, "Concurrent close leaked a reader count.");
    Assert(GetPlaybackReferenceCount(stream) == 0, "Concurrent close leaked playback ownership.");
    Assert(ReferenceEquals(stream.GetStream(), Stream.Null), "Concurrent close allowed a late reader.");
}

for (var i = 0; i < 100; i++)
{
    using var hub = CreateStream(Guid.NewGuid().ToString("N"));
    using var playback = CreateStream(Guid.NewGuid().ToString("N"));
    Parallel.Invoke(
        () =>
        {
            try
            {
                attachPlayback.Invoke(playback, new object[] { hub, false });
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ObjectDisposedException)
            {
            }
        },
        () => playback.Close().GetAwaiter().GetResult());

    Assert(GetPlaybackReferenceCount(hub) == 0, "Concurrent open attached playback after close.");
}

Console.WriteLine("Lifecycle checks passed.");

static HtspLiveStream CreateStream(string channelId)
{
    return new HtspLiveStream(new MediaSourceInfo(), channelId, NullLoggerFactory.Instance, null!, null!);
}

static int GetInt(HtspLiveStream stream, string fieldName)
{
    return (int)typeof(HtspLiveStream).GetField(fieldName, PrivateInstance)!.GetValue(stream)!;
}

static object GetField(HtspLiveStream stream, string fieldName)
{
    return typeof(HtspLiveStream).GetField(fieldName, PrivateInstance)!.GetValue(stream)!;
}

static void SetField(HtspLiveStream stream, string fieldName, object value)
{
    typeof(HtspLiveStream).GetField(fieldName, PrivateInstance)!.SetValue(stream, value);
}

static int GetPlaybackReferenceCount(HtspLiveStream stream)
{
    return (int)typeof(HtspLiveStream).GetMethod("GetSharedPlaybackReferenceCount", PrivateInstance)!.Invoke(stream, null)!;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
