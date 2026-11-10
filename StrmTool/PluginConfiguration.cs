using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace StrmTool
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        private int _refreshDelayMs = 1000;
        private int _maxConcurrentExtract = 5;
        private int _isoProbeTimeoutSeconds = 90;

        /// <summary>
        /// 刷新延迟时间（毫秒）
        /// </summary>
        public int RefreshDelayMs
        {
            get => _refreshDelayMs;
            set => _refreshDelayMs = Math.Max(0, value);
        }

        /// <summary>
        /// 是否启用新入库 strm 文件自动提取媒体信息
        /// </summary>
        public bool EnableAutoExtract { get; set; } = true;

        /// <summary>
        /// 是否启用媒体信息缓存
        /// </summary>
        public bool EnableMediaInfoCache { get; set; } = true;

        /// <summary>
        /// 提取任务的最大并发数（范围：1-50）
        /// </summary>
        public int MaxConcurrentExtract
        {
            get => _maxConcurrentExtract;
            set => _maxConcurrentExtract = Math.Clamp(value, 1, 50);
        }

        /// <summary>
        /// 是否无视是否已有媒体流，强制刷新
        /// </summary>
        public bool ForceRefreshIgnoreExisting { get; set; } = false;

        /// <summary>
        /// 是否无视缓存文件，强制从远程服务器获取
        /// </summary>
        public bool ForceRefreshIgnoreCache { get; set; } = false;

        /// <summary>
        /// 元数据恢复超时时间（分钟）
        /// </summary>
        public int MetadataRestoreTimeoutMinutes { get; set; } = 5;

        /// <summary>
        /// Enables local libbluray probing and remote HTTP Range/UDF probing.
        /// </summary>
        public bool EnableIsoProbe { get; set; } = true;

        /// <summary>
        /// Absolute path to a static ffprobe build compiled with libbluray. Local ISO only.
        /// </summary>
        public string CustomFfprobePath { get; set; } = string.Empty;

        /// <summary>
        /// Maximum time allowed for one ISO probe.
        /// </summary>
        public int IsoProbeTimeoutSeconds
        {
            get => _isoProbeTimeoutSeconds;
            set => _isoProbeTimeoutSeconds = Math.Clamp(value, 10, 600);
        }

        /// <summary>
        /// Skips the remote probe Jellyfin runs before every playback of an STRM, when the cache proves the stored
        /// media info is current. Read on every playback, so it takes effect without a restart.
        /// </summary>
        public bool SkipPlaybackProbe { get; set; } = true;

        /// <summary>
        /// Optional absolute directory or STRM file path to process. Empty means all paths.
        /// </summary>
        public string ProbePathFilter { get; set; } = string.Empty;

        private readonly object _scopeLock = new object();
        private string _scopeManifest;
        private string _scopeManifestContent;
        private HashSet<string> _scopePaths;

        public bool IsPathInProbeScope(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (string.IsNullOrWhiteSpace(ProbePathFilter)) return true;
            var candidate = NormalizePath(path);
            if (ProbePathFilter.StartsWith("@", StringComparison.Ordinal))
            {
                // A missing or malformed allowlist must never broaden the scan.
                try
                {
                    var manifest = ProbePathFilter.Substring(1).Trim();
                    if (!Path.IsPathRooted(manifest) || !File.Exists(manifest)) return false;
                    var manifestContent = File.ReadAllText(manifest);
                    lock (_scopeLock)
                    {
                        if (_scopePaths == null || _scopeManifest != manifest || _scopeManifestContent != manifestContent)
                        {
                            var paths = manifestContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                            if (paths.Length == 0 || paths.Any(x => !Path.IsPathRooted(x) || !x.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))) return false;
                            _scopePaths = new HashSet<string>(paths.Select(NormalizePath), StringComparer.Ordinal);
                            _scopeManifest = manifest;
                            _scopeManifestContent = manifestContent;
                        }
                        return _scopePaths.Contains(candidate);
                    }
                }
                catch { return false; }
            }
            var filter = NormalizePath(ProbePathFilter);
            return string.Equals(candidate, filter, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(filter + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            var normalized = path.Trim().Replace('\0', ' ')
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            normalized = normalized.TrimEnd(Path.DirectorySeparatorChar);
            return normalized.Length == 0 ? Path.DirectorySeparatorChar.ToString() : normalized;
        }
    }
}
