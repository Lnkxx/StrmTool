using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StrmTool
{
    internal enum MplsStreamKind
    {
        Video,
        Audio,
        Subtitle,
        Interactive,
        SecondaryAudio,
        SecondaryVideo,
        Unknown
    }

    internal sealed class MplsStreamInfo
    {
        public MplsStreamKind Kind { get; set; }

        public ushort PacketId { get; set; }

        public byte CodingType { get; set; }

        public byte FormatCode { get; set; }

        public byte RateCode { get; set; }

        public byte DynamicRangeCode { get; set; }

        public byte ColorSpaceCode { get; set; }

        public bool Hdr10Plus { get; set; }

        public bool IsDolbyVision { get; set; }

        public string Language { get; set; }
    }

    internal sealed class MplsPlayItem
    {
        public string ClipName { get; set; }

        public long InTime { get; set; }

        public long OutTime { get; set; }

        public List<MplsStreamInfo> Streams { get; } = new List<MplsStreamInfo>();

        public long DurationTicks => Math.Max(0, OutTime - InTime) * TimeSpan.TicksPerSecond / 45000;
    }

    internal sealed class MplsChapter
    {
        public int PlayItemIndex { get; set; }

        public long Timestamp { get; set; }

        public long PositionTicks { get; set; }
    }

    internal sealed class MplsPlaylist
    {
        public List<MplsPlayItem> PlayItems { get; } = new List<MplsPlayItem>();

        public List<MplsChapter> Chapters { get; } = new List<MplsChapter>();

        public long DurationTicks => PlayItems.Sum(item => item.DurationTicks);

        public IEnumerable<MplsStreamInfo> GetDistinctStreams()
        {
            return PlayItems
                .SelectMany(item => item.Streams)
                .GroupBy(stream => new
                {
                    stream.Kind,
                    stream.PacketId,
                    stream.CodingType,
                    stream.FormatCode,
                    stream.RateCode,
                    stream.DynamicRangeCode,
                    stream.ColorSpaceCode,
                    stream.Hdr10Plus,
                    stream.IsDolbyVision,
                    Language = stream.Language ?? string.Empty
                })
                .Select(group => group.First());
        }
    }

    /// <summary>
    /// Parses the playlist, stream table and chapter marks needed for media-info extraction.
    /// </summary>
    internal static class MplsParser
    {
        private const long MplsClockRate = 45000;

        public static MplsPlaylist Parse(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length < 40)
            {
                throw new InvalidDataException("MPLS file is too small.");
            }

            var reader = new BigEndianReader(data);
            var signature = reader.ReadAscii(0, 8);
            if (!signature.StartsWith("MPLS", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid MPLS signature.");
            }

            var playlistAddress = reader.ReadUInt32(8);
            var marksAddress = reader.ReadUInt32(12);
            var playlist = ParsePlaylist(reader, playlistAddress);
            ParseMarks(reader, marksAddress, playlist);
            return playlist;
        }

        private static MplsPlaylist ParsePlaylist(BigEndianReader reader, uint playlistAddress)
        {
            reader.EnsureRange(playlistAddress, 10);
            var sectionLength = reader.ReadUInt32(checked((int)playlistAddress));
            var sectionEnd = checked((long)playlistAddress + 4 + sectionLength);
            reader.EnsureRange(playlistAddress, checked((int)(sectionEnd - playlistAddress)));

            var position = checked((int)playlistAddress + 4);
            position += 2;
            var playItemCount = reader.ReadUInt16(position);
            position += 2;
            position += 2; // sub paths are not required to describe the primary title.

            var result = new MplsPlaylist();
            for (var index = 0; index < playItemCount; index++)
            {
                reader.EnsureRange(position, 2);
                var itemLength = reader.ReadUInt16(position);
                position += 2;
                var itemEnd = checked(position + itemLength);
                if (itemLength < 31 || itemEnd > sectionEnd)
                {
                    throw new InvalidDataException($"Invalid MPLS play-item length at index {index}.");
                }

                var item = new MplsPlayItem
                {
                    ClipName = reader.ReadAscii(position, 5)
                };
                position += 5;
                position += 4; // codec identifier, normally M2TS.

                position += 1; // first 8 of 11 reserved bits
                var flags = reader.ReadByte(position++);
                var isMultiAngle = (flags & 0x10) != 0;
                position += 1; // STC id
                item.InTime = reader.ReadUInt32(position);
                position += 4;
                item.OutTime = reader.ReadUInt32(position);
                position += 4;
                position += 8; // user-operation mask
                position += 1; // random access and reserved bits
                position += 1; // still mode
                position += 2; // still time

                if (isMultiAngle)
                {
                    reader.EnsureRange(position, 2);
                    var angleCount = reader.ReadByte(position++);
                    position += 1;
                    var additionalAngles = Math.Max(0, angleCount - 1);
                    position = checked(position + additionalAngles * 10);
                }

                ParseStreamTable(reader, ref position, itemEnd, item.Streams);
                result.PlayItems.Add(item);
                position = itemEnd;
            }

            return result;
        }

        private static void ParseStreamTable(
            BigEndianReader reader,
            ref int position,
            int playItemEnd,
            ICollection<MplsStreamInfo> streams)
        {
            if (position + 2 > playItemEnd)
            {
                return;
            }

            var tableLength = reader.ReadUInt16(position);
            position += 2;
            var tableEnd = checked(position + tableLength);
            if (tableLength == 0 || tableEnd > playItemEnd)
            {
                position = Math.Min(tableEnd, playItemEnd);
                return;
            }

            reader.EnsureRange(position, 14);
            position += 2;
            var videoCount = reader.ReadByte(position++);
            var audioCount = reader.ReadByte(position++);
            var presentationSubtitleCount = reader.ReadByte(position++);
            var interactiveCount = reader.ReadByte(position++);
            var secondaryAudioCount = reader.ReadByte(position++);
            var secondaryVideoCount = reader.ReadByte(position++);
            var pipSubtitleCount = reader.ReadByte(position++);
            var dolbyVisionVideoCount = reader.ReadByte(position++);
            position += 4;

            ParseStreams(reader, ref position, tableEnd, videoCount, MplsStreamKind.Video, streams);
            ParseStreams(reader, ref position, tableEnd, audioCount, MplsStreamKind.Audio, streams);
            ParseStreams(
                reader,
                ref position,
                tableEnd,
                presentationSubtitleCount + pipSubtitleCount,
                MplsStreamKind.Subtitle,
                streams);
            ParseStreams(reader, ref position, tableEnd, interactiveCount, MplsStreamKind.Interactive, streams);

            var ignoredStreams = new List<MplsStreamInfo>();
            for (var index = 0; index < secondaryAudioCount; index++)
            {
                ParseStreams(reader, ref position, tableEnd, 1, MplsStreamKind.SecondaryAudio, ignoredStreams);
                SkipReferenceList(reader, ref position, tableEnd);
            }

            for (var index = 0; index < secondaryVideoCount; index++)
            {
                ParseStreams(reader, ref position, tableEnd, 1, MplsStreamKind.SecondaryVideo, ignoredStreams);
                SkipReferenceList(reader, ref position, tableEnd);
                SkipReferenceList(reader, ref position, tableEnd);
            }

            ParseStreams(
                reader,
                ref position,
                tableEnd,
                dolbyVisionVideoCount,
                MplsStreamKind.Video,
                streams,
                true);
            position = tableEnd;
        }

        private static void ParseStreams(
            BigEndianReader reader,
            ref int position,
            int tableEnd,
            int count,
            MplsStreamKind kind,
            ICollection<MplsStreamInfo> streams,
            bool isDolbyVision = false)
        {
            for (var index = 0; index < count; index++)
            {
                if (position >= tableEnd)
                {
                    throw new InvalidDataException("MPLS stream table ended unexpectedly.");
                }

                var entryLength = reader.ReadByte(position++);
                var entryStart = position;
                var entryEnd = checked(entryStart + entryLength);
                if (entryLength < 3 || entryEnd > tableEnd)
                {
                    throw new InvalidDataException("Invalid MPLS stream-entry length.");
                }

                var streamType = reader.ReadByte(position++);
                ushort packetId = 0;
                switch (streamType)
                {
                    case 1:
                        packetId = reader.ReadUInt16(position);
                        break;
                    case 2:
                    case 4:
                        if (entryLength >= 5)
                        {
                            packetId = reader.ReadUInt16(position + 2);
                        }
                        break;
                    case 3:
                        if (entryLength >= 4)
                        {
                            packetId = reader.ReadUInt16(position + 1);
                        }
                        break;
                }

                position = entryEnd;
                if (position >= tableEnd)
                {
                    throw new InvalidDataException("MPLS stream attributes are missing.");
                }

                var attributesLength = reader.ReadByte(position++);
                var attributesStart = position;
                var attributesEnd = checked(attributesStart + attributesLength);
                if (attributesLength < 1 || attributesEnd > tableEnd)
                {
                    throw new InvalidDataException("Invalid MPLS stream-attributes length.");
                }

                var codingType = reader.ReadByte(position++);
                byte formatCode = 0;
                byte rateCode = 0;
                byte dynamicRangeCode = 0;
                byte colorSpaceCode = 0;
                var hdr10Plus = false;
                string language = null;

                if (IsVideo(codingType) && position < attributesEnd)
                {
                    var formatAndRate = reader.ReadByte(position++);
                    formatCode = (byte)(formatAndRate >> 4);
                    rateCode = (byte)(formatAndRate & 0x0f);
                    if (position < attributesEnd)
                    {
                        var dynamicRangeAndColorSpace = reader.ReadByte(position++);
                        dynamicRangeCode = (byte)(dynamicRangeAndColorSpace >> 4);
                        colorSpaceCode = (byte)(dynamicRangeAndColorSpace & 0x0f);
                    }

                    if (position < attributesEnd)
                    {
                        hdr10Plus = (reader.ReadByte(position) & 0x40) != 0;
                    }
                }
                else if (IsAudio(codingType) && position < attributesEnd)
                {
                    var formatAndRate = reader.ReadByte(position++);
                    formatCode = (byte)(formatAndRate >> 4);
                    rateCode = (byte)(formatAndRate & 0x0f);
                    language = ReadLanguage(reader, position, attributesEnd);
                }
                else if (codingType == 0x90 || codingType == 0x91)
                {
                    language = ReadLanguage(reader, position, attributesEnd);
                }
                else if (codingType == 0x92)
                {
                    position += 1; // character code
                    language = ReadLanguage(reader, position, attributesEnd);
                }

                streams.Add(new MplsStreamInfo
                {
                    Kind = kind,
                    PacketId = packetId,
                    CodingType = codingType,
                    FormatCode = formatCode,
                    RateCode = rateCode,
                    DynamicRangeCode = dynamicRangeCode,
                    ColorSpaceCode = colorSpaceCode,
                    Hdr10Plus = hdr10Plus,
                    IsDolbyVision = isDolbyVision,
                    Language = language
                });
                position = attributesEnd;
            }
        }

        private static void SkipReferenceList(BigEndianReader reader, ref int position, int tableEnd)
        {
            if (position + 2 > tableEnd)
            {
                throw new InvalidDataException("MPLS secondary-stream reference list is missing.");
            }

            var referenceCount = reader.ReadByte(position++);
            position += 1; // word alignment reservation
            position = checked(position + referenceCount);
            if ((referenceCount & 1) != 0)
            {
                position += 1;
            }

            if (position > tableEnd)
            {
                throw new InvalidDataException("MPLS secondary-stream reference list exceeds the stream table.");
            }
        }

        private static void ParseMarks(BigEndianReader reader, uint marksAddress, MplsPlaylist playlist)
        {
            if (marksAddress == 0 || marksAddress >= reader.Length)
            {
                return;
            }

            reader.EnsureRange(marksAddress, 6);
            var sectionLength = reader.ReadUInt32(checked((int)marksAddress));
            var sectionEnd = checked((long)marksAddress + 4 + sectionLength);
            reader.EnsureRange(marksAddress, checked((int)(sectionEnd - marksAddress)));

            var position = checked((int)marksAddress + 4);
            var markCount = reader.ReadUInt16(position);
            position += 2;
            for (var index = 0; index < markCount; index++)
            {
                reader.EnsureRange(position, 14);
                position += 1;
                var markType = reader.ReadByte(position++);
                var playItemIndex = reader.ReadUInt16(position);
                position += 2;
                var timestamp = reader.ReadUInt32(position);
                position += 4;
                position += 2; // elementary stream packet id
                position += 4; // mark duration

                if (markType != 1 || playItemIndex >= playlist.PlayItems.Count)
                {
                    continue;
                }

                var priorTicks = playlist.PlayItems.Take(playItemIndex).Sum(item => item.DurationTicks);
                var playItem = playlist.PlayItems[playItemIndex];
                var relativeClock = Math.Max(0, (long)timestamp - playItem.InTime);
                var positionTicks = priorTicks + relativeClock * TimeSpan.TicksPerSecond / MplsClockRate;
                if (positionTicks <= playlist.DurationTicks &&
                    playlist.Chapters.All(chapter => chapter.PositionTicks != positionTicks))
                {
                    playlist.Chapters.Add(new MplsChapter
                    {
                        PlayItemIndex = playItemIndex,
                        Timestamp = timestamp,
                        PositionTicks = positionTicks
                    });
                }
            }

            playlist.Chapters.Sort((left, right) => left.PositionTicks.CompareTo(right.PositionTicks));
        }

        private static string ReadLanguage(BigEndianReader reader, int position, int end)
        {
            return position + 3 <= end ? reader.ReadAscii(position, 3).Trim('\0', ' ') : null;
        }

        private static bool IsVideo(byte codingType)
        {
            return codingType == 0x01 || codingType == 0x02 || codingType == 0x1b ||
                   codingType == 0x20 || codingType == 0x24 || codingType == 0xea;
        }

        private static bool IsAudio(byte codingType)
        {
            return codingType == 0x03 || codingType == 0x04 ||
                   codingType is >= 0x80 and <= 0x86 ||
                   codingType == 0xa1 || codingType == 0xa2;
        }

        private sealed class BigEndianReader
        {
            private readonly byte[] _data;

            public BigEndianReader(byte[] data)
            {
                _data = data;
            }

            public int Length => _data.Length;

            public byte ReadByte(int offset)
            {
                EnsureRange(offset, 1);
                return _data[offset];
            }

            public ushort ReadUInt16(int offset)
            {
                EnsureRange(offset, 2);
                return (ushort)((_data[offset] << 8) | _data[offset + 1]);
            }

            public uint ReadUInt32(int offset)
            {
                EnsureRange(offset, 4);
                return ((uint)_data[offset] << 24) |
                       ((uint)_data[offset + 1] << 16) |
                       ((uint)_data[offset + 2] << 8) |
                       _data[offset + 3];
            }

            public string ReadAscii(int offset, int count)
            {
                EnsureRange(offset, count);
                return Encoding.ASCII.GetString(_data, offset, count);
            }

            public void EnsureRange(long offset, int count)
            {
                if (offset < 0 || count < 0 || offset > _data.Length - count)
                {
                    throw new EndOfStreamException(
                        $"MPLS read at offset {offset} for {count} bytes exceeds {_data.Length} bytes.");
                }
            }
        }
    }
}
