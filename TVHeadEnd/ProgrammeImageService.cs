using System;
using System.Globalization;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TVHeadEnd.HTSP;

namespace TVHeadEnd;

public sealed class ProgrammeImageService(
    HTSConnectionHandler connectionHandler,
    LiveTvService liveTvService,
    IMediaEncoder mediaEncoder,
    ILibraryManager libraryManager,
    ILogger<ProgrammeImageService> logger) : BackgroundService
{
    internal bool ExtractionRunning => _activeExtraction is { IsCompleted: false };
    internal DateTime LastBackgroundCaptureUtc => _lastBackgroundCaptureUtc;
    private Configuration.PluginConfiguration Configuration => liveTvService.Configuration;
    private string Identity => Configuration == null ? null : GetConnectionIdentity(Configuration.TVH_ServerName, Configuration.HTSP_Port, Configuration.Username);
    private string LibraryChannelId(string rawId) => liveTvService.NativeServerId == null ? rawId : NativeTunerHost.Prefix(liveTvService.NativeServerId) + rawId;
    private Task<string> _activeExtraction;
    private DateTime _lastBackgroundCaptureUtc;
    private readonly Queue<long> _backgroundCandidates = new();
    private string _backgroundCandidateIdentity;
    private string _lastBackgroundMux;

    internal static string GetConnectionIdentity(string host, int port, string username) =>
        string.Join("|", host?.Trim().ToLowerInvariant(), port.ToString(CultureInfo.InvariantCulture), username?.Trim());

    internal static string CurrentConnectionIdentity => Plugin.Instance is { } plugin
        ? GetConnectionIdentity(plugin.Configuration.TVH_ServerName, plugin.Configuration.HTSP_Port, plugin.Configuration.Username) : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await CaptureMissingImagesAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogDebug(ex, "Programme artwork scan skipped"); }
        }
    }

    internal static string GetImagePath(long channelId, string eventId, DateTime start) => GetImagePathForConnection(channelId, eventId, start, CurrentConnectionIdentity);

    internal static string GetImagePathForConnection(long channelId, string eventId, DateTime start, string identity)
    {
        var plugin = Plugin.Instance;
        if (plugin == null) return null;
        var key = string.Join("|", identity, channelId.ToString(CultureInfo.InvariantCulture), eventId, start.Ticks.ToString(CultureInfo.InvariantCulture));
        return Path.Combine(plugin.ImageCachePath, "programme-frame-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".jpg");
    }

    internal Task CaptureMissingImagesAsync(CancellationToken token) => CaptureMissingImagesForServerAsync(token, true);

    internal async Task CaptureMissingImagesForServerAsync(CancellationToken token, bool allowBackgroundCapture)
    {
        if (Configuration?.GenerateMissingProgrammeImages != true || _activeExtraction is { IsCompleted: false }) return;
        var identity = Identity;
        bool StillCurrent() => identity == Identity && identity == connectionHandler.GetProgrammeConnectionIdentity();
        if (!StillCurrent()) return;
        var channelIds = HtspLiveStream.GetWatchedChannelIdsForConnection(identity);
        if (channelIds.Length == 0 && allowBackgroundCapture && Configuration.CaptureUnwatchedProgrammeImages
            && DateTime.UtcNow - _lastBackgroundCaptureUtc >= TimeSpan.FromMinutes(1))
        {
            var available = libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.LiveTvChannel] })
                .OfType<LiveTvChannel>().Where(item => item.ServiceName == liveTvService.Name && item.ChannelType != ChannelType.Radio && (liveTvService.NativeServerId == null || item.ExternalId.StartsWith(NativeTunerHost.Prefix(liveTvService.NativeServerId), StringComparison.Ordinal)))
                .OrderBy(item => item.ExternalId, StringComparer.Ordinal).ToArray();
            var resolved = connectionHandler.ResolveChannelIds(available.Select(item => liveTvService.NativeServerId == null ? item.ExternalId : item.ExternalId[NativeTunerHost.Prefix(liveTvService.NativeServerId).Length..]));
            var candidate = GetNextBackgroundChannel(resolved, identity);
            if (candidate.HasValue) channelIds = [candidate.Value];
        }
        foreach (var channelId in channelIds)
        {
            token.ThrowIfCancellationRequested();
            if (Configuration?.GenerateMissingProgrammeImages != true || !StillCurrent() || _activeExtraction is { IsCompleted: false }) return;
            string samplePath = null, extractedPath = null, stagingPath = null, stampedPath = null, backupPath = null, imagePath = null;
            var published = false;
            var replaced = false;
            var backupTime = DateTime.MinValue;
            HtspLiveStream captureStream = null;
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var epg = connectionHandler.GetCachedEvents(channelId, now, now)
                    .Where(item => item.getLong("stop") > now)
                    .OrderByDescending(item => item.getLong("start")).FirstOrDefault();
                if (epg == null || !string.IsNullOrWhiteSpace(epg.getString("image", string.Empty))) continue;
                var start = DateTimeOffset.FromUnixTimeSeconds(epg.getLong("start")).UtcDateTime;
                var eventId = epg.getLong("eventId").ToString(CultureInfo.InvariantCulture);
                var path = GetImagePathForConnection(channelId, eventId, start, identity);
                imagePath = path;
                var channel = libraryManager.GetItemList(new InternalItemsQuery {
                    IncludeItemTypes = [BaseItemKind.LiveTvChannel], ExternalId = LibraryChannelId(connectionHandler.GetExternalChannelId(channelId))
                }).OfType<LiveTvChannel>().FirstOrDefault(item => item.ServiceName == liveTvService.Name);
                if (channel == null) continue;
                var programme = libraryManager.GetItemList(new InternalItemsQuery {
                    IncludeItemTypes = [BaseItemKind.LiveTvProgram], ChannelIds = [channel.Id], ExternalId = liveTvService.NativeServerId == null ? eventId : NativeTunerHost.Prefix(liveTvService.NativeServerId) + eventId + "_" + channel.ExternalId
                }).OfType<LiveTvProgram>().FirstOrDefault(item => item.StartDate == start);
                if (programme == null || (programme.HasImage(ImageType.Primary)
                    && programme.GetImageInfo(ImageType.Primary, 0)?.Path != path)) continue;
                var needsCapture = !File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= TimeSpan.FromMinutes(5);
                if (!needsCapture && programme.HasImage(ImageType.Primary)) continue;

                if (needsCapture)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    if (!HtspLiveStream.TryGetWatchedSample(channelId, start, identity, out var chunks, out var video))
                    {
                        if (!allowBackgroundCapture || !Configuration.CaptureUnwatchedProgrammeImages || HtspLiveStream.GetWatchedChannelIds().Length > 0
                            || DateTime.UtcNow - _lastBackgroundCaptureUtc < TimeSpan.FromMinutes(1)) continue;
                        _lastBackgroundCaptureUtc = DateTime.UtcNow;
                        captureStream = liveTvService.CreateCaptureStream(channelId);
                        await captureStream.OpenCaptureAsync(timeout.Token).ConfigureAwait(false);
                        while (!captureStream.TryGetBufferedSample(start, identity, out chunks, out video))
                        {
                            if (captureStream.IsCaptureClosed) throw new IOException("HTSP programme capture stopped before a keyframe arrived.");
                            if (!Configuration.GenerateMissingProgrammeImages || !Configuration.CaptureUnwatchedProgrammeImages
                                || !StillCurrent() || HtspLiveStream.GetWatchedChannelIds().Length > 0) break;
                            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                        }
                        if (chunks != null && video != null) _lastBackgroundMux = captureStream.MuxIdentity;
                        await captureStream.CloseCaptureAsync().ConfigureAwait(false);
                        captureStream.Dispose();
                        captureStream = null;
                        if (chunks == null || video == null) continue;
                    }
                    using var extractionTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    extractionTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    samplePath = Path.Combine(Path.GetTempPath(), "tvheadend-frame-" + Guid.NewGuid().ToString("N") + ".ts");
                    await HtspLiveStream.WriteBufferedSampleAsync(samplePath, chunks, extractionTimeout.Token).ConfigureAwait(false);
                    _activeExtraction = mediaEncoder.ExtractVideoImage(samplePath, "ts",
                        new MediaSourceInfo { Path = samplePath, Protocol = MediaProtocol.File, Container = "ts" },
                        video, (Video3DFormat?)null, TimeSpan.Zero, extractionTimeout.Token);
                    try { extractedPath = await _activeExtraction.WaitAsync(extractionTimeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        _ = CleanupLateExtractionAsync(_activeExtraction, samplePath);
                        samplePath = null;
                        throw;
                    }
                    if (string.IsNullOrEmpty(extractedPath) || !File.Exists(extractedPath)) continue;
                    var size = new FileInfo(extractedPath).Length;
                    if (size is <= 0 or > 20 * 1024 * 1024) continue;
                    connectionHandler.ValidateImage(extractedPath);
                    if (Configuration?.GenerateMissingProgrammeImages != true || !StillCurrent()) continue;
                    var logo = channel.GetImageInfo(ImageType.Primary, 0)?.Path;
                    if (File.Exists(logo))
                    {
                        stampedPath = Path.Combine(Path.GetTempPath(), "tvheadend-logo-" + Guid.NewGuid().ToString("N") + ".jpg");
                        if (!await StampLogoAsync(extractedPath, logo, stampedPath, timeout.Token).ConfigureAwait(false))
                        { File.Delete(stampedPath); stampedPath = null; }
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    stagingPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(stampedPath ?? extractedPath, stagingPath);
                    if (Configuration?.GenerateMissingProgrammeImages != true || !StillCurrent()) continue;
                }

                now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (Configuration?.GenerateMissingProgrammeImages != true || !StillCurrent()
                    || (programme.HasImage(ImageType.Primary) && programme.GetImageInfo(ImageType.Primary, 0)?.Path != path)
                    || !connectionHandler.GetCachedEvents(channelId, now, now).Any(item => item.getLong("eventId").ToString(CultureInfo.InvariantCulture) == eventId
                        && item.getLong("start") == epg.getLong("start") && item.getLong("stop") > now
                        && string.IsNullOrWhiteSpace(item.getString("image", string.Empty)))) continue;
                if (stagingPath != null)
                {
                    if (File.Exists(path))
                    {
                        backupTime = File.GetLastWriteTimeUtc(path);
                        backupPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        File.Copy(path, backupPath);
                    }
                    File.Move(stagingPath, path, true);
                    replaced = true;
                }
                var previousImage = programme.GetImageInfo(ImageType.Primary, 0);
                programme.SetImage(new ItemImageInfo { Path = path, Type = ImageType.Primary, DateModified = File.GetLastWriteTimeUtc(path) }, 0);
                try
                {
                    await libraryManager.UpdateItemAsync(programme, null, ItemUpdateType.ImageUpdate, token).ConfigureAwait(false);
                    published = true;
                }
                catch
                {
                    var image = programme.GetImageInfo(ImageType.Primary, 0);
                    if (image?.Path == path) programme.RemoveImage(image);
                    if (previousImage != null) programme.SetImage(previousImage, 0);
                    throw;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogDebug(ex, "Programme frame capture skipped for channel {ChannelId}", channelId); }
            finally
            {
                if (captureStream != null) { await captureStream.CloseCaptureAsync().ConfigureAwait(false); captureStream.Dispose(); }
                if (!published && replaced && backupPath != null && File.Exists(backupPath))
                {
                    try
                    {
                        File.Move(backupPath, imagePath, true);
                        File.SetLastWriteTimeUtc(imagePath, backupTime);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(ex, "Unable to restore programme artwork; backup retained at {BackupPath}", backupPath);
                        backupPath = null;
                    }
                }
                foreach (var path in new[] { samplePath, extractedPath, stagingPath, stampedPath, backupPath }.Where(path => path != null).Distinct())
                {
                    try { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { logger.LogDebug(ex, "Unable to remove programme capture temporary file"); }
                }
            }
        }
    }

    internal long? GetNextBackgroundChannel(long[] available, string identity)
    {
        var eligible = new HashSet<long>(available);
        HtspLiveStream.PruneKnownMuxes(identity, eligible);
        if (_backgroundCandidateIdentity != identity)
        {
            _backgroundCandidates.Clear();
            _lastBackgroundMux = null;
            _backgroundCandidateIdentity = identity;
        }
        while (_backgroundCandidates.Count > 0)
        {
            var channel = _backgroundCandidates.Dequeue();
            if (eligible.Contains(channel)) return channel;
        }
        foreach (var item in eligible.Select(channel => (Channel: channel, Mux: HtspLiveStream.GetKnownMux(identity, channel)))
            .OrderBy(item => item.Mux != null && item.Mux == _lastBackgroundMux ? 0 : item.Mux != null ? 1 : 2)
            .ThenBy(item => item.Mux, StringComparer.Ordinal).ThenBy(item => item.Channel))
            _backgroundCandidates.Enqueue(item.Channel);
        return _backgroundCandidates.Count > 0 ? _backgroundCandidates.Dequeue() : null;
    }

    internal async Task<bool> StampLogoAsync(string frame, string logo, string output, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        Process process = null;
        try
        {
            var dimensions = connectionHandler.GetValidatedImageSize(frame);
            connectionHandler.ValidateImage(logo);
            var width = Math.Max(1, (int)(dimensions.Width * 0.12));
            var height = Math.Max(1, (int)(dimensions.Height * 0.2));
            var margin = Math.Max(1, (int)(dimensions.Width * 0.02));
            var filter = FormattableString.Invariant($"[1:v]format=rgba,scale={width}:{height}:force_original_aspect_ratio=decrease[logo];[0:v][logo]overlay=W-w-{margin}:H-h-{margin}");
            var info = new ProcessStartInfo(mediaEncoder.EncoderPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-nostdin", "-y", "-v", "error", "-i", frame, "-i", logo, "-filter_complex", filter, "-frames:v", "1", "-q:v", "2", output }) info.ArgumentList.Add(arg);
            process = Process.Start(info) ?? throw new IOException("FFmpeg did not start.");
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(output))
            { logger.LogDebug("Programme logo overlay failed: {Error}", await errors.ConfigureAwait(false)); return false; }
            connectionHandler.ValidateImage(output);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogDebug(ex, "Programme logo overlay skipped"); return false; }
        finally
        {
            if (process != null)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
                process.Dispose();
            }
        }
    }

    private async Task CleanupLateExtractionAsync(Task<string> extraction, string samplePath)
    {
        string output = null;
        try { output = await extraction.ConfigureAwait(false); }
        catch (Exception ex) { logger.LogDebug(ex, "Cancelled programme extraction finished"); }
        foreach (var path in new[] { samplePath, output }.Where(path => path != null).Distinct())
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.LogDebug(ex, "Unable to remove programme capture temporary file"); }
        }
    }
}
