using System.Reflection;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using StrmTool;

int count = 0;
void Check(bool ok, string why) { if (!ok) throw new Exception("FAIL: " + why); count++; }

var dir = Path.Combine(Path.GetTempPath(), "extsub-checks-" + Guid.NewGuid());
Directory.CreateDirectory(dir);
try
{
    const string Srt = "1\r\n00:00:40,416 --> 00:00:41,584\r\n<i>示例字幕……</i>\r\n\r\n2\r\n00:00:44,587 --> 00:00:45,880\r\n<i>……一支舰队……</i>\r\n";
    const string Ass = "[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,你好\n";
    string video = Path.Combine(dir, "示例影片 (2026) - 2160p.strm");
    File.WriteAllText(video, "http://example.invalid/sample");
    void Write(string name, string text) => File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));
    void WriteBytes(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(dir, name), bytes);

    Write("示例影片 (2026) - 2160p.zh-CN.srt", Srt);
    Write("示例影片 (2026) - 2160p.default.chi.zh-cn.ass", Ass);
    Write("示例影片 (2026) - 2160p.eng.forced.srt", Srt);
    Write("示例影片 (2026) - 2160p.sdh.srt", Srt);
    Write("示例影片 (2026) - 2160p.ass", Ass);
    WriteBytes("示例影片 (2026) - 2160p.utf16.srt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Srt)).ToArray());
    // A large file whose 256 KiB sample boundary splits a multi-byte character.
    var big = new StringBuilder();
    for (int i = 1; big.Length < 200_000; i++) big.Append($"{i}\r\n00:{i / 60 % 60:00}:{i % 60:00},000 --> 00:{i / 60 % 60:00}:{i % 60:00},500\r\n中文字幕第{i}行\r\n\r\n");
    Write("示例影片 (2026) - 2160p.big.srt", big.ToString());
    // Accepted by FFmpeg too: blank lines first and loose spacing; ":000" end milliseconds (NAS 莲花楼 S01E13, which Jellyfin lists).
    Write("示例影片 (2026) - 2160p.loosetime.srt", "\r\n\r\n 1 \r\n00:00:01,000  -->  00:00:02,000\r\nx\r\n");
    Write("示例影片 (2026) - 2160p.colonms.srt", "1\r\n00:02:04,000 --> 00:02:08:000\r\nx\r\n");
    // Rejected: not this video, no delimiter, bad encodings, broken timing, unsupported or image formats.
    Write("示例影片 (2026) - 1080p.zh-CN.srt", Srt);
    Write("示例影片 (2026) - 2160pX.srt", Srt);
    WriteBytes("示例影片 (2026) - 2160p.gbk.srt", Encoding.ASCII.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\n").Concat(new byte[] { 0xC4, 0xE3, 0xBA, 0xC3, 0x0D, 0x0A }).ToArray());
    // NAS 疑犯追踪/汉尼拔/绝命毒师: the file starts inside the first cue, so ffprobe cannot detect SRT.
    WriteBytes("示例影片 (2026) - 2160p.cutstart.srt", Encoding.ASCII.GetBytes("2,660 --> 00:00:04,480\r\n").Concat(new byte[] { 0xC4, 0xE3, 0x0D, 0x0A, 0x0D, 0x0A })
        .Concat(Encoding.ASCII.GetBytes("2\r\n00:00:05,000 --> 00:00:06,000\r\nx\r\n")).ToArray());
    // NAS 继承之战 E01 / E02: a damaged "[Script Info]" header, and a doubled BOM.
    Write("示例影片 (2026) - 2160p.badhead.ass", " Info]\r\n" + Ass);
    WriteBytes("示例影片 (2026) - 2160p.doublebom.ass", new byte[] { 0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Ass)).ToArray());
    Write("示例影片 (2026) - 2160p.styles.ass", "[V4+ Styles]\n" + Ass);
    Write("示例影片 (2026) - 2160p.notime.srt", "1\r\nhello\r\n\r\n");
    // NAS 生活大爆炸 S01E06: ASS content under .srt; ffprobe (and Jellyfin) report it as ass.
    Write("示例影片 (2026) - 2160p.asscontent.srt", Ass);
    Write("示例影片 (2026) - 2160p.notvtt.vtt", "WEBVTTX\n\n00:01.000 --> 00:02.000\nx\n");
    Write("示例影片 (2026) - 2160p.sup", "not text");
    Write("示例影片 (2026) - 2160p.zh.txt", Srt);
    Write("示例影片 (2026) - 2160p.nfo", "<movie/>");

    ExternalSubtitleScanner.Reset();
    Check(ExternalSubtitleScanner.Discover(video).Count == 0, "unconfigured scanner discovers nothing (previous behavior)");

    ExternalSubtitleScanner.Configure(FakeLocalization.Create());
    var found = ExternalSubtitleScanner.Discover(video);
    foreach (var s in found) Console.WriteLine($"  found {Path.GetFileName(s.Path)} codec={s.Codec} lang={s.Language ?? "-"} title={s.Title ?? "-"} default={s.IsDefault} forced={s.IsForced} hi={s.IsHearingImpaired}");
    var names = found.Select(s => Path.GetFileName(s.Path)).ToHashSet();
    string[] expected = { "示例影片 (2026) - 2160p.zh-CN.srt", "示例影片 (2026) - 2160p.default.chi.zh-cn.ass", "示例影片 (2026) - 2160p.eng.forced.srt",
        "示例影片 (2026) - 2160p.sdh.srt", "示例影片 (2026) - 2160p.ass", "示例影片 (2026) - 2160p.utf16.srt", "示例影片 (2026) - 2160p.big.srt",
        "示例影片 (2026) - 2160p.gbk.srt", "示例影片 (2026) - 2160p.loosetime.srt", "示例影片 (2026) - 2160p.colonms.srt", "示例影片 (2026) - 2160p.asscontent.srt" };
    Check(names.SetEquals(expected), "exactly the valid sibling subtitles are discovered: " + string.Join(" | ", names));
    Check(found.All(s => s.IsExternal && s.Type == MediaStreamType.Subtitle && s.TimeBase == "1/1000" && File.Exists(s.Path)), "external subtitle stream shape");
    Check(found.Select(s => s.Path).SequenceEqual(found.Select(s => s.Path).OrderBy(p => p, StringComparer.Ordinal)), "ordered by file name");
    MediaStream By(string suffix) => found.Single(s => s.Path.EndsWith(suffix, StringComparison.Ordinal));
    Check(By(".zh-CN.srt").Codec == "subrip" && By("p.ass").Codec == "ass", "codec from extension matches ffprobe names");
    Check(By(".asscontent.srt").Codec == "ass", "content decides the codec, like ffprobe");
    Check(By(".default.chi.zh-cn.ass").IsDefault && !By(".zh-CN.srt").IsDefault, "default flag parsed by Jellyfin's parser");
    Check(By(".eng.forced.srt").IsForced && By(".eng.forced.srt").Language == "eng", "forced flag and ISO language");
    Check(By(".sdh.srt").IsHearingImpaired, "hearing-impaired flag");
    Check(By("p.ass").Language == null, "no tokens: no language");
    // Exact language codes come from Jellyfin's real LocalizationManager at runtime (NAS rows store e.g. "zh-cn");
    // here only the parser's rules are checked: a recognised tag is a language, not a title...
    Check(By(".zh-CN.srt").Language != null && By(".zh-CN.srt").Title == null, "recognised tag becomes the language");
    // ...and the rightmost language wins while the other token becomes the title (NAS: 疯狂动物城2 default.chi.zh-cn -> title chi).
    Check(By(".default.chi.zh-cn.ass").Language != null && By(".default.chi.zh-cn.ass").Title == "chi", "rightmost language wins, the other becomes the title");

    // Validation cache follows file changes.
    var zh = Path.Combine(dir, "示例影片 (2026) - 2160p.zh-CN.srt");
    Check(ExternalSubtitleScanner.IsValidTextSubtitle(zh), "valid before edit");
    File.WriteAllText(zh, "garbage without timings, longer than before to change the size", new UTF8Encoding(false));
    File.SetLastWriteTimeUtc(zh, DateTime.UtcNow.AddMinutes(1));
    Check(!ExternalSubtitleScanner.IsValidTextSubtitle(zh), "re-validated after the file changes");
    Write("示例影片 (2026) - 2160p.zh-CN.srt", Srt);

    // Merge: the 《示例影片》 case. Cache streams 0..8, Jellyfin's play-time re-probe left no externals.
    List<MediaStream> Cache() => new()
    {
        new() { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = 3840, Height = 2160, BitRate = 60000000, BitDepth = 10 },
        new() { Type = MediaStreamType.Audio, Index = 1, Codec = "dts", Language = "eng", Channels = 6 },
        new() { Type = MediaStreamType.Subtitle, Index = 2, Codec = "subrip", Language = "eng" },
    };
    var cache = Cache();
    var reprobed = new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = 3840, Height = 2160 },
        new() { Type = MediaStreamType.Audio, Index = 1, Codec = "dts", Language = "eng", Channels = 6 }, new() { Type = MediaStreamType.Subtitle, Index = 2, Codec = "subrip", Language = "eng" } };
    var zhOnly = found.Where(s => s.Path.EndsWith(".zh-CN.srt", StringComparison.Ordinal)).ToList();
    Check(TechnicalMetadataVerifier.ExternalsMissing(reprobed, zhOnly), "dropped external detected");
    var merged = TechnicalMetadataVerifier.WithStoredExternal(cache, reprobed, zhOnly);
    Check(merged.Count == 4 && merged[0].IsExternal && merged[0].Index == 0 && merged[1].Codec == "hevc" && merged[1].Index == 1
        && merged[3].Index == 3 && merged[1].BitRate == 60000000, "external first, cached embedded shifted by one");
    Check(cache[0].Index == 0 && cache[2].Index == 2, "cache objects are not renumbered");
    Check(!TechnicalMetadataVerifier.ExternalsMissing(merged, zhOnly), "nothing missing after merge (no restore loop)");
    Check(TechnicalMetadataVerifier.StreamsEqual(cache, merged), "merged result still verifies against the cache");

    // Merge onto stored streams that already carry Jellyfin's shift (embedded fine, one external lost).
    var oldExt = new MediaStream { Type = MediaStreamType.Subtitle, Index = 0, Codec = "ass", IsExternal = true, Path = By("p.ass").Path };
    var stored = new List<MediaStream> { oldExt };
    stored.AddRange(Cache().Select(s => { s.Index += 1; return s; }));
    var both = new List<MediaStream> { By("p.ass"), By(".zh-CN.srt") };
    Check(TechnicalMetadataVerifier.ExternalsMissing(stored, both) && !TechnicalMetadataVerifier.StoredStreamsDegraded(cache, stored), "only externals missing");
    var reattached = TechnicalMetadataVerifier.WithStoredExternal(stored, stored, both, stored.Count(s => s.IsExternal));
    Check(reattached.Count == 5 && ReferenceEquals(reattached[0], oldExt) && reattached[0].Index == 0 && reattached[1].Path == By(".zh-CN.srt").Path
        && reattached[1].Index == 1 && reattached[2].Codec == "hevc" && reattached[2].Index == 2 && reattached[4].Index == 4, "stored external kept, new one appended, embedded re-shifted");
    Check(reattached.Count(s => s.Path == oldExt.Path) == 1, "same path is not duplicated");
    Check(TechnicalMetadataVerifier.StreamsEqual(cache, reattached), "re-attached streams verify against the cache");
    Check(!TechnicalMetadataVerifier.ExternalsMissing(stored, null) && !TechnicalMetadataVerifier.ExternalsMissing(null, new List<MediaStream>()), "null-safe");
    var untouched = TechnicalMetadataVerifier.WithStoredExternal(cache, reprobed);
    Check(ReferenceEquals(untouched[0], cache[0]) && untouched.Count == 3, "no externals anywhere: identical to 2.4.3.4");

    // Restore trigger: same item, real size, full streams, only the external dropped.
    var needs = typeof(ItemUpdateListener).GetMethod("NeedsMetadataRestore", BindingFlags.NonPublic | BindingFlags.Static)!;
    var cacheData = new MediaInfoCacheData { Size = 88246893816, RunTimeTicks = 103557230000, MediaStreams = Cache() };
    var item = new Video { Path = video, Size = 88246893816, RunTimeTicks = 103557230000, Width = 3840, Height = 2160 };
    var fullStored = Cache();
    Check((bool)needs.Invoke(null, new object[] { item, cacheData, fullStored })!, "missing external alone triggers a restore");
    var withAll = TechnicalMetadataVerifier.WithStoredExternal(fullStored, fullStored, ExternalSubtitleScanner.Discover(video));
    Check(!(bool)needs.Invoke(null, new object[] { item, cacheData, withAll })!, "no restore once all externals are stored");
    ExternalSubtitleScanner.Reset();
    Check(!(bool)needs.Invoke(null, new object[] { item, cacheData, Cache() })!, "unconfigured: unchanged 2.4.3.4 decision");

    Console.WriteLine($"PASS {count} external subtitle checks");
}
finally
{
    ExternalSubtitleScanner.Reset();
    Directory.Delete(dir, true);
}

