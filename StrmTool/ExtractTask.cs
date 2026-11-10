using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    public class ExtractTask : IScheduledTask, IDisposable
    {
        protected volatile bool _disposed = false;
        protected readonly ILogger _logger;
        protected readonly StrmMediaInfoService _mediaInfoService;
        protected readonly MediaInfoCache _mediaCache;
        protected volatile PluginConfiguration _config;
        protected readonly SemaphoreSlim _semaphore;

        private readonly ILibraryManager _libraryManager;
        private readonly IMediaStreamRepository _mediaStreamRepository;
        private LibraryScanListener _scanListener;
        private ItemUpdateListener _updateListener;
        private readonly CancellationTokenSource _backgroundTaskCts = new CancellationTokenSource();
        private readonly object _eventLock = new object();

        public ExtractTask(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            IChapterRepository chapterRepository,
            ILogger<ExtractTask> logger,
            IItemRepository itemRepository,
            ILibraryMonitor libraryMonitor)
        {
            _logger = logger;
            _mediaInfoService = new StrmMediaInfoService(libraryManager, mediaEncoder, mediaStreamRepository, chapterRepository, logger, itemRepository);
            _mediaCache = new MediaInfoCache(logger, libraryMonitor);

            if (Plugin.Instance == null)
            {
                _logger.LogWarning("Plugin instance not found, using default configuration");
                _config = new PluginConfiguration();
            }
            else
            {
                _config = Plugin.Instance.Configuration;
            }

            _semaphore = new SemaphoreSlim(_config.MaxConcurrentExtract);

            _libraryManager = libraryManager;
            _mediaStreamRepository = mediaStreamRepository;

            try
            {
                _scanListener = new LibraryScanListener(_libraryManager, _logger, _config, _mediaCache);
                _scanListener.StrmFileDetected += OnStrmFileDetected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize library scan listener");
            }

            try
            {
                _updateListener = new ItemUpdateListener(_libraryManager, _logger, _config, _mediaCache, itemRepository, mediaStreamRepository, chapterRepository);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize item update listener");
            }
        }

        public string Category => "StrmTool";
        public string Key => "StrmToolTask";
        public string Description => Plugin.Instance?.GetLocalizedString("StrmTool.TaskDescription") ?? "Extract media technical information (codec, resolution, subtitles) from strm files";
        public string Name => Plugin.Instance?.GetLocalizedString("StrmTool.TaskName") ?? "Extract Strm Media Info";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        protected void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                // 注意：MaxConcurrentExtract 需要 Jellyfin 重启后生效
                // 避免在运行时替换 semaphore 导致并发控制失效
                _config = Plugin.Instance.Configuration;
            }
        }

        /// <summary>
        /// 处理 strm 文件项的通用方法
        /// </summary>
        protected async Task<int> ProcessStrmItemsAsync(
            List<BaseItem> items,
            Func<BaseItem, CancellationToken, Task> processItemAsync,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            int processed = 0;
            int succeeded = 0;
            int failed = 0;
            int total = items.Count;

            var tasks = items.Select(async item =>
            {
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    await processItemAsync(item, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref succeeded);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning("Item {Id} failed: {Category}", item.Id, ProbeFailurePolicy.Classify(ex));
                }
                finally
                {
                    _semaphore.Release();
                    int current = Interlocked.Increment(ref processed);
                    double percent = Math.Min((double)current / total * 100, 100);
                    progress.Report(percent);
                }
            });

            await Task.WhenAll(tasks);
            progress.Report(100);
            _logger.LogInformation("Batch result: verified {Success}, failed {Failed}, attempted {Attempted}", succeeded, failed, processed);
            return succeeded;
        }

        /// <summary>
        /// 尝试从缓存加载媒体流，如果成功则直接保存
        /// </summary>
        /// <param name="item">库条目</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>是否成功从缓存加载</returns>
        /// <summary>
        /// 探测媒体流并保存到缓存
        /// </summary>
        /// <param name="item">库条目</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>探测结果</returns>
        private bool NeedsTechnicalRefresh(BaseItem item, IReadOnlyList<MediaStream> storedStreams)
        {
            bool hasStreams = storedStreams.Any(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio);
            if (_config.ForceRefreshIgnoreExisting || !hasStreams || !(item.Size > 1024) || !(item.RunTimeTicks > 0)) return true;
            if (item is MediaBrowser.Controller.Entities.Video video && (!(video.Width > 0) || !(video.Height > 0))) return true;
            if (_mediaCache.TryGetFullCache(item.Path, out var cache))
            {
                // Reset fields (size <= 1024, no runtime, no dimensions) were handled above. Real values that differ
                // from the cache mean the two probes disagree (白夜追凶: 1740 vs 1746 with swapped-looking caches), so
                // native data is kept; the cache only fills detail a native re-probe dropped.
                if (_config.EnableIsoProbe && MediaInfoCache.NeedsIsoProbeUpgrade(cache, item.Path)) return true;
                return TechnicalMetadataVerifier.StoredStreamsDegraded(cache.MediaStreams, storedStreams);
            }
            // No identity-checked cache (none, or a legacy one whose STRM changed since): keep healthy native metadata.
            return false;
        }

        private async Task<MediaProbeResult> ProbeAndCacheAsync(BaseItem item, CancellationToken cancellationToken)
        {
            MediaProbeResult probeResult = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 2 + Random.Shared.NextDouble()), cancellationToken).ConfigureAwait(false);
                probeResult = await _mediaInfoService.ProbeMediaStreamsAsync(item, cancellationToken).ConfigureAwait(false);
                if (probeResult.Success && probeResult.Size > 1024 && probeResult.RunTimeTicks > 0
                    && (!(item is MediaBrowser.Controller.Entities.Video) || probeResult.MediaStreams.Any(x => x.Type == MediaStreamType.Video && x.Width > 0 && x.Height > 0))) break;
                if (probeResult.FailureKind != ProbeFailureKind.None && !ProbeFailurePolicy.ShouldRetry(probeResult.FailureKind)) break;
            }
            if (!probeResult.Success || probeResult.Size <= 1024 || !(probeResult.RunTimeTicks > 0)
                || (item is MediaBrowser.Controller.Entities.Video && !probeResult.MediaStreams.Any(x => x.Type == MediaStreamType.Video && x.Width > 0 && x.Height > 0)))
                throw new ProbeFailureException(probeResult.FailureKind == ProbeFailureKind.None ? ProbeFailureKind.InvalidMedia : probeResult.FailureKind);

            await _mediaInfoService.ApplyProbeResultAsync(item, probeResult, cancellationToken).ConfigureAwait(false);
            if (_config.EnableMediaInfoCache && probeResult.PersistenceVerified)
            {
                await _mediaCache.SaveFullCacheAsync(
                    item.Path,
                    probeResult.MediaStreams,
                    probeResult.Size,
                    probeResult.RunTimeTicks,
                    probeResult.Container,
                    probeResult.Chapters,
                    cancellationToken,
                    probeResult.StrmContentHash, probeResult.ChaptersProbed, probeResult.TotalBitrate).ConfigureAwait(false);
            }

            return probeResult;
        }

        private void OnStrmFileDetected(object sender, BaseItem item)
        {
            if (_disposed)
                return;

            RefreshConfig();

            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            if (!_config.EnableAutoExtract)
            {
                _logger.LogDebug("Auto-extract is disabled, skipping {Name}", fileName);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var timeoutMinutes = _config?.MetadataRestoreTimeoutMinutes ?? 5;
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_backgroundTaskCts.Token);
                    cts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
                    await Task.Delay(_config.RefreshDelayMs, cts.Token).ConfigureAwait(false);

                    if (_disposed)
                        return;

                    await ExtractSingleItemAsync(item, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Extraction cancelled for {Name}", fileName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Background extract {Id} failed: {Category}", item.Id, ProbeFailurePolicy.Classify(ex));
                }
            }, _backgroundTaskCts.Token);
        }

        public void CleanupListener()
        {
            lock (_eventLock)
            {
                if (_scanListener != null)
                {
                    _scanListener.StrmFileDetected -= OnStrmFileDetected;
                    _scanListener.Dispose();
                    _scanListener = null;
                }

                if (_updateListener != null)
                {
                    _updateListener.Dispose();
                    _updateListener = null;
                }
            }
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            RefreshConfig();
            _logger.LogInformation("Starting strm file scan...");

            try
            {
                var allStrmItems = _mediaInfoService.GetAllStrmItems(cancellationToken);
                var scopedStrmItems = allStrmItems
                    .Where(item => _config.IsPathInProbeScope(item.Path))
                    .ToList();
                var strmItems = new List<BaseItem>();
                int totalFound = allStrmItems.Count;
                int totalInScope = scopedStrmItems.Count;

                foreach (var item in scopedStrmItems)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var mediaStreams = _mediaInfoService.GetItemMediaStreams(item);

                        if (NeedsTechnicalRefresh(item, mediaStreams))
                        {
                            strmItems.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error checking media streams for {Name}", item.Name);
                        strmItems.Add(item);
                    }
                }

                _logger.LogInformation("Found {Count} strm files in library, {InScope} in configured probe path, {NeedRefresh} need media info",
                    totalFound, totalInScope, strmItems.Count);

                if (strmItems.Count == 0)
                {
                    progress.Report(100);
                    _logger.LogInformation("Nothing to process, task complete.");
                    return;
                }

                await ProcessStrmFiles(strmItems, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error during strm file scan");
                throw;
            }
        }

        private async Task ProcessStrmFiles(List<BaseItem> strmItems, IProgress<double> progress, CancellationToken cancellationToken)
        {
            int processed = await ProcessStrmItemsAsync(strmItems, ProcessSingleItemAsync, progress, cancellationToken);

            _logger.LogInformation("Task complete. Verified {Processed}/{Total} selected STRM files; failures are reported separately.",
                processed, strmItems.Count);
        }

        /// <summary>
        /// 核心处理逻辑：从缓存加载或探测媒体流
        /// </summary>
        private async Task<(List<MediaStream> before, List<MediaStream> after)> ProcessItemCoreAsync(
            BaseItem item, 
            string logPrefix,
            CancellationToken cancellationToken)
        {
            using var lease = await ProbeCoordinator.EnterAsync(item.Id, cancellationToken).ConfigureAwait(false);
            var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
            _logger.LogDebug("{Prefix} - Before: {Count} streams", logPrefix, beforeStreams.Count);

            // 首先尝试从缓存加载
            MediaInfoCacheData cached = null;
            bool loadedFromCache = _config.EnableMediaInfoCache && !_config.ForceRefreshIgnoreCache
                && _mediaCache.TryGetFullCache(item.Path, out cached)
                && !(_config.EnableIsoProbe && MediaInfoCache.NeedsIsoProbeUpgrade(cached, item.Path));

            if (!loadedFromCache)
            {
                await ProbeAndCacheAsync(item, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _mediaInfoService.ApplyProbeResultAsync(item, new MediaProbeResult
                {
                    MediaStreams = cached.MediaStreams, Size = cached.Size, RunTimeTicks = cached.RunTimeTicks,
                    Container = cached.Container, StrmContentHash = cached.StrmContentHash, SourcePath = item.Path, Chapters = cached.Chapters, ChaptersProbed = cached.ChaptersProbed || cached.Chapters?.Count > 0, TotalBitrate = cached.TotalBitrate
                }, cancellationToken).ConfigureAwait(false);
            }

            var afterStreams = _mediaInfoService.GetItemMediaStreams(item);
            return (beforeStreams, afterStreams);
        }

        private async Task ProcessSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Processing {Name}", item.Name);

            var (beforeStreams, afterStreams) = await ProcessItemCoreAsync(
                item, 
                "StrmTool", 
                cancellationToken).ConfigureAwait(false);

            bool hasVideo = afterStreams.Any(s => s.Type == MediaStreamType.Video);
            bool hasAudio = afterStreams.Any(s => s.Type == MediaStreamType.Audio);

            _logger.LogInformation(
                "{Name}: Probe done. Streams {Before}→{After}. Video:{Video}, Audio:{Audio}",
                item.Name,
                beforeStreams.Count,
                afterStreams.Count,
                hasVideo,
                hasAudio
            );

            if (!(hasVideo || hasAudio))
            {
                _logger.LogWarning("{Name} may still lack media stream info", item.Name);
            }
        }

        public async Task ExtractSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            RefreshConfig();
            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _logger.LogDebug("Auto-extracting media info for new strm file: {Name}", fileName);

                var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
                _logger.LogDebug("Before: {Count} streams", beforeStreams.Count);

                if (!NeedsTechnicalRefresh(item, beforeStreams))
                {
                    _logger.LogInformation("{Name} already has media stream info, skipping", fileName);
                    return;
                }

                // 使用提取的通用方法处理缓存和探测
                var (_, afterStreams) = await ProcessItemCoreAsync(
                    item, 
                    "Auto-extract", 
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Auto-extract complete for {Name}. Streams {Before}→{After}",
                    fileName,
                    beforeStreams.Count,
                    afterStreams.Count
                );
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Auto-extract {Id} failed: {Category}", item.Id, ProbeFailurePolicy.Classify(ex));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            _disposed = true;

            if (disposing)
            {
                try
                {
                    _backgroundTaskCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                CleanupListener();
                _backgroundTaskCts.Dispose();
                _semaphore.Dispose();
            }
        }
    }
}
