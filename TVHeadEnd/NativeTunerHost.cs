using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Configuration;

namespace TVHeadEnd;

public sealed record NativeServerStatus(string Name, string Server, bool Connected, string ServerVersion, int? HtspProtocolVersion, string StreamingMethod = null);

public sealed class NativeTunerHost : BackgroundService, ITunerHost, IConfigurableTunerHost
{
    public const string TunerType = "tvheadend";
    private readonly IConfigurationManager _serverConfiguration;
    private readonly ILoggerFactory _loggers;
    private readonly IServerApplicationHost _appHost;
    private readonly IHttpContextAccessor _httpContext;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<NativeTunerHost> _logger;
    private readonly Dictionary<string, Server> _servers = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;
    private readonly SemaphoreSlim _channelRefresh = new(1, 1);
    private List<ChannelInfo> _channelCache;
    private DateTime _channelCacheUtc;
    private readonly TimeSpan _serverTimeout = TimeSpan.FromSeconds(30);

    private sealed record Server(string Id, string Name, PluginConfiguration Settings,
        HTSConnectionHandler Connection, LiveTvService Service, ProgrammeImageService Artwork);

    public NativeTunerHost(IConfigurationManager serverConfiguration, ILoggerFactory loggers,
        IServerApplicationHost appHost, IHttpContextAccessor httpContext, IMediaEncoder encoder,
        ILibraryManager library, System.Net.Http.IHttpClientFactory httpClients, IImageEncoder images, ITaskManager tasks,
        PluginConfiguration configuration = null)
    {
        _serverConfiguration = serverConfiguration;
        _loggers = loggers;
        _appHost = appHost;
        _httpContext = httpContext;
        _encoder = encoder;
        _logger = loggers.CreateLogger<NativeTunerHost>();
        var config = configuration ?? Plugin.Instance?.Configuration ?? new PluginConfiguration();
        foreach (var entry in config.NativeServers ?? [])
        {
            if (entry == null) { _logger.LogWarning("Empty native TVHeadend server configuration skipped"); continue; }
            try
            {
                var settings = config.ForServer(entry);
                if (_servers.ContainsKey(entry.Id)) throw new ArgumentException("Duplicate native server ID.");
                var connection = new HTSConnectionHandler(loggers, httpClients, images, tasks, settings);
                var service = new LiveTvService(loggers, encoder, connection, appHost, httpContext, library, settings, entry.Id);
                var artwork = new ProgrammeImageService(connection, service, encoder, library, loggers.CreateLogger<ProgrammeImageService>());
                _servers.Add(entry.Id, new Server(entry.Id, string.IsNullOrWhiteSpace(entry.Name) ? settings.TVH_ServerName : entry.Name.Trim(), settings, connection, service, artwork));
            }
            catch (ArgumentException ex) { _logger.LogError(ex, "Invalid native TVHeadend server configuration; entry skipped"); }
        }
    }

    public string Name => "TVHeadend";
    public string Type => TunerType;
    public bool IsSupported => true;
    internal List<NameIdPair> GetGuideLineups() => _servers.Values.Select(server => new NameIdPair { Id = server.Id, Name = server.Name }).ToList();
    public IReadOnlyList<NativeServerStatus> GetServerStatuses() => _servers.Values.Select(server =>
    {
        var connection = server.Connection.GetConnectionStatus();
        return new NativeServerStatus(server.Name, server.Settings.TVH_ServerName + ":" + server.Settings.HTSP_Port,
            connection.Connected, connection.ServerVersion, connection.ProtocolVersion, StreamingMethods.GetEffective(server.Settings.StreamingMethod));
    }).ToArray();
    internal static string Prefix(string serverId) => "tvheadend_" + serverId + "_";
    private IEnumerable<TunerHostInfo> Tuners => _serverConfiguration.GetConfiguration<LiveTvOptions>("livetv")
        .TunerHosts.Where(t => string.Equals(t.Type, Type, StringComparison.OrdinalIgnoreCase));

    private Server GetServer(TunerHostInfo tuner)
    {
        if (!Uri.TryCreate(tuner.Url, UriKind.Absolute, out var url) || url.Scheme != "htsp" || url.UserInfo.Length != 0)
            throw new ArgumentException("Configure native servers in TVHeadend plugin settings, then save and restart Jellyfin.");
        var id = string.IsNullOrWhiteSpace(tuner.DeviceId) ? url.AbsolutePath.Trim('/') : tuner.DeviceId;
        if (!_servers.TryGetValue(id, out var server) || !string.Equals(url.Host.Trim('[', ']'), server.Settings.TVH_ServerName.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
            || url.Port != server.Settings.HTSP_Port) throw new ArgumentException("This tuner does not match a configured TVHeadend server. Save settings and restart Jellyfin.");
        return server;
    }

    private (Server Server, TunerHostInfo Tuner, string RawId) Resolve(string channelId)
    {
        foreach (var tuner in Tuners)
        {
            Server server;
            try { server = GetServer(tuner); }
            catch (ArgumentException) { continue; }
            var prefix = Prefix(server.Id);
            if (channelId.StartsWith(prefix, StringComparison.Ordinal)) return (server, tuner, channelId[prefix.Length..]);
        }
        throw new System.IO.FileNotFoundException("Channel is not provided by this TVHeadend tuner.");
    }

    public async Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
    {
        await _channelRefresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (enableCache && _channelCache != null && DateTime.UtcNow - _channelCacheUtc < TimeSpan.FromMinutes(5))
                return new List<ChannelInfo>(_channelCache);
            var channels = await LoadChannelsAsync(cancellationToken).ConfigureAwait(false);
            _channelCache = channels;
            _channelCacheUtc = DateTime.UtcNow;
            return new List<ChannelInfo>(channels);
        }
        finally { _channelRefresh.Release(); }
    }

    private async Task<List<ChannelInfo>> LoadChannelsAsync(CancellationToken cancellationToken)
    {
        var channels = new List<ChannelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tuner in Tuners)
        {
            try
            {
                var server = GetServer(tuner);
                if (!seen.Add(server.Id)) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, StoppingToken);
                timeout.CancelAfter(_serverTimeout);
                if (await server.Connection.WaitForInitialLoadAsync(timeout.Token).ConfigureAwait(false) != 0)
                    throw new TimeoutException("TVHeadend metadata did not load.");
                var items = (await server.Connection.BuildChannelInfos(timeout.Token).ConfigureAwait(false)).ToList();
                server.Connection.BeginImageRefresh(items.Select(c => "channel:" + Prefix(server.Id) + c.Id));
                timeout.Token.ThrowIfCancellationRequested();
                await Task.WhenAll(items.Select(async channel =>
                {
                    channel.Id = Prefix(server.Id) + channel.Id;
                    channel.TunerHostId = tuner.Id;
                    var source = channel.ImageUrl;
                    channel.ImagePath = null;
                    channel.ImageUrl = null;
                    channel.HasImage = false;
                    try
                    {
                        var image = await server.Connection.CacheImageAsync(source, "channel:" + channel.Id, timeout.Token).ConfigureAwait(false);
                        channel.ImagePath = image.ImagePath;
                        channel.ImageUrl = image.ImagePath ?? image.ImageUrl;
                        channel.HasImage = !string.IsNullOrEmpty(channel.ImageUrl);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || StoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _logger.LogDebug(ex, "Native channel artwork unavailable; keeping channel"); }
                })).ConfigureAwait(false);
                channels.AddRange(items);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || StoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "Native TVHeadend tuner unavailable; continuing with other servers"); }
        }
        return channels;
    }

    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime start, DateTime end, CancellationToken token)
    {
        var (server, _, rawId) = Resolve(channelId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, StoppingToken);
        timeout.CancelAfter(_serverTimeout);
        var programs = (await server.Service.GetProgramsAsync(rawId, start, end, timeout.Token).ConfigureAwait(false)).ToList();
        foreach (var program in programs)
        {
            program.Id = Prefix(server.Id) + program.Id;
            program.ChannelId = channelId;
        }
        return programs;
    }

    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, StoppingToken);
        token = operation.Token;
        token.ThrowIfCancellationRequested();
        (Server server, TunerHostInfo tuner, string rawId) resolved;
        try { resolved = Resolve(channelId); }
        catch (System.IO.FileNotFoundException) { return []; }
        var (server, _, rawId) = resolved;
        var source = await server.Service.GetChannelStream(rawId, string.Empty, token).ConfigureAwait(false);
        source.Id = LiveTvService.GetStableHtspMediaSourceId(channelId);
        return [source];
    }

    public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, StoppingToken);
        token = operation.Token;
        token.ThrowIfCancellationRequested();
        var (server, tuner, rawId) = Resolve(channelId);
        if (StreamingMethods.GetEffective(server.Settings.StreamingMethod) != StreamingMethods.Htsp)
        {
            var source = await server.Service.GetChannelStream(rawId, streamId, token).ConfigureAwait(false);
            source.Id = LiveTvService.GetStableHtspMediaSourceId(channelId);
            var httpStream = new MediaSourceLiveStream(source, () => server.Service.CloseLiveStream(rawId, CancellationToken.None), tuner.Id) { OriginalStreamId = source.Id };
            source.Id = httpStream.UniqueId;
            return httpStream;
        }
        var mediaSource = await server.Service.GetChannelStream(rawId, streamId, token).ConfigureAwait(false);
        mediaSource.Id = LiveTvService.GetStableHtspMediaSourceId(channelId);
        var stream = new HtspLiveStream(mediaSource, server.Connection.ResolveChannelId(rawId).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _loggers, _appHost, _httpContext, _encoder, server.Settings, tuner.Id);
        try { await stream.Open(token).ConfigureAwait(false); return stream; }
        catch { await stream.Close().ConfigureAwait(false); stream.Dispose(); throw; }
    }

    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(_servers.Values.Select(server => new TunerHostInfo
        {
            Type = Type, DeviceId = server.Id, FriendlyName = server.Name,
            Url = new UriBuilder("htsp", server.Settings.TVH_ServerName, server.Settings.HTSP_Port).Uri.AbsoluteUri
        }).ToList());
    }
    public async Task Validate(TunerHostInfo tuner)
    {
        var server = GetServer(tuner);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(StoppingToken);
        timeout.CancelAfter(_serverTimeout);
        if (await server.Connection.WaitForInitialLoadAsync(timeout.Token).ConfigureAwait(false) != 0)
            throw new TimeoutException("TVHeadend metadata did not load.");
    }

    private readonly CancellationTokenSource _stop = new();
    private CancellationToken StoppingToken => _stop.Token;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _serverConfiguration.GetConfiguration<LiveTvOptions>("livetv");
        var otherTuners = options.TunerHosts.Where(t => !string.Equals(t.Type, Type, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var server in _servers.Values)
        {
            var url = new UriBuilder("htsp", server.Settings.TVH_ServerName, server.Settings.HTSP_Port).Uri.AbsoluteUri;
            var existing = options.TunerHosts.FirstOrDefault(t => string.Equals(t.Type, Type, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(t.DeviceId, server.Id, StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrEmpty(t.DeviceId) && Uri.TryCreate(t.Url, UriKind.Absolute, out var previousUrl)
                        && string.Equals(previousUrl.AbsolutePath.Trim('/'), server.Id, StringComparison.OrdinalIgnoreCase))));
            var tuner = existing ?? new TunerHostInfo { Id = Guid.NewGuid().ToString("N"), Type = Type };
            tuner.Url = url;
            tuner.DeviceId = server.Id;
            tuner.FriendlyName = server.Name;
            otherTuners.Add(tuner);
        }
        options.TunerHosts = otherTuners.ToArray();
        var existingGuides = options.ListingProviders.Where(p => string.Equals(p.Type, Type, StringComparison.OrdinalIgnoreCase)).ToArray();
        var guides = options.ListingProviders.Where(p => !string.Equals(p.Type, Type, StringComparison.OrdinalIgnoreCase)).ToList();
        var legacyGuide = existingGuides.FirstOrDefault(p => string.IsNullOrEmpty(p.ListingsId)
            || (string.Equals(p.ListingsId, Type, StringComparison.OrdinalIgnoreCase) && !_servers.ContainsKey(p.ListingsId)));
        foreach (var server in _servers.Values)
        {
            var guide = existingGuides.FirstOrDefault(p => string.Equals(p.ListingsId, server.Id, StringComparison.OrdinalIgnoreCase));
            if (guide == null && legacyGuide != null) { guide = legacyGuide; legacyGuide = null; }
            guide ??= new ListingsProviderInfo { Id = Guid.NewGuid().ToString("N"), Type = Type };
            guide.ListingsId = server.Id;
            // Stock Jellyfin-web displays Path as the provider's secondary label; this provider does not read a guide file.
            guide.Path = server.Name;
            guide.EnableAllTuners = false;
            guide.EnabledTuners = otherTuners.Where(t => string.Equals(t.Type, Type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.DeviceId, server.Id, StringComparison.OrdinalIgnoreCase)).Select(t => t.Id).ToArray();
            guides.Add(guide);
        }
        options.ListingProviders = guides.ToArray();
        _serverConfiguration.SaveConfiguration("livetv", options);
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await HtspLiveStream.CloseNativeStreamsAsync().ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var lastBackgroundCapture = DateTime.MinValue;
        var roundRobin = 0;
        var servers = _servers.Values.ToArray();
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            for (var offset = 0; offset < servers.Length; offset++)
            {
                if (servers.Any(s => s.Artwork.ExtractionRunning)) break;
                var server = servers[(roundRobin + offset) % servers.Length];
                try
                {
                    await server.Artwork.CaptureMissingImagesForServerAsync(stoppingToken,
                        DateTime.UtcNow - lastBackgroundCapture >= TimeSpan.FromMinutes(1)).ConfigureAwait(false);
                    if (server.Artwork.LastBackgroundCaptureUtc > lastBackgroundCapture)
                        lastBackgroundCapture = server.Artwork.LastBackgroundCaptureUtc;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.LogDebug(ex, "Native programme artwork scan skipped"); }
            }
            if (servers.Length > 0) roundRobin = (roundRobin + 1) % servers.Length;
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        HtspLiveStream.CloseNativeStreamsAsync().GetAwaiter().GetResult();
        foreach (var server in _servers.Values) { server.Artwork.Dispose(); server.Connection.Dispose(); }
        base.Dispose();
        _stop.Dispose();
    }
}
