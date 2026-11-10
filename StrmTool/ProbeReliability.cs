using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace StrmTool;

public enum ProbeFailureKind { None, TransientNetwork, AccessDenied, RateLimited, InvalidMedia, SourceChanged, PersistenceMismatch, CacheWriteFailure, Unknown }
public sealed class ProbeFailureException : Exception
{
    public ProbeFailureKind Kind { get; }
    public ProbeFailureException(ProbeFailureKind kind) : base(kind.ToString()) { Kind = kind; }
}
public static class ProbeFailurePolicy
{
    public static ProbeFailureKind Classify(Exception error)
    {
        if (error is OperationCanceledException) throw error;
        if (error is ProbeFailureException failure) return failure.Kind;
        if (error is HttpRequestException request)
            return request.StatusCode == HttpStatusCode.Forbidden || request.StatusCode == HttpStatusCode.Unauthorized ? ProbeFailureKind.AccessDenied
                : (int?)request.StatusCode == 429 ? ProbeFailureKind.RateLimited : ProbeFailureKind.TransientNetwork;
        if (error is InvalidDataException || error is NotSupportedException) return ProbeFailureKind.InvalidMedia;
        if (error is UnauthorizedAccessException) return ProbeFailureKind.AccessDenied;
        if (error is TimeoutException || error is IOException) return ProbeFailureKind.TransientNetwork;
        return ProbeFailureKind.Unknown;
    }
    public static bool ShouldRetry(ProbeFailureKind kind) => kind == ProbeFailureKind.TransientNetwork || kind == ProbeFailureKind.Unknown;
}

// Fixed stripes bound memory use and serialize all writers for an item, including restores.
public static class ProbeCoordinator
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 257).Select(_ => new SemaphoreSlim(1,1)).ToArray();
    public static async Task<IDisposable> EnterAsync(Guid id, CancellationToken token)
    {
        var gate = Gates[(int)((uint)id.GetHashCode() % (uint)Gates.Length)];
        await gate.WaitAsync(token).ConfigureAwait(false);
        return new Lease(gate);
    }
    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim _gate;
        public Lease(SemaphoreSlim gate) { _gate=gate; }
        public void Dispose() { Interlocked.Exchange(ref _gate,null)?.Release(); }
    }
}

public static class TechnicalMetadataVerifier
{
    public static MediaStream PrimaryVideo(IEnumerable<MediaStream> streams) => streams?.FirstOrDefault(s => s.Type == MediaStreamType.Video && !string.Equals(s.Codec,"mjpeg",StringComparison.OrdinalIgnoreCase) && !string.Equals(s.Codec,"png",StringComparison.OrdinalIgnoreCase))
        ?? streams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
    public static void Verify(BaseItem actual, string expectedPath, MediaProbeResult expected, IReadOnlyList<MediaStream> persistedStreams)
    {
        if (actual == null || actual.Path != expectedPath || actual.Size != expected.Size || actual.RunTimeTicks != expected.RunTimeTicks
            || !string.Equals(actual.Container,expected.Container,StringComparison.OrdinalIgnoreCase))
            throw new ProbeFailureException(ProbeFailureKind.PersistenceMismatch);
        var video = PrimaryVideo(expected.MediaStreams);
        if (actual is Video v && (video == null || v.Width != video.Width || v.Height != video.Height))
            throw new ProbeFailureException(ProbeFailureKind.PersistenceMismatch);
        if (actual is Video && expected.TotalBitrate.HasValue && actual.TotalBitrate != expected.TotalBitrate)
            throw new ProbeFailureException(ProbeFailureKind.PersistenceMismatch);
        if (!StreamsEqual(expected.MediaStreams, persistedStreams))
            throw new ProbeFailureException(ProbeFailureKind.PersistenceMismatch);
        if (string.IsNullOrEmpty(expected.StrmContentHash) || MediaInfoCache.GetStrmContentHash(expectedPath) != expected.StrmContentHash)
            throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
    }
    public static bool StreamsEqual(IReadOnlyList<MediaStream> expected, IReadOnlyList<MediaStream> stored)
    {
        // External subtitles are owned by Jellyfin's own scan, not by the probe result.
        expected = Embedded(expected);
        stored = Embedded(stored);
        if (expected == null || stored == null || expected.Count != stored.Count) return false;
        // ColorRange is deliberately absent: Jellyfin 10.11 has the property but no database column, so it always
        // reads back null and comparing it made every ffprobe result fail verification (the 2.4.2 NAS trial failure).
        var fields = new[] { "Codec", "CodecTag", "Width", "Height", "BitDepth", "PixelFormat", "ColorPrimaries", "ColorTransfer", "ColorSpace", "AspectRatio", "RealFrameRate", "AverageFrameRate", "Level", "Profile", "Channels", "ChannelLayout", "SampleRate", "IsInterlaced", "Language", "IsDefault", "IsForced", "Comment", "BitRate", "Title", "DvProfile", "DvLevel", "DvVersionMajor", "DvVersionMinor", "RpuPresentFlag", "ElPresentFlag", "BlPresentFlag", "Hdr10PlusPresentFlag" };
        // Embedded streams are paired by position, because external subtitles shift their stored Index.
        for (int i = 0; i < expected.Count; i++)
        {
            var stream = expected[i];
            var match = stored[i];
            if (match.Type != stream.Type || fields.Any(name =>
            {
                var property = typeof(MediaStream).GetProperty(name);
                return property != null && !Equals(AsReadBack(name, property.GetValue(match)), AsReadBack(name, property.GetValue(stream)));
            })) return false;
        }
        return true;
    }

    // ISO 639-2 bibliographic codes and their terminology equivalents.
    private static readonly Dictionary<string, string> TerminologyLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho", ["cze"] = "ces", ["dut"] = "nld",
        ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu", ["gre"] = "ell", ["ice"] = "isl", ["mac"] = "mkd", ["mao"] = "mri",
        ["may"] = "msa", ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk", ["tib"] = "bod", ["wel"] = "cym",
    };

    /// <summary>
    /// Applies the conversions Jellyfin 10.11's MediaStreamRepository performs on a save/read round trip, so a
    /// faithful write is not reported as a mismatch: Language is read back as ISO 639-2/T ("chi" -> "zho"),
    /// Level is stored as float, and a null IsInterlaced is read back as false.
    /// </summary>
    private static object AsReadBack(string field, object value) => field switch
    {
        "Language" when value is string code => TerminologyLanguage.TryGetValue(code, out var t) ? t : code,
        "Level" when value is double level => (double)(float)level,
        "IsInterlaced" => value ?? false,
        _ => value,
    };

    private static IReadOnlyList<MediaStream> Embedded(IEnumerable<MediaStream> streams) => streams?.Where(s => !s.IsExternal).OrderBy(s => s.Index).ToList();

    /// <summary>
    /// Probe results never contain external subtitles, so carry the stored ones over instead of erasing them.
    /// Jellyfin 10.11 numbers external subtitles first (0..n-1) and shifts embedded streams by n (all 1310 STRM
    /// items with external subtitles on the NAS follow this), so the result keeps that layout. Probed streams are
    /// copied before renumbering: the same objects are later written to the cache with their original Index.
    /// </summary>
    /// <remarks>
    /// <paramref name="discovered"/> are subtitle files found on disk (<see cref="ExternalSubtitleScanner"/>); any not
    /// already stored are appended after the stored externals, so Jellyfin dropping them during a re-probe no longer
    /// loses them. <paramref name="embeddedShift"/> is subtracted from embedded indexes first: pass the stored
    /// external count when <paramref name="probed"/> are stored streams that already carry Jellyfin's shift.
    /// </remarks>
    public static List<MediaStream> WithStoredExternal(IEnumerable<MediaStream> probed, IEnumerable<MediaStream> stored,
        IEnumerable<MediaStream> discovered = null, int embeddedShift = 0)
    {
        var externals = MergeExternal(stored, discovered);
        var result = new List<MediaStream>();
        for (int i = 0; i < externals.Count; i++)
        {
            externals[i].Index = i;
            result.Add(externals[i]);
        }
        foreach (var stream in probed?.Where(s => !s.IsExternal).OrderBy(s => s.Index) ?? Enumerable.Empty<MediaStream>())
        {
            int index = stream.Index - embeddedShift + externals.Count;
            var copy = index == stream.Index ? stream : JsonSerializer.Deserialize<MediaStream>(JsonSerializer.Serialize(stream));
            copy.Index = index;
            result.Add(copy);
        }
        return result;
    }

    /// <summary>Stored externals in their stored order, then discovered files not already stored (matched by path).</summary>
    public static List<MediaStream> MergeExternal(IEnumerable<MediaStream> stored, IEnumerable<MediaStream> discovered)
    {
        var result = stored?.Where(s => s.IsExternal).OrderBy(s => s.Index).ToList() ?? new List<MediaStream>();
        var known = new HashSet<string>(result.Where(s => !string.IsNullOrEmpty(s.Path)).Select(s => s.Path), StringComparer.Ordinal);
        foreach (var stream in discovered ?? Enumerable.Empty<MediaStream>())
        {
            if (stream != null && stream.IsExternal && !string.IsNullOrEmpty(stream.Path) && known.Add(stream.Path))
            {
                result.Add(stream);
            }
        }
        return result;
    }

    /// <summary>True when a valid subtitle file on disk has no external stream in the stored streams.</summary>
    public static bool ExternalsMissing(IEnumerable<MediaStream> stored, IEnumerable<MediaStream> discovered)
    {
        var known = new HashSet<string>(stored?.Where(s => s.IsExternal && !string.IsNullOrEmpty(s.Path)).Select(s => s.Path)
            ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        return discovered?.Any(d => d != null && !string.IsNullOrEmpty(d.Path) && !known.Contains(d.Path)) ?? false;
    }

    // Fields a native Jellyfin re-probe of an STRM URL commonly loses or changes; Title/Comment noise is ignored.
    private static readonly string[] DetailFields = { "Codec", "Width", "Height", "BitRate", "BitDepth", "Profile", "PixelFormat", "ColorTransfer", "Channels", "SampleRate" };

    /// <summary>
    /// True only when the stored streams are missing detail the cache has and nothing contradicts the cache.
    /// When both sides hold different values (stream count, height, audio bitrate...) the two probes disagree and
    /// neither can be proven right, so the native data is kept; the NAS library has such same-file cases (e.g. a
    /// 50-stream cache against 20 stored streams). A cached 0 means "unknown" and never counts as detail.
    /// </summary>
    public static bool StoredStreamsDegraded(IReadOnlyList<MediaStream> cached, IReadOnlyList<MediaStream> stored)
    {
        cached = Embedded(cached);
        stored = Embedded(stored);
        if (cached == null || cached.Count == 0) return false;
        if (stored == null || stored.Count == 0) return true;
        if (stored.Count != cached.Count) return false;
        bool missingDetail = false;
        for (int i = 0; i < cached.Count; i++)
        {
            var stream = cached[i];
            var match = stored[i];
            if (match.Type != stream.Type) return false;
            foreach (var name in DetailFields)
            {
                // Subtitle canvas size (PGS 1920x1080 vs 3840x2160) is not media info worth arguing over.
                if (stream.Type == MediaStreamType.Subtitle && (name == "Width" || name == "Height")) continue;
                var property = typeof(MediaStream).GetProperty(name);
                if (property == null) continue;
                var expected = Known(property.GetValue(stream));
                var actual = Known(property.GetValue(match));
                if (expected == null) continue;
                if (actual == null) missingDetail = true;
                else if (expected is string e && actual is string a ? !string.Equals(e, a, StringComparison.OrdinalIgnoreCase) : !Equals(actual, expected)) return false;
            }
        }
        return missingDetail;
    }

    private static object Known(object value) => value switch
    {
        null => null,
        string s when string.IsNullOrEmpty(s) => null,
        int n when n == 0 => null,
        _ => value,
    };

    public static bool ChaptersEqual(IReadOnlyList<ChapterInfo> expected, IReadOnlyList<ChapterInfo> stored)
    {
        if (expected == null || stored == null || expected.Count != stored.Count) return false;
        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i].StartPositionTicks != stored[i].StartPositionTicks || expected[i].Name != stored[i].Name) return false;
            var end = typeof(ChapterInfo).GetProperty("EndPositionTicks");
            if (end != null && !Equals(end.GetValue(expected[i]), end.GetValue(stored[i]))) return false;
        }
        return true;
    }

}
