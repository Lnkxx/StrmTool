using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DiscUtils.Udf;

namespace StrmTool
{
    /// <summary>
    /// Approximate bitrates for one Blu-ray playlist. Values come from clip sizes and a few small samples,
    /// so they are estimates and must never be presented as measured stream statistics.
    /// </summary>
    internal sealed class IsoBitrateEstimate
    {
        public static readonly IsoBitrateEstimate None = new IsoBitrateEstimate();

        public long MainFeatureBytes { get; init; }
        public int? TotalBitrate { get; init; }
        public IReadOnlyDictionary<ushort, int> StreamBitrates { get; init; } = new Dictionary<ushort, int>();
        public long SampledBytes { get; init; }
        public string Status { get; init; } = "not-estimated";
    }

    /// <summary>
    /// Total bitrate = main clip bytes / playlist duration (directory metadata only).
    /// Per-stream bitrate = total bitrate x each PID's payload share in a few evenly spaced windows.
    /// </summary>
    internal static class IsoBitrateEstimator
    {
        private const int SourcePacketSize = 192;
        private const int WindowCount = 5;
        private const int WindowBytes = SourcePacketSize * 2730; // about 512 KiB
        private const long MinPlausibleBitrate = 1_000_000;
        private const long MaxPlausibleBitrate = 150_000_000;
        private const double MinSyncedShare = 0.9;
        private const double MinImageShare = 0.1;
        private static readonly TimeSpan SamplingBudget = TimeSpan.FromSeconds(10);

        public static IsoBitrateEstimate Estimate(UdfReader udf, MplsPlaylist playlist, CancellationToken cancellationToken)
        {
            var clips = FindClips(udf, playlist);
            if (clips.Count == 0)
            {
                return new IsoBitrateEstimate { Status = "clips-not-found" };
            }

            var mainBytes = clips.Sum(clip => clip.Length);
            var total = ComputeTotalBitrate(mainBytes, playlist.DurationTicks);
            if (!total.HasValue)
            {
                return new IsoBitrateEstimate { MainFeatureBytes = mainBytes, Status = "total-implausible" };
            }

            var tally = new PacketTally();
            long sampled = 0;
            try
            {
                sampled = Sample(udf, clips, mainBytes, tally, cancellationToken);
            }
            catch (Exception ex) when (IsSamplingFault(ex))
            {
                // Some discs have extents DiscUtils cannot read; the size-based total is still valid.
                tally = new PacketTally();
            }

            var estimates = ComputeStreamBitrates(tally, total.Value);
            return new IsoBitrateEstimate
            {
                MainFeatureBytes = mainBytes,
                TotalBitrate = total,
                StreamBitrates = estimates,
                SampledBytes = sampled,
                Status = estimates.Count > 0 ? "estimated" : "total-only"
            };
        }

        /// <summary>
        /// True only when the clip sizes are known and too small for the playlist duration or the disc image.
        /// </summary>
        public static bool IsDecoy(UdfReader udf, MplsPlaylist playlist, long imageBytes)
        {
            try
            {
                var clips = FindClips(udf, playlist);
                return clips.Count > 0 && IsDecoySize(clips.Sum(clip => clip.Length), playlist.DurationTicks, imageBytes);
            }
            catch (Exception ex) when (IsSamplingFault(ex))
            {
                return false;
            }
        }

        internal static bool IsDecoySize(long clipBytes, long durationTicks, long imageBytes) =>
            !ComputeTotalBitrate(clipBytes, durationTicks).HasValue || clipBytes < imageBytes * MinImageShare;

        private static bool IsSamplingFault(Exception ex) =>
            !(ex is OperationCanceledException)
            && !(ex is ProbeFailureException failure && failure.Kind == ProbeFailureKind.SourceChanged);

        internal static int? ComputeTotalBitrate(long bytes, long durationTicks)
        {
            if (bytes <= 0 || durationTicks <= 0)
            {
                return null;
            }

            var bitrate = bytes * 8.0 * TimeSpan.TicksPerSecond / durationTicks;
            return bitrate < MinPlausibleBitrate || bitrate > MaxPlausibleBitrate ? null : RoundBitrate(bitrate);
        }

        internal static IReadOnlyDictionary<ushort, int> ComputeStreamBitrates(PacketTally tally, int totalBitrate)
        {
            var result = new Dictionary<ushort, int>();
            // Encrypted payloads lose their sync pattern; a PID share taken from them would be noise.
            if (tally.Packets == 0 || tally.Synced < tally.Packets * MinSyncedShare)
            {
                return result;
            }

            foreach (var (pid, payload) in tally.PayloadBytes)
            {
                result[pid] = RoundBitrate(totalBitrate * (double)payload / (tally.Packets * SourcePacketSize));
            }

            return result;
        }

        /// <summary>Counts the payload of each PID in a buffer of whole 192-byte source packets.</summary>
        internal static void TallyPackets(ReadOnlySpan<byte> data, PacketTally tally)
        {
            for (var offset = 0; offset + SourcePacketSize <= data.Length; offset += SourcePacketSize)
            {
                tally.Packets++;
                var packet = data.Slice(offset + 4, 188);
                if (packet[0] != 0x47 || (packet[3] & 0xC0) != 0)
                {
                    continue;
                }

                tally.Synced++;
                var pid = (ushort)(((packet[1] & 0x1F) << 8) | packet[2]);
                var adaptation = (packet[3] >> 4) & 0x3;
                var payload = adaptation switch
                {
                    1 => 184,
                    3 => Math.Max(0, 183 - packet[4]),
                    _ => 0
                };
                tally.PayloadBytes.TryGetValue(pid, out var current);
                tally.PayloadBytes[pid] = current + payload;
            }
        }

        private static int RoundBitrate(double bitrate) => (int)(Math.Round(bitrate / 1000) * 1000);

        private static long Sample(
            UdfReader udf,
            List<(string Path, long Length)> clips,
            long mainBytes,
            PacketTally tally,
            CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(WindowBytes);
            var clock = Stopwatch.StartNew();
            long sampled = 0;
            try
            {
                for (var window = 0; window < WindowCount && clock.Elapsed < SamplingBudget; window++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Evenly spaced in bytes, so payload shares weight each stream by the space it really occupies.
                    var position = (long)((window + 0.5) / WindowCount * mainBytes);
                    var clip = clips[0];
                    foreach (var candidate in clips)
                    {
                        clip = candidate;
                        if (position < candidate.Length)
                        {
                            break;
                        }

                        position -= candidate.Length;
                    }

                    position -= position % SourcePacketSize;
                    using var stream = udf.OpenFile(clip.Path, FileMode.Open, FileAccess.Read);
                    stream.Seek(position, SeekOrigin.Begin);
                    var read = 0;
                    while (read < WindowBytes)
                    {
                        var count = stream.Read(buffer, read, WindowBytes - read);
                        if (count == 0)
                        {
                            break;
                        }

                        read += count;
                    }

                    sampled += read;
                    TallyPackets(buffer.AsSpan(0, read), tally);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return sampled;
        }

        private static List<(string Path, long Length)> FindClips(UdfReader udf, MplsPlaylist playlist)
        {
            var clips = new List<(string Path, long Length)>();
            foreach (var name in playlist.PlayItems.Select(item => item.ClipName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var path in new[] { $@"BDMV\STREAM\{name}.m2ts", $@"BDMV\STREAM\{name}.M2TS" })
                {
                    if (udf.FileExists(path))
                    {
                        clips.Add((path, udf.GetFileLength(path)));
                        break;
                    }
                }
            }

            return clips;
        }
    }

    internal sealed class PacketTally
    {
        public long Packets { get; set; }
        public long Synced { get; set; }
        public Dictionary<ushort, long> PayloadBytes { get; } = new Dictionary<ushort, long>();
    }
}
