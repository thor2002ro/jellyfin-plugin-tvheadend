using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
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
