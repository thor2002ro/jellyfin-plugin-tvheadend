using System;
using MediaBrowser.Model.Plugins;

namespace TVHeadEnd.Configuration
{
    /// <summary>
    /// Class PluginConfiguration
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string TVH_ServerName { get; set; }
		public int HTTP_Port { get; set; }
		public int HTSP_Port { get; set; }
        public string WebRoot { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public int Priority { get; set; }
        public string Profile { get; set; }
        public int Pre_Padding { get; set; }
        public int Post_Padding { get; set; }
        public string ChannelType { get; set; }
        public bool HideRecordingsChannel { get; set; }
        public bool EnableSubsMaudios { get; set; }
        public string StreamingMethod { get; set; }
        public bool ForceDeinterlace { get; set; }
        public int HTSPQueueDepth { get; set; }
        public int HTSPStallTimeoutSeconds { get; set; }
        public bool HTSPFilterControlStreams { get; set; }
        public bool HTSPSignalRecoveryEnabled { get; set; }
        public int HTSPSignalLockLossSeconds { get; set; }
        public int HTSPSignalUncBurstThreshold { get; set; }
        public int HTSPSignalIdrWaitSeconds { get; set; }
        public int HTSPSignalRecoveryMaxReconnects { get; set; }
        public int HTSPSignalRecoveryCooldownSeconds { get; set; }

        public PluginConfiguration()
        {
            TVH_ServerName = "localhost";
            HTTP_Port = 9981;
			HTSP_Port = 9982;
            WebRoot = "/";
            Username = "";
            Password = "";
            Priority = 5;
            Profile = "";
            Pre_Padding = 0;
            Post_Padding = 0;
            ChannelType = "Ignore";
            HideRecordingsChannel = false;
            EnableSubsMaudios = false;
            StreamingMethod = "";
            ForceDeinterlace = false;
            HTSPQueueDepth = 2000000;
            HTSPStallTimeoutSeconds = 15;
            HTSPFilterControlStreams = false;
            HTSPSignalRecoveryEnabled = true;
            HTSPSignalLockLossSeconds = 3;
            HTSPSignalUncBurstThreshold = 5;
            HTSPSignalIdrWaitSeconds = 3;
            HTSPSignalRecoveryMaxReconnects = 2;
            HTSPSignalRecoveryCooldownSeconds = 15;
        }
    }
}
