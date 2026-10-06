using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.Configuration;
using TVHeadEnd.HTSP;
using Xunit;

public class ConnectionTestTests
{
    [Theory]
    [InlineData("Htsp", "ok", true)]
    [InlineData("HttpTicket", "ok", true)]
    [InlineData("HttpBasic", "ok", true)]
    [InlineData("HttpTicket", "http-denied", false)]
    [InlineData("HttpBasic", "http-denied", false)]
    [InlineData("HttpTicket", "no-channels", false)]
    [InlineData("Htsp", "auth-denied", false)]
    [InlineData("Htsp", "protocol", false)]
    [InlineData("Htsp", "auth-timeout", false)]
    [InlineData("Htsp", "streaming-denied", false)]
    [InlineData("HttpTicket", "ticket-denied", false)]
    [InlineData("HttpTicket", "http-redirect", false)]
    [InlineData("HttpBasic", "http-tls", false)]
    [InlineData("HttpBasic", "http-login", false)]
    [InlineData("HttpTicket", "http-plain", false)]
    [InlineData("HttpTicket", "http-no-type", false)]
    [InlineData("HttpTicket", "no-webroot", true)]
    [InlineData("HttpBasic", "cancel-http", false)]
    [InlineData("Htsp", "cancel-auth", false)]
    public async Task TestsSelectedTransportWithAnIndependentBoundedConnection(string method, string failure, bool expected)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ticketRequests = 0;
        var subscriptions = 0;
        var authenticationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    var size = BinaryPrimitives.ReadInt32BigEndian(header);
                    Assert.InRange(size, 0, 65536);
                    var frame = new byte[size + 4];
                    header.CopyTo(frame, 0);
                    await network.ReadExactlyAsync(frame.AsMemory(4), stop.Token);
                    var request = HTSMessage.parse(frame, NullLogger<HTSMessage>.Instance);
                    var reply = new HTSMessage();
                    reply.putField("seq", request.GetField("seq"));
                    if (request.Method == "hello")
                    {
                        reply.putField("htspversion", failure == "protocol" ? 18 : 44);
                        reply.putField("challenge", new byte[32]);
                        if (failure != "no-webroot") reply.putField("webroot", "/root");
                    }
                    if (request.Method == "authenticate" && failure == "auth-denied") reply.putField("noaccess", 1);
                    if (request.Method == "authenticate" && failure is "auth-timeout" or "cancel-auth")
                    {
                        authenticationStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
                    }
                    if (request.Method == "getSysTime" && failure == "streaming-denied") reply.putField("noaccess", 1);
                    if (request.Method == "subscribe") subscriptions++;
                    if (request.Method == "getTicket")
                    {
                        ticketRequests++;
                        Assert.Equal(42, request.getInt("channelId"));
                        reply.putField("path", "/stream/channelid/42");
                        reply.putField("ticket", "secret+/=");
                        if (failure == "ticket-denied") reply.putField("noaccess", 1);
                    }
                    await network.WriteAsync(reply.BuildBytes(), stop.Token);
                    if (request.Method == "enableAsyncMetadata")
                    {
                        Assert.Equal(0, request.getInt("epg"));
                        if (failure != "no-channels")
                        {
                            var channel = new HTSMessage { Method = "channelAdd" };
                            channel.putField("channelId", 42);
                            await network.WriteAsync(channel.BuildBytes(), stop.Token);
                        }
                        await network.WriteAsync(new HTSMessage { Method = "initialSyncCompleted" }.BuildBytes(), stop.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException) { }
        });
        var http = new TestHttpHandler(method, failure);
        try
        {
            var controller = new PluginConnectionTestController(NullLoggerFactory.Instance, new HttpFactory(http));
            var configuration = new PluginConfiguration {
                TVH_ServerName = "127.0.0.1", HTSP_Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                HTTP_Port = 9981, UseHttps = true, WebRoot = "ignored", StreamingMethod = method,
                Username = "user", Password = "password" };
            if (failure is "cancel-auth" or "cancel-http")
            {
                using var callerCancellation = new CancellationTokenSource();
                var test = controller.TestConnection(configuration, callerCancellation.Token);
                await (failure == "cancel-auth" ? authenticationStarted.Task : http.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
                await callerCancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.WaitAsync(TimeSpan.FromSeconds(2)));
                return;
            }
            var result = await controller.TestConnection(configuration, stop.Token);
            Assert.Equal(expected, result.Success);
            Assert.DoesNotContain("password", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", result.Message, StringComparison.Ordinal);
            Assert.Equal(0, subscriptions);
            Assert.Equal(method == "Htsp" || failure is "no-channels" or "ticket-denied" ? 0 : 1, http.Requests);
            Assert.Equal(method != "Htsp" && failure != "no-channels" ? 1 : 0, ticketRequests);
            Assert.Equal("ignored", configuration.WebRoot);
            if (failure == "auth-timeout") Assert.Contains("timeout", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            await server;
        }
    }

    [Fact]
    public async Task InvalidSettingsFailBeforeNetworkWorkAndCallerCancellationPropagates()
    {
        var http = new TestHttpHandler("Htsp", "ok");
        var controller = new PluginConnectionTestController(NullLoggerFactory.Instance, new HttpFactory(http));
        var result = await controller.TestConnection(new PluginConfiguration { HTSP_Port = 0 }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(0, http.Requests);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.TestConnection(new PluginConfiguration(), cancellation.Token));
    }

    private sealed class TestHttpHandler(string method, string failure) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (failure == "cancel-http")
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (failure == "http-tls") throw new HttpRequestException("TLS failure with sensitive URI");
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal(failure == "no-webroot" ? "/stream/channelid/42" : "/root/stream/channelid/42", request.RequestUri.AbsolutePath);
            Assert.Contains("weight=1", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.True(request.Headers.ConnectionClose);
            if (method == "HttpBasic")
            {
                Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
                Assert.Equal("dXNlcjpwYXNzd29yZA==", request.Headers.Authorization.Parameter);
                Assert.DoesNotContain("ticket=", request.RequestUri.Query, StringComparison.Ordinal);
            }
            else
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Contains("ticket=secret%2B%2F%3D", request.RequestUri.Query, StringComparison.Ordinal);
            }
            var response = new HttpResponseMessage(failure == "http-denied" ? HttpStatusCode.Forbidden
                : failure == "http-redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK)
            { Content = new StringContent(string.Empty, System.Text.Encoding.UTF8,
                failure == "http-login" ? "text/html" : failure == "http-plain" ? "text/plain" : "video/mp2t") };
            if (failure == "http-no-type") response.Content.Headers.ContentType = null;
            return response;
        }
    }

    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }
}
