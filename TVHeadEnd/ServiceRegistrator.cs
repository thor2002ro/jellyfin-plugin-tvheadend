using System;
using System.IO;
using System.Linq;
using System.Xml;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.LiveTv;
using TVHeadEnd.Configuration;

namespace TVHeadEnd;

/// <summary>
/// Register LDAP services.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<HTSConnectionHandler>();
        serviceCollection.AddHttpClient(PluginConnectionTestController.HttpClientName)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        serviceCollection.AddSingleton<LiveTvService>();
        var startupConfiguration = ReadStartupConfiguration(serviceCollection);
        if (startupConfiguration.UseNativeTuners)
        {
            serviceCollection.AddSingleton<NativeTunerHost>(provider => ActivatorUtilities.CreateInstance<NativeTunerHost>(provider, startupConfiguration));
            serviceCollection.AddSingleton<ITunerHost>(provider => provider.GetRequiredService<NativeTunerHost>());
            serviceCollection.AddHostedService(provider => provider.GetRequiredService<NativeTunerHost>());
            serviceCollection.AddSingleton<IListingsProvider, NativeListingsProvider>();
        }
        else
        {
            RemoveNativeRegistrations(serviceCollection);
            serviceCollection.AddHostedService<ProgrammeImageService>();
            serviceCollection.AddSingleton<ILiveTvService>(provider => provider.GetRequiredService<LiveTvService>());
            serviceCollection.AddSingleton<IChannel, RecordingsChannel>();
        }
    }

    private static void RemoveNativeRegistrations(IServiceCollection services)
    {
        var manager = services.LastOrDefault(service => service.ServiceType == typeof(IConfigurationManager))?.ImplementationInstance as IConfigurationManager;
        if (manager == null) return;
        var options = manager.GetConfiguration<LiveTvOptions>("livetv");
        var tuners = options.TunerHosts.Where(tuner => !string.Equals(tuner.Type, NativeTunerHost.TunerType, StringComparison.OrdinalIgnoreCase)).ToArray();
        var guides = options.ListingProviders.Where(guide => !string.Equals(guide.Type, NativeTunerHost.TunerType, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tuners.Length == options.TunerHosts.Length && guides.Length == options.ListingProviders.Length) return;
        options.TunerHosts = tuners;
        options.ListingProviders = guides;
        manager.SaveConfiguration("livetv", options);
    }

    private static PluginConfiguration ReadStartupConfiguration(IServiceCollection services)
    {
        // Jellyfin registers services before constructing plugins. Reuse its already-registered paths and serializer.
        var paths = services.LastOrDefault(service => service.ServiceType == typeof(IApplicationPaths))?.ImplementationInstance as IApplicationPaths;
        var serializer = services.LastOrDefault(service => service.ServiceType == typeof(IXmlSerializer))?.ImplementationInstance as IXmlSerializer;
        if (paths == null || serializer == null) return Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var path = Path.Combine(paths.PluginConfigurationsPath, Path.ChangeExtension(Path.GetFileName(typeof(Plugin).Assembly.Location), ".xml"));
        if (!File.Exists(path)) return new PluginConfiguration();
        try { return (PluginConfiguration)serializer.DeserializeFromFile(typeof(PluginConfiguration), path) ?? new PluginConfiguration(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException) { return new PluginConfiguration(); }
    }
}
