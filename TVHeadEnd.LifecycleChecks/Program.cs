using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.Configuration;
using TVHeadEnd.HTSP;

const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

var sharedHubsField = typeof(HtspLiveStream).GetField("SharedHubsByChannelId", PrivateStatic)!;
var sharedHubs = (ConcurrentDictionary<string, HtspLiveStream>)sharedHubsField.GetValue(null)!;
var removeSharedHub = typeof(HtspLiveStream).GetMethod("RemoveSharedHub", PrivateStatic)!;
var releasePlayback = typeof(HtspLiveStream).GetMethod("ReleaseSharedPlaybackReference", PrivateInstance)!;
var attachPlayback = typeof(HtspLiveStream).GetMethod("TryAttachPlaybackToProducer", PrivateInstance)!;
var logQueueStatus = typeof(HtspLiveStream).GetMethod("LogQueueStatus", PrivateInstance)!;
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

using (var stream = CreateStream(Guid.NewGuid().ToString("N")))
{
    var message = new HTSMessage();
    message.putField("Idrops", new System.Numerics.BigInteger(1));
    message.putField("Pdrops", new System.Numerics.BigInteger(2));
    message.putField("Bdrops", new System.Numerics.BigInteger(3));
    message.putField("packets", new System.Numerics.BigInteger(10));
    message.putField("bytes", new System.Numerics.BigInteger(1000));
    message.putField("delay", new System.Numerics.BigInteger(500));

    logQueueStatus.Invoke(stream, new object[] { message });
    Assert(GetInt(stream, "_awaitingCleanVideoRandomAccess") == 0, "Queue drops should be accounted without forcing a clean-keyframe wait.");
    Assert(GetLong(stream, "_videoDamageEvents") == 1, "Queue damage was not counted.");
    Assert(((string)GetField(stream, "_lastVideoDamageReason")).Contains("queue dropped frames"), "Queue damage reason was not retained.");
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

AssertMuxerDoesNotRewriteDuplicateVideoDts();
AssertMuxerMarksConfirmedSourceClockJump();
AssertStartupCacheKeepsInitialBufferClockAcrossKeyframes();
AssertDefaultQueueDepthIsCentralized();
AssertRuntimeStatusKeepsRunningChannelCompatibilityAliases();
AssertImagesAreAuthenticatedAndRefreshedLocally();
AssertStoredChannelMetadataIsReconciled();
AssertPublicImageCacheFlow();

Console.WriteLine("Lifecycle checks passed.");

static HtspLiveStream CreateStream(string channelId)
{
    return new HtspLiveStream(new MediaSourceInfo(), channelId, NullLoggerFactory.Instance, null!, null!);
}

static int GetInt(HtspLiveStream stream, string fieldName)
{
    return (int)typeof(HtspLiveStream).GetField(fieldName, PrivateInstance)!.GetValue(stream)!;
}

static long GetLong(HtspLiveStream stream, string fieldName)
{
    return (long)typeof(HtspLiveStream).GetField(fieldName, PrivateInstance)!.GetValue(stream)!;
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

static void AssertMuxerDoesNotRewriteDuplicateVideoDts()
{
    var (muxer, _) = CreateH264Muxer();
    WriteH264Packet(muxer, pts: 90_010, dts: 90_000);
    var chunk = WriteH264Packet(muxer, pts: 90_010, dts: 90_000);
    var timestamps = ReadVideoPesTimestamps(chunk);

    Assert(timestamps.Dts == 0 && timestamps.Pts == 10, "Duplicate video DTS was rewritten.");
}

static void AssertMuxerMarksConfirmedSourceClockJump()
{
    var (muxer, stream) = CreateH264Muxer();
    WriteH264Packet(muxer, pts: 110_000, dts: 100_000);

    var pending = WriteH264Packet(muxer, pts: 1_110_000, dts: 1_100_000);
    var recovered = WriteH264Packet(muxer, pts: 1_111_000, dts: 1_101_000);

    Assert(pending.Length == 0, "Unconfirmed source-clock jump was not withheld.");
    Assert(recovered.Length > 0, "Confirmed source-clock jump did not resume muxing.");
    Assert((long)GetMuxerStreamField(stream, "TimestampDiscontinuityCount") == 1, "Confirmed source-clock jump did not emit a discontinuity.");
}

static void AssertStartupCacheKeepsInitialBufferClockAcrossKeyframes()
{
    using var stream = CreateStream(Guid.NewGuid().ToString("N"));
    var addChunk = typeof(HtspLiveStream).GetMethod("AddStartupCacheChunkLocked", PrivateInstance)!;
    var chunk = new byte[188];
    chunk[0] = 0x47;
    SetField(stream, "_primaryVideoStreamIndex", 0);
    SetField(stream, "_startupCacheStartedUtcTicks", 12345L);

    var args = new object[] { chunk, true, false, false };
    addChunk.Invoke(stream, args);

    Assert(GetLong(stream, "_startupCacheStartedUtcTicks") == 12345L, "A later keyframe restarted the initial tune buffer clock.");
}

static void AssertDefaultQueueDepthIsCentralized()
{
    var configuration = new PluginConfiguration();
    Assert(configuration.HTSPQueueDepth == PluginConfiguration.DefaultHTSPQueueDepth, "Default HTSP queue depth no longer matches the centralized default.");
    Assert(PluginConfiguration.CreateDefault().HTSPQueueDepth == configuration.HTSPQueueDepth, "Reset defaults no longer match fresh configuration defaults.");
}

static void AssertRuntimeStatusKeepsRunningChannelCompatibilityAliases()
{
    var channels = new[] { new HtspRunningChannelStatus { ChannelId = "1" } };
    var status = new PluginRuntimeStatus
    {
        RunningChannelCount = channels.Length,
        RunningChannels = channels
    };

    Assert(status.ActiveProducerCount == status.RunningChannelCount, "Legacy active-producer count alias no longer mirrors running channels.");
    Assert(ReferenceEquals(status.Producers, status.RunningChannels), "Legacy producers alias no longer mirrors running channels.");
}

static void AssertImagesAreAuthenticatedAndRefreshedLocally()
{
    var resolve = typeof(HTSConnectionHandler).GetMethod(
        "ResolveImageUrl",
        PrivateStatic,
        null,
        new[] { typeof(string), typeof(string) },
        null)!;

    Assert(
        (string)resolve.Invoke(null, new object[] { "http://tvh:9981/root", "imagecache/42" })!
            == "http://tvh:9981/root/imagecache/42",
        "Relative TVHeadend artwork was not made downloadable.");
    Assert(
        (string)resolve.Invoke(null, new object[] { "http://tvh:9981/root", "https://images.example/icon.png" })!
            == "https://images.example/icon.png",
        "Absolute guide artwork was rewritten.");
    Assert(
        resolve.Invoke(null, new object[] { "http://tvh:9981/root", "file:///secret.png" }) is null,
        "Unsupported image URL scheme was sent to TVHeadend.");

    var sameOrigin = typeof(HTSConnectionHandler).GetMethod("SameOrigin", PrivateStatic)!;
    Assert(
        (bool)sameOrigin.Invoke(null, new object[] { new Uri("http://tvh:9981/root"), new Uri("http://tvh:9981/image/42") })!,
        "TVHeadend image was not recognized as same-origin.");
    Assert(
        !(bool)sameOrigin.Invoke(null, new object[] { new Uri("http://tvh:9981/root"), new Uri("https://images.example/icon.png") })!,
        "TVHeadend credentials could leak to an external image host.");

    var shouldStop = typeof(HTSConnectionHandler).GetMethod("ShouldStopImageRefresh", PrivateStatic)!;
    Assert(
        (bool)shouldStop.Invoke(null, new object[] { new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden) })!,
        "Authentication failure did not stop the image refresh failure cascade.");
    Assert(
        !(bool)shouldStop.Invoke(null, new object[] { new HttpRequestException("Missing", null, HttpStatusCode.NotFound) })!,
        "One missing image incorrectly stopped unrelated image downloads.");

    var download = typeof(HTSConnectionHandler).GetMethod("DownloadImageAsync", PrivateStatic)!;
    var cacheDirectory = Path.Combine(Path.GetTempPath(), "tvheadend-image-check-" + Guid.NewGuid().ToString("N"));
    var handler = new ImageResponseHandler();
    using var client = new HttpClient(handler);
    var arguments = new object[]
    {
        client,
        new Uri("http://tvh:9981/root/imagecache/42"),
        new Dictionary<string, string> { ["Authorization"] = "Basic test" },
        cacheDirectory,
        "channel:42",
        (Action<string>)(path =>
        {
            if (new FileInfo(path).Length < 20)
            {
                throw new InvalidDataException("Invalid test image.");
            }
        }),
        (Func<bool>)(() => true),
        null!,
        null!,
        CancellationToken.None
    };

    try
    {
        var firstPath = ((Task<string>)download.Invoke(null, arguments)!).GetAwaiter().GetResult();
        var firstBytes = File.ReadAllBytes(firstPath);
        var secondPath = ((Task<string>)download.Invoke(null, arguments)!).GetAwaiter().GetResult();
        var secondBytes = File.ReadAllBytes(secondPath);
        var changedFormatPath = ((Task<string>)download.Invoke(null, arguments)!).GetAwaiter().GetResult();
        var changedFormatBytes = File.ReadAllBytes(changedFormatPath);
        var rejectedCorruptImage = false;
        try
        {
            ((Task<string>)download.Invoke(null, arguments)!).GetAwaiter().GetResult();
        }
        catch (InvalidDataException)
        {
            rejectedCorruptImage = true;
        }

        Assert(handler.SawAuthorization, "TVHeadend image request omitted authentication.");
        Assert(firstPath == secondPath, "Refreshing an image changed its stable local path.");
        Assert(!firstBytes.SequenceEqual(secondBytes), "Guide refresh did not replace changed image bytes.");
        Assert(firstPath != changedFormatPath, "A valid channel image format change was rejected.");
        Assert(rejectedCorruptImage, "A corrupt image with a valid signature was cached.");
        Assert(
            changedFormatBytes.SequenceEqual(File.ReadAllBytes(changedFormatPath)),
            "A rejected corrupt image overwrote the last valid cached image.");
        typeof(HTSConnectionHandler).GetMethod(
            "PruneSupersededChannelImage",
            PrivateStatic)!.Invoke(null, new object[] { changedFormatPath });
        Assert(!File.Exists(secondPath), "A superseded channel image format was retained.");
        Assert(File.Exists(changedFormatPath), "Pruning removed the current channel image.");
    }
    finally
    {
        Directory.Delete(cacheDirectory, true);
    }
}

static void AssertStoredChannelMetadataIsReconciled()
{
    var path = Path.GetTempFileName();
    try
    {
        var needsRefresh = typeof(LiveTvService).GetMethod(
            "StoredImageNeedsRefresh",
            PrivateStatic)!;
        var image = new ItemImageInfo
        {
            Type = ImageType.Primary,
            Path = path,
            DateModified = File.GetLastWriteTimeUtc(path)
        };

        Assert(
            !(bool)needsRefresh.Invoke(null, new object[] { image, path })!,
            "Unchanged stored channel metadata was updated again.");
        image.DateModified = image.DateModified.AddSeconds(-1);
        Assert(
            (bool)needsRefresh.Invoke(null, new object[] { image, path })!,
            "A canceled or failed metadata update was not retried.");
        image.DateModified = File.GetLastWriteTimeUtc(path);
        image.Path += ".old";
        Assert(
            (bool)needsRefresh.Invoke(null, new object[] { image, path })!,
            "A channel image format/path change was not reconciled.");
        image.Path = "https://images.example/icon.png";
        Assert(
            !(bool)needsRefresh.Invoke(null, new object[] { image, image.Path })!,
            "An unchanged external channel image was updated again.");
        Assert(
            (bool)needsRefresh.Invoke(null, new object[] { image, "https://images.example/new-icon.png" })!,
            "A changed external channel image URL was not reconciled.");
    }
    finally
    {
        File.Delete(path);
    }
}

static void AssertPublicImageCacheFlow()
{
    var root = Path.Combine(Path.GetTempPath(), "tvheadend-public-image-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var applicationPaths = CreateProxy<IApplicationPaths>((method, _) =>
            method.ReturnType == typeof(string) ? root : GetDefault(method.ReturnType));
        var xmlSerializer = CreateProxy<IXmlSerializer>((method, arguments) =>
            method.Name.StartsWith("Deserialize", StringComparison.Ordinal)
                ? Activator.CreateInstance((Type)arguments[0])
                : GetDefault(method.ReturnType));
        var plugin = new Plugin(applicationPaths, xmlSerializer);
        plugin.UpdateConfiguration(new PluginConfiguration
        {
            TVH_ServerName = "tvh",
            Username = "user",
            Password = "password"
        });

        var responseHandler = new ImageResponseHandler();
        var httpClient = new HttpClient(responseHandler);
        var imageEncoder = CreateProxy<IImageEncoder>((method, _) =>
            method.Name == nameof(IImageEncoder.GetImageSize)
                ? new ImageDimensions(1, 1)
                : GetDefault(method.ReturnType));
        using var handler = new HTSConnectionHandler(
            NullLoggerFactory.Instance,
            new TestHttpClientFactory(httpClient),
            imageEncoder);

        var imageDirectory = plugin.ImageCachePath;
        Directory.CreateDirectory(imageDirectory);
        var staleTemporaryPath = Path.Combine(imageDirectory, "stale.tmp");
        var recentTemporaryPath = Path.Combine(imageDirectory, "recent.tmp");
        File.WriteAllText(staleTemporaryPath, "stale");
        File.WriteAllText(recentTemporaryPath, "recent");
        File.SetLastWriteTimeUtc(staleTemporaryPath, DateTime.UtcNow.AddDays(-2));

        handler.BeginImageRefresh(["channel:42"]);
        Assert(!File.Exists(staleTemporaryPath), "A crash-abandoned image download was not pruned.");
        Assert(File.Exists(recentTemporaryPath), "An active image download was pruned.");

        var first = handler.CacheImageAsync(
            "imagecache/42",
            "channel:42",
            CancellationToken.None).GetAwaiter().GetResult();
        Assert(File.Exists(first.ImagePath), "The public cache flow did not create a local image.");
        Assert(responseHandler.SawAuthorization, "The public cache flow omitted TVHeadend authentication.");

        var requestCount = responseHandler.RequestCount;
        handler.BeginImageRefresh(["channel:42"]);
        var second = handler.CacheImageAsync(
            "imagecache/42",
            "channel:42",
            CancellationToken.None).GetAwaiter().GetResult();
        Assert(first.ImagePath == second.ImagePath, "An unchanged guide refresh changed the cached image path.");
        Assert(responseHandler.RequestCount == requestCount, "An unchanged guide refresh fetched the TVHeadend image again.");

        var missingResponse = new MissingImageResponseHandler();
        using var missingHandler = new HTSConnectionHandler(
            NullLoggerFactory.Instance,
            new TestHttpClientFactory(new HttpClient(missingResponse)),
            imageEncoder);
        missingHandler.BeginImageRefresh(["channel:404"]);
        var missing = missingHandler.CacheImageAsync(
            "imagecache/404",
            "channel:404",
            CancellationToken.None).GetAwaiter().GetResult();
        missingHandler.CacheImageAsync(
            "imagecache/404",
            "channel:404",
            CancellationToken.None).GetAwaiter().GetResult();
        Assert(missing.ImagePath is null, "A missing TVHeadend image produced a cache path.");
        Assert(missingResponse.RequestCount == 1, "A missing image was fetched repeatedly in one guide refresh.");
        missingHandler.BeginImageRefresh(["channel:404"]);
        missingHandler.CacheImageAsync(
            "imagecache/404",
            "channel:404",
            CancellationToken.None).GetAwaiter().GetResult();
        Assert(missingResponse.RequestCount == 2, "A missing image was not retried on the next guide refresh.");

        var blockingResponse = new BlockingImageResponseHandler();
        using var blockingHandler = new HTSConnectionHandler(
            NullLoggerFactory.Instance,
            new TestHttpClientFactory(new HttpClient(blockingResponse)),
            imageEncoder);
        blockingHandler.BeginImageRefresh(["channel:99"]);
        using var cancellation = new CancellationTokenSource();
        var canceledRequest = blockingHandler.CacheImageAsync(
            "imagecache/99",
            "channel:99",
            cancellation.Token);
        blockingResponse.Started.GetAwaiter().GetResult();
        cancellation.Cancel();
        var canceled = false;
        try
        {
            canceledRequest.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Assert(canceled, "Canceling a guide refresh did not release its caller.");
        blockingResponse.Release();
        var recovered = blockingHandler.CacheImageAsync(
            "imagecache/99",
            "channel:99",
            CancellationToken.None).GetAwaiter().GetResult();
        Assert(File.Exists(recovered.ImagePath), "A canceled caller prevented the shared cache download from completing.");
        Assert(blockingResponse.RequestCount == 1, "Retrying after caller cancellation duplicated the TVHeadend download.");

        blockingHandler.CacheImageAsync(
            null,
            "channel:99",
            CancellationToken.None).GetAwaiter().GetResult();
        Assert(File.Exists(recovered.ImagePath), "Invalidation deleted the last image before metadata reconciliation.");
        typeof(HTSConnectionHandler).GetMethod(
            "PruneChannelImages",
            PrivateInstance)!.Invoke(
                blockingHandler,
                new object[] { Array.Empty<string>(), new[] { "channel:99" } });
        Assert(!File.Exists(recovered.ImagePath), "A removed channel image was retained after reconciliation.");
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static T CreateProxy<T>(Func<MethodInfo, object[], object> invoke)
    where T : class
{
    var proxy = DispatchProxy.Create<T, InterfaceProxy>();
    ((InterfaceProxy)(object)proxy).InvokeMethod = invoke;
    return proxy;
}

static object GetDefault(Type type) =>
    type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);

static (object Muxer, object Stream) CreateH264Muxer()
{
    var (muxer, streams) = CreateMuxer("H264");
    return (muxer, streams[0]);
}

static (object Muxer, object[] Streams) CreateMuxer(params string[] codecs)
{
    var muxerType = typeof(HtspLiveStream).Assembly.GetType("TVHeadEnd.HtspTransportStreamMuxer")!;
    var streamInfoType = muxerType.GetNestedType("StreamInfo", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var muxer = Activator.CreateInstance(muxerType)!;
    var streams = new object[codecs.Length];
    var streamArray = Array.CreateInstance(streamInfoType, codecs.Length);
    for (var i = 0; i < codecs.Length; i++)
    {
        var stream = Activator.CreateInstance(streamInfoType)!;
        streamInfoType.GetProperty("Index")!.SetValue(stream, i);
        streamInfoType.GetProperty("Codec")!.SetValue(stream, codecs[i]);
        streams[i] = stream;
        streamArray.SetValue(stream, i);
    }

    muxerType.GetMethod("SetTimestampsAre90Khz")!.Invoke(muxer, new object[] { true });
    muxerType.GetMethod("SetStreams")!.Invoke(muxer, new object[] { streamArray, false });

    return (muxer, streams);
}

static byte[] WriteH264Packet(object muxer, long? pts, long? dts)
{
    return WritePacket(muxer, 0, new byte[] { 0x00, 0x00, 0x01, 0x09, 0xF0 }, pts, dts);
}

static byte[] WritePacket(object muxer, int streamIndex, byte[] payload, long? pts, long? dts)
{
    return (byte[])muxer.GetType().GetMethod("WritePacket")!.Invoke(
        muxer,
        new object[] { streamIndex, payload, pts, dts, false, false })!;
}

static (long Pts, long Dts) ReadVideoPesTimestamps(byte[] transportStream) => ReadPesTimestamps(transportStream, 0x101);

static (long Pts, long Dts) ReadPesTimestamps(byte[] transportStream, int pidToFind)
{
    for (var packetOffset = 0; packetOffset + 188 <= transportStream.Length; packetOffset += 188)
    {
        var packet = transportStream.AsSpan(packetOffset, 188);
        var pid = ((packet[1] & 0x1F) << 8) | packet[2];
        var adaptationControl = (packet[3] >> 4) & 0x03;
        if (pid != pidToFind || (packet[1] & 0x40) == 0 || (adaptationControl & 0x01) == 0)
        {
            continue;
        }

        var payloadOffset = 4;
        if ((adaptationControl & 0x02) != 0)
        {
            payloadOffset += 1 + packet[payloadOffset];
        }

        if (payloadOffset + 19 <= packet.Length
            && packet[payloadOffset] == 0x00
            && packet[payloadOffset + 1] == 0x00
            && packet[payloadOffset + 2] == 0x01
            && (packet[payloadOffset + 7] & 0xC0) == 0xC0)
        {
            return (ReadPesTimestamp(packet, payloadOffset + 9), ReadPesTimestamp(packet, payloadOffset + 14));
        }
    }

    throw new InvalidOperationException("Video PES with PTS and DTS was not found.");
}

static long ReadPesTimestamp(ReadOnlySpan<byte> data, int offset)
{
    return ((long)((data[offset] >> 1) & 0x07) << 30)
        | ((long)data[offset + 1] << 22)
        | ((long)((data[offset + 2] >> 1) & 0x7F) << 15)
        | ((long)data[offset + 3] << 7)
        | ((long)(data[offset + 4] & 0xFE) >> 1);
}

static object GetMuxerStreamField(object stream, string fieldName)
{
    return stream.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stream)!;
}

sealed class ImageResponseHandler : HttpMessageHandler
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly byte[] Gif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
    private int _requestCount;

    public bool SawAuthorization { get; private set; }
    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SawAuthorization |= string.Equals(
            request.Headers.Authorization?.Scheme,
            "Basic",
            StringComparison.OrdinalIgnoreCase);
        var requestCount = Interlocked.Increment(ref _requestCount);
        var content = requestCount switch
        {
            1 => new ByteArrayContent(Png),
            2 => new ByteArrayContent(Png.Append((byte)0).ToArray()),
            3 => new ByteArrayContent(Gif),
            _ => new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

sealed class TestHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _httpClient;

    public TestHttpClientFactory(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public HttpClient CreateClient(string name) => _httpClient;
}

sealed class MissingImageResponseHandler : HttpMessageHandler
{
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

class InterfaceProxy : DispatchProxy
{
    public Func<MethodInfo, object[], object> InvokeMethod { get; set; }

    protected override object Invoke(MethodInfo targetMethod, object[] args) =>
        InvokeMethod(targetMethod, args);
}

sealed class BlockingImageResponseHandler : HttpMessageHandler
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestCount;

    public Task Started => _started.Task;
    public int RequestCount => Volatile.Read(ref _requestCount);

    public void Release() => _release.TrySetResult(true);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        _started.TrySetResult(true);
        await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Png)
        };
    }
}
