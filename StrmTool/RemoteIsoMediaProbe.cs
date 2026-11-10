using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils.Udf;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace StrmTool
{
    /// <summary>
    /// Extracts Blu-ray playlist metadata from an HTTP ISO without downloading it in full.
    /// </summary>
    internal sealed class RemoteIsoMediaProbe
    {
        private const int MaxPlaylistBytes = 16 * 1024 * 1024;
        private const int MaxCachedChunks = 8;

        private readonly ILogger _logger;

        public RemoteIsoMediaProbe(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<MediaProbeResult> ProbeAsync(
            string sourceUrl,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)));

            using var remoteIso = await HttpRangeStream.OpenAsync(
                sourceUrl,
                TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)),
                timeoutCts.Token,
                maxCachedChunks: MaxCachedChunks).ConfigureAwait(false);

            try
            {
                var probeResult = await Task.Run(
                    () => ProbeUdf(remoteIso, timeoutCts.Token),
                    timeoutCts.Token).ConfigureAwait(false);

                _logger.LogInformation(
                    "Remote ISO probe read {Bytes} bytes in {Requests} range requests from a {Size} byte image",
                    remoteIso.BytesDownloaded,
                    remoteIso.RequestCount,
                    remoteIso.Length);
                return probeResult;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Remote ISO probe timed out after {Math.Max(10, timeoutSeconds)} seconds.");
            }
        }

        private MediaProbeResult ProbeUdf(HttpRangeStream remoteIso, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var udf = new UdfReader(remoteIso);
            var playlistFiles = FindPlaylistFiles(udf);
            if (playlistFiles.Count == 0)
            {
                throw new InvalidDataException("The ISO does not contain BDMV/PLAYLIST/*.mpls files.");
            }

            var parsedPlaylists = new List<(string Path, MplsPlaylist Playlist)>();
            foreach (var playlistPath in playlistFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var data = ReadFile(udf, playlistPath, MaxPlaylistBytes, cancellationToken);
                    var playlist = MplsParser.Parse(data);
                    if (playlist.PlayItems.Count > 0 && playlist.DurationTicks > 0)
                    {
                        _logger.LogDebug(
                            "Remote ISO playlist {Playlist}: {Duration}, {PlayItems} play items, {Streams} streams, {Chapters} chapters",
                            Path.GetFileName(playlistPath),
                            TimeSpan.FromTicks(playlist.DurationTicks),
                            playlist.PlayItems.Count,
                            playlist.GetDistinctStreams().Count(),
                            playlist.Chapters.Count);
                        parsedPlaylists.Add((playlistPath, playlist));
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                {
                    _logger.LogDebug(ex, "Skipping invalid Blu-ray playlist {Playlist}", playlistPath);
                }
            }

            var candidates = parsedPlaylists
                .Where(candidate => candidate.Playlist.DurationTicks <= TimeSpan.FromHours(24).Ticks)
                .Where(candidate => candidate.Playlist.GetDistinctStreams().Any(stream => stream.Kind == MplsStreamKind.Video))
                .OrderByDescending(candidate => candidate.Playlist.DurationTicks)
                .ThenByDescending(candidate => candidate.Playlist.PlayItems.Count)
                .ToList();
            // Decoy playlists loop a few tiny clips into hours of runtime, so their size cannot fill their duration or the disc.
            var selected = candidates.FirstOrDefault(candidate => !IsoBitrateEstimator.IsDecoy(udf, candidate.Playlist, remoteIso.Length));
            if (selected.Playlist == null && candidates.Count > 0)
            {
                selected = candidates[0];
            }

            if (selected.Playlist == null)
            {
                throw new InvalidDataException("No valid Blu-ray playlist was found in the remote ISO.");
            }

            var estimate = TryEstimateBitrates(udf, selected.Playlist, cancellationToken);
            _logger.LogInformation(
                "Remote ISO bitrate estimate: {Status}, {MainBytes} main-feature bytes, total {Total} bit/s, {Sampled} bytes sampled",
                estimate.Status,
                estimate.MainFeatureBytes,
                estimate.TotalBitrate,
                estimate.SampledBytes);

            var result = MapResult(selected.Playlist, remoteIso.Length, estimate);
            _logger.LogInformation(
                "Remote ISO selected playlist {Playlist}: {Duration}, {PlayItems} play items, {Streams} streams, {Chapters} chapters",
                Path.GetFileName(selected.Path),
                TimeSpan.FromTicks(selected.Playlist.DurationTicks),
                selected.Playlist.PlayItems.Count,
                result.MediaStreams.Count,
                result.Chapters.Count);
            return result;
        }

        /// <summary>
        /// A bitrate estimate is optional detail: only a changed source may fail the probe.
        /// </summary>
        private IsoBitrateEstimate TryEstimateBitrates(UdfReader udf, MplsPlaylist playlist, CancellationToken cancellationToken)
        {
            try
            {
                return IsoBitrateEstimator.Estimate(udf, playlist, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProbeFailureException failure) when (failure.Kind == ProbeFailureKind.SourceChanged)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Remote ISO bitrate estimate failed");
                return IsoBitrateEstimate.None;
            }
        }

        private static List<string> FindPlaylistFiles(UdfReader udf)
        {
            var candidates = new[]
            {
                @"BDMV\PLAYLIST",
                @"\BDMV\PLAYLIST",
                "BDMV/PLAYLIST",
                "/BDMV/PLAYLIST"
            };

            foreach (var directory in candidates)
            {
                try
                {
                    if (!udf.DirectoryExists(directory))
                    {
                        continue;
                    }

                    return udf.GetFiles(directory, "*.mpls", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (IOException)
                {
                }
                catch (ArgumentException)
                {
                }
            }

            return new List<string>();
        }

        private static byte[] ReadFile(
            UdfReader udf,
            string path,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            using var stream = udf.OpenFile(path, FileMode.Open, FileAccess.Read);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
            {
                throw new InvalidDataException(
                    $"Blu-ray playlist {path} has an invalid length of {stream.Length} bytes.");
            }

            var length = checked((int)stream.Length);
            var buffer = new byte[length];
            var offset = 0;
            while (offset < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Unexpected end of Blu-ray playlist {path}.");
                }

                offset += read;
            }

            return buffer;
        }

        private static MediaProbeResult MapResult(MplsPlaylist playlist, long isoSize, IsoBitrateEstimate estimate)
        {
            var result = new MediaProbeResult
            {
                ChaptersProbed = true,
                Size = isoSize,
                RunTimeTicks = playlist.DurationTicks,
                Container = "bluray",
                TotalBitrate = estimate.TotalBitrate
            };

            var streamIndex = 0;
            var sourceStreams = playlist.GetDistinctStreams().ToList();
            var hasDolbyVision = sourceStreams.Any(stream => stream.IsDolbyVision);
            var dolbyVisionApplied = false;
            foreach (var source in sourceStreams.Where(stream => !stream.IsDolbyVision))
            {
                var applyDolbyVision = hasDolbyVision && !dolbyVisionApplied &&
                                       source.Kind == MplsStreamKind.Video;
                var stream = MapStream(source, streamIndex++, applyDolbyVision);
                if (stream != null)
                {
                    if (stream.Type != MediaStreamType.Subtitle
                        && estimate.StreamBitrates.TryGetValue(source.PacketId, out var bitrate) && bitrate > 0)
                    {
                        stream.BitRate = bitrate;
                        AppendComment(stream, "Bitrate is estimated from a small sample.");
                    }

                    result.MediaStreams.Add(stream);
                    dolbyVisionApplied |= applyDolbyVision;
                }
            }

            for (var index = 0; index < playlist.Chapters.Count; index++)
            {
                var chapter = new ChapterInfo();
                Set(chapter, "Name", $"Chapter {index + 1}");
                Set(chapter, "StartPositionTicks", playlist.Chapters[index].PositionTicks);
                // Jellyfin's ChapterInfo has no EndPositionTicks (an Emby field); chapters end at the next start.
                result.Chapters.Add(chapter);
            }

            return result;
        }

        private static MediaStream MapStream(MplsStreamInfo source, int index, bool forceDolbyVision)
        {
            var type = source.Kind switch
            {
                MplsStreamKind.Video or MplsStreamKind.SecondaryVideo => MediaStreamType.Video,
                MplsStreamKind.Audio or MplsStreamKind.SecondaryAudio => MediaStreamType.Audio,
                MplsStreamKind.Subtitle => MediaStreamType.Subtitle,
                _ => (MediaStreamType?)null
            };
            if (!type.HasValue)
            {
                return null;
            }

            var codec = GetCodec(source.CodingType);
            var stream = new MediaStream
            {
                Index = index,
                Type = type.Value
            };
            stream.Codec = codec;
            stream.Language = string.IsNullOrWhiteSpace(source.Language) ? null : source.Language;
            stream.IsDefault = index == 0 && type == MediaStreamType.Video;
            stream.IsForced = false;

            if (type == MediaStreamType.Video)
            {
                var (width, height, interlaced) = GetVideoDimensions(source.FormatCode);
                var frameRate = GetFrameRate(source.RateCode);
                stream.Width = width;
                stream.Height = height;
                stream.IsInterlaced = interlaced ?? false;
                stream.RealFrameRate = frameRate;
                if (!interlaced.HasValue)
                {
                    AppendComment(stream, $"Blu-ray MPLS format code {source.FormatCode} did not identify scan mode; interlace state is unknown.");
                }

                ApplyPlaylistColor(stream, source);
                PreservePlaylistMarkers(stream, source, forceDolbyVision);
            }
            else if (type == MediaStreamType.Audio)
            {
                stream.Channels = GetChannels(source.FormatCode);
                stream.SampleRate = GetSampleRate(source.RateCode);
            }

            return stream;
        }

        private static string GetCodec(byte codingType)
        {
            return codingType switch
            {
                0x01 => "mpeg1video",
                0x02 => "mpeg2video",
                0x03 => "mp1",
                0x04 => "mp2",
                0x1b => "h264",
                0x20 => "mvc",
                0x24 => "hevc",
                0x80 => "pcm_bluray",
                0x81 => "ac3",
                0x82 => "dts",
                0x83 => "truehd",
                0x84 => "eac3",
                0x85 => "dts_hd",
                0x86 => "dts_hd_ma",
                0x90 => "hdmv_pgs_subtitle",
                0x91 => "hdmv_ig",
                0x92 => "textst",
                0xa1 => "eac3",
                0xa2 => "dts_hd",
                0xea => "vc1",
                _ => $"bluray_0x{codingType:x2}"
            };
        }

        private static (int? Width, int? Height, bool? Interlaced) GetVideoDimensions(byte formatCode)
        {
            return formatCode switch
            {
                1 => (720, 480, true),
                2 => (720, 576, true),
                3 => (720, 480, false),
                4 => (1920, 1080, true),
                5 => (1280, 720, false),
                6 => (1920, 1080, false),
                7 => (720, 576, false),
                8 => (3840, 2160, false),
                _ => (null, null, null)
            };
        }

        private static float? GetFrameRate(byte rateCode)
        {
            return rateCode switch
            {
                1 => 24000f / 1001f,
                2 => 24f,
                3 => 25f,
                4 => 30000f / 1001f,
                6 => 50f,
                7 => 60000f / 1001f,
                _ => null
            };
        }

        private static int? GetChannels(byte formatCode)
        {
            return formatCode switch
            {
                1 => 1,
                3 => 2,
                _ => null
            };
        }

        /// <summary>
        /// Blu-ray allows only 8-bit AVC, VC-1 and MPEG-2 and 10-bit HEVC, so bit depth follows from the codec.
        /// Color comes from the playlist markers: dynamic range 1 is HDR10; color space 1 is BT.709, 2 is BT.2020.
        /// </summary>
        internal static void ApplyPlaylistColor(MediaStream stream, MplsStreamInfo source)
        {
            switch (source.CodingType)
            {
                case 0x24:
                    stream.BitDepth = 10;
                    stream.PixelFormat = "yuv420p10le";
                    break;
                case 0x02 or 0x1b or 0xea:
                    stream.BitDepth = 8;
                    stream.PixelFormat = "yuv420p";
                    break;
            }

            if (source.ColorSpaceCode == 1)
            {
                stream.ColorPrimaries = "bt709";
                stream.ColorTransfer = "bt709";
                stream.ColorSpace = "bt709";
            }

            if (source.ColorSpaceCode == 2 || source.DynamicRangeCode == 1)
            {
                stream.ColorPrimaries = "bt2020";
                stream.ColorSpace = "bt2020nc";
                stream.ColorTransfer = source.DynamicRangeCode == 1 ? "smpte2084" : null;
            }

            if (source.Hdr10Plus)
            {
                stream.Hdr10PlusPresentFlag = true;
            }
        }

        private static void PreservePlaylistMarkers(MediaStream stream, MplsStreamInfo source, bool forceDolbyVision)
        {
            var markers = new List<string>();
            if (source.DynamicRangeCode != 0)
            {
                markers.Add($"dynamic-range code {source.DynamicRangeCode}");
            }

            if (source.Hdr10Plus)
            {
                markers.Add("HDR10+ playlist marker");
            }

            if (source.ColorSpaceCode != 0)
            {
                markers.Add($"color-space code {source.ColorSpaceCode}");
            }

            if (forceDolbyVision)
            {
                markers.Add("Dolby Vision playlist marker");
            }

            if (markers.Count == 0)
            {
                return;
            }

            AppendComment(stream, "Blu-ray MPLS source markers: " + string.Join(", ", markers) +
                ". Color and bit depth follow these markers and the Blu-ray format, not a video measurement; Dolby Vision profile/layer details are unknown.");
        }

        private static void AppendComment(MediaStream stream, string note)
        {
            stream.Comment = string.IsNullOrWhiteSpace(stream.Comment)
                ? note
                : stream.Comment + " " + note;
        }

        private static int? GetSampleRate(byte rateCode)
        {
            return rateCode switch
            {
                1 => 48000,
                4 => 96000,
                5 => 192000,
                12 => 48000,
                14 => 48000,
                _ => null
            };
        }

        private static void Set(object target, string propertyName, object value)
        {
            if (target == null)
            {
                return;
            }

            // An unknown name is a mapping bug (e.g. an Emby-only field), so fail loudly instead of dropping it.
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property?.CanWrite != true)
            {
                throw new MissingMemberException(target.GetType().Name, propertyName);
            }

            if (value == null)
            {
                return;
            }

            try
            {
                var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                var converted = targetType.IsInstanceOfType(value)
                    ? value
                    : Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                property.SetValue(target, converted);
            }
            catch
            {
                // Jellyfin model versions differ in optional media-stream field types.
            }
        }
    }
}
