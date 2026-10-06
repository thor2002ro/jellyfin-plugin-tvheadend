using System;
using System.Globalization;
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
    private Task<string> _activeExtraction;

    internal static string GetConnectionIdentity(string host, int port, string username) =>
        string.Join("|", host?.Trim().ToLowerInvariant(), port.ToString(CultureInfo.InvariantCulture), username?.Trim());

    internal static string CurrentConnectionIdentity => Plugin.Instance is { } plugin
        ? GetConnectionIdentity(plugin.Configuration.TVH_ServerName, plugin.Configuration.HTSP_Port, plugin.Configuration.Username) : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await CaptureMissingImagesAsync(stoppingToken).ConfigureAwait(false);
    }

    internal static string GetImagePath(long channelId, string eventId, DateTime start)
    {
        var plugin = Plugin.Instance;
        if (plugin == null) return null;
        var key = string.Join("|", CurrentConnectionIdentity, channelId.ToString(CultureInfo.InvariantCulture), eventId, start.Ticks.ToString(CultureInfo.InvariantCulture));
        return Path.Combine(plugin.ImageCachePath, "programme-frame-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".jpg");
    }

    internal async Task CaptureMissingImagesAsync(CancellationToken token)
    {
        if (Plugin.Instance?.Configuration.GenerateMissingProgrammeImages != true || _activeExtraction is { IsCompleted: false }) return;
        var identity = CurrentConnectionIdentity;
        bool StillCurrent() => identity == CurrentConnectionIdentity && identity == connectionHandler.GetProgrammeConnectionIdentity();
        if (!StillCurrent()) return;
        foreach (var channelId in HtspLiveStream.GetWatchedChannelIds())
        {
            token.ThrowIfCancellationRequested();
            if (Plugin.Instance?.Configuration.GenerateMissingProgrammeImages != true || !StillCurrent() || _activeExtraction is { IsCompleted: false }) return;
            string samplePath = null, extractedPath = null, stagingPath = null;
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var epg = connectionHandler.GetCachedEvents(channelId, now, now)
                    .Where(item => item.getLong("stop") > now)
                    .OrderByDescending(item => item.getLong("start")).FirstOrDefault();
                if (epg == null || !string.IsNullOrWhiteSpace(epg.getString("image", string.Empty))) continue;
                var start = DateTimeOffset.FromUnixTimeSeconds(epg.getLong("start")).UtcDateTime;
                var eventId = epg.getLong("eventId").ToString(CultureInfo.InvariantCulture);
                var path = GetImagePath(channelId, eventId, start);
                var channel = libraryManager.GetItemList(new InternalItemsQuery {
                    IncludeItemTypes = [BaseItemKind.LiveTvChannel], ExternalId = connectionHandler.GetExternalChannelId(channelId)
                }).OfType<LiveTvChannel>().FirstOrDefault(item => item.ServiceName == liveTvService.Name);
                if (channel == null) continue;
                var programme = libraryManager.GetItemList(new InternalItemsQuery {
                    IncludeItemTypes = [BaseItemKind.LiveTvProgram], ChannelIds = [channel.Id], ExternalId = eventId
                }).OfType<LiveTvProgram>().FirstOrDefault(item => item.StartDate == start);
                if (programme == null || programme.HasImage(ImageType.Primary)) continue;

                if (!File.Exists(path))
                {
                    if (!HtspLiveStream.TryGetWatchedSample(channelId, start, identity, out var chunks, out var video)) continue;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    samplePath = Path.Combine(Path.GetTempPath(), "tvheadend-frame-" + Guid.NewGuid().ToString("N") + ".ts");
                    await HtspLiveStream.WriteBufferedSampleAsync(samplePath, chunks, timeout.Token).ConfigureAwait(false);
                    _activeExtraction = mediaEncoder.ExtractVideoImage(samplePath, "ts",
                        new MediaSourceInfo { Path = samplePath, Protocol = MediaProtocol.File, Container = "ts" },
                        video, (Video3DFormat?)null, TimeSpan.Zero, timeout.Token);
                    try { extractedPath = await _activeExtraction.WaitAsync(timeout.Token).ConfigureAwait(false); }
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
                    if (Plugin.Instance?.Configuration.GenerateMissingProgrammeImages != true || !StillCurrent()) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    stagingPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(extractedPath, stagingPath);
                    File.Move(stagingPath, path, true);
                }

                now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (Plugin.Instance?.Configuration.GenerateMissingProgrammeImages != true || !StillCurrent() || programme.HasImage(ImageType.Primary)
                    || !connectionHandler.GetCachedEvents(channelId, now, now).Any(item => item.getLong("eventId").ToString(CultureInfo.InvariantCulture) == eventId
                        && item.getLong("start") == epg.getLong("start") && item.getLong("stop") > now
                        && string.IsNullOrWhiteSpace(item.getString("image", string.Empty)))) continue;
                programme.SetImage(new ItemImageInfo { Path = path, Type = ImageType.Primary, DateModified = File.GetLastWriteTimeUtc(path) }, 0);
                try { await libraryManager.UpdateItemAsync(programme, null, ItemUpdateType.ImageUpdate, token).ConfigureAwait(false); }
                catch
                {
                    var image = programme.GetImageInfo(ImageType.Primary, 0);
                    if (image?.Path == path) programme.RemoveImage(image);
                    throw;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogDebug(ex, "Programme frame capture skipped for channel {ChannelId}", channelId); }
            finally
            {
                foreach (var path in new[] { samplePath, extractedPath, stagingPath }.Where(path => path != null).Distinct())
                {
                    try { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { logger.LogDebug(ex, "Unable to remove programme capture temporary file"); }
                }
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
