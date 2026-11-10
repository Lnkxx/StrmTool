using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace StrmTool
{
    /// <summary>
    /// Runs the libbluray-enabled ffprobe and maps its JSON output to Jellyfin models.
    /// </summary>
    public sealed class IsoMediaProbe
    {
        private readonly ILogger _logger;

        public IsoMediaProbe(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<MediaProbeResult> ProbeAsync(
            string isoPath,
            string ffprobePath,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            if (!IsoDetector.IsLocalIsoPath(isoPath) && !IsoDetector.IsHttpPath(isoPath))
            {
                throw new ArgumentException("ISO probe requires a local path or an HTTP(S) URL.", nameof(isoPath));
            }

            if (string.IsNullOrWhiteSpace(ffprobePath))
            {
                throw new InvalidOperationException("Custom ffprobe path is not configured.");
            }

            if (!File.Exists(ffprobePath))
            {
                throw new FileNotFoundException("Custom ffprobe was not found.", ffprobePath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-print_format");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add("-show_format");
            startInfo.ArgumentList.Add("-show_streams");
            startInfo.ArgumentList.Add("-show_chapters");
            startInfo.ArgumentList.Add("-show_programs");
            startInfo.ArgumentList.Add("bluray:" + isoPath);

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start custom ffprobe.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("ffprobe exited with code " + process.ExitCode + ": " + Truncate(error));
                }

                return Parse(output, isoPath);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException("ISO ffprobe timed out after " + Math.Max(1, timeoutSeconds) + " seconds.");
            }
            catch
            {
                TryKill(process);
                throw;
            }
        }

        public static MediaProbeResult Parse(string json, string isoPath = null)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var result = new MediaProbeResult { ChaptersProbed = true };
            var format = GetObject(root, "format");

            result.Size = GetLong(format, "size");
            result.RunTimeTicks = ToTicks(GetDouble(format, "duration"));
            result.Container = GetString(format, "format_name");
            if (result.Size <= 0 && IsoDetector.IsLocalIsoPath(isoPath) && File.Exists(isoPath))
            {
                result.Size = new FileInfo(isoPath).Length;
            }

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var streamJson in streams.EnumerateArray())
                {
                    var stream = MapStream(streamJson);
                    if (stream != null)
                    {
                        result.MediaStreams.Add(stream);
                    }
                }
            }

            if (root.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
            {
                foreach (var chapterJson in chapters.EnumerateArray())
                {
                    var chapter = MapChapter(chapterJson);
                    if (chapter != null)
                    {
                        result.Chapters.Add(chapter);
                    }
                }
            }

            return result;
        }

        private static MediaStream MapStream(JsonElement json)
        {
            var codecType = GetString(json, "codec_type");
            MediaStreamType type;
            switch (codecType?.ToLowerInvariant())
            {
                case "video": type = MediaStreamType.Video; break;
                case "audio": type = MediaStreamType.Audio; break;
                case "subtitle": type = MediaStreamType.Subtitle; break;
                case "attachment": type = MediaStreamType.EmbeddedImage; break;
                default: return null;
            }

            var stream = new MediaStream
            {
                Index = GetInt(json, "index"),
                Type = type
            };

            var tags = GetObject(json, "tags");
            var title = GetString(tags, "title");
            stream.Codec = GetString(json, "codec_name");
            stream.CodecTag = GetString(json, "codec_tag_string");
            stream.Profile = GetString(json, "profile");
            stream.Language = GetString(tags, "language");
            stream.Title = title;
            stream.IsDefault = GetInt(GetObject(json, "disposition"), "default") == 1;
            stream.IsForced = GetInt(GetObject(json, "disposition"), "forced") == 1;
            stream.Width = GetPositiveInt(json, "width");
            stream.Height = GetPositiveInt(json, "height");
            stream.BitRate = GetPositiveInt(json, "bit_rate");
            stream.Channels = GetPositiveInt(json, "channels");
            stream.SampleRate = GetPositiveInt(json, "sample_rate");
            stream.ChannelLayout = GetString(json, "channel_layout");
            stream.TimeBase = GetString(json, "time_base");
            stream.RealFrameRate = GetFrameRate(json, "r_frame_rate");
            stream.AverageFrameRate = GetFrameRate(json, "avg_frame_rate");
            stream.Level = GetPositiveInt(json, "level");
            stream.BitDepth = type switch
            {
                MediaStreamType.Video => GetPositiveInt(json, "bits_per_raw_sample") ?? GetPositiveInt(json, "bits_per_sample"),
                MediaStreamType.Audio => GetPositiveInt(json, "bits_per_sample") ?? GetPositiveInt(json, "bits_per_raw_sample"),
                _ => null
            };
            stream.AspectRatio = GetString(json, "display_aspect_ratio");
            stream.PixelFormat = GetString(json, "pix_fmt");
            stream.ColorRange = GetString(json, "color_range");
            stream.ColorSpace = GetString(json, "color_space");
            stream.ColorTransfer = GetString(json, "color_transfer");
            stream.ColorPrimaries = GetString(json, "color_primaries");
            if (type == MediaStreamType.Video)
            {
                var isInterlaced = GetInterlaced(json, "field_order");
                // Jellyfin 10.11.6 models this as a non-nullable bool. Keep the
                // source uncertainty in Comment while avoiding a false positive.
                stream.IsInterlaced = isInterlaced ?? false;
                if (!isInterlaced.HasValue)
                {
                    var fieldOrder = GetString(json, "field_order") ?? "<missing>";
                    stream.Comment = $"ffprobe field_order '{fieldOrder}' is unknown; scan mode was not classified.";
                }
            }
            return stream;
        }

        private static ChapterInfo MapChapter(JsonElement json)
        {
            var startTicks = ToTicks(GetDouble(json, "start_time"));
            if (!startTicks.HasValue)
            {
                startTicks = ToTicks(GetDouble(json, "start"));
            }
            // Jellyfin's ChapterInfo has no EndPositionTicks (an Emby field); chapters end at the next start.
            var chapter = new ChapterInfo();
            Set(chapter, "StartPositionTicks", startTicks ?? 0);
            if (json.TryGetProperty("tags", out var tagsObject) && tagsObject.ValueKind == JsonValueKind.Object)
            {
                Set(chapter, "Name", GetString(tagsObject, "title") ?? GetString(tagsObject, "name"));
            }
            return chapter;
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
                // Jellyfin model versions differ in optional stream field types.
            }
        }

        private static JsonElement GetObject(JsonElement json, string name)
        {
            return json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
                ? value
                : default;
        }

        private static string GetString(JsonElement json, string name)
        {
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        private static int GetInt(JsonElement json, string name)
        {
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var value)) return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
        }

        private static int? GetPositiveInt(JsonElement json, string name)
        {
            var value = GetString(json, name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
                ? number
                : null;
        }

        private static long GetLong(JsonElement json, string name)
        {
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var value)) return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
        }

        private static double GetDouble(JsonElement json, string name)
        {
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var value)) return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
            return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : 0;
        }

        private static float? GetFrameRate(JsonElement json, string name)
        {
            var value = GetString(json, name);
            if (string.IsNullOrWhiteSpace(value)) return null;
            var parts = value.Split('/');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0)
            {
                return ToFrameRate(numerator / denominator);
            }
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? ToFrameRate(result) : null;
        }

        private static float? ToFrameRate(double value)
        {
            return double.IsFinite(value) && value > 0 ? (float)value : null;
        }

        private static bool? GetInterlaced(JsonElement json, string name)
        {
            var fieldOrder = GetString(json, name)?.ToLowerInvariant();
            if (string.Equals(fieldOrder, "progressive", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (fieldOrder is "tt" or "bb" or "tb" or "bt")
            {
                return true;
            }

            return null;
        }

        private static long? ToTicks(double seconds)
        {
            return seconds > 0 ? (long?)(seconds * TimeSpan.TicksPerSecond) : null;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
            }
        }

        private static string Truncate(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "no stderr" : value.Length > 1000 ? value.Substring(0, 1000) : value;
        }
    }
}
