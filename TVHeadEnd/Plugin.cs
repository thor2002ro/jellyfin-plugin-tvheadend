using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using TVHeadEnd.Configuration;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using MediaBrowser.Model.Drawing;

namespace TVHeadEnd
{
    /// <summary>
    /// Class Plugin
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private string _imageCachePath;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            if (string.IsNullOrWhiteSpace(Configuration.RecordingStreamSecret))
            {
                Configuration.RecordingStreamSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                SaveConfiguration(Configuration);
            }
        }

        public PluginConfiguration ResetConfigurationToDefaults()
        {
            var current = Configuration;
            var configuration = PluginConfiguration.CreateDefault();
            if (!string.IsNullOrWhiteSpace(current?.TVH_ServerName))
            {
                configuration.TVH_ServerName = current.TVH_ServerName;
            }

            configuration.Username = current?.Username ?? string.Empty;
            configuration.Password = current?.Password ?? string.Empty;
            configuration.RecordingStreamSecret = string.IsNullOrWhiteSpace(current?.RecordingStreamSecret)
                ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                : current.RecordingStreamSecret;
            SaveConfiguration(configuration);
            return configuration;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "tvheadend",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.tvheadend.html",
                    EnableInMainMenu = true
                },
                new PluginPageInfo
                {
                    Name = "tvheadendjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.tvheadend.js"
                }
            };
        }

        /// <summary>
        /// Gets the name of the plugin
        /// </summary>
        /// <value>The name.</value>
        public override string Name
        {
            get { return "TVHeadend"; }
        }

        /// <summary>
        /// Gets the description.
        /// </summary>
        /// <value>The description.</value>
        public override string Description
        {
            get
            {
                return "Provides live TV using TVHeadend as the source.";
            }
        }

        private Guid _id = new Guid("3fd018e5-5e78-4e58-b280-a0c068febee0");
        public override Guid Id
        {
            get { return _id; }
        }

        public string ImageCachePath
        {
            get
            {
                if (_imageCachePath != null)
                {
                    return _imageCachePath;
                }

                var configurationFilePath = GetType()
                    .GetProperty("ConfigurationFilePath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(this) as string;
                var root = string.IsNullOrWhiteSpace(configurationFilePath)
                    ? null
                    : Path.GetDirectoryName(configurationFilePath);
                root ??= Path.GetDirectoryName(GetType().Assembly.Location);
                root ??= DataFolderPath;
                _imageCachePath = Path.Combine(root, "tvheadend-images");
                return _imageCachePath;
            }
        }

        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>The instance.</value>
        public static Plugin Instance { get; private set; }
    }

}
