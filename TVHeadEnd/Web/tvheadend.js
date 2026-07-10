const TVHclientConfigurationPageVar = {
    pluginUniqueId: '3fd018e5-5e78-4e58-b280-a0c068febee0'
};

export default function (view, params) {
    function getStreamingMethod(config) {
        if (config.StreamingMethod) {
            return config.StreamingMethod;
        }
        return config.EnableSubsMaudios ? 'HttpBasic' : 'HttpTicket';
    }

    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        const page = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(function(config) {
            page.querySelector('#txtTVH_ServerName').value = config.TVH_ServerName || '';
            page.querySelector('#txtHTTP_Port').value = config.HTTP_Port || '9981';
            page.querySelector('#txtHTSP_Port').value = config.HTSP_Port || '9982';
            page.querySelector('#txtWebRoot').value = config.WebRoot || '/';
            page.querySelector('#txtUserName').value = config.Username || '';
            page.querySelector('#txtPassword').value = config.Password || '';
            page.querySelector('#txtPriority').value = config.Priority || '5';
            page.querySelector('#txtProfile').value = config.Profile || '';
            page.querySelector('#txtPrePadding').value = config.Pre_Padding || '0';
            page.querySelector('#txtPostPadding').value = config.Post_Padding || '0';
            page.querySelector('#selChannelType').value = config.ChannelType || 'Ignore';
            page.querySelector('#chkHideRecordingsChannel').checked = config.HideRecordingsChannel || false;
            page.querySelector('#selStreamingMethod').value = getStreamingMethod(config);
            page.querySelector('#chkForceDeinterlace').checked = config.ForceDeinterlace || false;
            page.querySelector('#txtHTSPQueueDepth').value = Number.isFinite(config.HTSPQueueDepth) ? config.HTSPQueueDepth : 2000000;
            page.querySelector('#txtHTSPStallTimeoutSeconds').value = Number.isFinite(config.HTSPStallTimeoutSeconds) ? config.HTSPStallTimeoutSeconds : 15;
            page.querySelector('#chkHTSPFilterControlStreams').checked = config.HTSPFilterControlStreams === true;
            page.querySelector('#chkHTSPSignalRecoveryEnabled').checked = config.HTSPSignalRecoveryEnabled !== false;
            page.querySelector('#txtHTSPSignalLockLossSeconds').value = Number.isFinite(config.HTSPSignalLockLossSeconds) ? config.HTSPSignalLockLossSeconds : 3;
            page.querySelector('#txtHTSPSignalUncBurstThreshold').value = Number.isFinite(config.HTSPSignalUncBurstThreshold) ? config.HTSPSignalUncBurstThreshold : 5;
            page.querySelector('#txtHTSPSignalIdrWaitSeconds').value = Number.isFinite(config.HTSPSignalIdrWaitSeconds) ? config.HTSPSignalIdrWaitSeconds : 3;
            page.querySelector('#txtHTSPSignalRecoveryMaxReconnects').value = Number.isFinite(config.HTSPSignalRecoveryMaxReconnects) ? config.HTSPSignalRecoveryMaxReconnects : 2;
            page.querySelector('#txtHTSPSignalRecoveryCooldownSeconds').value = Number.isFinite(config.HTSPSignalRecoveryCooldownSeconds) ? config.HTSPSignalRecoveryCooldownSeconds : 15;
            Dashboard.hideLoadingMsg();
        });
    });
    view.querySelector('.TVHclientConfigurationForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        const form = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(function(config) {
            config.TVH_ServerName = form.querySelector('#txtTVH_ServerName').value;
            config.HTTP_Port = form.querySelector('#txtHTTP_Port').value;
            config.HTSP_Port = form.querySelector('#txtHTSP_Port').value;
            config.WebRoot = form.querySelector('#txtWebRoot').value;
            config.Username = form.querySelector('#txtUserName').value;
            config.Password = form.querySelector('#txtPassword').value;
            config.Priority = form.querySelector('#txtPriority').value;
            config.Profile = form.querySelector('#txtProfile').value;
            config.Pre_Padding = form.querySelector('#txtPrePadding').value;
            config.Post_Padding = form.querySelector('#txtPostPadding').value;
            config.ChannelType = form.querySelector('#selChannelType').value;
            config.HideRecordingsChannel = form.querySelector('#chkHideRecordingsChannel').checked;
            config.StreamingMethod = form.querySelector('#selStreamingMethod').value;
            config.EnableSubsMaudios = config.StreamingMethod === 'HttpBasic';
            config.ForceDeinterlace = form.querySelector('#chkForceDeinterlace').checked;
            config.HTSPQueueDepth = Math.max(0, Math.min(20000000, parseInt(form.querySelector('#txtHTSPQueueDepth').value, 10) || 0));
            config.HTSPStallTimeoutSeconds = Math.max(0, Math.min(120, parseInt(form.querySelector('#txtHTSPStallTimeoutSeconds').value, 10) || 0));
            config.HTSPFilterControlStreams = form.querySelector('#chkHTSPFilterControlStreams').checked;
            config.HTSPSignalRecoveryEnabled = form.querySelector('#chkHTSPSignalRecoveryEnabled').checked;
            config.HTSPSignalLockLossSeconds = Math.max(1, Math.min(30, parseInt(form.querySelector('#txtHTSPSignalLockLossSeconds').value, 10) || 3));
            config.HTSPSignalUncBurstThreshold = Math.max(1, Math.min(1000, parseInt(form.querySelector('#txtHTSPSignalUncBurstThreshold').value, 10) || 5));
            config.HTSPSignalIdrWaitSeconds = Math.max(1, Math.min(15, parseInt(form.querySelector('#txtHTSPSignalIdrWaitSeconds').value, 10) || 3));
            config.HTSPSignalRecoveryMaxReconnects = Math.max(0, Math.min(10, parseInt(form.querySelector('#txtHTSPSignalRecoveryMaxReconnects').value, 10) || 0));
            config.HTSPSignalRecoveryCooldownSeconds = Math.max(1, Math.min(300, parseInt(form.querySelector('#txtHTSPSignalRecoveryCooldownSeconds').value, 10) || 15));
            ApiClient.updatePluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId, config).then(Dashboard.processPluginConfigurationUpdateResult);
        });
        return false;
    });
}
