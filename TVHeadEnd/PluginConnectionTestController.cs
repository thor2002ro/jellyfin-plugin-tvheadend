using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Configuration;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;

namespace TVHeadEnd;

public sealed record ConnectionTestResult(bool Success, string Message);

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("TVHeadEnd/Configuration/TestConnection")]
public sealed class PluginConnectionTestController(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory) : ControllerBase
{
    public const string HttpClientName = "TVHeadEndConnectionTest";
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(3);

    [HttpPost]
    public async Task<ConnectionTestResult> TestConnection([FromBody] PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var method = StreamingMethods.GetEffective(configuration.StreamingMethod);
        var host = configuration.TVH_ServerName?.Trim();
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown
            || configuration.HTSP_Port is < 1 or > 65535
            || (method != StreamingMethods.Htsp && configuration.HTTP_Port is < 1 or > 65535))
            return new(false, "Enter a valid hostname and ports between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(configuration.Username) || string.IsNullOrWhiteSpace(configuration.Password))
            return new(false, "Enter the TVHeadend credentials before testing.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var stage = "HTSP";
        var listener = new TestListener();
        using var connection = new HTSConnectionAsync(listener, "Jellyfin-TVHeadend-Test", "1", loggerFactory);
        try
        {
            var authenticated = await Task.Run(() =>
            {
                connection.open(host, configuration.HTSP_Port, token, maxAttempts: 1);
                return connection.authenticate(configuration.Username.Trim(), configuration.Password.Trim(), false, token, RpcTimeout, throwOnTimeout: true);
            }, token).ConfigureAwait(false);
            if (!authenticated)
            {
                var protocol = connection.getServerProtocolVersion();
                return new(false, protocol < HTSMessage.HTSP_MIN_SERVER_VERSION
                    ? $"HTSP protocol {protocol} is unsupported; version {HTSMessage.HTSP_MIN_SERVER_VERSION} or newer is required."
                    : "HTSP authentication failed. Check credentials and HTSP streaming permission.");
            }

            await RequestAsync(connection, new HTSMessage { Method = "getSysTime" }, token).ConfigureAwait(false);
            if (method == StreamingMethods.Htsp)
                return new(true, $"HTSP authentication and streaming access succeeded (protocol {connection.getNegotiatedProtocolVersion()}).");

            var metadata = new HTSMessage { Method = "enableAsyncMetadata" };
            metadata.putField("epg", 0);
            await RequestAsync(connection, metadata, token).ConfigureAwait(false);
            var channelId = await listener.Channel.Task.WaitAsync(RpcTimeout, token).ConfigureAwait(false);
            if (channelId == null) return new(false, "HTSP connected, but no accessible channels are available for the HTTP test.");
            var ticketRequest = new HTSMessage { Method = "getTicket" };
            ticketRequest.putField("channelId", channelId.Value);
            var ticket = await RequestAsync(connection, ticketRequest, token).ConfigureAwait(false);
            var path = ticket.getString("path", string.Empty);
            if (path != "/stream/channelid/" + channelId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return new(false, "TVHeadend returned an unexpected playback ticket path.");

            stage = method == StreamingMethods.HttpTicket ? "HTTP ticket" : "HTTP basic";
            var webRoot = HTSConnectionHandler.NormalizeWebRoot(connection.getServerWebRoot());
            var baseUrl = new UriBuilder(configuration.UseHttps ? "https" : "http", host, configuration.HTTP_Port, webRoot)
                .Uri.AbsoluteUri.TrimEnd('/');
            var query = "?weight=1";
            if (method == StreamingMethods.HttpTicket)
            {
                var value = ticket.getString("ticket", string.Empty);
                if (string.IsNullOrEmpty(value)) return new(false, "TVHeadend did not return a playback ticket.");
                query += "&ticket=" + Uri.EscapeDataString(value);
            }
            using var request = new HttpRequestMessage(HttpMethod.Head, baseUrl + path + query);
            request.Headers.ConnectionClose = true;
            if (method == StreamingMethods.HttpBasic)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(configuration.Username.Trim() + ":" + configuration.Password.Trim())));
            using var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(false, $"{stage} access was denied. Check credentials, authentication settings and HTTP streaming permission.");
            if (response.StatusCode != HttpStatusCode.OK)
                return new(false, $"{stage} test returned HTTP {(int)response.StatusCode}. Check the HTTP port, web root and channel availability.");
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType == null || !(contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || contentType.ToLowerInvariant() is "application/octet-stream" or "application/ogg" or "application/mp4"))
                return new(false, $"{stage} did not return media stream headers. Check the HTTP port, web root and proxy authentication.");
            return new(true, $"HTSP and {stage} access succeeded. The channel probe was closed; sustained playback was not tested.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, $"{stage} connection test timed out.");
        }
        catch (TimeoutException)
        {
            return new(false, $"{stage} did not respond before the test timeout.");
        }
        catch (UnauthorizedAccessException)
        {
            return new(false, $"{stage} access was denied. Check credentials and streaming permission.");
        }
        catch (HttpRequestException)
        {
            return new(false, $"{stage} connection failed. Check the hostname, HTTP port, HTTPS setting and certificate.");
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return new(false, $"{stage} connection failed. Check the hostname, port and TVHeadend availability.");
        }
    }

    private static async Task<HTSMessage> RequestAsync(HTSConnectionAsync connection, HTSMessage request, CancellationToken token)
    {
        var handler = new LoopBackResponseHandler();
        var sequence = connection.sendMessage(request, handler);
        try
        {
            var response = await handler.GetResponseAsync(token, RpcTimeout).ConfigureAwait(false);
            if (response == null) throw new TimeoutException();
            if (response.getInt("noaccess", 0) != 0 || !string.IsNullOrEmpty(response.getString("error", string.Empty)))
                throw new UnauthorizedAccessException();
            return response;
        }
        finally { connection.RemoveResponseHandler(sequence); }
    }

    private sealed class TestListener : HTSConnectionListener
    {
        private long? _firstChannel;
        public TaskCompletionSource<long?> Channel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void onMessage(HTSMessage response)
        {
            if (response.Method == "channelAdd" && _firstChannel == null && response.TryGetLong("channelId", out var id)) _firstChannel = id;
            if (response.Method == "initialSyncCompleted") Channel.TrySetResult(_firstChannel);
        }
        public void onError(Exception ex) => Channel.TrySetException(new IOException("HTSP connection failed."));
    }
}
