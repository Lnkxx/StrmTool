using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Emby.Naming.Common;
using Emby.Naming.ExternalFiles;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;

namespace StrmTool;

/// <summary>
/// Finds the text subtitle files that sit next to an STRM file, the same way Jellyfin 10.11 names them.
///
/// Jellyfin drops external subtitles when it re-probes an STRM (for example when playback starts), and the
/// cache restore used to carry over only externals still in the database at that moment, so they were lost
/// until the next folder scan. Restores now re-discover them from disk. Naming (language, title, default /
/// forced / hearing-impaired flags) is parsed by Jellyfin's own <see cref="ExternalPathParser"/>, so the
/// streams match what a native scan writes. Files FFmpeg's demuxer probe would not recognize (damaged headers,
/// a missing first cue) are skipped, as Jellyfin's ffprobe rejects them too. Until <see cref="Configure"/> runs
/// nothing is discovered, which keeps the previous behavior.
/// </summary>
public static class ExternalSubtitleScanner
{
    // FFmpeg probes read 2 KiB by default; 4 KiB leaves room for a UTF-16 file.
    private const int SampleBytes = 4 * 1024;

    // Extension -> codec name as Jellyfin's ffprobe reports it for an external file.
    private static readonly Dictionary<string, string> Codecs = new(StringComparer.OrdinalIgnoreCase)
    {
        [".srt"] = "subrip", [".ass"] = "ass", [".ssa"] = "ass", [".vtt"] = "webvtt",
    };

    private static readonly ConcurrentDictionary<string, (long Length, DateTime Modified, string Codec)> Validity = new(StringComparer.Ordinal);

    private static volatile ExternalPathParser _parser;
    private static volatile NamingOptions _options;

    public static bool IsConfigured => _parser != null;

    public static void Configure(ILocalizationManager localizationManager)
    {
        if (localizationManager == null)
        {
            return;
        }

        var options = new NamingOptions();
        _parser = new ExternalPathParser(options, localizationManager, DlnaProfileType.Subtitle);
        _options = options;
    }

    /// <summary>Test hook: forget the parser so discovery is disabled again.</summary>
    public static void Reset()
    {
        _parser = null;
        _options = null;
        Validity.Clear();
    }

    /// <summary>
    /// Valid external text subtitles for <paramref name="videoPath"/>, ordered by file name. Never throws.
    /// </summary>
    public static List<MediaStream> Discover(string videoPath)
    {
        var result = new List<MediaStream>();
        var parser = _parser;
        var options = _options;
        if (parser == null || options == null || string.IsNullOrEmpty(videoPath))
        {
            return result;
        }

        try
        {
            var directory = Path.GetDirectoryName(videoPath);
            var prefix = Path.GetFileNameWithoutExtension(videoPath);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(prefix) || !Directory.Exists(directory))
            {
                return result;
            }

            foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (!Codecs.ContainsKey(Path.GetExtension(file)))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || (name.Length > prefix.Length && !options.MediaFlagDelimiters.Contains(name[prefix.Length])))
                {
                    continue;
                }

                var info = parser.ParseFile(file, name.Substring(prefix.Length));
                var codec = info == null ? null : DetectCodec(file);
                if (codec == null)
                {
                    continue;
                }

                result.Add(new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    IsExternal = true,
                    Path = file,
                    Codec = codec,
                    Language = info.Language,
                    Title = info.Title,
                    IsDefault = info.IsDefault,
                    IsForced = info.IsForced,
                    IsHearingImpaired = info.IsHearingImpaired,
                    TimeBase = "1/1000",
                });
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>True when FFmpeg would recognize the file as a text subtitle.</summary>
    public static bool IsValidTextSubtitle(string path) => DetectCodec(path) != null;

    /// <summary>
    /// The codec ffprobe would report for the file (subrip / ass / webvtt), or null when FFmpeg would not recognize it.
    /// Results are remembered per (length, modified time).
    /// </summary>
    public static string DetectCodec(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                return null;
            }

            if (Validity.TryGetValue(path, out var known) && known.Length == fileInfo.Length && known.Modified == fileInfo.LastWriteTimeUtc)
            {
                return known.Codec;
            }

            var codec = CheckContent(path, fileInfo.Length);
            Validity[path] = (fileInfo.Length, fileInfo.LastWriteTimeUtc, codec);
            return codec;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Mirrors FFmpeg's srt / ass / webvtt demuxer probes, which decide whether Jellyfin's ffprobe of an external file
    /// succeeds (a failed probe means Jellyfin itself never lists the subtitle). The probes only look at the start of
    /// the file and accept any 8-bit encoding (GBK subtitles are common and work); UTF-16 is decoded first, like
    /// FFmpeg's text reader does. Calibrated against ffprobe on every subtitle next to an STRM on the NAS (2026-11-07).
    /// </summary>
    private static string CheckContent(string path, long length)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            int count = (int)Math.Min(length, SampleBytes);
            bytes = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(bytes, read, count - read);
                if (n == 0) break;
                read += n;
            }

            if (read < count) Array.Resize(ref bytes, read);
        }

        int pos = TextStart(ref bytes);
        while (pos < bytes.Length && (bytes[pos] == '\r' || bytes[pos] == '\n')) pos++;

        // Like ffprobe, the content decides; the extension only picks the first guess
        // (NAS 生活大爆炸 S01E06 is ASS saved as .srt, and Jellyfin lists it as ass).
        var guess = Codecs.TryGetValue(Path.GetExtension(path), out var byExtension) ? byExtension : null;
        foreach (var codec in new[] { guess, "subrip", "ass", "webvtt" }.Where(c => c != null).Distinct())
        {
            if (Probe(codec, bytes, pos))
            {
                return codec;
            }
        }

        return null;
    }

    private static bool Probe(string codec, byte[] bytes, int pos) => codec switch
    {
        "subrip" => SrtProbe(bytes, pos),
        "ass" or "ssa" => StartsWith(bytes, pos, "[Script Info]"),
        "webvtt" => StartsWith(bytes, pos, "WEBVTT") && (bytes.Length == pos + 6 || bytes[pos + 6] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'),
        _ => false,
    };

    /// <summary>Skips one UTF-8 BOM, or converts UTF-16 (with BOM) to UTF-8; returns the offset of the text.</summary>
    private static int TextStart(ref byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            var unicode = bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            int usable = (bytes.Length - 2) & ~1;
            bytes = Encoding.UTF8.GetBytes(unicode.GetString(bytes, 2, usable));
            return 0;
        }

        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
    }

    // FFmpeg srt_probe: the first non-empty line starts with an integer (anything may follow); the next line starts with a
    // digit (or '-' then a digit), contains " --> " and scans as "%*d:%*d:%*d%*1[,.]%*d --> %*d:%*d:%d".
    private static bool SrtProbe(byte[] bytes, int pos)
    {
        var first = ReadLine(bytes, ref pos);
        if (first == null) return false;
        var number = first.TrimStart(' ', '\t');
        if (number.StartsWith('+') || number.StartsWith('-')) number = number.Substring(1);
        if (number.Length == 0 || !char.IsAsciiDigit(number[0]) || first.TrimStart(' ', '\t').StartsWith('-')) return false;

        var second = ReadLine(bytes, ref pos);
        if (second == null) return false;
        var timing = second.StartsWith('-') ? second.Substring(1) : second;
        return timing.Length > 0 && char.IsAsciiDigit(timing[0]) && second.Contains(" --> ", StringComparison.Ordinal)
            && SrtProbeTiming.IsMatch(second);
    }

    // sscanf semantics: %d skips leading whitespace and takes a sign; a space in the format matches any whitespace.
    private static readonly Regex SrtProbeTiming = new(@"^\s*[-+]?\d+:\s*[-+]?\d+:\s*[-+]?\d+[,.]\s*[-+]?\d+\s*-->\s*[-+]?\d+:\s*[-+]?\d+:\s*[-+]?\d+", RegexOptions.Compiled);

    // Like FFmpeg's ff_subtitles_read_line: one line without its line ending, or null at the end of the sample.
    private static string ReadLine(byte[] bytes, ref int pos)
    {
        if (pos >= bytes.Length) return null;
        int start = pos;
        while (pos < bytes.Length && bytes[pos] != '\n' && bytes[pos] != '\r') pos++;
        var line = Encoding.Latin1.GetString(bytes, start, Math.Min(pos - start, 63));
        if (pos < bytes.Length && bytes[pos] == '\r') pos++;
        if (pos < bytes.Length && bytes[pos] == '\n') pos++;
        return line;
    }

    private static bool StartsWith(byte[] bytes, int pos, string ascii)
    {
        if (bytes.Length - pos < ascii.Length) return false;
        for (int i = 0; i < ascii.Length; i++)
        {
            if (bytes[pos + i] != ascii[i]) return false;
        }

        return true;
    }
}
