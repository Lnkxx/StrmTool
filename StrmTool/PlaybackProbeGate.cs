using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    /// <summary>Why the playback-time probe of an STRM is skipped or kept.</summary>
    public enum PlaybackProbeDecision
    {
        /// <summary>Everything Jellyfin's probe would produce is already stored and proven current.</summary>
        Skip,
        Disabled,
        NotStrm,
        OutOfScope,
        NoValidCache,
        IsoUpgradePending,
        ShortcutStale,
        StreamsIncomplete,
        NeedsRestore,
    }

    /// <summary>
    /// Jellyfin re-probes every .strm before each playback (MediaSourceManager.GetPlaybackMediaSources), whether or
    /// not its streams are already stored. This decides when that probe has nothing left to find. Any doubt keeps
    /// the probe, i.e. Jellyfin's unmodified behavior.
    /// </summary>
    public static class PlaybackProbeGate
    {
        public static PlaybackProbeDecision Evaluate(
            BaseItem item,
            PluginConfiguration config,
            MediaInfoCache cache,
            Func<Guid, IReadOnlyList<MediaStream>> getStoredStreams)
        {
            if (config == null || !config.SkipPlaybackProbe)
            {
                return PlaybackProbeDecision.Disabled;
            }

            if (item == null || !StrmMediaInfoService.IsStrmFile(item.Path))
            {
                return PlaybackProbeDecision.NotStrm;
            }

            if (!config.IsPathInProbeScope(item.Path))
            {
                return PlaybackProbeDecision.OutOfScope;
            }

            // Proves the cache describes the STRM as it is on disk now (content hash), with usable technical data.
            if (cache == null || !cache.TryGetFullCache(item.Path, out var cacheData))
            {
                return PlaybackProbeDecision.NoValidCache;
            }

            if (MediaInfoCache.NeedsIsoProbeUpgrade(cacheData, item.Path))
            {
                return PlaybackProbeDecision.IsoUpgradePending;
            }

            // The probe is also what copies the STRM's URL into the item. Skip only when that copy is current,
            // otherwise playback would be sent to the URL the STRM used to hold.
            var shortcut = ReadShortcutPath(item.Path);
            if (string.IsNullOrEmpty(shortcut) || !string.Equals(item.ShortcutPath, shortcut, StringComparison.Ordinal))
            {
                return PlaybackProbeDecision.ShortcutStale;
            }

            var stored = getStoredStreams?.Invoke(item.Id);
            if (stored == null
                || !stored.Any(s => s != null && !s.IsExternal && s.Type == MediaStreamType.Video)
                || !stored.Any(s => s != null && !s.IsExternal && s.Type == MediaStreamType.Audio))
            {
                return PlaybackProbeDecision.StreamsIncomplete;
            }

            // The item was reset (93-byte size, lost runtime/dimensions/stream detail/external subtitles).
            if (!(item.Size > 1024) || ItemUpdateListener.NeedsMetadataRestore(item, cacheData, stored))
            {
                return PlaybackProbeDecision.NeedsRestore;
            }

            return PlaybackProbeDecision.Skip;
        }

        /// <summary>The first usable line of an STRM, read the way Jellyfin's ProbeProvider.FetchShortcutInfo does.</summary>
        public static string ReadShortcutPath(string strmPath)
        {
            return File.ReadAllLines(strmPath)
                .Select(line => line.Replace("\t", string.Empty, StringComparison.Ordinal)
                    .Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", string.Empty, StringComparison.Ordinal)
                    .Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'));
        }
    }
}
