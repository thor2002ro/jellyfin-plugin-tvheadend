using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class PluginTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    [Fact]
    public async Task AlreadyCancelledImageCallerDoesNotStartADownload()
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-cancelled-image-" + Guid.NewGuid().ToString("N"));
        var (_, imageEncoder) = ConfigureImageCache(root);
        var response = new MissingImageResponseHandler();
        try
        {
            using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance,
                new TestHttpClientFactory(new HttpClient(response)), imageEncoder);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => handler.CacheImageAsync("artwork/cancelled", null, cancellation.Token));
            Xunit.Assert.Equal(0, response.RequestCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancelledEpgImageCallerDoesNotRetainTheCompletedSharedDownload()
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-cancelled-epg-" + Guid.NewGuid().ToString("N"));
        var (_, imageEncoder) = ConfigureImageCache(root);
        var response = new BlockingImageResponseHandler();
        try
        {
            using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance,
                new TestHttpClientFactory(new HttpClient(response)), imageEncoder);
            using var cancellation = new CancellationTokenSource();
            var caller = handler.CacheImageAsync("artwork/cancelled-epg", null, cancellation.Token);
            await response.Started.WaitAsync(TimeSpan.FromSeconds(5));
            var downloads = (ConcurrentDictionary<string, Lazy<Task<string>>>)typeof(HTSConnectionHandler)
                .GetField("_imageDownloads", PrivateInstance)!.GetValue(handler)!;
            var shared = downloads.Values.Single().Value;
            await cancellation.CancelAsync();
            await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);
            response.Release();
            var path = await shared.WaitAsync(TimeSpan.FromSeconds(5));
            Xunit.Assert.True(File.Exists(path));
            Xunit.Assert.Empty(downloads);
            var cached = await handler.CacheImageAsync("artwork/cancelled-epg", null, CancellationToken.None);
            Xunit.Assert.Equal(path, cached.ImagePath);
            Xunit.Assert.Equal(1, response.RequestCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("denied", true)]
    [InlineData("timeout", true)]
    [InlineData("missing", false)]
    public void HtspArtworkConnectionFailuresStopTheRefreshCascade(string failure, bool expected)
    {
        Exception error = failure switch {
            "denied" => new UnauthorizedAccessException(),
            "timeout" => new TimeoutException(),
            _ => new IOException("Image not found") };
        var stop = typeof(HTSConnectionHandler).GetMethod("ShouldStopImageRefresh", PrivateStatic)!;
        Xunit.Assert.Equal(expected, (bool)stop.Invoke(null, new object[] { error }));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task HtspArtworkPublicCacheHandlesPermissionsAndLateOpen(bool denied, bool lateOpen, bool httpFailure, bool cancelAuthentication)
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-htsp-art-" + Guid.NewGuid().ToString("N"));
        var (plugin, imageEncoder) = ConfigureImageCache(root);
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        plugin.UpdateConfiguration(new PluginConfiguration {
            TVH_ServerName = "127.0.0.1", HTSP_Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            WebRoot = "/root", Username = "user", Password = "password" });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var hellos = 0;
        var closes = 0;
        var denials = 0;
        var authenticationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthentication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            using var network = client.GetStream();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var header = new byte[4];
                    await network.ReadExactlyAsync(header, stop.Token);
                    var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
                    Xunit.Assert.InRange(length, 0, 65536);
                    var frame = new byte[length + 4];
                    header.CopyTo(frame, 0);
                    await network.ReadExactlyAsync(frame.AsMemory(4), stop.Token);
                    var request = HTSMessage.parse(frame, NullLogger<HTSMessage>.Instance);
                    var reply = new HTSMessage();
                    reply.putField("seq", request.GetField("seq"));
                    if (request.Method == "authenticate" && cancelAuthentication)
                    {
                        authenticationStarted.TrySetResult();
                        await releaseAuthentication.Task.WaitAsync(stop.Token);
                    }
                    if (request.Method == "hello")
                    {
                        hellos++;
                        reply.putField("htspversion", 44);
                        reply.putField("webroot", "/root");
                        reply.putField("challenge", new byte[32]);
                    }
                    if (request.Method == "fileOpen")
                    {
                        if (lateOpen) await Task.Delay(TimeSpan.FromSeconds(6), stop.Token);
                        if (denied)
                        {
                            denials++;
                            reply.putField("noaccess", 1);
                        }
                        else if (request.getString("file") == "imagecache/43") reply.putField("error", "Image not found");
                        else
                        {
                            Xunit.Assert.Equal("imagecache/42", request.getString("file"));
                            reply.putField("id", 7);
                            reply.putField("size", png.Length);
                        }
                    }
                    if (request.Method == "fileRead") reply.putField("data", png);
                    if (request.Method == "fileClose") closes++;
                    await network.WriteAsync(reply.BuildBytes(), stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is IOException) { }
        });
        var http = new ArtworkHttpResponseHandler(png);
        try
        {
            using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance,
                new TestHttpClientFactory(new HttpClient(http)), imageEncoder);
            handler.BeginImageRefresh(["channel:42"]);
            if (cancelAuthentication)
            {
                using var cancelledCaller = new CancellationTokenSource();
                var caller = Task.Run(() => handler.CacheImageAsync("imagecache/42", null, cancelledCaller.Token));
                await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    await cancelledCaller.CancelAsync();
                    await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => caller.WaitAsync(TimeSpan.FromSeconds(1)));
                }
                finally
                {
                    releaseAuthentication.TrySetResult();
                    var downloads = (ConcurrentDictionary<string, Lazy<Task<string>>>)typeof(HTSConnectionHandler)
                        .GetField("_imageDownloads", PrivateInstance)!.GetValue(handler)!;
                    var shared = downloads.Values.FirstOrDefault()?.Value;
                    if (shared != null) await shared.WaitAsync(TimeSpan.FromSeconds(5));
                }
                return;
            }
            if (httpFailure)
            {
                http.Forbidden = true;
                var unavailable = await handler.CacheImageAsync("artwork/private.png", "channel:private", stop.Token);
                Xunit.Assert.Null(unavailable.ImagePath);
                http.Forbidden = false;
                var suppressed = await handler.CacheImageAsync("artwork/other.png", "channel:other", stop.Token);
                Xunit.Assert.Null(suppressed.ImagePath);
                Xunit.Assert.Equal(1, http.RequestCount);
            }
            var channel = await handler.CacheImageAsync("imagecache/42", "channel:42", stop.Token);
            if (lateOpen)
            {
                Xunit.Assert.Null(channel.ImagePath);
                var connection = (HTSConnectionAsync)typeof(HTSConnectionHandler).GetField("_htsConnection", PrivateInstance)!.GetValue(handler)!;
                Xunit.Assert.True(connection.needsRestart(), "An unacknowledged file open retained its metadata socket and server handle.");
                var independentHttp = await handler.CacheImageAsync("artwork/ready.png", "channel:http", stop.Token);
                Xunit.Assert.NotNull(independentHttp.ImagePath);
                return;
            }
            Xunit.Assert.NotNull(channel.ImagePath);
            Xunit.Assert.Equal(png, await File.ReadAllBytesAsync(channel.ImagePath, stop.Token));
            var programmes = await Task.WhenAll(Enumerable.Range(0, 50)
                .Select(_ => handler.CacheImageAsync("imagecache/42", null, stop.Token)));
            Xunit.Assert.Single(programmes.Select(p => p.ImagePath).Distinct());
            var programme = programmes[0];
            Xunit.Assert.NotNull(programme.ImagePath);
            var failed = await handler.CacheImageAsync("imagecache/43", "channel:42", stop.Token);
            Xunit.Assert.Equal(channel.ImagePath, failed.ImagePath);
            Xunit.Assert.Equal(1, hellos);
            Xunit.Assert.Equal(denied ? 0 : 2, closes);
            Xunit.Assert.Equal((denied ? 3 : 0) + (httpFailure ? 1 : 0), http.RequestCount);
            Xunit.Assert.Equal(denied ? 1 : 0, denials);
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            await server;
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("imagecache/42", "http://tvh:9981/root/imagecache/42")]
    [InlineData("/imagecache/42", "http://tvh:9981/root/imagecache/42")]
    [InlineData("/root/imagecache/42", "http://tvh:9981/root/imagecache/42")]
    [InlineData("//images.example/logo.png", "http://images.example/logo.png")]
    [InlineData("https://images.example/logo.png", "https://images.example/logo.png")]
    public void ArtworkUrlResolutionDoesNotDuplicateWebRootsOrRewriteExternalHosts(string supplied, string expected)
    {
        var resolve = typeof(HTSConnectionHandler).GetMethod("ResolveImageUrl", PrivateStatic,
            null, new[] { typeof(string), typeof(string) }, null)!;
        Xunit.Assert.Equal(expected, resolve.Invoke(null, new object[] { "http://tvh:9981/root", supplied }));
    }

    [Theory]
    [InlineData("http://tvh:9981/root/imagecache/42", "imagecache/42")]
    [InlineData("http://tvh:9981/root/imagecache/0042", "imagecache/42")]
    [InlineData("http://other:9981/root/imagecache/42", null)]
    [InlineData("http://tvh:9981/imagecache/42", null)]
    [InlineData("http://tvh:9981/root/imagecache/42/extra", null)]
    [InlineData("http://tvh:9981/root/imagecache/0", null)]
    [InlineData("http://tvh:9981/root/imagecache/42?ticket=secret", null)]
    public void HtspArtworkRoutingRespectsServerAndWebRoot(string url, string expected)
    {
        var route = typeof(HTSConnectionHandler).GetMethod("GetHtspImagePath", PrivateStatic);
        Xunit.Assert.NotNull(route);
        Xunit.Assert.Equal(expected, route.Invoke(null, new object[] { new Uri("http://tvh:9981/root"), new Uri(url) }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HtspArtworkReaderContinuesPartialReadsAndClosesHandle(bool knownSize)
    {
        var read = typeof(HTSConnectionHandler).GetMethod("ReadHtspImageFileAsync", PrivateStatic);
        Xunit.Assert.NotNull(read);
        var reads = 0;
        var closed = false;
        Task<HTSMessage> Send(HTSMessage request, CancellationToken token)
        {
            var reply = new HTSMessage();
            if (request.Method == "fileOpen")
            {
                Xunit.Assert.Equal("imagecache/42", request.getString("file"));
                reply.putField("id", new System.Numerics.BigInteger(7));
                if (knownSize) reply.putField("size", new System.Numerics.BigInteger(4));
            }
            else if (request.Method == "fileRead")
            {
                Xunit.Assert.Equal(7L, (long)request.GetField("id"));
                reply.putField("data", reads++ switch { 0 => new byte[] { 1 }, 1 => new byte[] { 2, 3, 4 }, _ => Array.Empty<byte>() });
            }
            else if (request.Method == "fileClose") closed = true;
            return Task.FromResult(reply);
        }
        var data = await (Task<byte[]>)read.Invoke(null, new object[] {
            "imagecache/42", (Func<HTSMessage, CancellationToken, Task<HTSMessage>>)Send, CancellationToken.None });
        Xunit.Assert.Equal(new byte[] { 1, 2, 3, 4 }, data);
        Xunit.Assert.True(closed);
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("truncated")]
    [InlineData("missing-data")]
    [InlineData("oversized-chunk")]
    [InlineData("cancelled")]
    public async Task HtspArtworkFailuresAlwaysCloseTheHandle(string failure)
    {
        var read = typeof(HTSConnectionHandler).GetMethod("ReadHtspImageFileAsync", PrivateStatic);
        Xunit.Assert.NotNull(read);
        using var cancellation = new CancellationTokenSource();
        var closed = false;
        async Task<HTSMessage> Send(HTSMessage request, CancellationToken token)
        {
            var reply = new HTSMessage();
            if (request.Method == "fileOpen")
            {
                reply.putField("id", new System.Numerics.BigInteger(7));
                if (failure == "oversize") reply.putField("size", new System.Numerics.BigInteger(20 * 1024 * 1024 + 1));
                if (failure == "truncated") reply.putField("size", new System.Numerics.BigInteger(4));
            }
            if (request.Method == "fileRead")
            {
                if (failure == "cancelled")
                {
                    await cancellation.CancelAsync();
                    return await Task.FromCanceled<HTSMessage>(token);
                }
                if (failure != "missing-data") reply.putField("data", failure == "oversized-chunk"
                    ? new byte[(int)(long)request.GetField("size") + 1] : Array.Empty<byte>());
            }
            if (request.Method == "fileClose")
            {
                Xunit.Assert.False(token.IsCancellationRequested);
                closed = true;
            }
            return reply;
        }
        Func<Task> act = () => (Task<byte[]>)read.Invoke(null, new object[] {
            "imagecache/42", (Func<HTSMessage, CancellationToken, Task<HTSMessage>>)Send, cancellation.Token });
        if (failure == "cancelled") await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        else await Xunit.Assert.ThrowsAsync<InvalidDataException>(act);
        Xunit.Assert.True(closed);
    }

    [Theory]
    [InlineData("en", "eng")]
    [InlineData(" RO ", "ron")]
    [InlineData("en-US", "eng")]
    [InlineData(" FRA ", "fra")]
    [InlineData("ger", "ger")]
    [InlineData("und", null)]
    [InlineData("123", null)]
    [InlineData("", null)]
    public void AudioAndSubtitleLanguagesMatchJellyfinAndTransportDescriptors(string supplied, string expected)
    {
        var (muxer, sources) = CreateMuxer("AAC", "DVBSUB");
        foreach (var source in sources)
        {
            source.GetType().GetProperty("Language")!.SetValue(source, supplied);
            var build = typeof(HtspLiveStream).GetMethod("CreateMediaStream", PrivateStatic)!;
            var track = (MediaStream)build.Invoke(null, new[] { source, (object)0 })!;
            Xunit.Assert.Equal(expected, track.Language);
            var descriptors = (byte[])muxer.GetType().GetMethod("BuildDescriptors", PrivateStatic)!
                .Invoke(null, new[] { source, source.GetType().GetProperty("Codec")!.GetValue(source) })!;
            if (expected != null)
            {
                Xunit.Assert.Equal(new byte[] { 0x0A, 4 }, descriptors.Take(2));
                Xunit.Assert.Equal(expected, System.Text.Encoding.ASCII.GetString(descriptors, 2, 3));
            }
        }
        var bytes = (byte[])muxer.GetType().GetMethod("GetIsoLanguageBytes", PrivateStatic)!
            .Invoke(null, new object[] { supplied })!;
        Xunit.Assert.Equal(expected ?? "und", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void HtspVideoUsesBroadcastTimingInsteadOfFixedInterlacing()
    {
        var (_, source) = CreateH264Muxer();
        var duration = source.GetType().GetProperty("Duration");
        Xunit.Assert.NotNull(duration);
        duration.SetValue(source, 3000);
        source.GetType().GetProperty("AspectNum")!.SetValue(source, 16);
        source.GetType().GetProperty("AspectDen")!.SetValue(source, 9);
        var build = typeof(HtspLiveStream).GetMethod("CreateMediaStream", PrivateStatic)!;
        var video = (MediaStream)build.Invoke(null, new[] { source, (object)0 })!;
        Xunit.Assert.False(video.IsInterlaced);
        Xunit.Assert.Equal(30f, video.RealFrameRate);
        Xunit.Assert.Equal("16:9", video.AspectRatio);
    }

    [Fact]
    public void HtspProbeEnrichesMetadataWithoutReplacingOrRenumberingTracks()
    {
        var merge = typeof(HtspLiveStream).GetMethod("MergeProbeMetadata", PrivateStatic);
        Xunit.Assert.NotNull(merge);
        var video = new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc" };
        var audio = new MediaStream { Index = 2, Type = MediaStreamType.Audio, Language = "eng", Title = "Audio description" };
        var silentAudio = new MediaStream { Index = 3, Type = MediaStreamType.Audio, Language = "und" };
        var subtitle = new MediaStream { Index = 4, Type = MediaStreamType.Subtitle, Codec = "dvbsub" };
        var unprobedAudio = new MediaStream { Index = 5, Type = MediaStreamType.Audio, Language = "fra" };
        var streams = new List<MediaStream> { video, audio, silentAudio, subtitle, unprobedAudio };
        var probe = new List<MediaStream>
        {
            new() { Index = 0, Type = MediaStreamType.Video, IsInterlaced = false, BitDepth = 10,
                ColorTransfer = "smpte2084", Profile = "Main 10", RealFrameRate = 25, BitRate = 8000000 },
            new() { Index = 2, Type = MediaStreamType.Audio, Language = "fra" },
            new() { Index = 3, Type = MediaStreamType.Audio, ChannelLayout = "stereo", BitRate = 128000, Language = "ro" },
            new() { Index = 4, Type = MediaStreamType.Subtitle, Language = "deu" }
        };
        merge.Invoke(null, new object[] { streams, probe });
        Xunit.Assert.Equal(5, streams.Count);
        Xunit.Assert.Same(unprobedAudio, streams[4]);
        Xunit.Assert.Equal(10, video.BitDepth);
        Xunit.Assert.Equal("smpte2084", video.ColorTransfer);
        Xunit.Assert.Equal(25f, video.RealFrameRate);
        Xunit.Assert.Equal("eng", audio.Language);
        Xunit.Assert.Equal("Audio description", audio.Title);
        Xunit.Assert.Null(audio.BitRate);
        Xunit.Assert.Equal(128000, silentAudio.BitRate);
        Xunit.Assert.Equal("ron", silentAudio.Language);
        Xunit.Assert.Equal("deu", subtitle.Language);
        Xunit.Assert.Equal(4, subtitle.Index);
    }

    [Fact]
    public async Task ChannelTagsFollowUpdatesDeletionAndReconnect()
    {
        var helper = new TVHeadEnd.DataHelper.ChannelDataHelper(NullLogger<TVHeadEnd.DataHelper.ChannelDataHelper>.Instance);
        var update = helper.GetType().GetMethod("UpdateTag");
        Xunit.Assert.NotNull(update);
        HTSMessage Tag(string method, int id, string name = null)
        {
            var message = new HTSMessage { Method = method };
            message.putField("tagId", new System.Numerics.BigInteger(id));
            message.putField("tagName", name);
            return message;
        }
        update.Invoke(helper, new object[] { Tag("tagAdd", 1, "Sports") });
        update.Invoke(helper, new object[] { Tag("tagAdd", 2, "sports") });
        var service = new HTSMessage();
        service.putField("type", "hdtv");
        var channel = new HTSMessage();
        channel.putField("channelId", new System.Numerics.BigInteger(99));
        channel.putField("channelNumber", new System.Numerics.BigInteger(1));
        channel.putField("services", new System.Collections.ArrayList { service });
        channel.putField("tags", new System.Collections.ArrayList {
            new System.Numerics.BigInteger(1), new System.Numerics.BigInteger(2), new System.Numerics.BigInteger(500) });
        helper.Add(channel);
        var info = (await helper.BuildChannelInfos(CancellationToken.None)).Single();
        Xunit.Assert.Equal(new[] { "Sports" }, info.Tags);
        update.Invoke(helper, new object[] { Tag("tagUpdate", 1, "News") });
        update.Invoke(helper, new object[] { Tag("tagDelete", 2) });
        info = (await helper.BuildChannelInfos(CancellationToken.None)).Single();
        Xunit.Assert.Equal(new[] { "News" }, info.Tags);
        helper.Clean();
        helper.Add(channel);
        info = (await helper.BuildChannelInfos(CancellationToken.None)).Single();
        Xunit.Assert.Empty(info.Tags);
    }

    [Fact]
    public async Task HtspProbeReadsBufferedOutputAndDeletesItsTemporaryFile()
    {
        using var stream = CreateStream(Guid.NewGuid().ToString("N"));
        stream.MediaSource.MediaStreams = new List<MediaStream> {
            new() { Index = 0, Type = MediaStreamType.Video, Codec = "hevc" } };
        var queue = (Queue<byte[]>)GetField(stream, "_startupCache");
        queue.Enqueue(new byte[188 * 8000]);
        SetField(stream, "_startupCacheBytes", 188L * 8000);
        SetField(stream, "_startupCacheKeyframeAligned", true);
        var cacheKey = Guid.NewGuid().ToString("N");
        SetField(stream, "_metadataCacheKey", cacheKey);
        SetField(stream, "_metadataFormat", "format-1");
        var probe = typeof(HtspLiveStream).GetMethod("ProbeStreamMetadataAsync", PrivateInstance);
        Xunit.Assert.NotNull(probe);
        string sampledPath = null;
        var calls = 0;
        var encoder = CreateProxy<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>((method, args) =>
        {
            if (method.Name != "GetMediaInfo") return GetDefault(method.ReturnType);
            calls++;
            var request = (MediaBrowser.Controller.MediaEncoding.MediaInfoRequest)args[0];
            sampledPath = request.MediaSource.Path;
            Xunit.Assert.True(File.Exists(sampledPath));
            Xunit.Assert.Equal(MediaBrowser.Model.MediaInfo.MediaProtocol.File, request.MediaSource.Protocol);
            Xunit.Assert.Equal(0, GetInt(stream, "_activeStreamReaders"));
            return Task.FromResult(new MediaBrowser.Model.MediaInfo.MediaInfo {
                MediaStreams = new List<MediaStream> { new() {
                    Index = 0, Type = MediaStreamType.Video, BitDepth = 10, ColorTransfer = "smpte2084" } } });
        });
        SetField(stream, "_mediaEncoder", encoder);
        await (Task)probe.Invoke(stream, new object[] { CancellationToken.None });
        Xunit.Assert.NotNull(sampledPath);
        Xunit.Assert.False(File.Exists(sampledPath));
        Xunit.Assert.Equal(10, stream.MediaSource.MediaStreams[0].BitDepth);
        Xunit.Assert.Single(queue);
        await (Task)probe.Invoke(stream, new object[] { CancellationToken.None });
        Xunit.Assert.Equal(1, calls);
        SetField(stream, "_metadataFormat", "format-2");
        await (Task)probe.Invoke(stream, new object[] { CancellationToken.None });
        Xunit.Assert.Equal(2, calls);
        var cache = (System.Collections.IDictionary)typeof(HtspLiveStream).GetField("MetadataCache", PrivateStatic)!.GetValue(null)!;
        var value = ((string Format, DateTime ExpiresUtc, IReadOnlyList<MediaStream> Streams))cache[cacheKey];
        cache[cacheKey] = (value.Format, DateTime.UtcNow.AddMinutes(-1), value.Streams);
        await (Task)probe.Invoke(stream, new object[] { CancellationToken.None });
        Xunit.Assert.Equal(3, calls);
        cache.Remove(cacheKey);
    }

    [Fact]
    public async Task HtspProbeFailurePreservesTracksAndCancellationStopsTheProbe()
    {
        using var stream = CreateStream(Guid.NewGuid().ToString("N"));
        var tracks = new List<MediaStream> { new() { Index = 0, Type = MediaStreamType.Video, Codec = "h264" } };
        stream.MediaSource.MediaStreams = tracks;
        ((Queue<byte[]>)GetField(stream, "_startupCache")).Enqueue(new byte[188 * 8000]);
        SetField(stream, "_startupCacheBytes", 188L * 8000);
        SetField(stream, "_startupCacheKeyframeAligned", true);
        var probe = typeof(HtspLiveStream).GetMethod("ProbeStreamMetadataAsync", PrivateInstance)!;
        string path = null;
        var waitForCancellation = false;
        var encoder = CreateProxy<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>((method, args) =>
        {
            if (method.Name != "GetMediaInfo") return GetDefault(method.ReturnType);
            path = ((MediaBrowser.Controller.MediaEncoding.MediaInfoRequest)args[0]).MediaSource.Path;
            return waitForCancellation
                ? new TaskCompletionSource<MediaBrowser.Model.MediaInfo.MediaInfo>().Task
                : Task.FromException<MediaBrowser.Model.MediaInfo.MediaInfo>(new IOException("probe unavailable"));
        });
        SetField(stream, "_mediaEncoder", encoder);
        await (Task)probe.Invoke(stream, new object[] { CancellationToken.None });
        Xunit.Assert.Same(tracks, stream.MediaSource.MediaStreams);
        Xunit.Assert.False(File.Exists(path));
        waitForCancellation = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => (Task)probe.Invoke(stream, new object[] { cancellation.Token }));
        Xunit.Assert.False(File.Exists(path));
        Xunit.Assert.Equal(0, GetInt(stream, "_activeStreamReaders"));
    }

    [Fact]
    public void StaleSharedHubCannotRemoveReplacement()
    {
        var sharedHubsField = typeof(HtspLiveStream).GetField("SharedHubsByChannelId", PrivateStatic)!;
        var sharedHubs = (ConcurrentDictionary<string, HtspLiveStream>)sharedHubsField.GetValue(null)!;
        var removeSharedHub = typeof(HtspLiveStream).GetMethod("RemoveSharedHub", PrivateStatic)!;
        var channelId = Guid.NewGuid().ToString("N");
        using var staleHub = CreateStream(channelId);
        using var replacementHub = CreateStream(channelId);
        sharedHubs[channelId] = replacementHub;

        Assert(!(bool)removeSharedHub.Invoke(null, new object[] { channelId, staleHub })!, "A stale hub removed its replacement.");
        Assert(ReferenceEquals(sharedHubs[channelId], replacementHub), "The replacement hub was not preserved.");
        Assert((bool)removeSharedHub.Invoke(null, new object[] { channelId, replacementHub })!, "The current hub could not remove itself.");
    }

    [Fact]
    public async Task ClosingPlaybackReleasesReadersAndRejectsLateReaders()
    {
        using (var stream = CreateStream(Guid.NewGuid().ToString("N")))
        {
            using var reader = stream.GetStream();
            Assert(GetInt(stream, "_activeStreamReaders") == 1, "Creating a reader did not increment its count.");
            Assert(GetPlaybackReferenceCount(stream) == 1, "Creating a reader did not retain playback ownership.");

            await stream.Close();
            Assert(GetInt(stream, "_activeStreamReaders") == 0, "Closing playback did not remove owned readers.");
            Assert(GetPlaybackReferenceCount(stream) == 0, "Closing playback leaked its shared reference.");

            using var lateReader = stream.GetStream();
            Assert(ReferenceEquals(lateReader, Stream.Null), "Closed playback created a late reader.");
            Assert(GetPlaybackReferenceCount(stream) == 0, "A late reader restored closed playback ownership.");
        }
    }

    [Fact]
    public async Task DuplicateReleaseDoesNotResetIdleCloseTimer()
    {
        var sharedHubsField = typeof(HtspLiveStream).GetField("SharedHubsByChannelId", PrivateStatic)!;
        var sharedHubs = (ConcurrentDictionary<string, HtspLiveStream>)sharedHubsField.GetValue(null)!;
        var releasePlayback = typeof(HtspLiveStream).GetMethod("ReleaseSharedPlaybackReference", PrivateInstance)!;
        var registeredChannelId = Guid.NewGuid().ToString("N");
        var registeredHub = CreateStream(registeredChannelId);
        sharedHubs[registeredChannelId] = registeredHub;
        SetField(registeredHub, "_registeredAsSharedHub", true);
        using (registeredHub.GetStream())
        {
            await registeredHub.Close();
            var idleClose = GetField(registeredHub, "_sharedHubIdleCloseCancellationTokenSource");
            releasePlayback.Invoke(registeredHub, new object[] { registeredHub.UniqueId, "duplicate close" });
            Assert(ReferenceEquals(idleClose, GetField(registeredHub, "_sharedHubIdleCloseCancellationTokenSource")), "Duplicate release reset the idle-close timer.");
        }

        registeredHub.Dispose();
        Assert(GetInt(registeredHub, "_closeStarted") == 1, "Dispose did not close the unused producer.");
        Assert(!sharedHubs.ContainsKey(registeredChannelId), "Dispose left the shared hub registered.");
    }

    [Fact]
    public async Task DisposedProducerReleasesResourcesAfterFinalSharedPlaybackCloses()
    {
        var attachPlayback = typeof(HtspLiveStream).GetMethod("TryAttachPlaybackToProducer", PrivateInstance)!;
        var closeProducerNow = typeof(HtspLiveStream).GetMethod("CloseProducerNow", PrivateInstance)!;
        var deferredHub = CreateStream(Guid.NewGuid().ToString("N"));
        var deferredPlayback = CreateStream(Guid.NewGuid().ToString("N"));
        var deferredOwnerReader = deferredHub.GetStream();
        try
        {
            Assert(
                (bool)attachPlayback.Invoke(deferredPlayback, new object[] { deferredHub, false })!,
                "The deferred-disposal check could not attach a shared playback.");

            deferredHub.Dispose();
            Assert(GetInt(deferredHub, "_closeStarted") == 0, "Disposing the owner closed a producer that still had a shared playback.");

            await deferredPlayback.Close();
            Assert(
                await (Task<bool>)closeProducerNow.Invoke(deferredHub, new object[] { "last shared playback closed", true })!,
                "The producer did not close after its final shared playback left.");
            Assert(
                ThrowsObjectDisposed(() => ProbeSemaphore((SemaphoreSlim)GetField(deferredHub, "_connectionSemaphore"))),
                "Deferred producer disposal left its connection semaphore open.");
            Assert(
                ThrowsObjectDisposed(() => _ = ((CancellationTokenSource)GetField(deferredHub, "_lifetimeCancellationTokenSource")).Token),
                "Deferred producer disposal left its cancellation source open.");
        }
        finally
        {
            await deferredOwnerReader.DisposeAsync();
            deferredPlayback.Dispose();
            deferredHub.Dispose();
        }
    }

    [Fact]
    public async Task DisposingReaderReleasesReaderCount()
    {
        using (var stream = CreateStream(Guid.NewGuid().ToString("N")))
        {
            var reader = stream.GetStream();
            Assert(GetInt(stream, "_activeStreamReaders") == 1, "Creating a reader did not increment its count.");
            await reader.DisposeAsync();
            Assert(GetInt(stream, "_activeStreamReaders") == 0, "Disposing a reader leaked its count.");
            await stream.Close();
        }
    }

    [Fact]
    public void QueueDropsAreReportedWithoutForcingKeyframeWait()
    {
        var logQueueStatus = typeof(HtspLiveStream).GetMethod("LogQueueStatus", PrivateInstance)!;
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
            Assert(
                ((string)GetField(stream, "_lastVideoDamageReason")).Contains("queue dropped frames", StringComparison.Ordinal),
                "Queue damage reason was not retained.");
        }
    }

    [Fact]
    public async Task ConcurrentReaderCreationAndPlaybackCloseDoNotLeakOwnership()
    {
        for (var i = 0; i < 100; i++)
        {
            using var stream = CreateStream(Guid.NewGuid().ToString("N"));
            Stream reader = null;
            await Task.WhenAll(
                Task.Run(() => reader = stream.GetStream()),
                Task.Run(() => stream.Close()));
            await reader.DisposeAsync();

            Assert(GetInt(stream, "_activeStreamReaders") == 0, "Concurrent close leaked a reader count.");
            Assert(GetPlaybackReferenceCount(stream) == 0, "Concurrent close leaked playback ownership.");
            Assert(ReferenceEquals(stream.GetStream(), Stream.Null), "Concurrent close allowed a late reader.");
        }
    }

    [Fact]
    public async Task ConcurrentPlaybackAttachAndCloseDoNotLeakOwnership()
    {
        var attachPlayback = typeof(HtspLiveStream).GetMethod("TryAttachPlaybackToProducer", PrivateInstance)!;
        for (var i = 0; i < 100; i++)
        {
            using var hub = CreateStream(Guid.NewGuid().ToString("N"));
            using var playback = CreateStream(Guid.NewGuid().ToString("N"));
            await Task.WhenAll(
                Task.Run(() =>
                {
                    try
                    {
                        attachPlayback.Invoke(playback, new object[] { hub, false });
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is ObjectDisposedException)
                    {
                    }
                }),
                Task.Run(() => playback.Close()));

            Assert(GetPlaybackReferenceCount(hub) == 0, "Concurrent open attached playback after close.");
        }
    }

    [Fact]
    public void JellyfinReferencesMatchTargetAbi() => AssertSharedJellyfinReferencesMatchTargetAbi();

    [Fact]
    public void MuxerDoesNotRewriteDuplicateVideoDts() => AssertMuxerDoesNotRewriteDuplicateVideoDts();

    [Fact]
    public void MuxerMarksConfirmedSourceClockJump() => AssertMuxerMarksConfirmedSourceClockJump();

    [Fact]
    public void StartupCacheKeepsInitialBufferClockAcrossKeyframes() => AssertStartupCacheKeepsInitialBufferClockAcrossKeyframes();

    [Fact]
    public void DefaultQueueDepthIsCentralized() => AssertDefaultQueueDepthIsCentralized();

    [Fact]
    public void RuntimeStatusKeepsRunningChannelCompatibilityAliases() => AssertRuntimeStatusKeepsRunningChannelCompatibilityAliases();

    [Fact]
    public void ImagesAreAuthenticatedAndRefreshedLocally() => AssertImagesAreAuthenticatedAndRefreshedLocally();

    [Fact]
    public void StoredChannelMetadataIsReconciled() => AssertStoredChannelMetadataIsReconciled();

    [Fact]
    public void PublicImageCacheFlowCoalescesAndPrunes() => AssertPublicImageCacheFlow();

    [Fact]
    public async Task ConcurrentImageRequestsDoNotRetainCompletedOperation()
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-concurrent-image-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (plugin, imageEncoder) = ConfigureImageCache(root);
            var responseHandler = new BlockingImageResponseHandler();
            using var handler = new HTSConnectionHandler(
                NullLoggerFactory.Instance,
                new TestHttpClientFactory(new HttpClient(responseHandler)),
                imageEncoder);
            handler.BeginImageRefresh(["channel:concurrent"]);

            var first = handler.CacheImageAsync(
                "imagecache/concurrent",
                "channel:concurrent",
                CancellationToken.None);
            await responseHandler.Started.WaitAsync(TimeSpan.FromSeconds(5));
            var second = handler.CacheImageAsync(
                "imagecache/concurrent",
                "channel:concurrent",
                CancellationToken.None);
            responseHandler.Release();

            var results = await Task.WhenAll(first, second);
            Assert(responseHandler.RequestCount == 1, "Concurrent image callers started duplicate TVHeadend downloads.");
            Assert(results[0].ImagePath == results[1].ImagePath, "Concurrent image callers received different cache paths.");
            Assert(File.Exists(results[0].ImagePath), "The shared image download did not create a local file.");
            Assert(GetPrivateCollectionCount(handler, "_imageDownloads") == 0, "A completed concurrent image operation remained retained.");
            Assert(results[0].ImagePath.StartsWith(plugin.ImageCachePath, StringComparison.Ordinal), "The shared image was cached outside the plugin image directory.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

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

    static void ProbeSemaphore(SemaphoreSlim semaphore)
    {
        if (semaphore.Wait(0))
        {
            semaphore.Release();
        }
    }

    static bool ThrowsObjectDisposed(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    static void AssertSharedJellyfinReferencesMatchTargetAbi()
    {
        var buildManifestPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "build.yaml"));
        var buildManifest = File.ReadAllText(buildManifestPath);
        var targetAbiMatch = System.Text.RegularExpressions.Regex.Match(buildManifest, "(?m)^targetAbi:\\s*\"(?<version>[^\"]+)\"\\s*$");
        Assert(targetAbiMatch.Success, "The build manifest does not declare targetAbi.");

        var targetAbi = Version.Parse(targetAbiMatch.Groups["version"].Value);
        var sharedReferences = typeof(Plugin).Assembly.GetReferencedAssemblies()
            .Where(reference => reference.Name is "MediaBrowser.Common" or "MediaBrowser.Controller" or "MediaBrowser.Model")
            .ToArray();

        Assert(sharedReferences.Length == 3, "The plugin's Jellyfin shared-library references could not be identified.");
        foreach (var reference in sharedReferences)
        {
            Assert(
                reference.Version == targetAbi,
                $"The plugin references {reference.Name} {reference.Version}, but build.yaml declares target ABI {targetAbi}.");
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
        CancellationToken.None,
        null!
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

    static int GetPrivateCollectionCount(object instance, string fieldName)
    {
        var collection = instance.GetType().GetField(fieldName, PrivateInstance)!.GetValue(instance)!;
        return (int)collection.GetType().GetProperty("Count")!.GetValue(collection)!;
    }

    static (Plugin Plugin, IImageEncoder ImageEncoder) ConfigureImageCache(string root)
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

        var imageEncoder = CreateProxy<IImageEncoder>((method, _) =>
            method.Name == nameof(IImageEncoder.GetImageSize)
                ? new ImageDimensions(1, 1)
                : GetDefault(method.ReturnType));
        return (plugin, imageEncoder);
    }

    static void AssertPublicImageCacheFlow()
    {
        var root = Path.Combine(Path.GetTempPath(), "tvheadend-public-image-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (plugin, imageEncoder) = ConfigureImageCache(root);

            var responseHandler = new ImageResponseHandler();
            var httpClient = new HttpClient(responseHandler);
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
                "artwork/42",
                "channel:42",
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(File.Exists(first.ImagePath), "The public cache flow did not create a local image.");
            Assert(responseHandler.SawAuthorization, "The public cache flow omitted TVHeadend authentication.");
            Assert(GetPrivateCollectionCount(handler, "_imageDownloads") == 0, "A successful image operation remained retained.");

            var requestCount = responseHandler.RequestCount;
            handler.BeginImageRefresh(["channel:42"]);
            var second = handler.CacheImageAsync(
                "artwork/42",
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
                "artwork/404",
                "channel:404",
                CancellationToken.None).GetAwaiter().GetResult();
            missingHandler.CacheImageAsync(
                "artwork/404",
                "channel:404",
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(missing.ImagePath is null, "A missing TVHeadend image produced a cache path.");
            Assert(missingResponse.RequestCount == 1, "A missing image was fetched repeatedly in one guide refresh.");
            Assert(GetPrivateCollectionCount(missingHandler, "_imageDownloads") == 1, "A missing image result was not retained for its guide refresh.");
            missingHandler.BeginImageRefresh(["channel:404"]);
            missingHandler.CacheImageAsync(
                "artwork/404",
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
                "artwork/99",
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
                "artwork/99",
                "channel:99",
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(File.Exists(recovered.ImagePath), "A canceled caller prevented the shared cache download from completing.");
            Assert(blockingResponse.RequestCount == 1, "Retrying after caller cancellation duplicated the TVHeadend download.");
            Assert(GetPrivateCollectionCount(blockingHandler, "_imageDownloads") == 0, "A completed image operation remained retained after its first caller canceled.");

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
}

sealed class ArtworkHttpResponseHandler(byte[] image) : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    public bool Forbidden { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (Forbidden) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/43", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
    }
}

public sealed class ReleasePackagingTests
{
    [Fact]
    public void ReleaseBuildProducesInstallableZip()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var configuration = Directory.GetParent(AppContext.BaseDirectory)!.Parent!.Name;
        if (!string.Equals(configuration, "Release", StringComparison.Ordinal))
        {
            return;
        }

        var releaseDirectory = Path.Combine(repositoryRoot, "TVHeadEnd", "bin", configuration, "net10.0");
        var archives = Directory.GetFiles(releaseDirectory, "TVHeadEnd_*.zip");

        var archivePath = Assert.Single(archives);
        using var archive = ZipFile.OpenRead(archivePath);
        var plugin = Assert.Single(archive.Entries);
        Assert.Equal("TVHeadEnd.dll", plugin.FullName);
        Assert.True(plugin.Length > 0, "The packaged plugin DLL is empty.");
    }
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
