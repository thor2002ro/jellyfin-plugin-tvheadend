using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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
        private static readonly TimeSpan ImageCacheRetention = TimeSpan.FromDays(90);
        private const long MaximumImageBytes = 20L * 1024L * 1024L;

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
        private int _imageRefreshUnavailableGeneration = -1;
        private int _disposed;

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

        public HTSConnectionHandler(
            ILoggerFactory loggerFactory,
            IHttpClientFactory httpClientFactory,
            IImageEncoder imageEncoder)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<HTSConnectionHandler>();
            _httpClient = httpClientFactory.CreateClient();
            _imageEncoder = imageEncoder;

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

        public void BeginImageRefresh(IEnumerable<string> activeChannelCacheKeys)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var activeKeys = activeChannelCacheKeys.ToHashSet(StringComparer.Ordinal);
            lock (_channelImageSources)
            {
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

            Interlocked.Increment(ref _imageRefreshGeneration);
            var cacheDirectory = Path.Combine(Plugin.Instance.DataFolderPath, "images");
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
            if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps
                    ? absoluteUri.AbsoluteUri
                    : null;
            }

            return httpBaseUrl + "/" + imageUrl.TrimStart('/');
        }

        public async Task<(string ImagePath, string ImageUrl)> CacheImageAsync(
            string imageUrl,
            string cacheKey,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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
            var cacheDirectory = Path.Combine(Plugin.Instance.DataFolderPath, "images");
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
                _ => new Lazy<Task<string>>(
                    () => RefreshImageAsync(
                        imageUri,
                        cacheDirectory,
                        cacheKey,
                        hasStableCacheKey ? sourceFingerprint : null,
                        generation),
                    LazyThreadSafetyMode.ExecutionAndPublication));

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
                if (Volatile.Read(ref _imageRefreshUnavailableGeneration) == generation)
                {
                    return FindCachedImage(cacheDirectory, cacheKey);
                }

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
                    _disposeCancellation.Token).ConfigureAwait(false);

                return path;
            }
            catch (Exception ex)
            {
                if (ShouldStopImageRefresh(ex))
                {
                    if (Volatile.Read(ref _imageRefreshGeneration) == generation)
                    {
                        Interlocked.Exchange(ref _imageRefreshUnavailableGeneration, generation);
                        if (Volatile.Read(ref _imageRefreshGeneration) != generation)
                        {
                            Interlocked.CompareExchange(ref _imageRefreshUnavailableGeneration, -1, generation);
                        }
                    }
                }

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

                var cacheDirectory = Path.Combine(Plugin.Instance.DataFolderPath, "images");
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
            var cacheDirectory = Path.Combine(Plugin.Instance.DataFolderPath, "images");
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
            var cacheDirectory = Path.Combine(Plugin.Instance.DataFolderPath, "images");
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

        private static bool ShouldStopImageRefresh(Exception exception)
        {
            if (exception is OperationCanceledException)
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
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(cacheDirectory);
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUri);
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return FindCachedImage(cacheDirectory, cacheKey);
            }

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaximumImageBytes)
            {
                throw new InvalidDataException("TVHeadend image exceeds the 20 MiB cache limit.");
            }

            await response.Content.LoadIntoBufferAsync(MaximumImageBytes, timeout.Token).ConfigureAwait(false);
            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
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

        private void ValidateImage(string path)
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

                    case "initialSyncCompleted":
                        Volatile.Read(ref _initialLoad).TrySetResult(true);
                        break;

                    default:
                        break;
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
