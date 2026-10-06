using System;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.Configuration;
using TVHeadEnd.HTSP;
using Xunit;

public class NativeTunerTests
{
    [Fact]
    public void ServerSnapshotsKeepGlobalSettingsWithoutChangingThePrimaryConnection()
    {
        var settings = new PluginConfiguration { TVH_ServerName = "primary", HTSPQueueDepth = 1234 };
        var server = new NativeServerConfiguration { Id = "second", Host = "other", Username = "u", Password = "secret", Profile = "p" };
        var snapshot = settings.ForServer(server);
        Assert.Equal("other", snapshot.TVH_ServerName);
        Assert.Equal("secret", snapshot.Password);
        Assert.Equal(1234, snapshot.HTSPQueueDepth);
        Assert.Equal("primary", settings.TVH_ServerName);
        Assert.Equal("", settings.Password);
    }

    [Fact]
    public void SharingIsScopedByServerAccountProfileAndPassword()
    {
        string Key(PluginConfiguration config)
        {
            using var stream = new HtspLiveStream(new MediaSourceInfo(), "42", NullLoggerFactory.Instance, null, null, configuration: config);
            return (string)typeof(HtspLiveStream).GetProperty("SharingKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stream)!;
        }
        var one = new PluginConfiguration { TVH_ServerName = "one", Username = "u", Password = "a" };
        var same = new PluginConfiguration { TVH_ServerName = "one", Username = "u", Password = "a" };
        Assert.Equal(Key(one), Key(same));
        same.TVH_ServerName = "two"; Assert.NotEqual(Key(one), Key(same));
        same.TVH_ServerName = "one"; same.Username = "other"; Assert.NotEqual(Key(one), Key(same));
        same.Username = "u"; same.Profile = "other"; Assert.NotEqual(Key(one), Key(same));
        same.Profile = ""; same.Password = "b"; Assert.NotEqual(Key(one), Key(same));
        one.Username = "a|b"; one.Password = "c";
        same.Username = "a"; same.Password = "b|c";
        Assert.NotEqual(Key(one), Key(same));
    }

    [Theory]
    [InlineData("bad/id", "host", 9982)]
    [InlineData("good", "http://host", 9982)]
    [InlineData("good", "host", 0)]
    public void InvalidNativeServerSettingsAreRejected(string id, string host, int port)
    {
        Assert.Throws<ArgumentException>(() => new NativeServerConfiguration { Id = id, Host = host, HtspPort = port }.Validate());
    }

    [Fact]
    public async Task NativeChannelsAndGuideAreScopedCachedAndDoNotClaimOtherTuners()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var (plugin, images) = PluginTests.ConfigureImageCache(root);
        plugin.Configuration.UseNativeTuners = true;
        plugin.Configuration.NativeServers = [
            new() { Id = "one", Name = "Living room", Host = "::1", Username = "u", Password = "p" },
            new() { Id = "two", Name = "Bedroom", Host = "second", Username = "u", Password = "p" }];
        var options = new LiveTvOptions {
            TunerHosts = [new() { Id = "other", Type = "m3u", Url = "http://other" },
                new() { Id = "stable", Type = NativeTunerHost.TunerType, Url = "htsp://old:9982/one" }],
            ListingProviders = [new() { Id = "external-guide", Type = "xmltv" },
                new() { Id = "stable-guide", Type = NativeTunerHost.TunerType, ListingsId = NativeTunerHost.TunerType, Path = "old label" }]
        };
        var config = PluginTests.CreateProxy<IConfigurationManager>((method, args) => method.Name == "GetConfiguration" ? options : null);
        using var host = new NativeTunerHost(config, NullLoggerFactory.Instance, null, null, null, null, new HttpFactory(), images, null);
        try
        {
            await host.StartAsync(CancellationToken.None);
            var migrated = options.TunerHosts.Single(t => t.DeviceId == "one");
            Assert.Equal("stable", migrated.Id);
            Assert.Equal("htsp://[::1]:9982/", migrated.Url);
            var discovered = await host.DiscoverDevices(0, CancellationToken.None);
            Assert.Equal(2, discovered.Count);
            Assert.All(discovered, tuner => Assert.DoesNotContain(tuner.DeviceId, tuner.Url, StringComparison.Ordinal));
            Assert.Contains(options.TunerHosts, t => t.Id == "other");
            Assert.Contains(options.ListingProviders, p => p.Id == "external-guide");
            var guides = options.ListingProviders.Where(p => p.Type == NativeTunerHost.TunerType).ToArray();
            Assert.Equal(2, guides.Length);
            var guide = guides.Single(p => p.ListingsId == "one");
            Assert.Equal("stable-guide", guide.Id);
            Assert.Equal("Living room", guide.Path);
            Assert.Equal("Bedroom", guides.Single(p => p.ListingsId == "two").Path);
            Assert.False(guide.EnableAllTuners);
            Assert.DoesNotContain("other", guide.EnabledTuners);
            Assert.Equal("stable", Assert.Single(guide.EnabledTuners));
            var entries = (IDictionary)typeof(NativeTunerHost).GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            foreach (DictionaryEntry entry in entries)
            {
                var handler = (HTSConnectionHandler)entry.Value!.GetType().GetProperty("Connection")!.GetValue(entry.Value)!;
                typeof(HTSConnectionHandler).GetMethod("init", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(handler, null);
                typeof(HTSConnectionHandler).GetField("_connected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(handler, true);
                typeof(HTSConnectionHandler).GetField("_htsConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(handler, new HTSConnectionAsync(handler, "test", "1", NullLoggerFactory.Instance));
                var channel = new HTSMessage { Method = "channelAdd" };
                channel.putField("channelId", new BigInteger(42));
                channel.putField("channelName", "Channel " + entry.Key);
                channel.putField("channelNumber", new BigInteger(1));
                var service = new HTSMessage(); service.putField("type", "hdtv");
                channel.putField("services", new[] { service });
                handler.onMessage(channel);
                var programme = new HTSMessage { Method = "eventAdd" };
                programme.putField("channelId", new BigInteger(42));
                programme.putField("eventId", new BigInteger(123));
                programme.putField("start", new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60));
                programme.putField("stop", new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600));
                handler.onMessage(programme);
                handler.onMessage(new HTSMessage { Method = "initialSyncCompleted" });
            }
            Assert.Equal(new[] { "tvheadend_one_42", "tvheadend_two_42" }, (await host.GetChannels(false, CancellationToken.None)).Select(c => c.Id));
            var provider = new NativeListingsProvider(host);
            foreach (var serverGuide in guides)
            {
                var scopedChannel = Assert.Single(await provider.GetChannels(serverGuide, CancellationToken.None));
                Assert.Equal("tvheadend_" + serverGuide.ListingsId + "_42", scopedChannel.Id);
                Assert.Equal(scopedChannel.TunerHostId, Assert.Single(serverGuide.EnabledTuners));
            }
            Assert.Equal(new[] { "Living room", "Bedroom" }, (await provider.GetLineups(null, null, null)).Select(lineup => lineup.Name));
            Assert.Empty(await provider.GetProgramsAsync(guide, "tvheadend_two_42", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), CancellationToken.None));
            Assert.Equal("tvheadend_one_123", Assert.Single(await host.GetProgramsAsync("tvheadend_one_42", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(2), CancellationToken.None)).Id);
            Assert.Equal("tvheadend_two_123", Assert.Single(await host.GetProgramsAsync("tvheadend_two_42", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(2), CancellationToken.None)).Id);
            var first = entries["one"]!;
            var unavailable = (HTSConnectionHandler)first.GetType().GetProperty("Connection")!.GetValue(first)!;
            var cancelled = new TaskCompletionSource<bool>(); cancelled.SetCanceled();
            typeof(HTSConnectionHandler).GetField("_initialLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(unavailable, cancelled);
            Assert.Equal("tvheadend_two_42", Assert.Single(await host.GetChannels(false, CancellationToken.None)).Id);
            Assert.Single(await host.GetChannels(true, CancellationToken.None));
            foreach (DictionaryEntry entry in entries)
            {
                var handler = (HTSConnectionHandler)entry.Value!.GetType().GetProperty("Connection")!.GetValue(entry.Value)!;
                var deleted = new HTSMessage { Method = "channelDelete" };
                deleted.putField("channelId", new BigInteger(42)); handler.onMessage(deleted);
            }
            Assert.Single(await host.GetChannels(true, CancellationToken.None));
            Assert.Empty(await host.GetChannels(false, CancellationToken.None));
            Assert.Empty(await host.GetChannelStreamMediaSources("m3u_other", CancellationToken.None));
            await Assert.ThrowsAsync<FileNotFoundException>(() => host.GetChannelStream("m3u_other", "", new List<MediaBrowser.Controller.Library.ILiveStream>(), CancellationToken.None));
            Assert.Equal(3, options.TunerHosts.Length);
            Assert.Equal(3, options.ListingProviders.Length);
        }
        finally { await host.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SameChannelOnTwoServersOpensSeparateUpstreamsButReusesTheSameServer()
    {
        await using var one = new ChannelSwitchingTests.Server(emitFrame: true);
        await using var two = new ChannelSwitchingTests.Server(emitFrame: true);
        var app = PluginTests.CreateProxy<IServerApplicationHost>((method, _) => method.ReturnType == typeof(string) ? "http://127.0.0.1:8096" : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
        var firstConfig = new PluginConfiguration { TVH_ServerName = "127.0.0.1", HTSP_Port = one.Port, Username = "u", Password = "p", HTSPStallTimeoutSeconds = 0 };
        var secondConfig = new PluginConfiguration { TVH_ServerName = "127.0.0.1", HTSP_Port = two.Port, Username = "u", Password = "p", HTSPStallTimeoutSeconds = 0 };
        using var first = new HtspLiveStream(new() { Id = "tvheadend_one_42" }, "42", NullLoggerFactory.Instance, app, null, configuration: firstConfig, tunerHostId: "one");
        using var second = new HtspLiveStream(new() { Id = "tvheadend_two_42" }, "42", NullLoggerFactory.Instance, app, null, configuration: secondConfig, tunerHostId: "two");
        using var shared = new HtspLiveStream(new() { Id = "tvheadend_one_42" }, "42", NullLoggerFactory.Instance, app, null, configuration: firstConfig, tunerHostId: "one");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await first.Open(timeout.Token);
        await second.Open(timeout.Token);
        await shared.Open(timeout.Token);
        Assert.Single(one.Requests, r => r.Method == "subscribe");
        Assert.Single(two.Requests, r => r.Method == "subscribe");
        Assert.NotEqual(first.UniqueId, second.UniqueId);
        Assert.NotEqual(first.UniqueId, shared.UniqueId);
        Assert.Equal("one", first.TunerHostId);
        Assert.Equal("two", second.TunerHostId);
        Assert.True(Guid.TryParse(first.MediaSource.Id, out _));
        Assert.True(Guid.TryParse(second.MediaSource.Id, out _));
    }

    [Fact]
    public void SwitchingToTvheadendDvrRemovesOnlyManagedNativeEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var (plugin, _) = PluginTests.ConfigureImageCache(root);
        plugin.Configuration.UseNativeTuners = false;
        plugin.Configuration.NativeServers = [new() { Id = "saved", Host = "server", Username = "u", Password = "p" }];
        var options = new LiveTvOptions
        {
            TunerHosts = [new() { Id = "native", Type = "TVHeadend" }, new() { Id = "other", Type = "m3u" }],
            ListingProviders = [new() { Id = "native-guide", Type = "tvheadend" }, new() { Id = "other-guide", Type = "xmltv" }]
        };
        var saves = 0;
        var manager = PluginTests.CreateProxy<IConfigurationManager>((method, _) =>
        {
            if (method.Name == "GetConfiguration") return options;
            if (method.Name == "SaveConfiguration") saves++;
            return null;
        });
        var services = new ServiceCollection(); services.AddSingleton(manager);
        new ServiceRegistrator().RegisterServices(services, null);
        Assert.Equal("other", Assert.Single(options.TunerHosts).Id);
        Assert.Equal("other-guide", Assert.Single(options.ListingProviders).Id);
        Assert.Equal("saved", Assert.Single(plugin.Configuration.NativeServers).Id);
        Assert.Equal(1, saves);
        var repeat = new ServiceCollection(); repeat.AddSingleton(manager);
        new ServiceRegistrator().RegisterServices(repeat, null);
        Assert.Equal(1, saves);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Fact]
    public async Task GuideNamesRefreshWithoutReplacingProviderIdsOrMappings()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var (plugin, images) = PluginTests.ConfigureImageCache(root);
        plugin.Configuration.NativeServers = [new() { Id = "one", Name = "Renamed server", Host = "first", Username = "u", Password = "p" },
            new() { Id = NativeTunerHost.TunerType, Name = " ", Host = "second", Username = "u", Password = "p" }];
        var guide = new ListingsProviderInfo { Id = "existing-guide", Type = NativeTunerHost.TunerType, ListingsId = "one", Path = "Old server name",
            ChannelMappings = [new() { Name = "42", Value = "42" }] };
        var options = new LiveTvOptions { ListingProviders = [guide, new() { Id = "removed-server", Type = NativeTunerHost.TunerType, ListingsId = "removed" }] };
        var manager = PluginTests.CreateProxy<IConfigurationManager>((method, _) => method.Name == "GetConfiguration" ? options : null);
        using var host = new NativeTunerHost(manager, NullLoggerFactory.Instance, null, null, null, null, new HttpFactory(), images, null);
        try
        {
            await host.StartAsync(CancellationToken.None);
            Assert.Equal(2, options.ListingProviders.Length);
            Assert.Same(guide, options.ListingProviders.Single(p => p.ListingsId == "one"));
            Assert.Equal("Renamed server", guide.Path);
            Assert.Equal("42", Assert.Single(guide.ChannelMappings).Value);
            Assert.Equal("second", options.ListingProviders.Single(p => p.ListingsId == NativeTunerHost.TunerType).Path);
            Assert.Equal(2, options.ListingProviders.Select(p => p.Id).Distinct().Count());
            var provider = new NativeListingsProvider(host);
            Assert.Empty(await provider.GetProgramsAsync(options.ListingProviders.Single(p => p.ListingsId == NativeTunerHost.TunerType),
                "tvheadend_one_42", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), CancellationToken.None));
        }
        finally { await host.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class HttpFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public HttpFactory(HttpMessageHandler handler = null) { _handler = handler; }
        public HttpClient CreateClient(string name) => _handler == null ? new() : new(_handler, false);
    }

    private sealed class SlowArtwork : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unexpected completion.");
        }
    }

    [Fact]
    public async Task ArtworkTimeoutKeepsEveryHealthyChannel()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var (plugin, images) = PluginTests.ConfigureImageCache(root);
        plugin.Configuration.NativeServers = [new() { Id = "one", Host = "first", Username = "u", Password = "p" }];
        var options = new LiveTvOptions();
        var config = PluginTests.CreateProxy<IConfigurationManager>((method, _) => method.Name == "GetConfiguration" ? options : null);
        using var slow = new SlowArtwork();
        using var host = new NativeTunerHost(config, NullLoggerFactory.Instance, null, null, null, null, new HttpFactory(slow), images, null);
        try
        {
            await host.StartAsync(CancellationToken.None);
            var entries = (IDictionary)typeof(NativeTunerHost).GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            var entry = entries["one"]!;
            var handler = (HTSConnectionHandler)entry.GetType().GetProperty("Connection")!.GetValue(entry)!;
            typeof(HTSConnectionHandler).GetMethod("init", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(handler, null);
            typeof(HTSConnectionHandler).GetField("_connected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(handler, true);
            typeof(HTSConnectionHandler).GetField("_htsConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(handler, new HTSConnectionAsync(handler, "test", "1", NullLoggerFactory.Instance));
            for (var id = 1; id <= 3; id++)
            {
                var channel = new HTSMessage { Method = "channelAdd" };
                channel.putField("channelId", new BigInteger(id)); channel.putField("channelName", "channel" + id);
                channel.putField("channelNumber", new BigInteger(id));
                channel.putField("channelIcon", "/slow-logo/" + id);
                var service = new HTSMessage(); service.putField("type", "hdtv"); channel.putField("services", new[] { service });
                handler.onMessage(channel);
            }
            handler.onMessage(new HTSMessage { Method = "initialSyncCompleted" });
            typeof(NativeTunerHost).GetField("_serverTimeout", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, TimeSpan.FromMilliseconds(200));
            Assert.Equal(3, (await handler.BuildChannelInfos(CancellationToken.None)).Count());
            var errors = new List<Exception>();
            var logger = PluginTests.CreateProxy<ILogger<NativeTunerHost>>((method, arguments) => {
                if (method.Name == "Log" && arguments[3] is Exception error) errors.Add(error);
                return method.Name == "IsEnabled" ? true : null;
            });
            typeof(NativeTunerHost).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, logger);
            Assert.True((await host.GetChannels(false, CancellationToken.None)).Count == 3, string.Join(Environment.NewLine, errors));
            Assert.Equal(3, (await host.GetChannels(true, CancellationToken.None)).Count);
            using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.GetChannels(true, cancelled.Token));
        }
        finally { await host.StopAsync(CancellationToken.None); host.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdStartupReadsSavedModeBeforePluginConstruction(bool native)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var instance = typeof(Plugin).GetProperty("Instance")!;
        var previous = Plugin.Instance;
        instance.SetValue(null, null);
        try
        {
            var path = Path.Combine(root, "TVHeadEnd.xml");
            using (var output = File.Create(path)) new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration)).Serialize(output,
                new PluginConfiguration { UseNativeTuners = native, NativeServers = [new() { Id = "cold", Host = "cold-server", HtspPort = 19982, Username = "u", Password = "p" }] });
            var services = new ServiceCollection();
            services.AddSingleton(PluginTests.CreateProxy<IApplicationPaths>((method, _) => method.Name == "get_PluginConfigurationsPath" ? root : null));
            services.AddSingleton(PluginTests.CreateProxy<IXmlSerializer>((method, args) => {
                if (method.Name != "DeserializeFromFile") return null;
                using var stream = File.OpenRead((string)args[1]);
                return new System.Xml.Serialization.XmlSerializer((Type)args[0]).Deserialize(stream);
            }));
            new ServiceRegistrator().RegisterServices(services, null);
            Assert.Null(Plugin.Instance);
            Assert.Equal(native ? 0 : 1, services.Count(s => s.ServiceType == typeof(ILiveTvService)));
            Assert.Equal(native ? 1 : 0, services.Count(s => s.ServiceType == typeof(ITunerHost)));
            Assert.Equal(native ? 1 : 0, services.Count(s => s.ServiceType == typeof(IListingsProvider)));
            if (native)
            {
                services.AddSingleton<IConfigurationManager>(PluginTests.CreateProxy<IConfigurationManager>((method, _) => method.Name == "GetConfiguration" ? new LiveTvOptions() : null));
                services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
                services.AddSingleton<IServerApplicationHost>(PluginTests.CreateProxy<IServerApplicationHost>((_, _) => null));
                services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
                services.AddSingleton<IMediaEncoder>(PluginTests.CreateProxy<IMediaEncoder>((_, _) => null));
                services.AddSingleton<ILibraryManager>(PluginTests.CreateProxy<ILibraryManager>((_, _) => null));
                services.AddSingleton<IImageEncoder>(PluginTests.CreateProxy<IImageEncoder>((_, _) => null));
                services.AddSingleton<ITaskManager>(PluginTests.CreateProxy<ITaskManager>((_, _) => null));
                using var provider = services.BuildServiceProvider();
                Assert.Equal("cold-server:19982", Assert.Single(provider.GetRequiredService<NativeTunerHost>().GetServerStatuses()).Server);
                Assert.Null(Plugin.Instance);
            }
        }
        finally { instance.SetValue(null, previous); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NativeHttpBasicUsesTicketsAndGuidIdsForJellyfinRecording()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var (plugin, images) = PluginTests.ConfigureImageCache(root);
        plugin.Configuration.UseNativeTuners = true;
        plugin.Configuration.NativeServers = [new() { Id = "one", Host = "first", Username = "u", Password = "p", StreamingMethod = StreamingMethods.HttpBasic }];
        var options = new LiveTvOptions();
        var config = PluginTests.CreateProxy<IConfigurationManager>((method, _) => method.Name == "GetConfiguration" ? options : null);
        var encoder = PluginTests.CreateProxy<IMediaEncoder>((method, _) => method.Name == "GetMediaInfo" ? Task.FromResult<MediaInfo>(null) : null);
        using var host = new NativeTunerHost(config, NullLoggerFactory.Instance, null, null, encoder, null, new HttpFactory(), images, null);
        try
        {
            await host.StartAsync(CancellationToken.None);
            var entries = (IDictionary)typeof(NativeTunerHost).GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            var entry = entries["one"]!;
            var handler = (HTSConnectionHandler)entry.GetType().GetProperty("Connection")!.GetValue(entry)!;
            var service = (LiveTvService)entry.GetType().GetProperty("Service")!.GetValue(entry)!;
            typeof(HTSConnectionHandler).GetMethod("init", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(handler, null);
            typeof(HTSConnectionHandler).GetField("_connected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(handler, true);
            typeof(HTSConnectionHandler).GetField("_htsConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(handler, new HTSConnectionAsync(handler, "test", "1", NullLoggerFactory.Instance));
            var tickets = typeof(LiveTvService).GetField("_channelTicketHandler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            var cache = (ConcurrentDictionary<string, Lazy<Task<AccessTicketHandler.Ticket>>>)typeof(AccessTicketHandler).GetField("_ticketCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tickets)!;
            cache["42"] = new(() => Task.FromResult(new AccessTicketHandler.Ticket { Id = "1", Path = "/stream/channel/42", TicketParam = "a b&", Expires = DateTime.UtcNow.AddMinutes(1) }));
            var source = Assert.Single(await host.GetChannelStreamMediaSources("tvheadend_one_42", CancellationToken.None));
            Assert.True(Guid.TryParse(source.Id, out _));
            Assert.EndsWith("?ticket=a%20b%26", source.Path, StringComparison.Ordinal);
            Assert.Empty(source.RequiredHttpHeaders);
            using var stream = await host.GetChannelStream("tvheadend_one_42", source.Id, [], CancellationToken.None);
            Assert.Equal(source.Id, stream.OriginalStreamId);
            Assert.True(Guid.TryParse(stream.MediaSource.Id, out _));
            Assert.Equal(stream.UniqueId, stream.MediaSource.Id);
            Assert.EndsWith("?ticket=a%20b%26", stream.MediaSource.Path, StringComparison.Ordinal);
            Assert.Empty(stream.MediaSource.RequiredHttpHeaders);
            Assert.Equal(options.TunerHosts[0].Id, stream.TunerHostId);
            await stream.Close();
        }
        finally { await host.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
