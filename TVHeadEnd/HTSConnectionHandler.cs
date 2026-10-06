using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Configuration;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;


namespace TVHeadEnd
{
    public sealed class HTSConnectionHandler : HTSConnectionListener, IDisposable
    {
        private const int DvrPriorityImportant = 0;
        private const int DvrPriorityNormal = 2;
        private const int DvrPriorityNotSet = 5;
        private const int LegacyDvrPriorityDefault = 6;
        private const long MaximumImageBytes = 20L * 1024L * 1024L;

        private readonly object _lock = new Object();
        private static readonly TimeSpan InitialLoadTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(10);
        // Jellyfin rebuilds every provider's guide; event pushes must not cause frequent full rebuilds.
        private static readonly TimeSpan GuideRefreshInterval = TimeSpan.FromHours(12);
        private static readonly TimeSpan ImageCacheRetention = TimeSpan.FromDays(90);

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<HTSConnectionHandler> _logger;
        private readonly HttpClient _httpClient;
        private readonly IImageEncoder _imageEncoder;
        private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _imageDownloads = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _channelImageLocks = new();
        private readonly ConcurrentDictionary<string, string> _channelImageSources = new();
        private readonly SemaphoreSlim _imageDownloadSlots = new(4);
        private readonly CancellationTokenSource _disposeCancellation = new();
        private int _imageRefreshGeneration;
        private int _httpImageRefreshUnavailableGeneration = -1;
        private int _htspImageRefreshUnavailableGeneration = -1;
        private int _disposed;

        private TaskCompletionSource<bool> _initialLoad = CreateInitialLoadCompletion();
        private volatile Boolean _connected = false;
        private volatile Boolean _configured = false;

        private HTSConnectionAsync _htsConnection;
        private HTSConnectionAsync _htspArtworkDeniedConnection;
        private int _priority;
        private string _profile;
        private string _httpBaseUrl;
        private string _channelType;
        private string _tvhServerName;
        internal string GetProgrammeConnectionIdentity() => _configured
            ? ProgrammeImageService.GetConnectionIdentity(_tvhServerName, _htspPort, _userName) : null;
        private int _httpPort;
        private int _htspPort;
        private string _webRoot;
        private string _userName;
        private string _password;
        private string _streamingMethod;
        private bool _useHttps;
        private bool _forceDeinterlace;
        private TimeZoneInfo _tvhTimeZone;

        // Data helpers
        private readonly ChannelDataHelper _channelDataHelper;
        private readonly EpgDataHelper _epgDataHelper = new();
        private readonly ITaskManager _taskManager;
        private readonly object _guideRefreshLock = new();
        private readonly Timer _guideRefreshTimer;
        private DateTime _lastGuideRefreshUtc = DateTime.MinValue;
        private bool _guideRefreshPending;
        private readonly DvrDataHelper _dvrDataHelper;
        private readonly AutorecDataHelper _autorecDataHelper;

        private Dictionary<string, string> _headers = new Dictionary<string, string>();

        public HTSConnectionHandler(
            ILoggerFactory loggerFactory,
            IHttpClientFactory httpClientFactory,
            IImageEncoder imageEncoder,
            ITaskManager taskManager = null)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<HTSConnectionHandler>();
            _httpClient = httpClientFactory.CreateClient();
            _imageEncoder = imageEncoder;
            _taskManager = taskManager;
            _guideRefreshTimer = new Timer(_ => RefreshGuide(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            _logger.LogDebug("[TVHclient] HTSConnectionHandler");

            _channelDataHelper = new ChannelDataHelper(loggerFactory.CreateLogger<ChannelDataHelper>());
            _dvrDataHelper = new DvrDataHelper(loggerFactory.CreateLogger<DvrDataHelper>());
            _autorecDataHelper = new AutorecDataHelper(loggerFactory.CreateLogger<AutorecDataHelper>());
        }

        private static TaskCompletionSource<bool> CreateInitialLoadCompletion()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void ResetInitialLoad()
        {
            lock (_guideRefreshLock)
            {
                _guideRefreshPending = false;
                if (_disposed == 0) _guideRefreshTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                var previous = Interlocked.Exchange(ref _initialLoad, CreateInitialLoadCompletion());
                _epgDataHelper.Clean();
                previous.TrySetResult(false);
            }
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
            _tvhTimeZone = null;
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

            if (_priority == LegacyDvrPriorityDefault)
            {
                _priority = DvrPriorityNotSet;
            }
            else if (_priority < DvrPriorityImportant || _priority > DvrPriorityNotSet)
            {
                _logger.LogWarning(
                    "[TVHclient] Recording priority {ConfiguredPriority} is outside the supported range [{LowestPriority}-{HighestPriority}]; using {FallbackPriority}",
                    _priority,
                    DvrPriorityImportant,
                    DvrPriorityNotSet,
                    DvrPriorityNormal);
                _priority = DvrPriorityNormal;
            }

            _tvhServerName = config.TVH_ServerName.Trim();
            _httpPort = config.HTTP_Port;
            _htspPort = config.HTSP_Port;
            _webRoot = NormalizeWebRoot(config.WebRoot);
            _userName = config.Username.Trim();
            _password = config.Password.Trim();
            _useHttps = config.UseHttps;

            _httpBaseUrl = BuildHttpBaseUrl();

            string authInfo = _userName + ":" + _password;
            authInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes(authInfo));
            _headers["Authorization"] = "Basic " + authInfo;
            _channelDataHelper.SetChannelType4Other(_channelType);
            _configured = true;
        }

        internal static string NormalizeWebRoot(string webRoot)
        {
            if (string.IsNullOrWhiteSpace(webRoot))
            {
                return string.Empty;
            }

            var normalized = webRoot.Trim().TrimEnd('/');
            return normalized.Length == 0 || normalized.StartsWith('/') ? normalized : "/" + normalized;
        }

        private string BuildHttpBaseUrl()
        {
            var scheme = _useHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            return new UriBuilder(scheme, _tvhServerName, _httpPort, _webRoot).Uri.AbsoluteUri.TrimEnd('/');
        }

        private void ApplyServerWebRoot(string reportedWebRoot)
        {
            var resolvedWebRoot = NormalizeWebRoot(reportedWebRoot);
            if (string.Equals(resolvedWebRoot, _webRoot, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogInformation(
                "[TVHclient] Using TVHeadend-reported web root '{ReportedWebRoot}' instead of configured '{ConfiguredWebRoot}'",
                resolvedWebRoot,
                _webRoot);
            _webRoot = resolvedWebRoot;
            _httpBaseUrl = BuildHttpBaseUrl();
        }

        public void BeginImageRefresh(IEnumerable<string> activeChannelCacheKeys)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var activeKeys = activeChannelCacheKeys.ToHashSet(StringComparer.Ordinal);
            lock (_channelImageSources)
            {
                Interlocked.Increment(ref _imageRefreshGeneration);
                foreach (var cacheKey in _channelImageSources.Keys)
                {
                    if (!activeKeys.Contains(cacheKey))
                    {
                        _channelImageSources.TryRemove(cacheKey, out _);
                        _channelImageLocks.TryRemove(cacheKey, out _);
                    }
                }
            }

            foreach (var download in _imageDownloads)
            {
                if (download.Value.IsValueCreated && download.Value.Value.IsCompleted)
                {
                    _imageDownloads.TryRemove(download);
                }
            }

            var cacheDirectory = GetImageCacheDirectory();
            var activePrefixes = activeKeys
                .Select(GetImageFilePrefix)
                .ToHashSet(StringComparer.Ordinal);
            try
            {
                if (Directory.Exists(cacheDirectory))
                {
                    foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*.tmp"))
                    {
                        if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                        {
                            File.Delete(path);
                        }
                    }

                    foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*.recording"))
                        if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow - ImageCacheRetention) File.Delete(path);

                    foreach (var path in EnumerateCachedImages(cacheDirectory, string.Empty))
                    {
                        var sourcePath = Path.Combine(
                            cacheDirectory,
                            Path.GetFileNameWithoutExtension(path) + ".source");
                        if (!File.Exists(sourcePath)
                            && File.GetLastWriteTimeUtc(path) < DateTime.UtcNow - ImageCacheRetention)
                        {
                            File.Delete(path);
                        }
                    }

                    foreach (var sourcePath in Directory.EnumerateFiles(cacheDirectory, "*.source"))
                    {
                        var filePrefix = Path.GetFileNameWithoutExtension(sourcePath);
                        if (activePrefixes.Contains(filePrefix))
                        {
                            continue;
                        }

                        var images = EnumerateCachedImages(cacheDirectory, filePrefix).ToArray();
                        if (images.Length == 0
                            || images.All(path => File.GetLastWriteTimeUtc(path) < DateTime.UtcNow - ImageCacheRetention))
                        {
                            foreach (var path in images)
                            {
                                File.Delete(path);
                            }

                            File.Delete(sourcePath);
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "[TVHclient] Could not prune stale cached images");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "[TVHclient] Could not prune stale cached images");
            }
        }

        private string ResolveImageUrl(string imageUrl)
        {
            init();
            return ResolveImageUrl(_httpBaseUrl, imageUrl);
        }

        private static string ResolveImageUrl(string httpBaseUrl, string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return null;
            }

            imageUrl = imageUrl.Trim();
            var baseUri = new Uri(httpBaseUrl);
            if (imageUrl.StartsWith("//", StringComparison.Ordinal))
            {
                return new Uri(baseUri, imageUrl).AbsoluteUri;
            }
            if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps
                    ? absoluteUri.AbsoluteUri
                    : null;
            }

            var webRoot = baseUri.AbsolutePath.TrimEnd('/');
            if (imageUrl.StartsWith(webRoot + "/", StringComparison.Ordinal))
            {
                return baseUri.GetLeftPart(UriPartial.Authority) + imageUrl;
            }

            return httpBaseUrl + "/" + imageUrl.TrimStart('/');
        }

        public async Task<(string ImagePath, string ImageUrl)> CacheImageAsync(
            string imageUrl,
            string cacheKey,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            var resolvedUrl = ResolveImageUrl(imageUrl);
            if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var imageUri)
                || !Uri.TryCreate(_httpBaseUrl, UriKind.Absolute, out var baseUri)
                || !SameOrigin(baseUri, imageUri))
            {
                if (cacheKey is not null)
                {
                    InvalidateCachedChannelImage(cacheKey);
                }

                return (null, resolvedUrl);
            }

            cacheKey ??= resolvedUrl;
            var cacheDirectory = GetImageCacheDirectory();
            var cachedPath = FindCachedImage(cacheDirectory, cacheKey);
            var sourceFingerprint = GetImageFilePrefix(resolvedUrl);
            var hasStableCacheKey = !string.Equals(cacheKey, resolvedUrl, StringComparison.Ordinal);
            if (hasStableCacheKey)
            {
                // Publish the newest guide value before inspecting the cache. Older
                // in-flight downloads must not commit after a channel reverts.
                lock (_channelImageSources)
                {
                    _channelImageSources[cacheKey] = sourceFingerprint;
                }
            }
            else if (cachedPath != null)
            {
                TouchCachedImage(cachedPath);
                return (cachedPath, null);
            }

            var generation = Volatile.Read(ref _imageRefreshGeneration);
            var operationKey = cacheKey + "\0" + (hasStableCacheKey ? sourceFingerprint : string.Empty);
            var download = _imageDownloads.GetOrAdd(
                operationKey,
                _ =>
                {
                    Lazy<Task<string>> operation = null;
                    operation = new Lazy<Task<string>>(async () =>
                    {
                        var path = await RefreshImageAsync(imageUri, cacheDirectory, cacheKey,
                            hasStableCacheKey ? sourceFingerprint : null, generation).ConfigureAwait(false);
                        var matchesSource = !hasStableCacheKey;
                        if (hasStableCacheKey && path is not null)
                        {
                            var sourcePath = Path.Combine(cacheDirectory, GetImageFilePrefix(cacheKey) + ".source");
                            matchesSource = string.Equals(FindCachedChannelImage(cacheDirectory, cacheKey,
                                sourcePath, sourceFingerprint), path, StringComparison.OrdinalIgnoreCase);
                        }
                        // The shared operation owns cleanup even if every API caller has cancelled its wait.
                        if (path is not null && matchesSource)
                            _imageDownloads.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(operationKey, operation));
                        return path;
                    }, LazyThreadSafetyMode.ExecutionAndPublication);
                    return operation;
                });

            var refreshedPath = await download.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            return (refreshedPath ?? cachedPath, null);
        }

        private async Task<string> RefreshImageAsync(
            Uri imageUri,
            string cacheDirectory,
            string cacheKey,
            string sourceFingerprint,
            int generation)
        {
            var slotAcquired = false;
            SemaphoreSlim channelLock = null;
            var channelLockAcquired = false;
            try
            {
                await _imageDownloadSlots.WaitAsync(_disposeCancellation.Token).ConfigureAwait(false);
                slotAcquired = true;
                if (sourceFingerprint != null)
                {
                    channelLock = _channelImageLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
                    await channelLock.WaitAsync(_disposeCancellation.Token).ConfigureAwait(false);
                    channelLockAcquired = true;
                    if (!_channelImageSources.TryGetValue(cacheKey, out var currentSource)
                        || !string.Equals(currentSource, sourceFingerprint, StringComparison.Ordinal))
                    {
                        return FindCachedImage(cacheDirectory, cacheKey);
                    }

                    var sourcePath = Path.Combine(cacheDirectory, GetImageFilePrefix(cacheKey) + ".source");
                    var existingPath = FindCachedChannelImage(
                        cacheDirectory,
                        cacheKey,
                        sourcePath,
                        sourceFingerprint);
                    if (existingPath is not null)
                    {
                        return existingPath;
                    }
                }

                var htspPath = GetHtspImagePath(new Uri(_httpBaseUrl), imageUri);
                var path = await DownloadImageAsync(
                    _httpClient,
                    imageUri,
                    _headers,
                    cacheDirectory,
                    cacheKey,
                    ValidateImage,
                    sourceFingerprint is null
                        ? null
                        : () => _channelImageSources.TryGetValue(cacheKey, out var latest)
                            && string.Equals(latest, sourceFingerprint, StringComparison.Ordinal),
                    sourceFingerprint is null ? null : _channelImageSources,
                    sourceFingerprint,
                    _disposeCancellation.Token,
                    token => ReadArtworkBytesAsync(imageUri, htspPath, generation, token)).ConfigureAwait(false);

                return path;
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException || Volatile.Read(ref _disposed) == 0)
                {
                    _logger.LogWarning(ex, "[TVHclient] Could not refresh cached image {ImageUrl}", imageUri);
                }

                return FindCachedImage(cacheDirectory, cacheKey);
            }
            finally
            {
                if (channelLockAcquired)
                {
                    channelLock.Release();
                }

                if (slotAcquired)
                {
                    _imageDownloadSlots.Release();
                }
            }
        }

        internal void PruneChannelImages(
            IEnumerable<string> currentPaths,
            IEnumerable<string> unusedCacheKeys)
        {
            try
            {
                foreach (var currentPath in currentPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    PruneSupersededChannelImage(currentPath);
                }

                var cacheDirectory = GetImageCacheDirectory();
                if (Directory.Exists(cacheDirectory))
                {
                    foreach (var cacheKey in unusedCacheKeys.Distinct(StringComparer.Ordinal))
                    {
                        foreach (var path in EnumerateCachedImages(cacheDirectory, GetImageFilePrefix(cacheKey)))
                        {
                            File.Delete(path);
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "[TVHclient] Could not prune superseded channel images");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "[TVHclient] Could not prune superseded channel images");
            }
        }

        internal bool IsCachedChannelImage(string cacheKey, string path)
        {
            var cacheDirectory = GetImageCacheDirectory();
            return !string.IsNullOrEmpty(path)
                && string.Equals(
                    Path.GetDirectoryName(path),
                    cacheDirectory,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    GetImageFilePrefix(cacheKey),
                    StringComparison.Ordinal);
        }

        private void InvalidateCachedChannelImage(string cacheKey)
        {
            var cacheDirectory = GetImageCacheDirectory();
            var filePrefix = GetImageFilePrefix(cacheKey);
            try
            {
                lock (_channelImageSources)
                {
                    _channelImageSources.TryRemove(cacheKey, out _);
                    if (Directory.Exists(cacheDirectory))
                    {
                        File.Delete(Path.Combine(cacheDirectory, filePrefix + ".source"));
                    }
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "[TVHclient] Could not invalidate cached channel image");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "[TVHclient] Could not invalidate cached channel image");
            }

            _channelImageLocks.TryRemove(cacheKey, out _);
        }

        private static void PruneSupersededChannelImage(string currentPath)
        {
            var cacheDirectory = Path.GetDirectoryName(currentPath);
            var filePrefix = Path.GetFileNameWithoutExtension(currentPath);
            foreach (var path in EnumerateCachedImages(cacheDirectory, filePrefix))
            {
                if (!string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                }
            }
        }

        private async Task<byte[]> ReadArtworkBytesAsync(Uri imageUri, string htspPath, int generation, CancellationToken token)
        {
            if (htspPath is not null)
            {
                if (Volatile.Read(ref _htspImageRefreshUnavailableGeneration) == generation) return null;
                try { return await ReadHtspImageAsync(htspPath, token).ConfigureAwait(false); }
                catch (UnauthorizedAccessException)
                {
                    // Recorder permission is optional for artwork; reuse the authenticated HTTP path.
                }
                catch (Exception ex) when (ShouldStopImageRefresh(ex))
                {
                    MarkImageRefreshUnavailable(ref _htspImageRefreshUnavailableGeneration, generation);
                    throw;
                }
            }

            if (Volatile.Read(ref _httpImageRefreshUnavailableGeneration) == generation) return null;
            try { return await ReadHttpImageAsync(_httpClient, imageUri, _headers, token).ConfigureAwait(false); }
            catch (Exception ex) when (ShouldStopImageRefresh(ex))
            {
                MarkImageRefreshUnavailable(ref _httpImageRefreshUnavailableGeneration, generation);
                throw;
            }
        }

        private void MarkImageRefreshUnavailable(ref int unavailableGeneration, int generation)
        {
            // Share the refresh-generation lock so an older failure cannot overwrite a newer refresh's state.
            lock (_channelImageSources)
            {
                if (Volatile.Read(ref _imageRefreshGeneration) == generation)
                    Volatile.Write(ref unavailableGeneration, generation);
            }
        }

        private static bool ShouldStopImageRefresh(Exception exception)
        {
            if (exception is OperationCanceledException or UnauthorizedAccessException or TimeoutException)
            {
                return true;
            }

            if (exception is not HttpRequestException requestException)
            {
                return false;
            }

            return !requestException.StatusCode.HasValue
                || requestException.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden
                || (int)requestException.StatusCode.Value >= 500;
        }

        private static string GetImageCacheDirectory()
        {
            return Plugin.Instance.ImageCachePath;
        }

        private static bool SameOrigin(Uri left, Uri right)
        {
            return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
                && left.Port == right.Port;
        }

        private static bool SourceMatches(
            string sourcePath,
            string sourceFingerprint,
            string extension)
        {
            try
            {
                return File.Exists(sourcePath)
                    && string.Equals(
                        File.ReadAllText(sourcePath),
                        sourceFingerprint + extension,
                        StringComparison.Ordinal);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string GetHtspImagePath(Uri baseUri, Uri imageUri)
        {
            var prefix = baseUri.AbsolutePath.TrimEnd('/') + "/imagecache/";
            if (!SameOrigin(baseUri, imageUri) || imageUri.Query.Length != 0 || imageUri.Fragment.Length != 0
                || !imageUri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var value = imageUri.AbsolutePath[prefix.Length..];
            return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? "imagecache/" + id.ToString(CultureInfo.InvariantCulture) : null;
        }

        private async Task<byte[]> ReadHtspImageAsync(string path, CancellationToken cancellationToken)
        {
            // Match metadata loading: reconnect/authentication must not block a Jellyfin caller's cancellable wait.
            await Task.Run(() => ensureConnection(cancellationToken), cancellationToken).ConfigureAwait(false);
            // File handles belong to a connection; reconnecting must never send an old handle to a new socket.
            var connection = _htsConnection;
            if (ReferenceEquals(_htspArtworkDeniedConnection, connection))
                throw new UnauthorizedAccessException("TVHeadend denied HTSP image access.");
            return await ReadHtspImageFileAsync(path, async (message, token) =>
            {
                token.ThrowIfCancellationRequested();
                var handler = new LoopBackResponseHandler();
                var sequence = connection.sendMessage(message, handler);
                try
                {
                    var reply = await handler.GetResponseAsync(token, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (reply.getInt("noaccess", 0) != 0)
                    {
                        _htspArtworkDeniedConnection = connection;
                        throw new UnauthorizedAccessException("TVHeadend denied HTSP image access.");
                    }
                    if (reply.containsField("error")) throw new IOException(reply.getString("error"));
                    return reply;
                }
                catch (Exception ex) when (message.Method is "fileOpen" or "fileClose"
                    && ex is OperationCanceledException or TimeoutException)
                {
                    // An unacknowledged open/close leaves handle ownership unknown; socket closure releases it.
                    connection.Dispose();
                    throw;
                }
                finally { connection.RemoveResponseHandler(sequence); }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadHtspImageFileAsync(
            string path,
            Func<HTSMessage, CancellationToken, Task<HTSMessage>> send,
            CancellationToken cancellationToken)
        {
            var open = new HTSMessage { Method = "fileOpen" };
            open.putField("file", path);
            var reply = await send(open, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!reply.TryGetLong("id", out var id) || id < 0 || id > uint.MaxValue)
                throw new InvalidDataException("TVHeadend returned an invalid image file handle.");
            try
            {
                long size = -1;
                if (reply.containsField("size") && (!reply.TryGetLong("size", out size) || size < 0 || size > MaximumImageBytes))
                    throw new InvalidDataException("TVHeadend image exceeds the 20 MiB cache limit or has an invalid size.");
                using var buffer = new MemoryStream();
                while (size < 0 || buffer.Length < size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var want = Math.Min(256 * 1024, (size >= 0 ? size : MaximumImageBytes + 1) - buffer.Length);
                    var request = new HTSMessage { Method = "fileRead" };
                    request.putField("id", id);
                    request.putField("size", want);
                    reply = await send(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (!reply.containsField("data") || reply.GetField("data") is not byte[] data
                        || data.Length > want || buffer.Length + data.Length > MaximumImageBytes)
                        throw new InvalidDataException("TVHeadend returned invalid or oversized image data.");
                    if (data.Length == 0)
                    {
                        if (size >= 0 && buffer.Length != size) throw new InvalidDataException("TVHeadend image was truncated.");
                        break;
                    }
                    // A short read is not EOF: HTSP explicitly permits fewer bytes than requested.
                    buffer.Write(data, 0, data.Length);
                }
                return buffer.ToArray();
            }
            finally
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var close = new HTSMessage { Method = "fileClose" };
                close.putField("id", id);
                try { await send(close, closeTimeout.Token).WaitAsync(closeTimeout.Token).ConfigureAwait(false); }
                catch (Exception) { /* Best effort: socket closure also releases Tvheadend file handles. */ }
            }
        }

        private static async Task<string> DownloadImageAsync(
            HttpClient httpClient,
            Uri imageUri,
            IReadOnlyDictionary<string, string> headers,
            string cacheDirectory,
            string cacheKey,
            Action<string> validateImage,
            Func<bool> canCommit,
            object commitLock,
            string sourceFingerprint,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task<byte[]>> readImage = null)
        {
            Directory.CreateDirectory(cacheDirectory);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var data = readImage is null
                ? await ReadHttpImageAsync(httpClient, imageUri, headers, timeout.Token).ConfigureAwait(false)
                : await readImage(timeout.Token).ConfigureAwait(false);
            if (data is null) return FindCachedImage(cacheDirectory, cacheKey);
            if (data.LongLength > MaximumImageBytes) throw new InvalidDataException("TVHeadend image exceeds the 20 MiB cache limit.");
            var filePrefix = GetImageFilePrefix(cacheKey);
            var extension = GetImageExtension(data);
            var cachedPath = FindCachedImage(cacheDirectory, cacheKey);

            var temporaryPath = Path.Combine(
                cacheDirectory,
                filePrefix + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                await File.WriteAllBytesAsync(temporaryPath, data, timeout.Token).ConfigureAwait(false);
                validateImage(temporaryPath);
                if (canCommit is not null && !canCommit())
                {
                    return cachedPath;
                }

                var path = Path.Combine(cacheDirectory, filePrefix + extension);
                if (commitLock is null)
                {
                    File.Move(temporaryPath, path, true);
                }
                else
                {
                    lock (commitLock)
                    {
                        if (!canCommit())
                        {
                            return cachedPath;
                        }

                        File.Move(temporaryPath, path, true);
                        File.WriteAllText(
                            Path.Combine(cacheDirectory, filePrefix + ".source"),
                            sourceFingerprint + extension);
                    }
                }

                if (canCommit is null)
                {
                    foreach (var stalePath in EnumerateCachedImages(cacheDirectory, filePrefix))
                    {
                        if (!string.Equals(stalePath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(stalePath);
                        }
                    }
                }

                return path;
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        private static async Task<byte[]> ReadHttpImageAsync(HttpClient httpClient, Uri imageUri,
            IReadOnlyDictionary<string, string> headers, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUri);
            foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumImageBytes)
                throw new InvalidDataException("TVHeadend image exceeds the 20 MiB cache limit.");
            await response.Content.LoadIntoBufferAsync(MaximumImageBytes, token).ConfigureAwait(false);
            return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        }

        internal void ValidateImage(string path)
        {
            var dimensions = _imageEncoder.GetImageSize(path);
            if (dimensions.Width <= 0 || dimensions.Height <= 0)
            {
                throw new InvalidDataException("TVHeadend response is not a valid image.");
            }
        }

        private static string GetImageExtension(ReadOnlySpan<byte> header)
        {
            if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            {
                return ".png";
            }

            if (header.Length >= 3 && header[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF }))
            {
                return ".jpg";
            }

            if (header.Length >= 4 && header[..4].SequenceEqual("GIF8"u8))
            {
                return ".gif";
            }

            if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            {
                return ".webp";
            }

            if (header.Length >= 12
                && header[4..8].SequenceEqual("ftyp"u8)
                && (header[8..12].SequenceEqual("avif"u8) || header[8..12].SequenceEqual("avis"u8)))
            {
                return ".avif";
            }

            if (header.Length >= 2 && header[..2].SequenceEqual("BM"u8))
            {
                return ".bmp";
            }

            if (header.Length >= 4 && header[..4].SequenceEqual(new byte[] { 0x00, 0x00, 0x01, 0x00 }))
            {
                return ".ico";
            }

            var text = Encoding.UTF8.GetString(header[..Math.Min(header.Length, 512)]);
            if (text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            {
                return ".svg";
            }

            throw new InvalidDataException("TVHeadend response is not a supported image.");
        }

        private static string FindCachedImage(string cacheDirectory, string cacheKey)
        {
            return Directory.Exists(cacheDirectory)
                ? EnumerateCachedImages(cacheDirectory, GetImageFilePrefix(cacheKey)).FirstOrDefault()
                : null;
        }

        private static string FindCachedChannelImage(
            string cacheDirectory,
            string cacheKey,
            string sourcePath,
            string sourceFingerprint)
        {
            return Directory.Exists(cacheDirectory)
                ? EnumerateCachedImages(cacheDirectory, GetImageFilePrefix(cacheKey))
                    .FirstOrDefault(path => SourceMatches(
                        sourcePath,
                        sourceFingerprint,
                        Path.GetExtension(path)))
                : null;
        }

        private static IEnumerable<string> EnumerateCachedImages(string cacheDirectory, string filePrefix)
        {
            return Directory.EnumerateFiles(cacheDirectory, filePrefix + "*")
                .Where(path => Path.GetExtension(path) is ".avif" or ".bmp" or ".gif" or ".ico" or ".jpg" or ".png" or ".svg" or ".webp");
        }

        private static string GetImageFilePrefix(string cacheKey)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant();
        }

        private static void TouchCachedImage(string path)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public Dictionary<string, string> GetHeaders()
        {
            init();
            return new Dictionary<string, string>(_headers);
        }

        private void ensureConnection(CancellationToken cancellationToken = default)
        {
            init();

            lock (_lock)
            {
                if (_htsConnection == null || _htsConnection.needsRestart())
                {
                    _logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection: create new HTS connection");
                    _htsConnection?.Dispose();
                    Version version = typeof(HTSConnectionHandler).Assembly.GetName().Version;
                    _htsConnection = new HTSConnectionAsync(this, "Jellyfin-TVHeadend", version.ToString(), _loggerFactory);
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

                    ApplyServerWebRoot(_htsConnection.getServerWebRoot());
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

        public (bool Connected, string ServerVersion, int? ProtocolVersion) GetConnectionStatus()
        {
            var connection = _htsConnection;
            return _connected && connection != null
                ? (true, connection.getServerversion(), connection.getServerProtocolVersion())
                : (false, null, null);
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
            ensureConnection();
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
            cancellationToken.ThrowIfCancellationRequested();
            var revision = RecordingRevision;
            var recordings = await _dvrDataHelper.buildDvrInfos(cancellationToken).ConfigureAwait(false);
            var artwork = recordings.Select(recording =>
            {
                var source = recording.ImageUrl;
                recording.ImageUrl = null;
                recording.HasImage = false;
                return (recording, source);
            }).ToArray();
            using var artworkBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            artworkBudget.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await Parallel.ForEachAsync(artwork, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = artworkBudget.Token },
                    async (item, token) =>
                    {
                        try { await ResolveRecordingArtworkAsync(item.recording, item.source, revision, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { _logger.LogDebug(ex, "Recording artwork unavailable for {RecordingId}", item.recording.Id); }
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var recording in recordings) recording.ChannelId = GetExternalChannelId(recording.ChannelId);

            return recordings;
        }

        private async Task ResolveRecordingArtworkAsync(MyRecordingInfo recording, string directImage, long revision, CancellationToken token)
        {
            var identity = GetProgrammeConnectionIdentity();
            if (identity == null || identity != ProgrammeImageService.CurrentConnectionIdentity) return;
            var directory = GetImageCacheDirectory();
            var key = "recording:" + identity + "|" + recording.Id;
            var association = Path.Combine(directory, GetImageFilePrefix(key) + ".recording");
            var epg = long.TryParse(recording.ProgramId, out var eventId) ? _epgDataHelper.GetEvent(eventId) : null;
            long.TryParse(recording.ChannelId, out var channelId);
            if (epg != null && (!epg.TryGetLong("channelId", out var epgChannel) || epgChannel != channelId
                || !epg.TryGetLong("start", out var start) || !epg.TryGetLong("stop", out var stop)
                || start < 0 || stop < start || stop > 253402300799L
                || DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime >= recording.EndDate
                || DateTimeOffset.FromUnixTimeSeconds(stop).UtcDateTime <= recording.StartDate)) epg = null;
            var source = ResolveImageUrl(directImage) ?? ResolveImageUrl(epg?.getString("image", null));
            if (string.IsNullOrWhiteSpace(source) && File.Exists(association))
                source = ResolveImageUrl(await File.ReadAllTextAsync(association, token).ConfigureAwait(false));
            var sourceKey = key + ":association";
            var sourceFingerprint = GetImageFilePrefix(source ?? "generated");
            bool StillCurrent() => revision == RecordingRevision && identity == ProgrammeImageService.CurrentConnectionIdentity
                && identity == GetProgrammeConnectionIdentity()
                && _channelImageSources.TryGetValue(sourceKey, out var latest) && latest == sourceFingerprint;
            if (revision != RecordingRevision) return;
            lock (_channelImageSources) _channelImageSources[sourceKey] = sourceFingerprint;
            string image = null;
            if (!string.IsNullOrWhiteSpace(source))
            {
                var cached = await CacheImageAsync(source, key, token).ConfigureAwait(false);
                image = cached.ImagePath ?? cached.ImageUrl;
                var resolved = ResolveImageUrl(source);
                if (image != null && resolved != null && StillCurrent())
                {
                    Directory.CreateDirectory(directory);
                    if (!File.Exists(association) || await File.ReadAllTextAsync(association, token).ConfigureAwait(false) != resolved)
                    {
                        var temporary = association + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            await File.WriteAllTextAsync(temporary, resolved, token).ConfigureAwait(false);
                            lock (_channelImageSources)
                                if (StillCurrent()) File.Move(temporary, association, true);
                        }
                        finally { File.Delete(temporary); }
                    }
                }
            }
            if (image == null && Plugin.Instance.Configuration.GenerateMissingProgrammeImages)
            {
                image = FindCachedImage(directory, key);
                if (image == null && epg != null)
                {
                    var generated = ProgrammeImageService.GetImagePath(channelId, recording.ProgramId,
                        DateTimeOffset.FromUnixTimeSeconds(epg.getLong("start")).UtcDateTime);
                    if (File.Exists(generated))
                    {
                        ValidateImage(generated);
                        image = Path.Combine(directory, GetImageFilePrefix(key) + ".jpg");
                        var temporary = image + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            File.Copy(generated, temporary);
                            token.ThrowIfCancellationRequested();
                            lock (_channelImageSources)
                            {
                                if (!StillCurrent()) return;
                                File.Move(temporary, image, true);
                            }
                        }
                        finally { File.Delete(temporary); }
                    }
                }
            }
            if (!StillCurrent()) return;
            if (File.Exists(image)) TouchCachedImage(image);
            if (File.Exists(association)) TouchCachedImage(association);
            recording.ImageUrl = image;
            recording.HasImage = !string.IsNullOrEmpty(image);
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
                        if (_channelDataHelper.UpdateTag(response)) ScheduleGuideRefresh();
                        break;

                    case "channelAdd":
                    case "channelUpdate":
                        if (_channelDataHelper.Add(response)) ScheduleGuideRefresh();
                        break;

                    case "channelDelete":
                        if (response.TryGetLong("channelId", out var channelId))
                        {
                            var changed = _channelDataHelper.Remove(channelId);
                            changed |= _epgDataHelper.RemoveChannel(channelId);
                            if (changed) ScheduleGuideRefresh();
                        }
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
                        if (_epgDataHelper.Update(response))
                        {
                            ScheduleGuideRefresh();
                        }
                        break;

                    case "initialSyncCompleted":
                        Volatile.Read(ref _initialLoad).TrySetResult(true);
                        break;

                    default:
                        break;
                }
            }
        }

        public HTSMessage[] GetCachedEvents(long channelId, long startUnix = long.MinValue, long endUnix = long.MaxValue)
            => _epgDataHelper.GetEvents(channelId, startUnix, endUnix);

        internal long RecordingRevision => _dvrDataHelper.Revision;

        internal string GetExternalChannelId(long channelId) => _channelDataHelper.GetExternalChannelId(channelId);

        private void ScheduleGuideRefresh()
        {
            lock (_guideRefreshLock)
            {
                var initialLoad = Volatile.Read(ref _initialLoad).Task;
                if (!initialLoad.IsCompletedSuccessfully || !initialLoad.Result) return;
                if (_disposed != 0 || _taskManager == null || _guideRefreshPending) return;
                _guideRefreshPending = true;
                var remaining = _lastGuideRefreshUtc.Add(GuideRefreshInterval) - DateTime.UtcNow;
                _guideRefreshTimer.Change(remaining > TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
            }
        }

        private void RefreshGuide()
        {
            lock (_guideRefreshLock)
            {
                if (_disposed != 0 || !_guideRefreshPending) return;
                try
                {
                    var worker = _taskManager.ScheduledTasks.FirstOrDefault(task => task.ScheduledTask.Key == "RefreshGuide");
                    if (worker == null)
                    {
                        _guideRefreshPending = false;
                        return;
                    }
                    var lastRefresh = worker.LastExecutionResult?.EndTimeUtc ?? DateTime.MinValue;
                    if (lastRefresh < _lastGuideRefreshUtc) lastRefresh = _lastGuideRefreshUtc;
                    var remaining = lastRefresh.Add(GuideRefreshInterval) - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        _guideRefreshTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                        return;
                    }
                    if (worker.State != TaskState.Idle)
                    {
                        _guideRefreshTimer.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
                        return;
                    }
                    _taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
                    _lastGuideRefreshUtc = DateTime.UtcNow;
                    _guideRefreshPending = false;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not queue Jellyfin guide refresh");
                    _guideRefreshTimer.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (_lock)
            {
                _htsConnection?.Dispose();
                _htsConnection = null;
                _connected = false;
                ResetInitialLoad();
            }

            _disposeCancellation.Cancel();
            _guideRefreshTimer.Dispose();
            try
            {
                Task.WhenAll(_imageDownloads.Values
                    .Where(download => download.IsValueCreated)
                    .Select(download => download.Value)).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            _httpClient.Dispose();
            foreach (var channelLock in _channelImageLocks.Values)
            {
                channelLock.Dispose();
            }

            _imageDownloadSlots.Dispose();
            _disposeCancellation.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
