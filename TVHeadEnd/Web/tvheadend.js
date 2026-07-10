const TVHclientConfigurationPageVar = {
    pluginUniqueId: '3fd018e5-5e78-4e58-b280-a0c068febee0'
};

export default function (view, params) {
    let statusTimer = null;
    let statusRequestInFlight = null;

    function getStreamingMethod(config) {
        if (config.StreamingMethod) return config.StreamingMethod;
        return config.EnableSubsMaudios ? 'HttpBasic' : 'HttpTicket';
    }

    function intValue(element, fallback, min, max) {
        const parsed = parseInt(element.value, 10);
        const value = Number.isFinite(parsed) ? parsed : fallback;
        return Math.max(min, Math.min(max, value));
    }

    function loadProfiles(page, selectedProfile) {
        const select = page.querySelector('#txtProfile');
        const status = page.querySelector('#profileStatus');
        const setOptions = profiles => {
            select.options.length = 0;
            select.add(new Option('Default', ''));
            profiles.forEach(profile => select.add(new Option(profile, profile)));
            if (selectedProfile && !profiles.includes(selectedProfile)) select.add(new Option(`${selectedProfile} (unavailable)`, selectedProfile));
            select.value = selectedProfile;
        };

        setOptions([]);
        select.disabled = true;
        return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('TVHeadEnd/Profiles'), dataType: 'json' })
            .then(profiles => {
                const names = Array.isArray(profiles) ? profiles : [];
                setOptions(names);
                status.textContent = `${names.length + 1} recording profile${names.length ? 's' : ''} loaded from TVHeadend.`;
            })
            .catch(() => { status.textContent = 'Profiles could not be loaded; the saved selection is retained.'; })
            .finally(() => { select.disabled = false; });
    }

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    function formatNumber(value) {
        return value == null ? '—' : Number(value).toLocaleString();
    }

    function formatBytes(value) {
        if (value == null) return '—';
        const units = ['B', 'KiB', 'MiB', 'GiB'];
        let number = Number(value);
        let unit = 0;
        while (number >= 1024 && unit < units.length - 1) { number /= 1024; unit++; }
        return `${number.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
    }

    function formatPercent(value, raw) {
        if (value != null) return `${Number(value).toFixed(1)}%${raw == null ? '' : ` (${raw})`}`;
        return raw == null ? '—' : `raw ${raw}`;
    }

    function formatAge(ms) {
        if (ms == null || ms < 0) return '—';
        if (ms < 1000) return `${ms} ms`;
        return `${(ms / 1000).toFixed(1)} s`;
    }

    function metric(label, value) {
        return `<div class="tvhMetric"><span class="tvhMetricLabel">${escapeHtml(label)}</span><span class="tvhMetricValue">${escapeHtml(value)}</span></div>`;
    }

    function signalMetric(label, percent, raw) {
        const numericPercent = percent == null ? null : Math.max(0, Math.min(100, Number(percent)));
        const meter = numericPercent == null ? '' : `<div class="tvhMeter" role="progressbar" aria-label="${escapeHtml(label)}" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${numericPercent.toFixed(1)}"><span class="tvhMeterFill" style="width:${numericPercent.toFixed(1)}%"></span></div>`;
        return `<div class="tvhMetric"><span class="tvhMetricLabel">${escapeHtml(label)}</span><span class="tvhMetricValue">${escapeHtml(formatPercent(percent, raw))}</span>${meter}</div>`;
    }

    function setRefreshState(page, busy) {
        const button = page.querySelector('#btnRefreshStatus');
        const label = button ? button.querySelector('span') : null;
        const tuners = page.querySelector('#activeTuners');
        if (button) {
            button.disabled = busy;
            button.setAttribute('aria-busy', busy ? 'true' : 'false');
        }
        if (label) label.textContent = busy ? 'Refreshing…' : 'Refresh';
        if (tuners) tuners.setAttribute('aria-busy', busy ? 'true' : 'false');
    }

    function installControlTooltips(page) {
        const fallbackTooltips = {
            chkHideRecordingsChannel: 'Hide the synthetic TVHeadend Recordings channel from Jellyfin channel lists.'
        };

        page.querySelectorAll('.checkboxContainer input[type="checkbox"]').forEach(input => {
            const container = input.closest('.checkboxContainer');
            const label = input.closest('label');
            const description = container ? container.querySelector('.fieldDescription') : null;
            const labelText = label ? label.textContent.replace(/\s+/g, ' ').trim() : '';
            const tooltip = (description && description.textContent.replace(/\s+/g, ' ').trim())
                || fallbackTooltips[input.id]
                || `Toggle ${labelText || input.id}.`;

            input.title = tooltip;
            if (label) label.title = tooltip;

            if (description) {
                if (!description.id) description.id = `${input.id}Description`;
                input.setAttribute('aria-describedby', description.id);
            }
        });

        const refreshButton = page.querySelector('#btnRefreshStatus');
        if (refreshButton) {
            refreshButton.title = 'Refresh live plugin and tuner status now. Status also refreshes automatically every five seconds.';
            refreshButton.setAttribute('aria-label', 'Refresh live plugin and tuner status');
        }

        const saveButton = page.querySelector('.TVHclientConfigurationForm button[type="submit"]');
        if (saveButton) {
            saveButton.title = 'Save all TVHeadend plugin settings shown on this page.';
            saveButton.setAttribute('aria-label', 'Save TVHeadend plugin settings');
        }
    }

    function updateDependentState(page) {
        const recoveryEnabled = page.querySelector('#chkHTSPSignalRecoveryEnabled').checked;
        const recovery = page.querySelector('#signalRecoverySettings');
        recovery.classList.toggle('tvhDependentDisabled', !recoveryEnabled);
        recovery.querySelectorAll('input').forEach(input => { input.disabled = !recoveryEnabled; });

        const healthEnabled = page.querySelector('#chkHTSPHealthLoggingEnabled').checked;
        const health = page.querySelector('#healthIntervalContainer');
        health.classList.toggle('tvhDependentDisabled', !healthEnabled);
        health.querySelector('input').disabled = !healthEnabled;
    }

    function renderStatus(page, status) {
        const producers = Array.isArray(status.Producers) ? status.Producers : [];
        page.querySelector('#statusUpdated').textContent = `Updated ${new Date(status.GeneratedUtc).toLocaleTimeString()}`;
        page.querySelector('#statusSummary').innerHTML =
            metric('Plugin version', status.PluginVersion || 'unknown') +
            metric('TVHeadend server', status.Server || 'not configured') +
            metric('Streaming method', status.StreamingMethod || 'legacy/default') +
            metric('Active producers', String(status.ActiveProducerCount || 0));

        const container = page.querySelector('#activeTuners');
        const expandedSubscriptions = new Set(Array.from(container.querySelectorAll('details[open][data-subscription-id]'), details => details.dataset.subscriptionId));
        if (!producers.length) {
            container.innerHTML = '<div class="tvhEmpty">No active HTSP tuner subscriptions. Start a live channel to populate runtime signal and stream statistics.</div>';
            container.setAttribute('aria-busy', 'false');
            return;
        }

        container.setAttribute('aria-busy', 'false');
        container.innerHTML = producers.map(producer => {
            const drops = Number(producer.QueueIDrops || 0) + Number(producer.QueuePDrops || 0) + Number(producer.QueueBDrops || 0);
            const lockClass = producer.HasLock ? 'tvhBadgeGood' : 'tvhBadgeBad';
            const stateClass = producer.State === 'streaming' ? 'tvhBadgeGood' : producer.State === 'recovering' ? 'tvhBadgeWarn' : '';
            const streamRows = (producer.Streams || []).map(stream => `<tr>
                <td>${stream.Index}</td><td>0x${Number(stream.Pid || 0).toString(16).toUpperCase()}</td>
                <td>${escapeHtml(stream.Codec || 'unknown')}</td><td>${escapeHtml(stream.Language || '—')}</td>
                <td>${escapeHtml(stream.Title || '—')}</td><td>${formatNumber(stream.Packets)}</td>
                <td>${formatBytes(stream.Bytes)}</td><td>${formatNumber(stream.RandomAccessFrames)}</td>
            </tr>`).join('');

            return `<section class="tvhProducer">
                <div class="tvhProducerTitle"><h3>${escapeHtml(producer.Service || `Channel ${producer.ChannelId}`)}</h3>
                    <div class="tvhBadgeGroup"><span class="tvhBadge ${stateClass}">${escapeHtml(producer.State || 'unknown')}</span><span class="tvhBadge ${lockClass}">${producer.HasLock ? 'LOCK' : 'NO LOCK'}</span>${producer.AwaitingCleanVideo ? '<span class="tvhBadge tvhBadgeWarn">waiting for clean video</span>' : ''}</div>
                </div>
                <div class="tvhStatusSummary">
                    ${metric('Adapter', producer.Adapter || '—')}
                    ${metric('Network / mux', [producer.Network, producer.Mux].filter(Boolean).join(' · ') || '—')}
                    ${signalMetric('Signal', producer.SignalPercent, producer.SignalRaw)}
                    ${signalMetric('SNR', producer.SnrPercent, producer.SnrRaw)}
                    ${metric('BER / UNC', `${formatNumber(producer.Ber)} / ${formatNumber(producer.Unc)}`)}
                    ${metric('Queue drops I/P/B', `${formatNumber(producer.QueueIDrops)}/${formatNumber(producer.QueuePDrops)}/${formatNumber(producer.QueueBDrops)}`)}
                    ${metric('Queue', `${formatNumber(producer.QueuePackets)} packets · ${formatBytes(producer.QueueBytes)}`)}
                    ${metric('Last mux packet', formatAge(producer.LastMuxPacketAgeMs))}
                    ${metric('Viewers / readers', `${producer.SharedPlaybackCount || 0} / ${producer.ActiveReaderCount || 0}`)}
                    ${metric('Reconnects', `${producer.ReconnectAttempts || 0} normal · ${producer.SignalRecoveryAttempts || 0} signal`)}
                    ${metric('Startup cache', `${producer.KeyframeStartupReady ? 'ready' : 'waiting'} · ${formatBytes(producer.StartupCacheBytes)}`)}
                    ${metric('Subscription', `#${producer.SubscriptionId || 0} · ${escapeHtml(producer.ChannelId || '')}`)}
                </div>
                <details data-subscription-id="${Number(producer.SubscriptionId || 0)}"><summary>Stream statistics (${(producer.Streams || []).length})</summary>
                    <div class="tvhTableWrap" tabindex="0" role="region" aria-label="Stream statistics for ${escapeHtml(producer.Service || `channel ${producer.ChannelId}`)}"><table class="tvhTable"><caption class="tvhSrOnly">Per-stream packet and keyframe statistics</caption><thead><tr><th scope="col">Index</th><th scope="col">PID</th><th scope="col">Codec</th><th scope="col">Language</th><th scope="col">Title</th><th scope="col">Packets</th><th scope="col">Bytes</th><th scope="col">Keyframes</th></tr></thead><tbody>${streamRows}</tbody></table></div>
                </details>
            </section>`;
        }).join('');
        container.querySelectorAll('details[data-subscription-id]').forEach(details => { details.open = expandedSubscriptions.has(details.dataset.subscriptionId); });
    }

    function loadStatus(page, showLoading) {
        if (statusRequestInFlight) return statusRequestInFlight;
        const announcer = page.querySelector('#statusAnnouncer');
        if (showLoading) {
            page.querySelector('#statusUpdated').textContent = 'Refreshing runtime information…';
            if (announcer) announcer.textContent = 'Refreshing TVHeadend runtime information.';
        }
        setRefreshState(page, true);
        statusRequestInFlight = ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('TVHeadEnd/Status'),
            dataType: 'json'
        }).then(status => {
            renderStatus(page, status);
            if (showLoading && announcer) announcer.textContent = 'TVHeadend runtime information refreshed.';
        }).catch(error => {
            page.querySelector('#statusUpdated').textContent = 'Unable to load runtime status';
            if (announcer) announcer.textContent = 'Unable to load TVHeadend runtime status.';
            page.querySelector('#activeTuners').innerHTML = `<div class="tvhEmpty" role="alert">Status endpoint failed: ${escapeHtml(error && error.message ? error.message : 'unknown error')}</div>`;
        }).finally(() => {
            statusRequestInFlight = null;
            setRefreshState(page, false);
        });
        return statusRequestInFlight;
    }

    function startStatusPolling(page) {
        stopStatusPolling();
        loadStatus(page, true);
        statusTimer = setInterval(() => loadStatus(page, false), 5000);
    }

    function stopStatusPolling() {
        if (statusTimer) { clearInterval(statusTimer); statusTimer = null; }
    }

    installControlTooltips(view);

    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        const page = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(config => {
            page.querySelector('#txtTVH_ServerName').value = config.TVH_ServerName || '';
            page.querySelector('#txtHTTP_Port').value = config.HTTP_Port || 9981;
            page.querySelector('#txtHTSP_Port').value = config.HTSP_Port || 9982;
            page.querySelector('#txtWebRoot').value = config.WebRoot || '/';
            page.querySelector('#txtUserName').value = config.Username || '';
            page.querySelector('#txtPassword').value = config.Password || '';
            page.querySelector('#txtPriority').value = Number.isInteger(config.Priority) && config.Priority >= 0 && config.Priority <= 4 ? config.Priority : 2;
            loadProfiles(page, config.Profile || '');
            page.querySelector('#txtPrePadding').value = Number.isFinite(config.Pre_Padding) ? config.Pre_Padding : 0;
            page.querySelector('#txtPostPadding').value = Number.isFinite(config.Post_Padding) ? config.Post_Padding : 0;
            page.querySelector('#selChannelType').value = config.ChannelType || 'Ignore';
            page.querySelector('#chkHideRecordingsChannel').checked = config.HideRecordingsChannel === true;
            page.querySelector('#selStreamingMethod').value = getStreamingMethod(config);
            page.querySelector('#chkForceDeinterlace').checked = config.ForceDeinterlace === true;
            page.querySelector('#txtHTSPQueueDepth').value = Number.isFinite(config.HTSPQueueDepth) ? config.HTSPQueueDepth : 2000000;
            page.querySelector('#txtHTSPStallTimeoutSeconds').value = Number.isFinite(config.HTSPStallTimeoutSeconds) ? config.HTSPStallTimeoutSeconds : 15;
            page.querySelector('#chkHTSPFilterControlStreams').checked = config.HTSPFilterControlStreams === true;
            page.querySelector('#chkHTSPSignalRecoveryEnabled').checked = config.HTSPSignalRecoveryEnabled !== false;
            page.querySelector('#txtHTSPSignalLockLossSeconds').value = Number.isFinite(config.HTSPSignalLockLossSeconds) ? config.HTSPSignalLockLossSeconds : 3;
            page.querySelector('#txtHTSPSignalUncBurstThreshold').value = Number.isFinite(config.HTSPSignalUncBurstThreshold) ? config.HTSPSignalUncBurstThreshold : 5;
            page.querySelector('#txtHTSPSignalIdrWaitSeconds').value = Number.isFinite(config.HTSPSignalIdrWaitSeconds) ? config.HTSPSignalIdrWaitSeconds : 3;
            page.querySelector('#txtHTSPSignalRecoveryMaxReconnects').value = Number.isFinite(config.HTSPSignalRecoveryMaxReconnects) ? config.HTSPSignalRecoveryMaxReconnects : 2;
            page.querySelector('#txtHTSPSignalRecoveryCooldownSeconds').value = Number.isFinite(config.HTSPSignalRecoveryCooldownSeconds) ? config.HTSPSignalRecoveryCooldownSeconds : 15;
            page.querySelector('#chkHTSPEnableStreamSharing').checked = config.HTSPEnableStreamSharing !== false;
            page.querySelector('#chkHTSPKeyframeStartupEnabled').checked = config.HTSPKeyframeStartupEnabled !== false;
            page.querySelector('#chkHTSPHealthLoggingEnabled').checked = config.HTSPHealthLoggingEnabled !== false;
            page.querySelector('#txtHTSPHealthLogIntervalSeconds').value = Number.isFinite(config.HTSPHealthLogIntervalSeconds) ? config.HTSPHealthLogIntervalSeconds : 30;
            page.querySelector('#chkHTSPSignalHealthLoggingEnabled').checked = config.HTSPSignalHealthLoggingEnabled !== false;
            page.querySelector('#chkHTSPDetailedDiagnostics').checked = config.HTSPDetailedDiagnostics === true;
            updateDependentState(page);
            Dashboard.hideLoadingMsg();
            startStatusPolling(page);
        });
    });

    view.addEventListener('viewhide', stopStatusPolling);
    view.querySelector('#btnRefreshStatus').addEventListener('click', () => loadStatus(view, true));
    view.querySelector('#chkHTSPSignalRecoveryEnabled').addEventListener('change', () => updateDependentState(view));
    view.querySelector('#chkHTSPHealthLoggingEnabled').addEventListener('change', () => updateDependentState(view));

    view.querySelector('.TVHclientConfigurationForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        const form = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(config => {
            config.TVH_ServerName = form.querySelector('#txtTVH_ServerName').value.trim();
            config.HTTP_Port = intValue(form.querySelector('#txtHTTP_Port'), 9981, 1, 65535);
            config.HTSP_Port = intValue(form.querySelector('#txtHTSP_Port'), 9982, 1, 65535);
            config.WebRoot = form.querySelector('#txtWebRoot').value.trim() || '/';
            config.Username = form.querySelector('#txtUserName').value;
            config.Password = form.querySelector('#txtPassword').value;
            config.Priority = intValue(form.querySelector('#txtPriority'), 2, 0, 4);
            config.Profile = form.querySelector('#txtProfile').value.trim();
            config.Pre_Padding = intValue(form.querySelector('#txtPrePadding'), 0, 0, 86400);
            config.Post_Padding = intValue(form.querySelector('#txtPostPadding'), 0, 0, 86400);
            config.ChannelType = form.querySelector('#selChannelType').value;
            config.HideRecordingsChannel = form.querySelector('#chkHideRecordingsChannel').checked;
            config.StreamingMethod = form.querySelector('#selStreamingMethod').value;
            config.EnableSubsMaudios = config.StreamingMethod === 'HttpBasic';
            config.ForceDeinterlace = form.querySelector('#chkForceDeinterlace').checked;
            config.HTSPQueueDepth = intValue(form.querySelector('#txtHTSPQueueDepth'), 2000000, 0, 20000000);
            config.HTSPStallTimeoutSeconds = intValue(form.querySelector('#txtHTSPStallTimeoutSeconds'), 15, 0, 120);
            config.HTSPFilterControlStreams = form.querySelector('#chkHTSPFilterControlStreams').checked;
            config.HTSPSignalRecoveryEnabled = form.querySelector('#chkHTSPSignalRecoveryEnabled').checked;
            config.HTSPSignalLockLossSeconds = intValue(form.querySelector('#txtHTSPSignalLockLossSeconds'), 3, 1, 30);
            config.HTSPSignalUncBurstThreshold = intValue(form.querySelector('#txtHTSPSignalUncBurstThreshold'), 5, 1, 1000);
            config.HTSPSignalIdrWaitSeconds = intValue(form.querySelector('#txtHTSPSignalIdrWaitSeconds'), 3, 1, 15);
            config.HTSPSignalRecoveryMaxReconnects = intValue(form.querySelector('#txtHTSPSignalRecoveryMaxReconnects'), 2, 0, 10);
            config.HTSPSignalRecoveryCooldownSeconds = intValue(form.querySelector('#txtHTSPSignalRecoveryCooldownSeconds'), 15, 1, 300);
            config.HTSPEnableStreamSharing = form.querySelector('#chkHTSPEnableStreamSharing').checked;
            config.HTSPKeyframeStartupEnabled = form.querySelector('#chkHTSPKeyframeStartupEnabled').checked;
            config.HTSPHealthLoggingEnabled = form.querySelector('#chkHTSPHealthLoggingEnabled').checked;
            config.HTSPHealthLogIntervalSeconds = intValue(form.querySelector('#txtHTSPHealthLogIntervalSeconds'), 30, 5, 600);
            config.HTSPSignalHealthLoggingEnabled = form.querySelector('#chkHTSPSignalHealthLoggingEnabled').checked;
            config.HTSPDetailedDiagnostics = form.querySelector('#chkHTSPDetailedDiagnostics').checked;
            return ApiClient.updatePluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId, config);
        }).then(result => {
            Dashboard.processPluginConfigurationUpdateResult(result);
            loadStatus(view, false);
            loadProfiles(view, form.querySelector('#txtProfile').value.trim());
        });
        return false;
    });
}
