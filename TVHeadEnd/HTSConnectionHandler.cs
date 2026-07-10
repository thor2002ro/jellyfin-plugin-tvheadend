using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Configuration;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;

namespace TVHeadEnd
{
    public sealed class HTSConnectionHandler : HTSConnectionListener, IDisposable
    {
        private readonly object _lock = new Object();
        private static readonly TimeSpan InitialLoadTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(10);

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<HTSConnectionHandler> _logger;

        private TaskCompletionSource<bool> _initialLoad = CreateInitialLoadCompletion();
        private volatile Boolean _connected = false;
        private volatile Boolean _configured = false;

        private HTSConnectionAsync _htsConnection;
        private int _priority;
        private string _profile;
        private string _httpBaseUrl;
        private string _channelType;
        private string _tvhServerName;
        private int _httpPort;
        private int _htspPort;
        private string _webRoot;
        private string _userName;
        private string _password;
        private string _streamingMethod;
        private bool _forceDeinterlace;
        private TimeZoneInfo _tvhTimeZone;

        // Data helpers
        private readonly ChannelDataHelper _channelDataHelper;
        private readonly DvrDataHelper _dvrDataHelper;
        private readonly AutorecDataHelper _autorecDataHelper;

        private Dictionary<string, string> _headers = new Dictionary<string, string>();

        public HTSConnectionHandler(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<HTSConnectionHandler>();

            // System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger.LogDebug("[TVHclient] HTSConnectionHandler");

            _channelDataHelper = new ChannelDataHelper(loggerFactory.CreateLogger<ChannelDataHelper>());
            _dvrDataHelper = new DvrDataHelper(loggerFactory.CreateLogger<DvrDataHelper>());
            _autorecDataHelper = new AutorecDataHelper(loggerFactory.CreateLogger<AutorecDataHelper>());

            // The channel type is applied in Init(), once the configuration has been read.
            // ChannelDataHelper defaults to "Ignore" until then.
        }

        private static TaskCompletionSource<bool> CreateInitialLoadCompletion()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void ResetInitialLoad()
        {
            var previous = Interlocked.Exchange(ref _initialLoad, CreateInitialLoadCompletion());
            previous.TrySetResult(false);
        }

        public async Task<int> WaitForInitialLoadAsync(CancellationToken cancellationToken)
        {
            await Task.Run(() => ensureConnection(cancellationToken), cancellationToken).ConfigureAwait(false);
            try
            {
                return await Volatile.Read(ref _initialLoad).Task.WaitAsync(InitialLoadTimeout, cancellationToken).ConfigureAwait(false) ? 0 : -1;
            }
            catch (TimeoutException)
            {
                return -1;
            }
        }

        private void Init()
        {
            if (_configured == true)
            {
                return;
            }

            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Init()");

            var config = Plugin.Instance.Configuration;

            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Config initialized");

            if (string.IsNullOrEmpty(config.TVH_ServerName))
            {
                const string Message = "[TVHclient] HTSConnectionHandler.EnsureConnection: TVH server name must be configured";
                _logger.LogError(Message);
                throw new InvalidOperationException(Message);
            }

            if (string.IsNullOrEmpty(config.Username))
            {
                const string Message = "[TVHclient] HTSConnectionHandler.EnsureConnection: username must be configured";
                _logger.LogError(Message);
                throw new InvalidOperationException(Message);
            }

            if (string.IsNullOrEmpty(config.Password))
            {
                const string Message = "[TVHclient] HTSConnectionHandler.EnsureConnection: password must be configured";
                _logger.LogError(Message);
                throw new InvalidOperationException(Message);
            }

            _priority = config.Priority;
            _profile = config.Profile.Trim();
            _channelType = config.ChannelType.Trim();
            _streamingMethod = StreamingMethods.GetEffective(config.StreamingMethod);
            _forceDeinterlace = config.ForceDeinterlace;
            var timeZoneId = config.TVH_TimeZoneId?.Trim();
            if (!string.IsNullOrEmpty(timeZoneId))
            {
                try
                {
                    _tvhTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                }
                catch (TimeZoneNotFoundException)
                {
                    _logger.LogWarning("[TVHclient] Unknown TVHeadend timezone '{TimeZoneId}'; falling back to the current server UTC offset", timeZoneId);
                }
                catch (InvalidTimeZoneException)
                {
                    _logger.LogWarning("[TVHclient] Invalid TVHeadend timezone '{TimeZoneId}'; falling back to the current server UTC offset", timeZoneId);
                }
            }

            if ((_priority < 0 || _priority > 4) && _priority != 6)
            {
                _priority = 2;
                _logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection: invalid priority - set to 2");
            }

            _tvhServerName = config.TVH_ServerName.Trim();
            _httpPort = config.HTTP_Port;
            _htspPort = config.HTSP_Port;

            _userName = config.Username.Trim();
            _password = config.Password.Trim();

            var httpScheme = config.UseHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            _httpBaseUrl = new UriBuilder(httpScheme, _tvhServerName, _httpPort, _webRoot).Uri.AbsoluteUri.TrimEnd('/');

            string authInfo = _userName + ":" + _password;
            authInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes(authInfo));
            _headers["Authorization"] = "Basic " + authInfo;

            // The constructor runs before any configuration is available, so the channel type
            // has to be handed to the data helper here, once it has actually been read.
            _channelDataHelper.SetChannelType4Other(_channelType);

            _configured = true;
        }

        /// <summary>
        /// Trims a web root into the '' or '/prefix' form used when building URLs.
        /// </summary>
        /// <param name="webRoot">The raw web root.</param>
        /// <returns>The normalized web root.</returns>
        private static string NormalizeWebRoot(string? webRoot)
        {
            if (string.IsNullOrWhiteSpace(webRoot))
            {
                return string.Empty;
            }

            string trimmed = webRoot.Trim().TrimEnd('/');

            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
        }

        /// <summary>
        /// Adopts the web root TVHeadend reported during the handshake.
        /// </summary>
        /// <remarks>
        /// The server knows its own path prefix, so it is the only source for this value; an
        /// absent field means TVHeadend is served from the root. The HTTP URLs are rebuilt
        /// because they are assembled in Init(), before a connection exists.
        /// </remarks>
        /// <param name="reportedWebRoot">The web root from the hello response.</param>
        private void ApplyServerWebRoot(string? reportedWebRoot)
        {
            string resolved = NormalizeWebRoot(reportedWebRoot);

            if (string.Equals(resolved, _webRoot, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogInformation(
                "[TVHclient] HTSConnectionHandler: TVHeadend reported web root '{ReportedWebRoot}'",
                resolved);

            _webRoot = resolved;
            _httpBaseUrl = BuildHttpBaseUrl();
        }

        /// <summary>
        /// Builds the TVHeadend HTTP base URL from the current settings.
        /// </summary>
        /// <returns>The HTTP base URL.</returns>
        private string BuildHttpBaseUrl()
        {
            if (_enableSubsMaudios)
            {
                // Use HTTP basic auth instead of TVH ticketing system for authentication to allow the users to switch subs or audio tracks at any time
                return "http://" + _userName + ":" + _password + "@" + _tvhServerName + ":" + _httpPort + _webRoot;
            }

            return "http://" + _tvhServerName + ":" + _httpPort + _webRoot;
        }

        /// <summary>
        /// Turns an image reference from an HTSP message into an absolute URL.
        /// </summary>
        /// <remarks>
        /// TVHeadend's imagecache references are version dependent: below the per-field
        /// threshold the server sends an absolute <c>http://</c> URL, between HTSP v8 and v14
        /// a root-relative <c>/imagecache/N</c> path, and from v15 on a relative
        /// <c>imagecache/N</c> path. EPG providers may also supply an absolute URL directly.
        /// Anything that is not already absolute is resolved against the configured TVHeadend
        /// HTTP endpoint, so every negotiated protocol version yields a usable URL.
        /// </remarks>
        /// <param name="image">The raw image value from an HTSP message.</param>
        /// <returns>An absolute URL, or <c>null</c> when no image was supplied.</returns>
        public string? ResolveImageUrl(string? image)
        {
            if (string.IsNullOrEmpty(image))
            {
                return null;
            }

            if (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return _channelDataHelper.GetChannelIcon4ChannelId(channelId);
            }
            else
            {
                return GetHttpBaseUrl() + "/" + channelIcon.TrimStart('/');
            }

            return GetAuthenticatedUrl(image);
        }

        /// <summary>
        /// Builds an absolute, credentialed URL for a resource served by TVHeadend over HTTP.
        /// </summary>
        /// <remarks>
        /// The web root is the one reported by the server, so a connection is established first.
        /// </remarks>
        /// <param name="relativePath">The path below the web root, with or without a leading slash.</param>
        /// <returns>An absolute URL including the configured credentials.</returns>
        public string GetAuthenticatedUrl(string relativePath)
        {
            EnsureConnection();

            return "http://" + _userName + ":" + _password + "@" + _tvhServerName + ":" + _httpPort + _webRoot
                + "/" + relativePath.TrimStart('/');
        }

        public string? GetChannelImageUrl(string channelId)
        {
            Init();

            _logger.LogDebug("[TVHclient] HTSConnectionHandler.GetChannelImage: channelId: {Id}", channelId);

            return ResolveImageUrl(_channelDataHelper.GetChannelIcon4ChannelId(channelId));
        }

        public Dictionary<string, string> GetHeaders()
        {
            return new Dictionary<string, string>(_headers);
        }

        // private static Stream ImageToPNGStream(Image image)
        // {
        //    Stream stream = new System.IO.MemoryStream();
        //    image.Save(stream, ImageFormat.Png);
        //    stream.Position = 0;
        //    return stream;
        // }

        private void ensureConnection(CancellationToken cancellationToken = default)
        {
            Init();

            lock (_lock)
            {
                if (_htsConnection == null || _htsConnection.needsRestart())
                {
                    _logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection: create new HTS connection");
                    _htsConnection?.Dispose();
                    Version version = Assembly.GetEntryAssembly().GetName().Version;
                    _htsConnection = new HTSConnectionAsync(this, "TVHclient4Emby-" + version.ToString(), "" + HTSMessage.HTSP_VERSION, _loggerFactory);
                    _connected = false;
                    ResetInitialLoad();
                    _channelDataHelper.Clean();
                    _dvrDataHelper.clean();
                    _autorecDataHelper.clean();
                }

                if (!_connected)
                {
                    _logger.LogDebug(
                        "[TVHclient] HTSConnectionHandler.ensureConnection: used connection parameters: " +
                        "TVH Server = '{Servername}'; HTTP Port = '{Httpport}'; HTSP Port = '{Htspport}'; Web-Root = '{Webroot}'; " +
                        "User = '{User}'; Password set = '{Passexists}'",
                        _tvhServerName,
                        _httpPort,
                        _htspPort,
                        _webRoot,
                        _userName,
                        _password.Length > 0);

                    _htsConnection.open(_tvhServerName, _htspPort, cancellationToken, maxAttempts: 3);
                    _connected = _htsConnection.authenticate(_userName, _password, true, cancellationToken, AuthenticationTimeout);
                    if (!_connected)
                    {
                        _htsConnection.Dispose();
                        throw new UnauthorizedAccessException("TVHeadend HTSP authentication failed.");
                    }

                    if (_connected)
                    {
                        ApplyServerWebRoot(_htsConnection.GetWebRoot());
                    }

                    _logger.LogInformation(
                        "[TVHclient] HTSConnectionHandler.EnsureConnection: connection established = {Connected}; "
                        + "TVH server = '{ServerName}' {ServerVersion}; HTSP version negotiated = {NegotiatedHtspVersion} "
                        + "(server supports up to {ServerHtspVersion}, client up to {ClientHtspVersion})",
                        _connected,
                        _htsConnection.GetServername(),
                        _htsConnection.GetServerversion(),
                        _htsConnection.GetNegotiatedProtocolVersion(),
                        _htsConnection.GetServerProtocolVersion(),
                        HTSMessage.HtspVersion);
                }
            }
        }

        public int SendMessage(HTSMessage message, HTSResponseHandler responseHandler)
        {
            ensureConnection();
            return _htsConnection.sendMessage(message, responseHandler);
        }

        public void RemoveResponseHandler(int sequence)
        {
            _htsConnection?.RemoveResponseHandler(sequence);
        }

        /// <summary>
        /// Gets the HTSP version in effect for the current connection.
        /// </summary>
        /// <returns>The negotiated HTSP version.</returns>
        public int GetNegotiatedProtocolVersion()
        {
            EnsureConnection();
            return _htsConnection!.GetNegotiatedProtocolVersion();
        }

        public Task<IEnumerable<ChannelInfo>> BuildChannelInfos(CancellationToken cancellationToken)
        {
            return _channelDataHelper.BuildChannelInfos(cancellationToken);
        }

        public long ResolveChannelId(string channelId)
        {
            return _channelDataHelper.ResolveChannelId(channelId);
        }

        public long ResolveDvrId(string dvrId)
        {
            return _dvrDataHelper.ResolveDvrId(dvrId);
        }

        public int GetPriority()
        {
            Init();
            return _priority;
        }

        public int GetServerUtcOffsetMinutes()
        {
            ensureConnection();
            return _htsConnection.getServerUtcOffsetMinutes();
        }

        public int GetServerUtcOffsetMinutes(DateTime utcInstant)
        {
            ensureConnection();
            utcInstant = utcInstant.Kind == DateTimeKind.Utc ? utcInstant : utcInstant.ToUniversalTime();

            if (_tvhTimeZone != null)
            {
                return checked((int)_tvhTimeZone.GetUtcOffset(utcInstant).TotalMinutes);
            }

            int serverOffset = _htsConnection.getServerUtcOffsetMinutes();
            int localOffset = checked((int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes);
            return localOffset == serverOffset
                ? checked((int)TimeZoneInfo.Local.GetUtcOffset(utcInstant).TotalMinutes)
                : serverOffset;
        }

        public string GetAutorecTitle(string id)
        {
            return _autorecDataHelper.GetTitle(id);
        }

        public String GetProfile()
        {
            Init();
            return _profile;
        }

        public string GetHttpBaseUrl()
        {
            // The web root is taken from the HTSP handshake, so a connection is required
            // before the base URL is known to be correct.
            EnsureConnection();
            return _httpBaseUrl;
        }

        public string GetStreamingMethod()
        {
            init();
            return _streamingMethod;
        }

        public bool GetForceDeinterlace()
        {
            Init();
            return _forceDeinterlace;
        }

        public async Task<IEnumerable<MyRecordingInfo>> BuildDvrInfos(CancellationToken cancellationToken)
        {
            var recordings = await _dvrDataHelper.buildDvrInfos(cancellationToken).ConfigureAwait(false);
            foreach (var recording in recordings)
            {
                recording.ChannelId = GetExternalChannelId(recording.ChannelId);
            }

            return recordings;
        }

        public async Task<IEnumerable<SeriesTimerInfo>> BuildAutorecInfos(CancellationToken cancellationToken)
        {
            var timers = await _autorecDataHelper.buildAutorecInfos(cancellationToken, GetServerUtcOffsetMinutes()).ConfigureAwait(false);
            foreach (var timer in timers)
            {
                timer.ChannelId = GetExternalChannelId(timer.ChannelId);
            }

            return timers;
        }

        public async Task<IEnumerable<TimerInfo>> BuildPendingTimersInfos(CancellationToken cancellationToken)
        {
            var timers = await _dvrDataHelper.buildPendingTimersInfos(cancellationToken).ConfigureAwait(false);
            foreach (var timer in timers)
            {
                timer.ChannelId = GetExternalChannelId(timer.ChannelId);
            }

            return timers;
        }

        private string GetExternalChannelId(string channelId)
        {
            return long.TryParse(channelId, out var numericId) ? _channelDataHelper.GetExternalChannelId(numericId) : channelId;
        }

        public void OnError(Exception ex)
        {
            _logger.LogError(ex, "[TVHclient] HTSConnectionHandler: HTSP error");
            lock (_lock)
            {
                _htsConnection?.Dispose();
                _htsConnection = null;
                _connected = false;
                ResetInitialLoad();
            }
        }

        public void OnMessage(HTSMessage? response)
        {
            if (response != null)
            {
                switch (response.Method)
                {
                    case "tagAdd":
                    case "tagUpdate":
                    case "tagDelete":
                        // _logger.LogCritical("[TVHclient] tad add/update/delete {Resp}", response.ToString());
                        break;

                    case "channelAdd":
                    case "channelUpdate":
                        _channelDataHelper.Add(response);
                        break;

                    case "dvrEntryAdd":
                        _dvrDataHelper.DvrEntryAdd(response);
                        break;
                    case "dvrEntryUpdate":
                        _dvrDataHelper.DvrEntryUpdate(response);
                        break;
                    case "dvrEntryDelete":
                        _dvrDataHelper.DvrEntryDelete(response);
                        break;

                    case "autorecEntryAdd":
                        _autorecDataHelper.AutorecEntryAdd(response);
                        break;
                    case "autorecEntryUpdate":
                        _autorecDataHelper.AutorecEntryUpdate(response);
                        break;
                    case "autorecEntryDelete":
                        _autorecDataHelper.AutorecEntryDelete(response);
                        break;

                    case "eventAdd":
                    case "eventUpdate":
                    case "eventDelete":
                        // should not happen as we don't subscribe for this events.
                        break;

                    // case "subscriptionStart":
                    // case "subscriptionGrace":
                    // case "subscriptionStop":
                    // case "subscriptionSkip":
                    // case "subscriptionSpeed":
                    // case "subscriptionStatus":
                    //    _logger.LogCritical("[TVHclient] subscription events {Resp}", response.ToString());
                    //    break;

                    // case "queueStatus":
                    //    _logger.LogCritical("[TVHclient] queueStatus event {Resp}", response.ToString());
                    //    break;

                    // case "signalStatus":
                    //    _logger.LogCritical("[TVHclient] signalStatus event {Resp}", response.ToString());
                    //    break;

                    // case "timeshiftStatus":
                    //    _logger.LogCritical("[TVHclient] timeshiftStatus event {Resp}", response.ToString());
                    //    break;

                    // case "muxpkt": // streaming data
                    //    _logger.LogCritical("[TVHclient] muxpkt event {Resp}", response.ToString());
                    //    break;

                    case "initialSyncCompleted":
                        Volatile.Read(ref _initialLoad).TrySetResult(true);
                        break;

                    default:
                        // _logger.LogCritical("[TVHclient] Method '{Method}' not handled in LiveTvService.cs", response.Method);
                        break;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _htsConnection?.Dispose();
                _htsConnection = null;
                _connected = false;
                ResetInitialLoad();
            }

            GC.SuppressFinalize(this);
        }
    }
}
