using System.Reflection;
using System.Runtime.CompilerServices;
using MediaBrowser.Model.Entities;
using StrmTool;

static class Program
{
    private static int _assertions;

    public static int Main()
    {
        CheckLocalProbeJson();
        CheckRemotePlaylistMarkers();
        CheckBitrateEstimate();
        CheckPlaylistColor();
        Console.WriteLine($"IsoChecks passed ({_assertions} assertions).");
        return 0;
    }

    private static void CheckPlaylistColor()
    {
        var hdr = new MediaStream { Type = MediaStreamType.Video };
        RemoteIsoMediaProbe.ApplyPlaylistColor(hdr, new MplsStreamInfo { CodingType = 0x24, DynamicRangeCode = 1, ColorSpaceCode = 2 });
        Equal(10, hdr.BitDepth, "HEVC depth");
        Equal("smpte2084", hdr.ColorTransfer, "HDR10 transfer");
        Equal("bt2020", hdr.ColorPrimaries, "HDR10 primaries");
        Equal(Jellyfin.Data.Enums.VideoRange.HDR, hdr.VideoRange, "Jellyfin sees HDR");

        var sdr = new MediaStream { Type = MediaStreamType.Video };
        RemoteIsoMediaProbe.ApplyPlaylistColor(sdr, new MplsStreamInfo { CodingType = 0x1b });
        Equal(8, sdr.BitDepth, "AVC depth");
        Equal(null, sdr.ColorTransfer, "unmarked SDR keeps color unknown");

        var wide = new MediaStream { Type = MediaStreamType.Video };
        RemoteIsoMediaProbe.ApplyPlaylistColor(wide, new MplsStreamInfo { CodingType = 0x24, ColorSpaceCode = 2 });
        Equal("bt2020", wide.ColorPrimaries, "BT.2020 SDR primaries");
        Equal(null, wide.ColorTransfer, "BT.2020 SDR is not HDR");
    }

    private static void CheckBitrateEstimate()
    {
        var gib = 1024L * 1024 * 1024;
        Equal(true, IsoBitrateEstimator.IsDecoySize(gib * 3 / 2, TimeSpan.FromMinutes(171).Ticks, gib * 58), "plausible rate, tiny share of disc");
        Equal(false, IsoBitrateEstimator.IsDecoySize(gib * 21, TimeSpan.FromMinutes(120).Ticks, gib * 59), "real feature with large extras");
        Equal<int?>(25_714_000, IsoBitrateEstimator.ComputeTotalBitrate(22_039_345_152, TimeSpan.FromSeconds(6856.6666).Ticks), "total from clip size");
        Equal<int?>(null, IsoBitrateEstimator.ComputeTotalBitrate(381_941_760, TimeSpan.FromMinutes(175.11).Ticks), "decoy playlist rejected");
        Equal<int?>(null, IsoBitrateEstimator.ComputeTotalBitrate(0, TimeSpan.FromMinutes(100).Ticks), "unknown size");

        // 4 packets: PID 0x1011 twice (one with adaptation field), PID 0x1100 once, null packet once.
        var data = new byte[192 * 4];
        void Packet(int index, int pid, byte control, byte adaptationLength = 0)
        {
            var start = index * 192 + 4;
            data[start] = 0x47;
            data[start + 1] = (byte)(pid >> 8);
            data[start + 2] = (byte)pid;
            data[start + 3] = control;
            data[start + 4] = adaptationLength;
        }
        Packet(0, 0x1011, 0x10);
        Packet(1, 0x1011, 0x30, 83);
        Packet(2, 0x1100, 0x10);
        Packet(3, 0x1FFF, 0x10);
        var tally = new PacketTally();
        IsoBitrateEstimator.TallyPackets(data, tally);
        Equal(4L, tally.Packets, "packets");
        Equal(4L, tally.Synced, "synced");
        Equal(284L, tally.PayloadBytes[0x1011], "payload with adaptation field");
        var rates = IsoBitrateEstimator.ComputeStreamBitrates(tally, 7_680_000);
        Equal(2_840_000, rates[0x1011], "video share");
        Equal(1_840_000, rates[0x1100], "audio share");

        // Encrypted payloads have no sync pattern: only the total may be used.
        var scrambled = new PacketTally();
        IsoBitrateEstimator.TallyPackets(new byte[192 * 4], scrambled);
        Equal(0, IsoBitrateEstimator.ComputeStreamBitrates(scrambled, 7_680_000).Count, "scrambled sample gives no stream rates");
    }

    private static void CheckLocalProbeJson()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "Samples", "ffprobe-sample.json");
        var result = IsoMediaProbe.Parse(File.ReadAllText(samplePath));
        Equal("bluray", result.Container, "container");
        Equal(123456789L, result.Size, "format size");
        Equal<long?>(TimeSpan.FromSeconds(120.5).Ticks, result.RunTimeTicks, "runtime ticks");
        Equal(4, result.MediaStreams.Count, "stream count");
        Equal(1, result.Chapters.Count, "chapter count");

        var video = result.MediaStreams[0];
        Equal(MediaStreamType.Video, video.Type, "first stream type");
        Equal("Main 10", video.Profile, "video profile");
        Equal<double?>(153, video.Level, "video level");
        Equal<int?>(10, video.BitDepth, "video bit depth");
        Equal<float?>(24000f / 1001f, video.RealFrameRate, "real frame rate");
        Equal<float?>(24000f / 1001f, video.AverageFrameRate, "average frame rate");
        Equal("16:9", video.AspectRatio, "display aspect ratio");
        Equal("yuv420p10le", video.PixelFormat, "pixel format");
        Equal("tv", video.ColorRange, "color range");
        Equal("bt2020nc", video.ColorSpace, "color space");
        Equal("smpte2084", video.ColorTransfer, "color transfer");
        Equal("bt2020", video.ColorPrimaries, "color primaries");
        Equal(false, video.IsInterlaced, "progressive field order");

        var audio = result.MediaStreams[1];
        Equal(MediaStreamType.Audio, audio.Type, "audio stream type");
        Equal<int?>(24, audio.BitDepth, "audio bit depth uses BitDepth");

        var unknown = result.MediaStreams[2];
        Equal(false, unknown.IsInterlaced, "unknown field order is not marked interlaced");
        Contains(unknown.Comment, "field_order 'unknown'", "unknown scan mode provenance");
        Equal<double?>(null, unknown.Level, "unknown level stays unset");
        Equal<int?>(null, unknown.BitDepth, "unknown bit depth stays unset");
        Equal<float?>(null, unknown.RealFrameRate, "invalid real frame rate stays unset");
        Equal<float?>(null, unknown.AverageFrameRate, "invalid average frame rate stays unset");

        var interlaced = result.MediaStreams[3];
        Equal(true, interlaced.IsInterlaced, "known interlaced field order");
    }

    private static void CheckRemotePlaylistMarkers()
    {
        var assembly = typeof(IsoMediaProbe).Assembly;
        var sourceType = assembly.GetType("StrmTool.MplsStreamInfo", throwOnError: true)!;
        var source = RuntimeHelpers.GetUninitializedObject(sourceType);
        SetMember(source, "Kind", Enum.Parse(sourceType.GetProperty("Kind")!.PropertyType, "Video"));
        SetMember(source, "CodingType", (byte)0x24);
        SetMember(source, "FormatCode", (byte)8);
        SetMember(source, "RateCode", (byte)1);
        SetMember(source, "Language", "eng");
        SetMember(source, "DynamicRangeCode", (byte)2);
        SetMember(source, "Hdr10Plus", true);
        SetMember(source, "ColorSpaceCode", (byte)2);

        var probeType = assembly.GetType("StrmTool.RemoteIsoMediaProbe", throwOnError: true)!;
        var mapStream = probeType.GetMethod("MapStream", BindingFlags.NonPublic | BindingFlags.Static)!;
        var video = (MediaStream)mapStream.Invoke(null, new[] { source, 0, true })!;
        Equal<int?>(3840, video.Width, "remote MPLS width");
        Equal<int?>(2160, video.Height, "remote MPLS height");
        Equal<float?>(24000f / 1001f, video.RealFrameRate, "remote MPLS nominal frame rate");
        Equal<float?>(null, video.AverageFrameRate, "remote MPLS does not invent average frame rate");
        Equal<int?>(10, video.BitDepth, "remote MPLS depth follows the Blu-ray format");
        Equal<string?>(null, video.ColorTransfer, "dynamic-range code 2 does not claim PQ");
        Equal<string?>("bt2020", video.ColorPrimaries, "remote MPLS primaries follow the color-space marker");
        Equal<int?>(null, video.DvProfile, "remote MPLS does not infer Dolby Vision profile");
        Contains(video.Comment, "dynamic-range code 2", "raw dynamic-range marker retained");
        Contains(video.Comment, "HDR10+ playlist marker", "HDR10+ marker retained");
        Contains(video.Comment, "Dolby Vision playlist marker", "Dolby Vision marker retained");
        Contains(video.Comment, "not a video measurement", "remote marker provenance and limitation");
    }

    private static void SetMember(object instance, string name, object value)
    {
        var type = instance.GetType();
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is not null)
        {
            var setter = property.GetSetMethod(nonPublic: true);
            if (setter is null)
            {
                throw new InvalidOperationException($"Test fixture could not set {type.Name}.{name}.");
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            setter.Invoke(instance, new[] { Convert.ChangeType(value, propertyType) });
            return;
        }

        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Test fixture could not find {type.Name}.{name}.");
        field.SetValue(instance, value);
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'.");
        }
    }

    private static void Contains(string? value, string expected, string label)
    {
        _assertions++;
        if (value is null || !value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label}: expected '{value}' to contain '{expected}'.");
        }
    }
}
