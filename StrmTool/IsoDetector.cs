using System;

namespace StrmTool
{
    /// <summary>
    /// Identifies STRM files that point at an ISO image. Detection deliberately does
    /// not inspect ffprobe's format_name because bluray probing reports mpegts.
    /// </summary>
    public static class IsoDetector
    {
        public static bool IsIsoOriginal(string strmPath, string sourcePath)
        {
            return (!string.IsNullOrWhiteSpace(strmPath) &&
                    strmPath.EndsWith(".iso.strm", StringComparison.OrdinalIgnoreCase)) ||
                   IsIsoPath(sourcePath);
        }

        public static bool IsIsoPath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return false;
            }

            var path = sourcePath.Trim();
            var queryIndex = path.IndexOfAny(new[] { '?', '#' });
            if (queryIndex >= 0)
            {
                path = path.Substring(0, queryIndex);
            }

            return path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLocalIsoPath(string sourcePath)
        {
            if (!IsIsoPath(sourcePath))
            {
                return false;
            }

            var path = sourcePath.Trim();
            return !path.Contains("://", StringComparison.Ordinal) &&
                   !path.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase) &&
                   !path.StartsWith("smb:", StringComparison.OrdinalIgnoreCase) &&
                   !path.StartsWith("nfs:", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsHttpPath(string sourcePath)
        {
            return !string.IsNullOrWhiteSpace(sourcePath) &&
                   (sourcePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    sourcePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }
}
