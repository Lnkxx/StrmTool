using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    public class MediaInfoCache
    {
        private readonly ILogger _logger;
        private readonly ILibraryMonitor _libraryMonitor;
        private const string CacheFileSuffix = ".strmtool.json";
        private const string IsoProbeStage = "iso-color-mapped";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public MediaInfoCache(ILogger logger, ILibraryMonitor libraryMonitor = null)
        {
            _logger = logger;
            _libraryMonitor = libraryMonitor;
        }

        /// <summary>
        /// 获取缓存文件路径
        /// </summary>
        private static string GetCachePath(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
                return null;

            // 确保路径是绝对路径（Jellyfin 的 item.Path 应该总是绝对路径）
            if (!Path.IsPathRooted(strmPath))
                return null;

            var directory = Path.GetDirectoryName(strmPath);
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            var fileName = Path.GetFileNameWithoutExtension(strmPath) + CacheFileSuffix;
            var cachePath = Path.Combine(directory, fileName);

            return cachePath;
        }

        /// <summary>
        /// 检查路径是否包含路径遍历攻击模式
        /// </summary>
        /// <summary>
        /// 检查路径是否包含路径遍历攻击模式
        /// 用于验证 strm 文件内容中的路径
        /// </summary>
        public static bool ContainsPathTraversal(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            // 检查空字符（注入攻击）
            if (path.Contains('\0'))
            {
                return true;
            }

            // 检查基本的路径遍历模式
            var normalized = path.Replace('/', '\\');
            if (normalized.Contains(@"..\\") || normalized.Contains(@"\\..") || 
                normalized.StartsWith(@"..") || normalized.EndsWith(@".."))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 检查缓存文件是否存在
        /// </summary>
        public bool HasCacheFile(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                return false;
            }

            var cachePath = GetCachePath(strmPath);
            return !string.IsNullOrWhiteSpace(cachePath) && File.Exists(cachePath);
        }

        /// <summary>
        /// 验证缓存路径并返回（通用验证方法）
        /// </summary>
        private (bool valid, string cachePath) ValidateCachePath(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                _logger.LogDebug("Invalid strm path for cache lookup");
                return (false, null);
            }

            var cachePath = GetCachePath(strmPath);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                _logger.LogDebug("Failed to get cache path for: {Path}", strmPath);
                return (false, null);
            }

            if (!File.Exists(cachePath))
            {
                return (false, null);
            }

            return (true, cachePath);
        }

        /// <summary>
        /// 验证缓存数据是否有效
        /// </summary>
        private bool ValidateCacheData(MediaInfoCacheData cache, string strmPath, bool requireMediaStreams = true)
        {
            if (cache?.IsValid != true)
                return false;
            
            if (cache.Size <= 1024 || !(cache.RunTimeTicks > 0) || cache.MediaStreams == null || !cache.MediaStreams.Any(x => x.Type == MediaStreamType.Video || x.Type == MediaStreamType.Audio))
                return false;

            if (cache.MediaStreams.Any(x => x.Type == MediaStreamType.Video && (!(x.Width > 0) || !(x.Height > 0)))) return false;
            if (string.IsNullOrEmpty(cache.StrmContentHash) && !TryAdoptLegacyIdentity(cache, strmPath))
                return false;
            if (cache.StrmContentHash != GetStrmContentHash(strmPath))
                return false;
            return true;
        }

        /// <summary>
        /// A remote ISO cached before the current bitrate and color mapping must be probed once more. The cache stays readable
        /// meanwhile, because metadata restore depends on it.
        /// </summary>
        public static bool NeedsIsoProbeUpgrade(MediaInfoCacheData cache, string strmPath)
        {
            if (cache == null || !string.Equals(cache.Container, "bluray", StringComparison.OrdinalIgnoreCase)
                || cache.CompletedStages?.Contains(IsoProbeStage) == true)
                return false;
            try { return IsoDetector.IsHttpPath(File.ReadAllText(strmPath).Trim()); }
            catch (IOException) { return false; }
        }

        /// <summary>
        /// Caches written before 1.2 carry no STRM hash. If the STRM has not been modified since the cache was
        /// written, the cache describes the current content, so bind it to the current hash (in memory only).
        /// </summary>
        private static bool TryAdoptLegacyIdentity(MediaInfoCacheData cache, string strmPath)
        {
            try
            {
                var written = cache.Timestamp.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(cache.Timestamp, DateTimeKind.Utc)
                    : cache.Timestamp.ToUniversalTime();
                if (written == default || File.GetLastWriteTimeUtc(strmPath) > written)
                    return false;
                cache.StrmContentHash = GetStrmContentHash(strmPath);
                return !string.IsNullOrEmpty(cache.StrmContentHash);
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

        public static string GetStrmContentHash(string path)
        {
            try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))); }
            catch { return null; }
        }

        /// <summary>
        /// 检查并读取缓存（同步版本，保持兼容性）
        /// </summary>
        public bool TryGetCachedMediaStreams(string strmPath, out List<MediaStream> mediaStreams)
        {
            mediaStreams = null;

            var (valid, cachePath) = ValidateCachePath(strmPath);
            if (!valid)
                return false;

            try
            {
                var json = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache, strmPath))
                    return false;

                mediaStreams = cache.MediaStreams;
                _logger.LogDebug("Loaded cached media streams from {Path}", cachePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading cache from {Path}", strmPath);
                return false;
            }
        }

        /// <summary>
        /// 检查并读取缓存（异步版本）
        /// </summary>
        public async Task<(bool success, List<MediaStream> mediaStreams)> TryGetCachedMediaStreamsAsync(string strmPath, CancellationToken cancellationToken = default)
        {
            var (valid, cachePath) = ValidateCachePath(strmPath);
            if (!valid)
                return (false, null);

            try
            {
                var json = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache, strmPath))
                    return (false, null);

                _logger.LogDebug("Loaded cached media streams from {Path}", cachePath);
                return (true, cache.MediaStreams);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading cache from {Path}", strmPath);
                return (false, null);
            }
        }

        /// <summary>
        /// 原子写入文件的私有方法
        /// </summary>
        private async Task WriteFileAtomicallyAsync(string filePath, string content, CancellationToken cancellationToken)
        {
            var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            // A sidecar is not library content: unreported, each write makes Jellyfin refresh the whole library folder.
            _libraryMonitor?.ReportFileSystemChangeBeginning(filePath);
            _libraryMonitor?.ReportFileSystemChangeBeginning(tempPath);
            try
            {
                await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);

                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, null);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }
            }
            catch
            {
                CleanupTempFile(tempPath);
                throw;
            }
            finally
            {
                _libraryMonitor?.ReportFileSystemChangeComplete(filePath, false);
                _libraryMonitor?.ReportFileSystemChangeComplete(tempPath, false);
            }
        }

        /// <summary>
        /// 清理临时文件
        /// </summary>
        private void CleanupTempFile(string tempPath)
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to cleanup temp cache file {Path}", tempPath);
                }
            }
        }

        /// <summary>
        /// 验证保存缓存的前置条件
        /// </summary>
        private (bool valid, string cachePath) ValidateSaveCache(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                _logger.LogDebug("Invalid strm path for cache save");
                return (false, null);
            }

            var cachePath = GetCachePath(strmPath);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                _logger.LogDebug("Failed to get cache path for: {Path}", strmPath);
                return (false, null);
            }

            return (true, cachePath);
        }

        /// <summary>
        /// 保存缓存（原子写入，先写临时文件再重命名）
        /// </summary>
        public async Task SaveCacheAsync(string strmPath, IEnumerable<MediaStream> mediaStreams, CancellationToken cancellationToken = default)
        {
            try
            {
                var (valid, cachePath) = ValidateSaveCache(strmPath);
                if (!valid)
                    return;

                var cache = new MediaInfoCacheData
                {
                    Version = "1.0",
                    Timestamp = DateTime.UtcNow,
                    MediaStreams = mediaStreams.ToList(),
                    Chapters = new List<ChapterInfo>(),
                    IsValid = true
                };

                var json = JsonSerializer.Serialize(cache, JsonOptions);
                await WriteFileAtomicallyAsync(cachePath, json, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Saved cache to {Path}", cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving cache to {Path}", strmPath);
            }
        }

        /// <summary>
        /// 保存完整媒体信息缓存（包含Size等元数据）
        /// </summary>
        public async Task SaveFullCacheAsync(
            string strmPath,
            IEnumerable<MediaStream> mediaStreams,
            long size,
            long? runTimeTicks,
            string container,
            IEnumerable<ChapterInfo> chapters = null,
            CancellationToken cancellationToken = default,
            string expectedStrmContentHash = null,
            bool chaptersProbed = false,
            int? totalBitrate = null)
        {
            try
            {
                var (valid, cachePath) = ValidateSaveCache(strmPath);
                if (!valid)
                    return;

                var hash = GetStrmContentHash(strmPath);
                if (size <= 1024 || hash == null || (expectedStrmContentHash != null && hash != expectedStrmContentHash))
                    throw new InvalidOperationException("Invalid size or source changed before cache write");
                var cache = new MediaInfoCacheData
                {
                    Version = "1.2",
                    ChaptersProbed = chaptersProbed,
                    ProbeVersion = "2.4.3.4",
                    CompletedStages = totalBitrate.HasValue
                        ? new[] { "basic", "native-persistence-verified", IsoProbeStage }
                        : new[] { "basic", "native-persistence-verified" },
                    TotalBitrate = totalBitrate,
                    SourceKind = IsoDetector.IsIsoOriginal(strmPath, File.ReadAllText(strmPath).Trim()) ? "iso" : "media",
                    StrmContentHash = hash,
                    Timestamp = DateTime.UtcNow,
                    MediaStreams = mediaStreams?.ToList() ?? new List<MediaStream>(),
                    IsValid = true,
                    Size = size,
                    RunTimeTicks = runTimeTicks,
                    Container = container,
                    Chapters = chapters?.ToList() ?? new List<ChapterInfo>()
                };

                var json = JsonSerializer.Serialize(cache, JsonOptions);
                await WriteFileAtomicallyAsync(cachePath, json, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Saved full cache (Size={Size}) to {Path}", size, cachePath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                throw new ProbeFailureException(ProbeFailureKind.CacheWriteFailure);
            }
        }

        /// <summary>
        /// 尝试读取完整缓存数据（包含元数据）
        /// </summary>
        public bool TryGetFullCache(string strmPath, out MediaInfoCacheData cacheData)
        {
            cacheData = null;

            var (valid, cachePath) = ValidateCachePath(strmPath);
            if (!valid)
                return false;

            try
            {
                var json = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache, strmPath, requireMediaStreams: false))
                    return false;

                cacheData = cache;
                _logger.LogDebug("Loaded full cache (Size={Size}) from {Path}", cache.Size, cachePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading full cache from {Path}", strmPath);
                return false;
            }
        }

        /// <summary>
        /// 清除缓存
        /// </summary>
        public void ClearCache(string strmPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(strmPath))
                {
                    _logger.LogDebug("Invalid strm path for cache clear");
                    return;
                }

                var cachePath = GetCachePath(strmPath);
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    _logger.LogDebug("Failed to get cache path for: {Path}", strmPath);
                    return;
                }

                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                    _logger.LogInformation("Cleared cache: {Path}", cachePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error clearing cache: {Path}", strmPath);
            }
        }

        /// <summary>
        /// 清除目录下所有缓存
        /// </summary>
        public void ClearAllCaches(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                    return;

                var jsonFiles = Directory.GetFiles(directoryPath, "*" + CacheFileSuffix, SearchOption.AllDirectories);
                int cleared = 0;

                foreach (var jsonFile in jsonFiles)
                {
                    var fileName = Path.GetFileName(jsonFile);
                    if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(CacheFileSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // 从缓存文件名反推出 strm 文件名
                    var baseName = Path.GetFileNameWithoutExtension(fileName);
                    var strmPath = Path.Combine(Path.GetDirectoryName(jsonFile) ?? string.Empty, baseName + StrmMediaInfoService.StrmFileExtension);

                    // 使用 GetCachePath 验证路径安全性
                    var expectedCachePath = GetCachePath(strmPath);
                    if (expectedCachePath == null)
                    {
                        continue;
                    }

                    // 确保找到的 json 文件确实是对应的缓存文件
                    if (!string.Equals(expectedCachePath, jsonFile, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // 安全删除缓存文件
                    try
                    {
                        File.Delete(jsonFile);
                        cleared++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error deleting cache file {Path}", jsonFile);
                    }
                }

                _logger.LogInformation("Cleared {Count} cache files in {Dir}", cleared, directoryPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing caches in {Dir}", directoryPath);
            }
        }
    }

    /// <summary>
    /// 缓存数据结构
    /// </summary>
    public class MediaInfoCacheData
    {
        [JsonPropertyName("chaptersProbed")]
        public bool ChaptersProbed { get; set; }
        [JsonPropertyName("probeVersion")]
        public string ProbeVersion { get; set; }
        [JsonPropertyName("completedStages")]
        public string[] CompletedStages { get; set; }
        [JsonPropertyName("sourceKind")]
        public string SourceKind { get; set; }

        [JsonPropertyName("strmContentHash")]
        public string StrmContentHash { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("mediaStreams")]
        public List<MediaStream> MediaStreams { get; set; }

        [JsonPropertyName("isValid")]
        public bool IsValid { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("runTimeTicks")]
        public long? RunTimeTicks { get; set; }

        [JsonPropertyName("container")]
        public string Container { get; set; }

        [JsonPropertyName("totalBitrate")]
        public int? TotalBitrate { get; set; }

        [JsonPropertyName("chapters")]
        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
    }
}
