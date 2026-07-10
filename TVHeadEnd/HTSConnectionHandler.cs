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

            //System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger.LogDebug("[TVHclient] HTSConnectionHandler");

            _channelDataHelper = new ChannelDataHelper(loggerFactory.CreateLogger<ChannelDataHelper>());
            _dvrDataHelper = new DvrDataHelper(loggerFactory.CreateLogger<DvrDataHelper>());
            _autorecDataHelper = new AutorecDataHelper(loggerFactory.CreateLogger<AutorecDataHelper>());

            _channelDataHelper.SetChannelType4Other(_channelType);
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

        private void init()
        {
            if(_configured == true)
            {
                return ;
            }
            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Init()");

            var config = Plugin.Instance.Configuration;

            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Config initialized");

            if (string.IsNullOrEmpty(config.TVH_ServerName))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: TVH server name must be configured";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            if (string.IsNullOrEmpty(config.Username))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: username must be configured";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            if (string.IsNullOrEmpty(config.Password))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: password must be configured";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
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
            _webRoot = config.WebRoot;
            if (_webRoot.EndsWith("/"))
            {
                _webRoot = _webRoot.Substring(0, _webRoot.Length - 1);
            }
            _userName = config.Username.Trim();
            _password = config.Password.Trim();

            var httpScheme = config.UseHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            _httpBaseUrl = new UriBuilder(httpScheme, _tvhServerName, _httpPort, _webRoot).Uri.AbsoluteUri.TrimEnd('/');

            string authInfo = _userName + ":" + _password;
            authInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes(authInfo));
            _headers["Authorization"] = "Basic " + authInfo;
            _configured = true;
        }

        public string GetChannelImageUrl(string channelId)
        {
            init();

            _logger.LogDebug("[TVHclient] HTSConnectionHandler.GetChannelImage: channelId: {id}", channelId);

            String channelIcon = _channelDataHelper.GetChannelIcon4ChannelId(channelId);

            if (string.IsNullOrEmpty(channelIcon))
            {
                return null;
            }

            if (channelIcon.StartsWith("http"))
            {
                return _channelDataHelper.GetChannelIcon4ChannelId(channelId);
            }
            else
            {
                return GetHttpBaseUrl() + "/" + channelIcon.TrimStart('/');
            }
        }

        public Dictionary<string, string> GetHeaders()
        {
            return new Dictionary<string, string>(_headers);
        }

        //private static Stream ImageToPNGStream(Image image)
        //{
        //    Stream stream = new System.IO.MemoryStream();
        //    image.Save(stream, ImageFormat.Png);
        //    stream.Position = 0;
        //    return stream;
        //}

        private void ensureConnection(CancellationToken cancellationToken = default)
        {
            init();

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
                    _logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection: used connection parameters: " +
                        "TVH Server = '{servername}'; HTTP Port = '{httpport}'; HTSP Port = '{htspport}'; Web-Root = '{webroot}'; " +
                        "User = '{user}'; Password set = '{passexists}'",
                        _tvhServerName, _httpPort, _htspPort, _webRoot, _userName, (_password.Length > 0));

                    _htsConnection.open(_tvhServerName, _htspPort, cancellationToken, maxAttempts: 3);
                    _connected = _htsConnection.authenticate(_userName, _password, true, cancellationToken, AuthenticationTimeout);
                    if (!_connected)
                    {
                        _htsConnection.Dispose();
                        throw new UnauthorizedAccessException("TVHeadend HTSP authentication failed.");
                    }

                    _logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection: connection established {c}", _connected);
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

        public String GetServername()
        {
            ensureConnection();
            return _htsConnection.getServername();
        }

        public String GetServerVersion()
        {
            ensureConnection();
            return _htsConnection.getServerversion();
        }

        public int GetServerProtocolVersion()
        {
            ensureConnection();
            return _htsConnection.getServerProtocolVersion();
        }

        public String GetDiskSpace()
        {
            ensureConnection();
            return _htsConnection.getDiskspace();
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
            init();
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
            init();
            return _profile;
        }

        public String GetHttpBaseUrl()
        {
            init();
            return _httpBaseUrl;
        }

        public string GetStreamingMethod()
        {
            init();
            return _streamingMethod;
        }

        public bool GetForceDeinterlace()
        {
            init();
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

        public void onError(Exception ex)
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

        public void onMessage(HTSMessage response)
        {
            if (response != null)
            {
                switch (response.Method)
                {
                    case "tagAdd":
                    case "tagUpdate":
                    case "tagDelete":
                        //_logger.LogCritical("[TVHclient] tad add/update/delete {resp}", response.ToString());
                        break;

                    case "channelAdd":
                    case "channelUpdate":
                        _channelDataHelper.Add(response);
                        break;

                    case "dvrEntryAdd":
                        _dvrDataHelper.dvrEntryAdd(response);
                        break;
                    case "dvrEntryUpdate":
                        _dvrDataHelper.dvrEntryUpdate(response);
                        break;
                    case "dvrEntryDelete":
                        _dvrDataHelper.dvrEntryDelete(response);
                        break;

                    case "autorecEntryAdd":
                        _autorecDataHelper.autorecEntryAdd(response);
                        break;
                    case "autorecEntryUpdate":
                        _autorecDataHelper.autorecEntryUpdate(response);
                        break;
                    case "autorecEntryDelete":
                        _autorecDataHelper.autorecEntryDelete(response);
                        break;

                    case "eventAdd":
                    case "eventUpdate":
                    case "eventDelete":
                        // should not happen as we don't subscribe for this events.
                        break;

                    //case "subscriptionStart":
                    //case "subscriptionGrace":
                    //case "subscriptionStop":
                    //case "subscriptionSkip":
                    //case "subscriptionSpeed":
                    //case "subscriptionStatus":
                    //    _logger.LogCritical("[TVHclient] subscription events {resp}", response.ToString());
                    //    break;

                    //case "queueStatus":
                    //    _logger.LogCritical("[TVHclient] queueStatus event {resp}", response.ToString());
                    //    break;

                    //case "signalStatus":
                    //    _logger.LogCritical("[TVHclient] signalStatus event {resp}", response.ToString());
                    //    break;

                    //case "timeshiftStatus":
                    //    _logger.LogCritical("[TVHclient] timeshiftStatus event {resp}", response.ToString());
                    //    break;

                    //case "muxpkt": // streaming data
                    //    _logger.LogCritical("[TVHclient] muxpkt event {resp}", response.ToString());
                    //    break;

                    case "initialSyncCompleted":
                        Volatile.Read(ref _initialLoad).TrySetResult(true);
                        break;

                    default:
                        //_logger.LogCritical("[TVHclient] Method '{method}' not handled in LiveTvService.cs", response.Method);
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
