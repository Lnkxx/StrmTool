using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool
{
    /// <summary>
    /// 媒体探测结果，包含流信息和元数据
    /// </summary>
    public class MediaProbeResult
    {
        public List<MediaStream> MediaStreams { get; set; } = new List<MediaStream>();
        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
        public long Size { get; set; }
        public ProbeFailureKind FailureKind { get; set; }
        public bool PersistenceVerified { get; set; }
        public bool ChaptersProbed { get; set; }
        public string StrmContentHash { get; set; }
        public string SourcePath { get; set; }
        public long? RunTimeTicks { get; set; }
        public string Container { get; set; }
        public int? TotalBitrate { get; set; }
        public bool Success => MediaStreams != null && MediaStreams.Any(x => x.Type == MediaStreamType.Video || x.Type == MediaStreamType.Audio);
    }

    /// <summary>
    /// STRM 媒体流相关共享服务：扫描、读取媒体流、探测远程媒体并写入数据库。
    /// </summary>
    public class StrmMediaInfoService
    {
        // STRM 文件扩展名常量
        public const string StrmFileExtension = ".strm";

        /// <summary>
        /// 检查路径是否是 strm 文件
        /// </summary>
        public static bool IsStrmFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.EndsWith(StrmFileExtension, StringComparison.OrdinalIgnoreCase);
        }

        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IMediaStreamRepository _mediaStreamRepository;
        private readonly IChapterRepository _chapterRepository;
        private readonly IItemRepository _itemRepository;
        private static readonly SemaphoreSlim IsoGate = new SemaphoreSlim(1, 1);
        private readonly IsoMediaProbe _isoMediaProbe;
        private readonly RemoteIsoMediaProbe _remoteIsoMediaProbe;

        public StrmMediaInfoService(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            IChapterRepository chapterRepository,
            ILogger logger,
            IItemRepository itemRepository)
        {
            _libraryManager = libraryManager;
            _mediaEncoder = mediaEncoder;
            _mediaStreamRepository = mediaStreamRepository;
            _chapterRepository = chapterRepository;
            _itemRepository = itemRepository;
            _logger = logger;
            _isoMediaProbe = new IsoMediaProbe(logger);
            _remoteIsoMediaProbe = new RemoteIsoMediaProbe(logger);
        }

        /// <summary>
        /// 递归查找目录下所有的 strm 文件
        /// </summary>
        /// <param name="directoryPath">要扫描的目录路径</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>找到的所有 strm 文件对应的库条目列表</returns>
        public List<BaseItem> FindStrmFilesInDirectory(string directoryPath, CancellationToken cancellationToken)
        {
            var strmFiles = new List<BaseItem>();
            var dirs = new Stack<string>();
            dirs.Push(directoryPath);

            while (dirs.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var current = dirs.Pop();
                try
                {
                    IEnumerable<string> files = Enumerable.Empty<string>();
                    try
                    {
                        files = Directory.EnumerateFiles(current)
                            .Where(path => IsStrmFile(path));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error enumerating files in {Directory}", current);
                    }

                    foreach (var strmPath in files)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        try
                        {
                            var item = _libraryManager.FindByPath(strmPath, false);
                            if (item != null)
                            {
                                strmFiles.Add(item);
                            }
                            else
                            {
                                _logger.LogDebug("Could not find library item for path: {Path}", strmPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error processing file {Path}", strmPath);
                        }
                    }

                    IEnumerable<string> subDirs = Enumerable.Empty<string>();
                    try
                    {
                        subDirs = Directory.EnumerateDirectories(current);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error enumerating directories in {Directory}", current);
                    }

                    foreach (var sub in subDirs)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        dirs.Push(sub);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning directory {Directory}", current);
                }
            }

            return strmFiles;
        }

        /// <summary>
        /// 获取库中所有的 strm 文件（去重）
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有 strm 文件对应的库条目列表，永不为 null</returns>
        public List<BaseItem> GetAllStrmItems(CancellationToken cancellationToken)
        {
            var rootFolders = _libraryManager.GetVirtualFolders()
                .SelectMany(vf => vf.Locations)
                .Distinct()
                .ToList();

            var strmItems = new List<BaseItem>();
            foreach (var rootFolder in rootFolders)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    strmItems.AddRange(FindStrmFilesInDirectory(rootFolder, cancellationToken));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning folder {Folder}", rootFolder);
                }
            }

            return strmItems
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Path))
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// 获取库条目的媒体流信息
        /// </summary>
        /// <param name="item">库条目对象</param>
        /// <returns>媒体流列表，若无法获取则返回空列表</returns>
        public List<MediaStream> GetItemMediaStreams(BaseItem item)
        {
            try
            {
                // 使用 IHasMediaSources 接口获取媒体流，避免反射
                if (item is IHasMediaSources hasMediaSources)
                {
                    var streams = hasMediaSources.GetMediaStreams();
                    if (streams != null)
                    {
                        return streams.ToList();
                    }
                }

                return new List<MediaStream>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting media streams for {ItemType}", item.GetType().Name);
                return new List<MediaStream>();
            }
        }

        /// <summary>
        /// 保存库条目的媒体流信息
        /// </summary>
        /// <param name="itemId">库条目ID</param>
        /// <param name="mediaStreams">要保存的媒体流列表</param>
        /// <param name="cancellationToken">取消令牌</param>
        public void SaveMediaStreams(Guid itemId, List<MediaStream> mediaStreams, CancellationToken cancellationToken)
        {
            _mediaStreamRepository.SaveMediaStreams(itemId, mediaStreams, cancellationToken);
        }

        /// <summary>
        /// 探测媒体流并返回完整结果（包含元数据）
        /// </summary>
        public async Task<MediaProbeResult> ProbeMediaStreamsAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var sourcePath = item.Path;
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var result = new MediaProbeResult { SourcePath = sourcePath };

            try
            {
                var capturedHash = MediaInfoCache.GetStrmContentHash(sourcePath);
                if (capturedHash == null) throw new IOException("STRM cannot be read");
                var strmContent = ReadStrmSourcePath(sourcePath);
                if (MediaInfoCache.GetStrmContentHash(sourcePath) != capturedHash) throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
                if (string.IsNullOrWhiteSpace(strmContent))
                {
                    _logger.LogWarning("STRM file is empty: {Path}", sourcePath);
                    return result;
                }

                var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
                if (config.EnableIsoProbe && IsoDetector.IsIsoOriginal(sourcePath, strmContent) &&
                    IsoDetector.IsHttpPath(strmContent))
                {
                    try
                    {
                        _logger.LogInformation("Probing remote ISO with HTTP Range for {Name}", fileName);
                        var remoteResult = await RunIsoAsync(() => _remoteIsoMediaProbe.ProbeAsync(
                            strmContent, config.IsoProbeTimeoutSeconds, cancellationToken), cancellationToken).ConfigureAwait(false);

                        if (remoteResult.MediaStreams.Count > 0)
                        {
                            if (MediaInfoCache.GetStrmContentHash(sourcePath) != capturedHash) throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
                            remoteResult.StrmContentHash = capturedHash;
                            remoteResult.SourcePath = sourcePath;
                            _logger.LogInformation(
                                "Successfully probed remote ISO {Name}: {Streams} streams, {Chapters} chapters",
                                fileName,
                                remoteResult.MediaStreams.Count,
                                remoteResult.Chapters.Count);
                            return remoteResult;
                        }

                        _logger.LogWarning("Remote ISO probe returned no media streams for {Name}", fileName);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result.FailureKind = ProbeFailurePolicy.Classify(ex);
                        _logger.LogWarning("Remote ISO probe failed for {Id}: {Category}", item.Id, result.FailureKind);
                    }

                    // Jellyfin's regular ffprobe path cannot open an HTTP ISO as a Blu-ray disc.
                    return result;
                }

                if (config.EnableIsoProbe && IsoDetector.IsIsoOriginal(sourcePath, strmContent) &&
                    IsoDetector.IsLocalIsoPath(strmContent) &&
                    !string.IsNullOrWhiteSpace(config.CustomFfprobePath))
                {
                    try
                    {
                        _logger.LogDebug("Probing local ISO source with bluray protocol for {Name}", fileName);
                        var isoResult = await RunIsoAsync(() => _isoMediaProbe.ProbeAsync(
                            strmContent, config.CustomFfprobePath, config.IsoProbeTimeoutSeconds, cancellationToken), cancellationToken).ConfigureAwait(false);

                        if (isoResult.MediaStreams.Count > 0)
                        {
                            if (MediaInfoCache.GetStrmContentHash(sourcePath) != capturedHash) throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
                            isoResult.StrmContentHash = capturedHash;
                            isoResult.SourcePath = sourcePath;
                            _logger.LogDebug("Successfully probed ISO {Name}: {Streams} streams, {Chapters} chapters",
                                fileName, isoResult.MediaStreams.Count, isoResult.Chapters.Count);

                            return isoResult;
                        }

                        _logger.LogWarning("ISO ffprobe returned no media streams for {Name}; falling back to Jellyfin media probing", fileName);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ISO probe failed for {Name}; falling back to Jellyfin media probing", fileName);
                    }
                }

                var isAudio = item.MediaType == MediaType.Audio;
                var mediaInfo = await _mediaEncoder.GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaSource = new MediaSourceInfo
                        {
                            Path = strmContent,
                            Protocol = GetProtocolFromPath(strmContent),
                        },
                        MediaType = isAudio ? DlnaProfileType.Audio : DlnaProfileType.Video,
                        ExtractChapters = false,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (mediaInfo?.MediaStreams != null && mediaInfo.MediaStreams.Count > 0)
                {
                    // 保存媒体流信息（不保存 Item 元数据，避免与 Jellyfin 的元数据重置产生竞态条件）
                    // Item 元数据会在 ItemUpdateListener 中从缓存恢复
                    if (MediaInfoCache.GetStrmContentHash(sourcePath) != capturedHash) throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
                    result.StrmContentHash = capturedHash;

                    // 填充返回结果（包含需要的元数据，供缓存使用）
                    result.MediaStreams = mediaInfo.MediaStreams.ToList();
                    result.Size = mediaInfo.Size.GetValueOrDefault();
                    result.RunTimeTicks = mediaInfo.RunTimeTicks;
                    result.Container = mediaInfo.Container;

                    _logger.LogDebug("Successfully saved {Count} media streams for {Name} (item metadata will be restored later via cache)",
                        mediaInfo.MediaStreams.Count, fileName);
                    return result;
                }

                _logger.LogDebug("No media streams found for {Name}", fileName);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.FailureKind = ProbeFailurePolicy.Classify(ex);
                _logger.LogWarning("Probe failed for {Id}: {Category}", item.Id, result.FailureKind);
                return result;
            }
        }

        public void SaveChapters(Guid itemId, IEnumerable<ChapterInfo> chapters, CancellationToken cancellationToken)
        {
            var chapterList = chapters?.ToList() ?? new List<ChapterInfo>();
            if (chapterList.Count == 0)
            {
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _chapterRepository.SaveChapters(itemId, chapterList);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save {Count} ISO chapters for item {ItemId}", chapterList.Count, itemId);
            }
        }

        private static async Task<MediaProbeResult> RunIsoAsync(Func<Task<MediaProbeResult>> probe, CancellationToken token)
        {
            await IsoGate.WaitAsync(token).ConfigureAwait(false);
            try { return await probe().ConfigureAwait(false); }
            finally { IsoGate.Release(); }
        }

        public async Task ApplyProbeResultAsync(BaseItem item, MediaProbeResult result, CancellationToken cancellationToken)
        {
            var expectedPath = result.SourcePath ?? item.Path;
            var stream = TechnicalMetadataVerifier.PrimaryVideo(result.MediaStreams);
            if (!result.Success || result.Size <= 1024 || !(result.RunTimeTicks > 0)
                || (item is Video && (stream == null || !(stream.Width > 0) || !(stream.Height > 0))))
                throw new ProbeFailureException(ProbeFailureKind.InvalidMedia);
            if (string.IsNullOrEmpty(result.StrmContentHash) || result.StrmContentHash != MediaInfoCache.GetStrmContentHash(expectedPath))
                throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
            var current = _libraryManager.GetItemById(item.Id);
            if (current == null || current.Path != expectedPath)
                throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
            var previous = _itemRepository.RetrieveItem(item.Id);
            if (previous == null || previous.Path != expectedPath)
                throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
            var oldStreams = _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id }).ToList();
            var oldChapters = _chapterRepository.GetChapters(item.Id).ToList();
            DateTime? committedAt = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.StrmContentHash != MediaInfoCache.GetStrmContentHash(expectedPath)
                    || _itemRepository.RetrieveItem(item.Id)?.Path != expectedPath)
                    throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
                // Re-read so renumbering external subtitles cannot touch oldStreams, which the rollback needs intact.
                var currentStreams = _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id });
                _mediaStreamRepository.SaveMediaStreams(item.Id,
                    TechnicalMetadataVerifier.WithStoredExternal(result.MediaStreams, currentStreams,
                        ExternalSubtitleScanner.Discover(expectedPath)), cancellationToken);
                if (result.ChaptersProbed)
                {
                    if (result.Chapters?.Count > 0) _chapterRepository.SaveChapters(item.Id, result.Chapters);
                    else await _chapterRepository.DeleteChaptersAsync(item.Id, cancellationToken).ConfigureAwait(false);
                }
                current.Size = result.Size;
                current.RunTimeTicks = result.RunTimeTicks;
                current.Container = result.Container;
                if (current is Video video)
                {
                    video.Width = stream.Width.GetValueOrDefault(); video.Height = stream.Height.GetValueOrDefault();
                    video.TotalBitrate = !string.Equals(result.Container, "bluray", StringComparison.OrdinalIgnoreCase)
                        && !IsoDetector.IsIsoOriginal(expectedPath, File.ReadAllText(expectedPath).Trim())
                        ? (int)Math.Min(int.MaxValue, Math.Round(result.Size * 8.0 * TimeSpan.TicksPerSecond / result.RunTimeTicks.Value)) : result.TotalBitrate;
                }
                await current.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                committedAt = current.DateLastSaved;
                TechnicalMetadataVerifier.Verify(_itemRepository.RetrieveItem(item.Id), expectedPath, result,
                    _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id }));
                if (result.ChaptersProbed)
                {
                    result.Chapters ??= new List<ChapterInfo>();
                    var chapters = _chapterRepository.GetChapters(item.Id);
                    if (!TechnicalMetadataVerifier.ChaptersEqual(result.Chapters, chapters))
                        throw new ProbeFailureException(ProbeFailureKind.PersistenceMismatch);
                }
                result.PersistenceVerified = true;
                _logger.LogInformation("Persistence verified for {Id}: {Size} bytes", item.Id, result.Size);
            }
            catch
            {
                // Restore only this source's technical fields, never a newly moved/replaced source or watch state.
                try
                {
                    using var rollbackCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var latest = _libraryManager.GetItemById(item.Id);
                    if (latest?.Path == expectedPath && _itemRepository.RetrieveItem(item.Id)?.Path == expectedPath
                        && MediaInfoCache.GetStrmContentHash(expectedPath) == result.StrmContentHash)
                    {
                        _mediaStreamRepository.SaveMediaStreams(item.Id, oldStreams, rollbackCts.Token);
                        if (oldChapters.Count > 0) _chapterRepository.SaveChapters(item.Id, oldChapters);
                        else await _chapterRepository.DeleteChaptersAsync(item.Id, rollbackCts.Token).ConfigureAwait(false);
                        latest.Size = previous.Size; latest.RunTimeTicks = previous.RunTimeTicks; latest.Container = previous.Container;
                        if (latest is Video video && previous is Video prior)
                        { video.Width = prior.Width; video.Height = prior.Height; video.TotalBitrate = prior.TotalBitrate; }
                        await latest.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, rollbackCts.Token).ConfigureAwait(false);
                        var restored = _itemRepository.RetrieveItem(item.Id);
                        if (restored == null || restored.Path != expectedPath || restored.Size != previous.Size || restored.RunTimeTicks != previous.RunTimeTicks
                            || restored.Container != previous.Container || (restored is Video restoredVideo && previous is Video priorVideo
                                && (restoredVideo.Width != priorVideo.Width || restoredVideo.Height != priorVideo.Height || restoredVideo.TotalBitrate != priorVideo.TotalBitrate))
                            || !TechnicalMetadataVerifier.StreamsEqual(oldStreams, _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id }))
                            || !TechnicalMetadataVerifier.ChaptersEqual(oldChapters, _chapterRepository.GetChapters(item.Id)))
                            throw new ProbeFailureException(ProbeFailureKind.PersistenceMismatch);
                        _logger.LogWarning("Verified prior technical metadata restored for {Id}", item.Id);
                    }
                    else if (latest != null && StrmMediaInfoService.IsStrmFile(latest.Path))
                    {
                        // Invalidate only this task's exact committed result, including on a newly moved path.
                        var stored = _itemRepository.RetrieveItem(item.Id);
                        var storedStreams = _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id });
                        var storedChapters = _chapterRepository.GetChapters(item.Id);
                        bool ours = committedAt.HasValue && stored != null && stored.DateLastSaved == committedAt.Value
                            && stored.Path == latest.Path && stored.Size == result.Size && stored.RunTimeTicks == result.RunTimeTicks
                            && stored.Container == result.Container && stored.TotalBitrate == current.TotalBitrate
                            && (!(stored is Video storedVideo) || (storedVideo.Width == stream?.Width && storedVideo.Height == stream?.Height))
                            && TechnicalMetadataVerifier.StreamsEqual(result.MediaStreams, storedStreams)
                            && TechnicalMetadataVerifier.ChaptersEqual(result.ChaptersProbed ? result.Chapters : oldChapters, storedChapters);
                        if (ours)
                        {
                            _mediaStreamRepository.SaveMediaStreams(item.Id, new List<MediaStream>(), rollbackCts.Token);
                            await _chapterRepository.DeleteChaptersAsync(item.Id, rollbackCts.Token).ConfigureAwait(false);
                            latest.Size = File.Exists(latest.Path) ? new FileInfo(latest.Path).Length : 0;
                            latest.RunTimeTicks = null; latest.Container = null;
                            if (latest is Video invalidVideo) { invalidVideo.Width = 0; invalidVideo.Height = 0; invalidVideo.TotalBitrate = null; }
                            await latest.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, rollbackCts.Token).ConfigureAwait(false);
                            var invalidated = _itemRepository.RetrieveItem(item.Id);
                            if (invalidated == null || invalidated.Path != latest.Path || invalidated.Size != latest.Size
                                || invalidated.RunTimeTicks.HasValue || invalidated.Container != null || invalidated.TotalBitrate != null
                                || (invalidated is Video invalidatedVideo && (invalidatedVideo.Width != 0 || invalidatedVideo.Height != 0))
                                || _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id }).Count != 0
                                || _chapterRepository.GetChapters(item.Id).Count != 0)
                                throw new ProbeFailureException(ProbeFailureKind.PersistenceMismatch);
                            _logger.LogWarning("Invalidated changed-source metadata for {Id}; automatic re-probe required", item.Id);
                        }
                        else _logger.LogWarning("Source changed for {Id}; another result was preserved; inspection or re-probe required", item.Id);
                    }
                }
                catch { _logger.LogError("Technical rollback requires inspection for {Id}", item.Id); }
                throw;
            }
        }

        private static MediaProtocol GetProtocolFromPath(string path)
        {
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Http;
            }

            if (path.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtmp;
            }

            if (path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtsp;
            }

            if (path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Ftp;
            }

            return MediaProtocol.File;
        }

        private string ReadStrmSourcePath(string strmFilePath)
        {
            try
            {
                foreach (var line in File.ReadLines(strmFilePath))
                {
                    var sourcePath = line.Trim();
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        // 安全验证：检查路径遍历攻击
                        if (MediaInfoCache.ContainsPathTraversal(sourcePath))
                        {
                            _logger.LogWarning("Potential path traversal attack detected in strm file: {Path}", strmFilePath);
                            return string.Empty;
                        }
                        return sourcePath;
                    }
                }

                return string.Empty;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                _logger.LogWarning(ex, "Failed to read strm file: {Path}", strmFilePath);
                return string.Empty;
            }
        }


    }
}
