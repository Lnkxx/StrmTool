using MediaBrowser.Model.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;

namespace StrmTool
{
    /// <summary>
    /// 监听Item更新事件，当strm文件的Size等元数据被重置时，从缓存恢复
    /// </summary>
    public class ItemUpdateListener : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly MediaInfoCache _mediaCache;
        private readonly IItemRepository _itemRepository;
        private readonly IMediaStreamRepository _mediaStreamRepository;
        private readonly IChapterRepository _chapterRepository;
        private readonly ConcurrentDictionary<Guid, RestoreOperation> _runningTasks = new();
        private readonly object _lifecycleLock = new();
        private PluginConfiguration _config;
        private volatile bool _isDisposed;
        private static volatile ItemUpdateListener _current;

        /// <summary>The listener of the running plugin, for callers outside the scheduled task (playback).</summary>
        public static ItemUpdateListener Current => _current;

        private sealed class RestoreOperation
        {
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public RestoreOperation(CancellationTokenSource cancellationSource)
            {
                CancellationSource = cancellationSource;
            }

            public CancellationTokenSource CancellationSource { get; }
            public Task Task => _completion.Task;
            public ProbeFailureKind FailureKind { get; private set; }

            public void Complete() => _completion.TrySetResult();

            public void Cancel(CancellationToken token) => _completion.TrySetCanceled(token);

            public void Fail(Exception exception, ProbeFailureKind failureKind)
            {
                FailureKind = failureKind;
                _completion.TrySetException(exception);
            }
        }

        public ItemUpdateListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config)
            : this(libraryManager, logger, config, null, null, null)
        {
        }

        public ItemUpdateListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config,
            MediaInfoCache mediaCache)
            : this(libraryManager, logger, config, mediaCache, null, null)
        {
        }

        public ItemUpdateListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config,
            MediaInfoCache mediaCache,
            IItemRepository itemRepository,
            IMediaStreamRepository mediaStreamRepository,
            IChapterRepository chapterRepository = null)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaCache = mediaCache ?? new MediaInfoCache(logger);
            _itemRepository = itemRepository;
            _mediaStreamRepository = mediaStreamRepository;
            _chapterRepository = chapterRepository;
            _config = config;

            _libraryManager.ItemUpdated += OnItemUpdated;
            _current = this;
            _logger.LogInformation("Item update listener initialized");
        }

        /// <summary>
        /// 刷新配置引用
        /// </summary>
        public void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                _config = Plugin.Instance.Configuration;
            }
        }

        private static bool TryRemoveExact(
            ConcurrentDictionary<Guid, RestoreOperation> operations,
            Guid itemId,
            RestoreOperation operation)
        {
            return ((ICollection<KeyValuePair<Guid, RestoreOperation>>)operations)
                .Remove(new KeyValuePair<Guid, RestoreOperation>(itemId, operation));
        }

        private void ObserveCompletedOperation(Guid itemId, RestoreOperation operation)
        {
            if (operation.Task.IsFaulted)
            {
                // Accessing Exception marks it observed while keeping the failure on the tracked task.
                _ = operation.Task.Exception;
                _logger.LogDebug("Metadata restore for item {ItemId} failed with {FailureKind}", itemId, operation.FailureKind);
            }
        }

        /// <summary>
        /// 移除已完成任务并观察其错误，避免任务异常被丢弃。
        /// </summary>
        private void CleanupCompletedTasks()
        {
            if (_runningTasks.Count <= 100)
            {
                return;
            }

            foreach (var pair in _runningTasks)
            {
                if (pair.Value.Task.IsCompleted)
                {
                    ObserveCompletedOperation(pair.Key, pair.Value);
                    TryRemoveExact(_runningTasks, pair.Key, pair.Value);
                }
            }
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (_isDisposed || e?.Item == null)
            {
                return;
            }

            var item = e.Item;
            try
            {
                RefreshConfig();

                if (!StrmMediaInfoService.IsStrmFile(item.Path) || !_config.IsPathInProbeScope(item.Path))
                {
                    return;
                }

                if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData) || !NeedsMetadataRestore(item, cacheData, GetStoredStreams(item.Id)))
                {
                    return;
                }

                StartRestore(item.Id, item.Path);
            }
            catch (Exception ex)
            {
                var failureKind = GetFailureKind(ex);
                _logger.LogError("Metadata restore handler failed for item {ItemId} with {FailureKind}", item.Id, failureKind);
            }
        }

        /// <summary>
        /// Starts a restore for the item unless one is already running. Returns the operation that covers the item
        /// (the running one or the new one), or null when the listener is disposed.
        /// </summary>
        private RestoreOperation StartRestore(Guid itemId, string path)
        {
            RestoreOperation operation;
            lock (_lifecycleLock)
            {
                if (_isDisposed)
                {
                    return null;
                }

                if (_runningTasks.TryGetValue(itemId, out var existing))
                {
                    if (!existing.Task.IsCompleted)
                    {
                        _logger.LogDebug("Item {ItemId} already has an active metadata restore", itemId);
                        return existing;
                    }

                    ObserveCompletedOperation(itemId, existing);
                    TryRemoveExact(_runningTasks, itemId, existing);
                }

                var timeoutMinutes = Math.Max(1, _config?.MetadataRestoreTimeoutMinutes ?? 5);
                var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
                operation = new RestoreOperation(cancellationSource);
                if (!_runningTasks.TryAdd(itemId, operation))
                {
                    cancellationSource.Dispose();
                    _logger.LogDebug("Item {ItemId} already has an active metadata restore", itemId);
                    return _runningTasks.TryGetValue(itemId, out var racing) ? racing : null;
                }
            }

            // This async method returns a real Task; it does not introduce an async-void delegate.
            _ = RunRestoreOperationAsync(itemId, path, operation);
            CleanupCompletedTasks();
            return operation;
        }

        /// <summary>
        /// Restores a reset STRM item from its cache right now and waits for it, so playback does not have to wait
        /// for a probe instead. The restore itself decides whether anything needs writing.
        /// </summary>
        /// <returns>True when the restore ran to completion within <paramref name="timeout"/>.</returns>
        public async Task<bool> TryRestoreNowAsync(BaseItem item, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (_isDisposed || item == null || _itemRepository == null || _mediaStreamRepository == null)
            {
                return false;
            }

            try
            {
                RefreshConfig();
                var operation = StartRestore(item.Id, item.Path);
                if (operation == null)
                {
                    return false;
                }

                await operation.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                // Timed out, cancelled or failed: the tracked operation keeps its own outcome and logging.
                return false;
            }
        }

        private static ProbeFailureKind GetFailureKind(Exception exception)
        {
            return exception is OperationCanceledException ? ProbeFailureKind.None : ProbeFailurePolicy.Classify(exception);
        }

        private IReadOnlyList<MediaStream> GetStoredStreams(Guid itemId)
        {
            return _mediaStreamRepository?.GetMediaStreams(new MediaStreamQuery { ItemId = itemId }).ToList();
        }

        internal static bool NeedsMetadataRestore(BaseItem item, MediaInfoCacheData cacheData, IReadOnlyList<MediaStream> storedStreams)
        {
            if (cacheData == null)
            {
                return false;
            }

            // Only a reset (Jellyfin sets an STRM's size to the pointer file, e.g. 93 bytes, and may drop runtime or
            // dimensions) is restored. Real values that differ from the cache are a probe disagreement: keep native.
            bool sizeReset = cacheData.Size > 1024 && !(item.Size > 1024);
            var cachedVideo = TechnicalMetadataVerifier.PrimaryVideo(cacheData.MediaStreams);
            bool needsUpdate = sizeReset || (cacheData.RunTimeTicks > 0 && !(item.RunTimeTicks > 0))
                || (item is Video video && cachedVideo != null && cachedVideo.Width > 0 && cachedVideo.Height > 0
                    && (!(video.Width > 0) || !(video.Height > 0)))
                || (item is Video bitrateVideo && cacheData.TotalBitrate > 0 && !(bitrateVideo.TotalBitrate > 0))
                // Jellyfin's own Probe Provider rewrites STRM streams without bitrate/bit depth.
                || (storedStreams != null && TechnicalMetadataVerifier.StoredStreamsDegraded(cacheData.MediaStreams, storedStreams))
                // ...and without the external subtitles next to the STRM (e.g. when playback starts).
                || (storedStreams != null && TechnicalMetadataVerifier.ExternalsMissing(storedStreams, ExternalSubtitleScanner.Discover(item.Path)));

            if (!needsUpdate && cacheData.RunTimeTicks.HasValue && !item.RunTimeTicks.HasValue)
            {
                needsUpdate = true;
            }

            return needsUpdate;
        }

        private async Task RunRestoreOperationAsync(Guid itemId, string expectedPath, RestoreOperation operation)
        {
            var cancellationToken = operation.CancellationSource.Token;
            try
            {
                // Wait for any active probe, then re-read both the item and its identity-checked cache.
                using var lease = await ProbeCoordinator.EnterAsync(itemId, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var latestItem = _libraryManager.GetItemById(itemId);
                if (latestItem == null || !string.Equals(latestItem.Path, expectedPath, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Item {ItemId} changed path or disappeared before metadata restore", itemId);
                    operation.Complete();
                    return;
                }

                if (!_mediaCache.TryGetFullCache(latestItem.Path, out var cacheData)
                    || !string.Equals(cacheData.StrmContentHash, MediaInfoCache.GetStrmContentHash(latestItem.Path), StringComparison.Ordinal))
                {
                    _logger.LogDebug("Item {ItemId} cache identity changed before metadata restore", itemId);
                    operation.Complete();
                    return;
                }

                if (!NeedsMetadataRestore(latestItem, cacheData, GetStoredStreams(itemId)))
                {
                    _logger.LogDebug("Item {ItemId} no longer needs metadata restore", itemId);
                    operation.Complete();
                    return;
                }

                await RestoreItemMetadataAsync(latestItem, cacheData, cancellationToken).ConfigureAwait(false);
                operation.Complete();
            }
            catch (OperationCanceledException)
            {
                operation.Cancel(cancellationToken);
                _logger.LogDebug("Metadata restore cancelled for item {ItemId}", itemId);
            }
            catch (Exception ex)
            {
                var failureKind = GetFailureKind(ex);
                operation.Fail(ex, failureKind);
                _logger.LogError("Metadata restore failed for item {ItemId} with {FailureKind}", itemId, failureKind);
            }
            finally
            {
                operation.CancellationSource.Dispose();
            }
        }

        private async Task RestoreItemMetadataAsync(BaseItem item, MediaInfoCacheData cacheData, CancellationToken cancellationToken)
        {
            if (_itemRepository == null || _mediaStreamRepository == null)
            {
                throw new InvalidOperationException("Persistence verification repositories are unavailable");
            }

            var expected = new MediaProbeResult
            {
                MediaStreams = cacheData.MediaStreams ?? new List<MediaStream>(),
                Chapters = cacheData.Chapters ?? new List<ChapterInfo>(),
                Size = cacheData.Size,
                RunTimeTicks = cacheData.RunTimeTicks,
                Container = cacheData.Container,
                StrmContentHash = cacheData.StrmContentHash,
                TotalBitrate = cacheData.TotalBitrate
            };

            if (string.IsNullOrEmpty(expected.StrmContentHash)
                || !string.Equals(expected.StrmContentHash, MediaInfoCache.GetStrmContentHash(item.Path), StringComparison.Ordinal))
            {
                throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
            }

            var fileName = Path.GetFileNameWithoutExtension(item.Path);
            _logger.LogInformation("Restoring metadata for {Name} (Size: {OldSize} -> {NewSize})",
                fileName, item.Size, cacheData.Size);

            item.Size = cacheData.Size;
            item.RunTimeTicks = cacheData.RunTimeTicks;
            item.Container = cacheData.Container;
            if (item is Video video && cacheData.MediaStreams != null)
            {
                var stream = TechnicalMetadataVerifier.PrimaryVideo(cacheData.MediaStreams);
                if (stream != null)
                {
                    video.Width = stream.Width.GetValueOrDefault();
                    video.Height = stream.Height.GetValueOrDefault();
                }

                // Preserve the existing ISO bitrate behavior: never derive it from whole-disc size.
                if (cacheData.RunTimeTicks > 0
                    && !string.Equals(cacheData.Container, "bluray", StringComparison.OrdinalIgnoreCase)
                    && !IsoDetector.IsIsoOriginal(item.Path, File.ReadAllText(item.Path).Trim()))
                {
                    video.TotalBitrate = (int)Math.Min(int.MaxValue,
                        Math.Round(cacheData.Size * 8.0 * TimeSpan.TicksPerSecond / cacheData.RunTimeTicks.Value));
                }
                else if (cacheData.TotalBitrate.HasValue)
                {
                    video.TotalBitrate = cacheData.TotalBitrate;
                }
            }

            // The streams themselves must be restored too: Jellyfin does reset them when it re-probes an STRM.
            var storedStreams = GetStoredStreams(item.Id);
            var discoveredExternal = ExternalSubtitleScanner.Discover(item.Path);
            if (TechnicalMetadataVerifier.StoredStreamsDegraded(expected.MediaStreams, storedStreams))
            {
                _mediaStreamRepository.SaveMediaStreams(item.Id,
                    TechnicalMetadataVerifier.WithStoredExternal(expected.MediaStreams, storedStreams, discoveredExternal), cancellationToken);
            }
            else if (storedStreams != null && TechnicalMetadataVerifier.ExternalsMissing(storedStreams, discoveredExternal))
            {
                // Embedded streams are fine (native data is kept as-is); only the external subtitles were dropped.
                int storedExternalCount = storedStreams.Count(s => s.IsExternal);
                _mediaStreamRepository.SaveMediaStreams(item.Id,
                    TechnicalMetadataVerifier.WithStoredExternal(storedStreams, storedStreams, discoveredExternal, storedExternalCount),
                    cancellationToken);
                _logger.LogInformation("Re-attached external subtitles for item {ItemId}", item.Id);
            }

            // Jellyfin's re-probe of an STRM clears its chapters too; existing chapters are never overwritten.
            bool restoreChapters = _chapterRepository != null && cacheData.ChaptersProbed && expected.Chapters.Count > 0
                && _chapterRepository.GetChapters(item.Id).Count == 0;
            if (restoreChapters)
            {
                _chapterRepository.SaveChapters(item.Id, expected.Chapters);
            }

            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);

            if (restoreChapters && !TechnicalMetadataVerifier.ChaptersEqual(expected.Chapters, _chapterRepository.GetChapters(item.Id)))
            {
                throw new ProbeFailureException(ProbeFailureKind.PersistenceMismatch);
            }

            var persistedItem = _itemRepository.RetrieveItem(item.Id);
            var persistedStreams = _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id }).ToList();
            TechnicalMetadataVerifier.Verify(persistedItem, item.Path, expected, persistedStreams);

            _logger.LogInformation("Successfully restored and verified metadata for item {ItemId}", item.Id);
        }

        public void Dispose()
        {
            RestoreOperation[] operations;
            lock (_lifecycleLock)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                if (ReferenceEquals(_current, this))
                {
                    _current = null;
                }

                _libraryManager.ItemUpdated -= OnItemUpdated;
                operations = _runningTasks.Values.ToArray();
            }

            foreach (var operation in operations)
            {
                try
                {
                    if (!operation.Task.IsCompleted)
                    {
                        operation.CancellationSource.Cancel();
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (operations.Length > 0)
            {
                try
                {
                    var allTasks = Task.WhenAll(operations.Select(operation => operation.Task));
                    if (!allTasks.Wait(TimeSpan.FromSeconds(30)))
                    {
                        _logger.LogWarning("Timed out waiting for {Count} metadata restore tasks", operations.Length);
                    }
                    else if (allTasks.IsFaulted)
                    {
                        _ = allTasks.Exception;
                    }
                }
                catch (AggregateException)
                {
                    // Individual failures were retained on their tracked tasks and logged by item id and kind.
                }
            }

            foreach (var pair in _runningTasks)
            {
                if (pair.Value.Task.IsCompleted)
                {
                    ObserveCompletedOperation(pair.Key, pair.Value);
                    TryRemoveExact(_runningTasks, pair.Key, pair.Value);
                }
            }

            _logger.LogInformation("Item update listener disposed");
        }
    }
}
