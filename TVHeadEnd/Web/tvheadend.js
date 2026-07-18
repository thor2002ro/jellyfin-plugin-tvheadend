const TVHclientConfigurationPageVar = {
    pluginUniqueId: '3fd018e5-5e78-4e58-b280-a0c068febee0'
};

export default function (view, params) {
    let statusTimer = null;
    let statusRequestInFlight = null;
    const bytesPerMiB = 1024 * 1024;
    const defaultQueueDepthBytes = 10 * bytesPerMiB;
    const maxQueueDepthMiB = 20;

    function getStreamingMethod(config) {
        return config.StreamingMethod || 'Htsp';
    }

    function intValue(element, fallback, min, max) {
        const parsed = parseInt(element.value, 10);
        const value = Number.isFinite(parsed) ? parsed : fallback;
        return Math.max(min, Math.min(max, value));
    }

    function queueDepthBytesToMiB(bytes) {
        const value = Math.max(0, Math.min(maxQueueDepthMiB * bytesPerMiB, Number(bytes || 0)));
        return (value / bytesPerMiB).toFixed(2).replace(/\.?0+$/, '');
    }

    function loadQueueDepth(element, bytes) {
        const value = Number.isFinite(bytes) ? bytes : defaultQueueDepthBytes;
        element.dataset.bytes = String(value);
        element.value = queueDepthBytesToMiB(value);
    }

    function saveQueueDepth(element) {
        if (element.value === queueDepthBytesToMiB(Number(element.dataset.bytes))) {
            return Number(element.dataset.bytes);
        }

        const parsed = parseFloat(element.value);
        const mib = Number.isFinite(parsed) ? Math.max(0, Math.min(maxQueueDepthMiB, parsed)) : defaultQueueDepthBytes / bytesPerMiB;
        return Math.round(mib * bytesPerMiB);
    }

    function priorityValue(value) {
        return [0, 1, 2, 3, 4, 6].includes(Number(value)) ? Number(value) : 2;
    }

    function loadConfig(page, config) {
        const values = config || {};
        page.querySelector('#txtTVH_ServerName').value = values.TVH_ServerName || '';
        loadTimeZones(page.querySelector('#txtTVH_TimeZoneId'), page.querySelector('#tvhTimeZones'), values.TVH_TimeZoneId || '');
        page.querySelector('#txtHTTP_Port').value = Number.isFinite(values.HTTP_Port) ? values.HTTP_Port : 9981;
        page.querySelector('#chkUseHttps').checked = values.UseHttps === true;
        page.querySelector('#txtHTSP_Port').value = Number.isFinite(values.HTSP_Port) ? values.HTSP_Port : 9982;
        page.querySelector('#txtWebRoot').value = values.WebRoot || '/';
        page.querySelector('#txtUserName').value = values.Username || '';
        page.querySelector('#txtPassword').value = values.Password || '';
        page.querySelector('#txtPriority').value = priorityValue(values.Priority);
        loadProfiles(page, values.Profile || '');
        page.querySelector('#txtPrePadding').value = Number.isFinite(values.Pre_Padding) ? values.Pre_Padding : 0;
        page.querySelector('#txtPostPadding').value = Number.isFinite(values.Post_Padding) ? values.Post_Padding : 0;
        page.querySelector('#selChannelType').value = values.ChannelType || 'Ignore';
        page.querySelector('#chkHideRecordingsChannel').checked = values.HideRecordingsChannel === true;
        page.querySelector('#selStreamingMethod').value = getStreamingMethod(values);
        page.querySelector('#chkForceDeinterlace').checked = values.ForceDeinterlace === true;
        loadQueueDepth(page.querySelector('#txtHTSPQueueDepth'), values.HTSPQueueDepth);
        page.querySelector('#txtHTSPInitialTuneBufferMs').value = Number.isFinite(values.HTSPInitialTuneBufferMs) ? values.HTSPInitialTuneBufferMs : 0;
        page.querySelector('#txtHTSPStallTimeoutSeconds').value = Number.isFinite(values.HTSPStallTimeoutSeconds) ? values.HTSPStallTimeoutSeconds : 15;
        page.querySelector('#chkHTSPFilterControlStreams').checked = values.HTSPFilterControlStreams === true;
        page.querySelector('#chkHTSPSignalRecoveryEnabled').checked = values.HTSPSignalRecoveryEnabled !== false;
        page.querySelector('#txtHTSPSignalLockLossSeconds').value = Number.isFinite(values.HTSPSignalLockLossSeconds) ? values.HTSPSignalLockLossSeconds : 3;
        page.querySelector('#txtHTSPSignalUncBurstThreshold').value = Number.isFinite(values.HTSPSignalUncBurstThreshold) ? values.HTSPSignalUncBurstThreshold : 5;
        page.querySelector('#txtHTSPSignalIdrWaitSeconds').value = Number.isFinite(values.HTSPSignalIdrWaitSeconds) ? values.HTSPSignalIdrWaitSeconds : 3;
        page.querySelector('#txtHTSPSignalRecoveryMaxReconnects').value = Number.isFinite(values.HTSPSignalRecoveryMaxReconnects) ? values.HTSPSignalRecoveryMaxReconnects : 2;
        page.querySelector('#txtHTSPSignalRecoveryCooldownSeconds').value = Number.isFinite(values.HTSPSignalRecoveryCooldownSeconds) ? values.HTSPSignalRecoveryCooldownSeconds : 15;
        page.querySelector('#chkHTSPEnableStreamSharing').checked = values.HTSPEnableStreamSharing !== false;
        page.querySelector('#chkHTSPKeyframeStartupEnabled').checked = values.HTSPKeyframeStartupEnabled !== false;
        page.querySelector('#chkHTSPHealthLoggingEnabled').checked = values.HTSPHealthLoggingEnabled !== false;
        page.querySelector('#txtHTSPHealthLogIntervalSeconds').value = Number.isFinite(values.HTSPHealthLogIntervalSeconds) ? values.HTSPHealthLogIntervalSeconds : 30;
        page.querySelector('#chkHTSPSignalHealthLoggingEnabled').checked = values.HTSPSignalHealthLoggingEnabled !== false;
        page.querySelector('#chkHTSPDetailedDiagnostics').checked = values.HTSPDetailedDiagnostics === true;
        updateDependentState(page);
    }

    function loadProfiles(page, selectedProfile) {
        const select = page.querySelector('#txtProfile');
        const status = page.querySelector('#profileStatus');
        const setOptions = profiles => {
            select.options.length = 0;
            select.add(new Option('Default', ''));
            profiles.forEach(profile => select.add(new Option(profile.Name, profile.Id || profile.Name)));
            const selected = profiles.find(profile => profile.Id === selectedProfile || profile.Name === selectedProfile);
            if (selected) select.value = selected.Id || selected.Name;
            else if (selectedProfile) {
                select.add(new Option(`${selectedProfile} (unavailable)`, selectedProfile));
                select.value = selectedProfile;
            }
        };

        setOptions([]);
        select.disabled = true;
        return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('TVHeadEnd/Profiles'), dataType: 'json' })
            .then(profiles => {
                const items = Array.isArray(profiles) ? profiles : [];
                setOptions(items);
                status.textContent = `${items.length + 1} recording profile${items.length ? 's' : ''} loaded from TVHeadend.`;
            })
            .catch(response => {
                status.textContent = response?.status === 403
                    ? 'TVHeadend denied DVR profile access. Enable Basic recorder access and allow the required DVR configuration for the configured user.'
                    : 'Profiles could not be loaded; the saved selection is retained.';
            })
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

    function formatPercent(value) {
        return value == null ? '—' : `${Number(value).toFixed(1)}%`;
    }

    function formatAge(ms) {
        if (ms == null || ms < 0) return '—';
        if (ms < 1000) return `${ms} ms`;
        return `${(ms / 1000).toFixed(1)} s`;
    }

    function metric(label, value, help) {
        const title = help ? ` title="${escapeHtml(help)}" aria-label="${escapeHtml(`${label}: ${value}. ${help}`)}"` : '';
        return `<div class="tvhMetric"${title}><span class="tvhMetricLabel">${escapeHtml(label)}</span><span class="tvhMetricValue">${escapeHtml(value)}</span></div>`;
    }

    function signalMetric(label, percent, absolute, unit, help) {
        const numericPercent = percent == null ? null : Math.max(0, Math.min(100, Number(percent)));
        const meter = numericPercent == null ? '' : `<div class="tvhMeter" role="progressbar" aria-label="${escapeHtml(label)}" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${numericPercent.toFixed(1)}"><span class="tvhMeterFill" style="width:${numericPercent.toFixed(1)}%"></span></div>`;
        const absoluteValue = absolute == null ? '' : ` (${Number(absolute).toFixed(1)} ${unit})`;
        const value = formatPercent(percent) + absoluteValue;
        const title = help ? ` title="${escapeHtml(help)}" aria-label="${escapeHtml(`${label}: ${value}. ${help}`)}"` : '';
        return `<div class="tvhMetric"${title}><span class="tvhMetricLabel">${escapeHtml(label)}</span><span class="tvhMetricValue">${escapeHtml(value)}</span>${meter}</div>`;
    }

    function sumStreamField(channel, field) {
        return (channel.Streams || []).reduce((total, stream) => total + Number(stream[field] || 0), 0);
    }

    function dropSummary(channel) {
        const queueI = Number(channel.QueueIDrops || 0);
        const queueP = Number(channel.QueuePDrops || 0);
        const queueB = Number(channel.QueueBDrops || 0);
        const queue = queueI + queueP + queueB;
        const mux = sumStreamField(channel, 'TimestampAnomalyDrops');
        const video = Number(channel.DamagedVideoDrops || 0);

        return {
            value: `${formatNumber(queue + mux + video)} total · queue ${formatNumber(queueI)}/${formatNumber(queueP)}/${formatNumber(queueB)} · mux ${formatNumber(mux)} · video ${formatNumber(video)}`,
            help: 'Dropped packets or frames. Queue is TVHeadend I/P/B frame drops, mux is plugin timestamp-safety drops, video is damaged inter-frame video withheld until a clean keyframe.'
        };
    }

    function videoDamageSummary(channel) {
        const age = channel.VideoDamageAgeMs == null ? '' : ` · ${formatAge(channel.VideoDamageAgeMs)}`;
        const reason = channel.LastVideoDamageReason ? ` · ${channel.LastVideoDamageReason}` : '';

        return {
            value: `${formatNumber(channel.VideoDamageEvents)} events · ${formatNumber(channel.DamagedVideoDrops)} drops${age}${reason}`,
            help: 'Times the plugin detected unsafe video and withheld frames until a clean random-access frame arrived.'
        };
    }

    function reconnectSummary(channel) {
        return {
            value: `${formatNumber(channel.ReconnectAttempts || 0)} normal · ${formatNumber(channel.SignalRecoveryAttempts || 0)} signal`,
            help: 'Normal reconnects plus reconnects triggered by tuner signal recovery.'
        };
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

        const resetButton = page.querySelector('#btnResetDefaults');
        if (resetButton) {
            resetButton.title = 'Reset TVHeadend plugin settings to their default values, keeping hostname, username, and password.';
            resetButton.setAttribute('aria-label', 'Reset TVHeadend plugin settings to defaults, keeping hostname, username, and password');
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
        const runningChannels = Array.isArray(status.RunningChannels) ? status.RunningChannels : Array.isArray(status.Producers) ? status.Producers : [];
        const serverStatus = status.Connected
            ? `${status.Server || 'not configured'} · Connected · ${status.ServerVersion || 'unknown version'} · HTSP ${status.HtspProtocolVersion == null ? 'unknown' : status.HtspProtocolVersion}`
            : `${status.Server || 'not configured'} · Disconnected`;
        page.querySelector('#statusUpdated').textContent = `Updated ${new Date(status.GeneratedUtc).toLocaleTimeString()}`;
        page.querySelector('#statusSummary').innerHTML =
            metric('Plugin version', status.PluginVersion || 'unknown') +
            metric('TVHeadend server', serverStatus) +
            metric('Streaming method', status.StreamingMethod || 'legacy/default') +
            metric('Running Channels', String(status.RunningChannelCount ?? status.ActiveProducerCount ?? 0));

        const container = page.querySelector('#activeTuners');
        const expandedSubscriptions = new Set(Array.from(container.querySelectorAll('details[open][data-subscription-id]'), details => details.dataset.subscriptionId));
        if (!runningChannels.length) {
            container.innerHTML = '<div class="tvhEmpty">No active HTSP tuner subscriptions. Start a live channel to populate runtime signal and stream statistics.</div>';
            container.setAttribute('aria-busy', 'false');
            return;
        }

        container.setAttribute('aria-busy', 'false');
        container.innerHTML = runningChannels.map(channel => {
            const drops = dropSummary(channel);
            const videoDamage = videoDamageSummary(channel);
            const reconnects = reconnectSummary(channel);
            const lockClass = channel.HasLock ? 'tvhBadgeGood' : 'tvhBadgeBad';
            const stateClass = channel.State === 'streaming' ? 'tvhBadgeGood' : channel.State === 'recovering' ? 'tvhBadgeWarn' : '';
            const streamRows = (channel.Streams || []).map(stream => `<tr>
                <td>${stream.Index}</td><td>0x${Number(stream.Pid || 0).toString(16).toUpperCase()}</td>
                <td>${escapeHtml(stream.Codec || 'unknown')}</td><td>${escapeHtml(stream.Language || '—')}</td>
                <td>${escapeHtml(stream.Title || '—')}</td><td>${formatNumber(stream.Packets)}</td>
                <td>${formatBytes(stream.Bytes)}</td><td>${formatNumber(stream.RandomAccessFrames)}</td>
                <td title="Resets are detected timeline jumps. Drops are mux packets rejected because their timestamps were unsafe. AUD is the number of H.264/H.265 access unit delimiters inserted.">resets ${formatNumber(stream.TimestampDiscontinuities)} · drops ${formatNumber(stream.TimestampAnomalyDrops)} · AUD ${formatNumber(stream.AudInsertions)}</td>
            </tr>`).join('');

            return `<section class="tvhRunningChannel">
                <div class="tvhRunningChannelTitle"><h3>${escapeHtml(channel.Service || `Channel ${channel.ChannelId}`)}</h3>
                    <div class="tvhBadgeGroup"><span class="tvhBadge ${stateClass}">${escapeHtml(channel.State || 'unknown')}</span><span class="tvhBadge ${lockClass}">${channel.HasLock ? 'LOCK' : 'NO LOCK'}</span>${channel.AwaitingCleanVideo ? '<span class="tvhBadge tvhBadgeWarn">waiting for clean video</span>' : ''}</div>
                </div>
                <div class="tvhStatusSummary">
                    ${metric('Adapter', channel.Adapter || '—', 'TV tuner adapter reported by TVHeadend.')}
                    ${metric('Network / mux', [channel.Network, channel.Mux].filter(Boolean).join(' · ') || '—', 'Broadcast network and multiplex currently feeding this stream.')}
                    ${signalMetric('Signal', channel.SignalPercent, channel.SignalDbm, 'dBm', 'Tuner signal strength. Higher is better.')}
                    ${signalMetric('SNR', channel.SnrPercent, channel.SnrDb, 'dB', 'Signal-to-noise ratio. Higher means a cleaner signal.')}
                    ${metric('BER / UNC', `${formatNumber(channel.Ber)} / ${formatNumber(channel.Unc)}`, 'Bit errors and uncorrected blocks from the tuner. Lower is better; UNC growth usually means damaged input.')}
                    ${metric('Drops', drops.value, drops.help)}
                    ${metric('Video damage', videoDamage.value, videoDamage.help)}
                    ${metric('Queue', `${formatNumber(channel.QueuePackets)} packets · ${formatBytes(channel.QueueBytes)}`, 'TVHeadend subscription queue depth currently reported for this live stream.')}
                    ${metric('Last mux packet', formatAge(channel.LastMuxPacketAgeMs), 'Time since the plugin last received a playable mux packet. Large values can mean a stalled stream.')}
                    ${metric('Viewers / readers', `${channel.SharedPlaybackCount || 0} / ${channel.ActiveReaderCount || 0}`, 'Shared playback sessions and active HTTP readers attached to this running channel.')}
                    ${metric('Reconnects', reconnects.value, reconnects.help)}
                    ${metric('Startup cache', `${channel.KeyframeStartupReady ? 'ready' : 'waiting'} · ${formatBytes(channel.StartupCacheBytes)}`, 'Buffered startup data used so new viewers can begin on a clean keyframe.')}
                    ${metric('HTSP id', `#${channel.SubscriptionId || 0} · ${channel.ChannelId || ''}`, 'Client-assigned HTSP subscription id. It increments on reconnect; it is not the active subscription count.')}
                </div>
                <details data-subscription-id="${Number(channel.SubscriptionId || 0)}"><summary>Stream statistics (${(channel.Streams || []).length})</summary>
                    <div class="tvhTableWrap" tabindex="0" role="region" aria-label="Stream statistics for ${escapeHtml(channel.Service || `channel ${channel.ChannelId}`)}"><table class="tvhTable"><caption class="tvhSrOnly">Per-stream packet, keyframe, and event statistics</caption><thead><tr><th scope="col">Index</th><th scope="col">PID</th><th scope="col">Codec</th><th scope="col">Language</th><th scope="col">Title</th><th scope="col">Packets</th><th scope="col">Bytes</th><th scope="col">Keyframes</th><th scope="col">Events</th></tr></thead><tbody>${streamRows}</tbody></table></div>
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

    function loadTimeZones(input, list, selected) {
        const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : [];
        if (selected && !zones.includes(selected)) { zones.unshift(selected); }
        list.textContent = '';
        zones.forEach(zone => list.appendChild(new Option(zone, zone)));
        input.value = selected || '';
    }

    installControlTooltips(view);

    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        const page = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(config => {
            loadConfig(page, config);
            Dashboard.hideLoadingMsg();
            startStatusPolling(page);
        });
    });

    view.addEventListener('viewhide', stopStatusPolling);
    view.querySelector('#btnRefreshStatus').addEventListener('click', () => loadStatus(view, true));
    view.querySelector('#chkHTSPSignalRecoveryEnabled').addEventListener('change', () => updateDependentState(view));
    view.querySelector('#chkHTSPHealthLoggingEnabled').addEventListener('change', () => updateDependentState(view));
    view.querySelector('#btnResetDefaults').addEventListener('click', function () {
        if (!window.confirm('Reset TVHeadend plugin settings to defaults? Hostname, username, and password will be kept.')) return;
        Dashboard.showLoadingMsg();
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('TVHeadEnd/Configuration/ResetDefaults'),
            dataType: 'json'
        }).then(config => {
            loadConfig(view, config);
            loadStatus(view, false);
        }).finally(() => Dashboard.hideLoadingMsg());
    });

    view.querySelector('.TVHclientConfigurationForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        const form = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(config => {
            config.TVH_ServerName = form.querySelector('#txtTVH_ServerName').value.trim();
            config.TVH_TimeZoneId = form.querySelector('#txtTVH_TimeZoneId').value.trim();
            config.HTTP_Port = intValue(form.querySelector('#txtHTTP_Port'), 9981, 1, 65535);
            config.UseHttps = form.querySelector('#chkUseHttps').checked;
            config.HTSP_Port = intValue(form.querySelector('#txtHTSP_Port'), 9982, 1, 65535);
            config.WebRoot = form.querySelector('#txtWebRoot').value.trim() || '/';
            config.Username = form.querySelector('#txtUserName').value;
            config.Password = form.querySelector('#txtPassword').value;
            config.Priority = priorityValue(form.querySelector('#txtPriority').value);
            config.Profile = form.querySelector('#txtProfile').value.trim();
            config.Pre_Padding = intValue(form.querySelector('#txtPrePadding'), 0, 0, 86400);
            config.Post_Padding = intValue(form.querySelector('#txtPostPadding'), 0, 0, 86400);
            config.ChannelType = form.querySelector('#selChannelType').value;
            config.HideRecordingsChannel = form.querySelector('#chkHideRecordingsChannel').checked;
            config.StreamingMethod = form.querySelector('#selStreamingMethod').value;
            config.ForceDeinterlace = form.querySelector('#chkForceDeinterlace').checked;
            config.HTSPQueueDepth = saveQueueDepth(form.querySelector('#txtHTSPQueueDepth'));
            config.HTSPInitialTuneBufferMs = intValue(form.querySelector('#txtHTSPInitialTuneBufferMs'), 0, 0, 3000);
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
