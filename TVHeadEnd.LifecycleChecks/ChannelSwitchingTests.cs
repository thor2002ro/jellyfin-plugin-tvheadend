using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.HTSP;
using Xunit;

public class ChannelSwitchingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingAuthenticationAvoidsUnusedMetadataRoundTrips(bool metadata)
    {
        await using var server = new Server();
        using var connection = new HTSConnectionAsync(new Listener(), "test", "1", NullLoggerFactory.Instance);
        connection.open("127.0.0.1", server.Port, server.Token, 1);
        Assert.True(connection.authenticate("user", "pass", metadata, server.Token, TimeSpan.FromSeconds(2)));
        if (metadata) await server.WaitFor("enableAsyncMetadata");
        Assert.Equal(metadata ? new[] { "hello", "authenticate", "getDiskSpace", "getSysTime", "enableAsyncMetadata" }
            : new[] { "hello", "authenticate" }, server.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task IdleWeightDropsOnlyAfterTheFinalViewerAndRestoresBeforeReuse()
    {
        await using var server = new Server();
        using var stream = new HtspLiveStream(new MediaSourceInfo(), "42", NullLoggerFactory.Instance, null, null);
        var add = typeof(HtspLiveStream).GetMethod("TryAddSharedPlaybackReference", Private)!;
        var release = typeof(HtspLiveStream).GetMethod("ReleaseSharedPlaybackReference", Private)!;
        var connect = typeof(HtspLiveStream).GetMethod("ConnectAndSubscribeAsync", Private)!;
        add.Invoke(stream, new object[] { "viewer-1" });
        add.Invoke(stream, new object[] { "viewer-2" });
        await (Task)connect.Invoke(stream, new object[] { "127.0.0.1", server.Port, "user", "pass", null, server.Token, false })!;
        var first = await server.WaitFor("subscribe");
        Assert.Equal(100, first.getInt("weight"));
        release.Invoke(stream, new object[] { "viewer-1", "switch" });
        Assert.DoesNotContain(server.Requests, request => request.Method == "subscriptionChangeWeight");
        release.Invoke(stream, new object[] { "viewer-2", "switch" });
        var idle = await server.WaitFor("subscriptionChangeWeight");
        Assert.Equal(1, idle.getInt("weight"));
        Assert.Equal(first.getInt("subscriptionId"), idle.getInt("subscriptionId"));
        release.Invoke(stream, new object[] { "viewer-2", "duplicate" });
        add.Invoke(stream, new object[] { "viewer-3" });
        var active = await server.WaitFor("subscriptionChangeWeight");
        Assert.Equal(100, active.getInt("weight"));
        Assert.Equal(first.getInt("subscriptionId"), active.getInt("subscriptionId"));
        await (Task)connect.Invoke(stream, new object[] { "127.0.0.1", server.Port, "user", "pass", null, server.Token, false })!;
        var replacement = await server.WaitFor("subscribe");
        Assert.Equal(100, replacement.getInt("weight"));
        Assert.NotEqual(first.getInt("subscriptionId"), replacement.getInt("subscriptionId"));
        release.Invoke(stream, new object[] { "viewer-3", "switch again" });
        Assert.Equal(replacement.getInt("subscriptionId"), (await server.WaitFor("subscriptionChangeWeight")).getInt("subscriptionId"));
        Assert.Equal(new[] { 1, 100, 1 }, server.Requests.Where(request => request.Method == "subscriptionChangeWeight")
            .Select(request => request.getInt("weight")));
        await (Task)connect.Invoke(stream, new object[] { "127.0.0.1", server.Port, "user", "pass", null, server.Token, false })!;
        Assert.Equal(1, (await server.WaitFor("subscribe")).getInt("weight"));
        Assert.Equal(3, server.Requests.Count(request => request.Method == "subscriptionChangeWeight"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferenceChangesDuringSubscribeUseTheFinalViewerState(bool viewerReturns)
    {
        await using var server = new Server(pauseSubscribe: true);
        using var stream = new HtspLiveStream(new MediaSourceInfo(), "42", NullLoggerFactory.Instance, null, null);
        var add = typeof(HtspLiveStream).GetMethod("TryAddSharedPlaybackReference", Private)!;
        var release = typeof(HtspLiveStream).GetMethod("ReleaseSharedPlaybackReference", Private)!;
        var connect = typeof(HtspLiveStream).GetMethod("ConnectAndSubscribeAsync", Private)!;
        add.Invoke(stream, new object[] { "viewer-1" });
        var opening = (Task)connect.Invoke(stream, new object[] { "127.0.0.1", server.Port, "user", "pass", null, server.Token, false })!;
        await server.WaitFor("subscribe");
        release.Invoke(stream, new object[] { "viewer-1", "switch during startup" });
        if (viewerReturns) add.Invoke(stream, new object[] { "viewer-2" });
        server.ReleaseSubscribe.TrySetResult();
        await opening.WaitAsync(TimeSpan.FromSeconds(3));
        if (viewerReturns)
            Assert.DoesNotContain(server.Requests, request => request.Method == "subscriptionChangeWeight");
        else
            Assert.Equal(1, (await server.WaitFor("subscriptionChangeWeight")).getInt("weight"));
    }

    private sealed class Listener : HTSConnectionListener
    {
        public void onMessage(HTSMessage response) { }
        public void onError(Exception ex) { }
    }

    internal sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly Channel<HTSMessage> _messages = Channel.CreateUnbounded<HTSMessage>();
        private readonly Task _server;
        public ConcurrentQueue<HTSMessage> Requests { get; } = new();
        public CancellationToken Token => _stop.Token;
        public TaskCompletionSource ReleaseSubscribe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Server(bool pauseSubscribe = false, bool emitFrame = false)
        {
            _listener.Start();
            _server = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(Token);
                        using var network = client.GetStream();
                        try
                        {
                            while (!_stop.IsCancellationRequested)
                            {
                                var header = new byte[4];
                                await network.ReadExactlyAsync(header, Token);
                                var size = BinaryPrimitives.ReadInt32BigEndian(header);
                                Assert.InRange(size, 0, 65536);
                                var frame = new byte[size + 4];
                                header.CopyTo(frame, 0);
                                await network.ReadExactlyAsync(frame.AsMemory(4), Token);
                                var request = HTSMessage.parse(frame, NullLogger<HTSMessage>.Instance);
                                Requests.Enqueue(request);
                                _messages.Writer.TryWrite(request);
                                if (pauseSubscribe && request.Method == "subscribe") await ReleaseSubscribe.Task.WaitAsync(Token);
                                var reply = new HTSMessage();
                                reply.putField("seq", request.GetField("seq"));
                                if (request.Method == "hello")
                                {
                                    reply.putField("htspversion", 44);
                                    reply.putField("challenge", new byte[32]);
                                }
                                await network.WriteAsync(reply.BuildBytes(), Token);
                                if (emitFrame && request.Method == "subscribe")
                                {
                                    var video = new Dictionary<string, object> { ["index"] = 0, ["type"] = "H264", ["width"] = 400, ["height"] = 200 };
                                    var start = new HTSMessage { Method = "subscriptionStart" };
                                    start.putField("subscriptionId", request.GetField("subscriptionId"));
                                    start.putField("streams", new ArrayList { video });
                                    start.putField("sourceinfo", new Dictionary<string, object> { ["network"] = "Loopback", ["mux"] = "Loopback mux", ["mux_uuid"] = "loopback-mux" });
                                    await network.WriteAsync(start.BuildBytes(), Token);
                                    var packet = new HTSMessage { Method = "muxpkt" };
                                    packet.putField("subscriptionId", request.GetField("subscriptionId"));
                                    packet.putField("stream", 0);
                                    packet.putField("frametype", (int)'I');
                                    packet.putField("pts", 0);
                                    packet.putField("payload", new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0, 0x1e, 0xab,
                                        0, 0, 0, 1, 0x68, 0xce, 0xdc, 0x80, 0, 0, 0, 1, 0x65, 0x88, 0x84, 0x21 });
                                    await network.WriteAsync(packet.BuildBytes(), Token);
                                }
                            }
                        }
                        catch (IOException) { }
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public async Task<HTSMessage> WaitFor(string method)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            while (true)
            {
                var message = await _messages.Reader.ReadAsync(timeout.Token);
                if (message.Method == method) return message;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _server;
            _stop.Dispose();
        }
    }
}
