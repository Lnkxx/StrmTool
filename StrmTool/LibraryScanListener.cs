using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace StrmTool
{
    public class LibraryScanListener : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private PluginConfiguration _config;
        private volatile bool _isDisposed = false;
        private readonly ConcurrentDictionary<Guid, ProbeStamp> _recentProbes = new();

        private sealed class ProbeStamp
        {
            public ProbeStamp(string path, string strmContentHash, DateTime timestamp)
            {
                Path = path;
                StrmContentHash = strmContentHash;
                Timestamp = timestamp;
            }

            public string Path { get; }
            public string StrmContentHash { get; }
            public DateTime Timestamp { get; }
        }

        // 使用事件解耦，而不是直接依赖 ExtractTask
        public event EventHandler<BaseItem> StrmFileDetected;

        public LibraryScanListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config)
            : this(libraryManager, logger, config, null)
        {
        }

        public LibraryScanListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config,
            MediaInfoCache mediaCache)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _config = config;

            // 订阅事件
            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnIncompleteItemUpdated;
            _logger.LogInformation("Library scan listener initialized");
        }

        /// <summary>
        /// 刷新配置引用，确保获取最新的配置值
        /// </summary>
        public void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                _config = Plugin.Instance.Configuration;
            }
        }

        private void OnIncompleteItemUpdated(object sender, ItemChangeEventArgs e)
        {
            var item = e.Item;
            if (_isDisposed || item == null || !StrmMediaInfoService.IsStrmFile(item.Path)) return;
            RefreshConfig();
            if (!_config.EnableAutoExtract || !_config.IsPathInProbeScope(item.Path)) return;
            if (item.Size > 1024 && item.RunTimeTicks > 0
                && (!(item is Video video) || (video.Width > 0 && video.Height > 0))) return;
            // Native metadata updates can arrive repeatedly while a probe is in flight.
            var now = DateTime.UtcNow;
            var stamp = new ProbeStamp(item.Path, MediaInfoCache.GetStrmContentHash(item.Path), now);
            if (!TryMarkProbe(item.Id, stamp)) return;
            OnItemAdded(sender, e);
            foreach (var entry in _recentProbes)
            {
                if (now - entry.Value.Timestamp > TimeSpan.FromMinutes(10))
                {
                    ((ICollection<KeyValuePair<Guid, ProbeStamp>>)_recentProbes)
                        .Remove(new KeyValuePair<Guid, ProbeStamp>(entry.Key, entry.Value));
                }
            }
        }

        private bool TryMarkProbe(Guid itemId, ProbeStamp stamp)
        {
            while (true)
            {
                if (_recentProbes.TryGetValue(itemId, out var previous))
                {
                    var sameSource = string.Equals(previous.Path, stamp.Path, StringComparison.Ordinal)
                        && string.Equals(previous.StrmContentHash, stamp.StrmContentHash, StringComparison.Ordinal);
                    if (sameSource && stamp.Timestamp - previous.Timestamp < TimeSpan.FromMinutes(5))
                    {
                        return false;
                    }

                    if (_recentProbes.TryUpdate(itemId, stamp, previous))
                    {
                        return true;
                    }
                }
                else if (_recentProbes.TryAdd(itemId, stamp))
                {
                    return true;
                }
            }
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (_isDisposed)
                return;

            var itemId = e?.Item?.Id;
            try
            {
                // 刷新配置以确保获取最新的设置
                RefreshConfig();

                if (!_config.EnableAutoExtract)
                    return;

                var item = e.Item;

                // 检查是否是 strm 文件
                if (!StrmMediaInfoService.IsStrmFile(item.Path))
                {
                    return;
                }

                if (!_config.IsPathInProbeScope(item.Path))
                {
                    _logger.LogDebug("STRM file is outside configured probe path, skipping: {Path}", item.Path);
                    return;
                }

                // 再次检查是否已释放，防止竞态条件
                if (_isDisposed)
                    return;

                // 使用实际文件名而不是 item.Name，因为 item.Name 可能还没有完全解析
                var fileName = Path.GetFileNameWithoutExtension(item.Path);
                _logger.LogInformation("New strm file detected: {Name} ({Path})", fileName, item.Path);

                // 新入库文件通过事件通知 ExtractTask 进行处理
                StrmFileDetected?.Invoke(this, item);
            }
            catch (Exception ex)
            {
                var failureKind = ex is OperationCanceledException ? ProbeFailureKind.None : ProbeFailurePolicy.Classify(ex);
                _logger.LogError("STRM item notification failed for item {ItemId} with {FailureKind}", itemId, failureKind);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                _libraryManager.ItemAdded -= OnItemAdded;
                _libraryManager.ItemUpdated -= OnIncompleteItemUpdated;
            }
            catch (ObjectDisposedException)
            {
            }

            _logger.LogInformation("Library scan listener disposed");
        }
    }
}
