namespace AngleSharp.Renderer;

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;

/// <summary>
/// Unwraps the web font formats WOFF (https://www.w3.org/TR/WOFF/) and WOFF2
/// (https://www.w3.org/TR/WOFF2/) into the plain TrueType/OpenType (sfnt) data Skia reads.
/// </summary>
/// <remarks>
/// WOFF only compresses each table with zlib. WOFF2 compresses the whole table stream with Brotli
/// and may additionally store `glyf`/`loca` and `hmtx` in a transformed form, which has to be
/// rebuilt into the regular tables. Font collections (`ttcf`) are not supported.
/// </remarks>
internal static class WebFontDecoder
{
    private const uint WoffSignature = 0x774F4646; // wOFF
    private const uint Woff2Signature = 0x774F4632; // wOF2

    /// <summary>
    /// Returns the font data as sfnt: WOFF and WOFF2 are decoded, anything else is returned as
    /// it is. Returns <see langword="false"/> for a WOFF/WOFF2 file that cannot be decoded.
    /// </summary>
    public static bool TryDecode(byte[] data, out byte[] sfnt)
    {
        sfnt = data;

        if (data.Length < 4)
        {
            return false;
        }

        var signature = BinaryPrimitives.ReadUInt32BigEndian(data);

        try
        {
            switch (signature)
            {
                case WoffSignature:
                    sfnt = DecodeWoff(data);
                    return true;
                case Woff2Signature:
                    sfnt = DecodeWoff2(data);
                    return true;
                default:
                    return true;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IndexOutOfRangeException or NotSupportedException or OverflowException or EndOfStreamException or KeyNotFoundException)
        {
            sfnt = [];
            return false;
        }
    }

    private static byte[] DecodeWoff(byte[] data)
    {
        var flavor = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(12));
        var tables = new List<(uint Tag, byte[] Data)>(numTables);

        for (var index = 0; index < numTables; index++)
        {
            var entry = data.AsSpan(44 + (index * 20), 20);
            var tag = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var compressedLength = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var originalLength = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            var stored = data.AsSpan(offset, compressedLength);

            if (compressedLength < originalLength)
            {
                using var input = new MemoryStream(stored.ToArray());
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                var table = new byte[originalLength];
                zlib.ReadExactly(table);
                tables.Add((tag, table));
            }
            else
            {
                tables.Add((tag, stored.ToArray()));
            }
        }

        return BuildSfnt(flavor, tables);
    }

    // The WOFF2 known-table list, indexed by the 6-bit tag field of a table directory entry.
    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    private static readonly uint GlyfTag = Tag("glyf");
    private static readonly uint LocaTag = Tag("loca");
    private static readonly uint HmtxTag = Tag("hmtx");
    private static readonly uint HeadTag = Tag("head");
    private static readonly uint HheaTag = Tag("hhea");
    private static readonly uint MaxpTag = Tag("maxp");

    private static uint Tag(string tag) =>
        ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    private sealed record Woff2Table(uint Tag, int TransformVersion, int OriginalLength, int TransformLength)
    {
        // For glyf/loca, version 0 is the transform and 3 the null transform; for every other
        // table it is the other way around.
        public bool IsTransformed => Tag == GlyfTag || Tag == LocaTag ? TransformVersion == 0 : TransformVersion != 0;

        public int StreamLength => IsTransformed ? TransformLength : OriginalLength;
    }

    private static byte[] DecodeWoff2(byte[] data)
    {
        var flavor = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));

        if (flavor == Tag("ttcf"))
        {
            throw new NotSupportedException("WOFF2 font collections are not supported.");
        }

        var numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(12));
        var totalCompressedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
        var reader = new ByteReader(data, 48);
        var directory = new List<Woff2Table>(numTables);

        for (var index = 0; index < numTables; index++)
        {
            var flags = reader.ReadByte();
            var tagIndex = flags & 0x3F;
            var tag = tagIndex == 0x3F ? reader.ReadUInt32() : Tag(KnownTags[tagIndex]);
            var transformVersion = (flags >> 6) & 0x03;
            var originalLength = (int)reader.ReadUIntBase128();
            var table = new Woff2Table(tag, transformVersion, originalLength, 0);

            if (table.IsTransformed)
            {
                table = table with { TransformLength = (int)reader.ReadUIntBase128() };
            }

            directory.Add(table);
        }

        var stream = new byte[directory.Sum(static table => (long)table.StreamLength)];

        using (var input = new MemoryStream(data, reader.Position, totalCompressedSize))
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
        {
            brotli.ReadExactly(stream);
        }

        var raw = new Dictionary<uint, byte[]>();
        var offset = 0;

        foreach (var table in directory)
        {
            raw[table.Tag] = stream.AsSpan(offset, table.StreamLength).ToArray();
            offset += table.StreamLength;
        }

        var tables = new Dictionary<uint, byte[]>(raw);
        short[]? glyphXMins = null;
        var indexFormat = raw.TryGetValue(HeadTag, out var head) ? BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) : (short)0;

        if (directory.FirstOrDefault(static table => table.Tag == GlyfTag) is { IsTransformed: true })
        {
            var (glyf, loca, xMins) = ReconstructGlyf(raw[GlyfTag], indexFormat);
            tables[GlyfTag] = glyf;
            tables[LocaTag] = loca;
            glyphXMins = xMins;
        }

        if (directory.FirstOrDefault(static table => table.Tag == HmtxTag) is { IsTransformed: true })
        {
            var numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(raw[MaxpTag].AsSpan(4));
            var numberOfHMetrics = BinaryPrimitives.ReadUInt16BigEndian(raw[HheaTag].AsSpan(34));
            glyphXMins ??= ReadGlyphXMins(tables[GlyfTag], tables[LocaTag], numGlyphs, indexFormat);
            tables[HmtxTag] = ReconstructHmtx(raw[HmtxTag], numGlyphs, numberOfHMetrics, glyphXMins);
        }

        return BuildSfnt(flavor, directory.Select(table => (table.Tag, tables[table.Tag])).ToList());
    }

    /// <summary>
    /// Rebuilds `glyf` and `loca` from the WOFF2 transformed glyph table (section 5.1).
    /// </summary>
    private static (byte[] Glyf, byte[] Loca, short[] XMins) ReconstructGlyf(byte[] transformed, short indexFormat)
    {
        var header = new ByteReader(transformed, 0);
        header.ReadUInt16(); // reserved
        var optionFlags = header.ReadUInt16();
        var numGlyphs = header.ReadUInt16();
        header.ReadUInt16(); // indexFormat, already known from head
        var nContourStreamSize = (int)header.ReadUInt32();
        var nPointsStreamSize = (int)header.ReadUInt32();
        var flagStreamSize = (int)header.ReadUInt32();
        var glyphStreamSize = (int)header.ReadUInt32();
        var compositeStreamSize = (int)header.ReadUInt32();
        var bboxStreamSize = (int)header.ReadUInt32();
        var instructionStreamSize = (int)header.ReadUInt32();

        var position = header.Position;
        ByteReader NextStream(int size)
        {
            var stream = new ByteReader(transformed, position);
            position += size;
            return stream;
        }

        var nContourStream = NextStream(nContourStreamSize);
        var nPointsStream = NextStream(nPointsStreamSize);
        var flagStream = NextStream(flagStreamSize);
        var glyphStream = NextStream(glyphStreamSize);
        var compositeStream = NextStream(compositeStreamSize);
        var bboxBitmapLength = 4 * ((numGlyphs + 31) / 32);
        var bboxBitmap = transformed.AsSpan(position, bboxBitmapLength).ToArray();
        var bboxStream = new ByteReader(transformed, position + bboxBitmapLength);
        position += bboxStreamSize;
        var instructionStream = NextStream(instructionStreamSize);
        var overlapBitmap = (optionFlags & 1) != 0 ? transformed.AsSpan(position, (numGlyphs + 7) / 8).ToArray() : null;

        using var glyf = new MemoryStream();
        var offsets = new uint[numGlyphs + 1];
        var xMins = new short[numGlyphs];

        for (var glyph = 0; glyph < numGlyphs; glyph++)
        {
            offsets[glyph] = (uint)glyf.Length;
            var numberOfContours = nContourStream.ReadInt16();
            var hasBbox = (bboxBitmap[glyph >> 3] & (0x80 >> (glyph & 7))) != 0;

            if (numberOfContours == 0)
            {
                continue;
            }

            var output = new BigEndianWriter();

            if (numberOfContours > 0)
            {
                // Simple glyph: point counts, then one flag and a variable-length coordinate
                // triplet per point.
                var endPoints = new ushort[numberOfContours];
                var pointCount = 0;

                for (var contour = 0; contour < numberOfContours; contour++)
                {
                    pointCount += Read255UInt16(ref nPointsStream);
                    endPoints[contour] = (ushort)(pointCount - 1);
                }

                var onCurve = new bool[pointCount];
                var xs = new int[pointCount];
                var ys = new int[pointCount];
                int x = 0, y = 0;

                for (var point = 0; point < pointCount; point++)
                {
                    var flag = flagStream.ReadByte();
                    onCurve[point] = (flag & 0x80) == 0;
                    var (dx, dy) = DecodeTriplet(flag & 0x7F, ref glyphStream);
                    x += dx;
                    y += dy;
                    xs[point] = x;
                    ys[point] = y;
                }

                var instructionLength = Read255UInt16(ref glyphStream);
                var instructions = instructionStream.ReadBytes(instructionLength);
                short xMin, yMin, xMax, yMax;

                if (hasBbox)
                {
                    (xMin, yMin, xMax, yMax) = (bboxStream.ReadInt16(), bboxStream.ReadInt16(), bboxStream.ReadInt16(), bboxStream.ReadInt16());
                }
                else
                {
                    (xMin, yMin, xMax, yMax) = pointCount == 0
                        ? ((short)0, (short)0, (short)0, (short)0)
                        : ((short)xs.Min(), (short)ys.Min(), (short)xs.Max(), (short)ys.Max());
                }

                var overlaps = overlapBitmap is not null && (overlapBitmap[glyph >> 3] & (0x80 >> (glyph & 7))) != 0;
                output.WriteInt16(numberOfContours);
                output.WriteInt16(xMin);
                output.WriteInt16(yMin);
                output.WriteInt16(xMax);
                output.WriteInt16(yMax);

                foreach (var endPoint in endPoints)
                {
                    output.WriteUInt16(endPoint);
                }

                output.WriteUInt16((ushort)instructionLength);
                output.WriteBytes(instructions);

                // Flags without the repeat/short-vector compression: every coordinate is a
                // 16-bit delta, which keeps the encoding simple and is valid TrueType.
                for (var point = 0; point < pointCount; point++)
                {
                    var flag = (byte)(onCurve[point] ? 0x01 : 0x00);

                    if (point == 0 && overlaps)
                    {
                        flag |= 0x40;
                    }

                    output.WriteByte(flag);
                }

                for (var point = 0; point < pointCount; point++)
                {
                    output.WriteInt16((short)(xs[point] - (point == 0 ? 0 : xs[point - 1])));
                }

                for (var point = 0; point < pointCount; point++)
                {
                    output.WriteInt16((short)(ys[point] - (point == 0 ? 0 : ys[point - 1])));
                }

                xMins[glyph] = xMin;
            }
            else
            {
                // Composite glyph: the component records are stored verbatim; the bounding box is
                // always explicit.
                var bbox = (XMin: bboxStream.ReadInt16(), YMin: bboxStream.ReadInt16(), XMax: bboxStream.ReadInt16(), YMax: bboxStream.ReadInt16());
                var components = new BigEndianWriter();
                var haveInstructions = false;
                ushort flags;

                do
                {
                    flags = compositeStream.ReadUInt16();
                    components.WriteUInt16(flags);
                    components.WriteBytes(compositeStream.ReadBytes(2)); // glyph index
                    components.WriteBytes(compositeStream.ReadBytes((flags & 0x0001) != 0 ? 4 : 2));

                    if ((flags & 0x0008) != 0)
                    {
                        components.WriteBytes(compositeStream.ReadBytes(2));
                    }
                    else if ((flags & 0x0040) != 0)
                    {
                        components.WriteBytes(compositeStream.ReadBytes(4));
                    }
                    else if ((flags & 0x0080) != 0)
                    {
                        components.WriteBytes(compositeStream.ReadBytes(8));
                    }

                    haveInstructions |= (flags & 0x0100) != 0;
                }
                while ((flags & 0x0020) != 0);

                output.WriteInt16(-1);
                output.WriteInt16(bbox.XMin);
                output.WriteInt16(bbox.YMin);
                output.WriteInt16(bbox.XMax);
                output.WriteInt16(bbox.YMax);
                output.WriteBytes(components.ToArray());

                if (haveInstructions)
                {
                    var instructionLength = Read255UInt16(ref glyphStream);
                    output.WriteUInt16((ushort)instructionLength);
                    output.WriteBytes(instructionStream.ReadBytes(instructionLength));
                }

                xMins[glyph] = bbox.XMin;
            }

            glyf.Write(output.ToArray());

            // Glyphs are 4-byte aligned, which also keeps short loca offsets even.
            while (glyf.Length % 4 != 0)
            {
                glyf.WriteByte(0);
            }
        }

        offsets[numGlyphs] = (uint)glyf.Length;
        var loca = new BigEndianWriter();

        foreach (var glyphOffset in offsets)
        {
            if (indexFormat == 0)
            {
                loca.WriteUInt16((ushort)(glyphOffset / 2));
            }
            else
            {
                loca.WriteUInt32(glyphOffset);
            }
        }

        return (glyf.ToArray(), loca.ToArray(), xMins);
    }

    /// <summary>
    /// The coordinate delta of one point, from its flag and 1-4 bytes of the glyph stream
    /// (WOFF2 section 5.2, "triplet encoding").
    /// </summary>
    private static (int Dx, int Dy) DecodeTriplet(int flag, ref ByteReader stream)
    {
        static int WithSign(int flag, int value) => (flag & 1) != 0 ? value : -value;

        if (flag < 10)
        {
            return (0, WithSign(flag, ((flag & 14) << 7) + stream.ReadByte()));
        }

        if (flag < 20)
        {
            return (WithSign(flag, (((flag - 10) & 14) << 7) + stream.ReadByte()), 0);
        }

        if (flag < 84)
        {
            var b0 = flag - 20;
            var b1 = stream.ReadByte();
            return (WithSign(flag, 1 + (b0 & 0x30) + (b1 >> 4)), WithSign(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F)));
        }

        if (flag < 120)
        {
            var b0 = flag - 84;
            var first = stream.ReadByte();
            var second = stream.ReadByte();
            return (WithSign(flag, 1 + ((b0 / 12) << 8) + first), WithSign(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + second));
        }

        if (flag < 124)
        {
            var first = stream.ReadByte();
            var second = stream.ReadByte();
            var third = stream.ReadByte();
            return (WithSign(flag, (first << 4) + (second >> 4)), WithSign(flag >> 1, ((second & 0x0F) << 8) + third));
        }

        var x = stream.ReadUInt16();
        var y = stream.ReadUInt16();
        return (WithSign(flag, x), WithSign(flag >> 1, y));
    }

    private static int Read255UInt16(ref ByteReader stream)
    {
        var code = stream.ReadByte();

        return code switch
        {
            253 => stream.ReadUInt16(),
            254 => stream.ReadByte() + 506,
            255 => stream.ReadByte() + 253,
            _ => code,
        };
    }

    /// <summary>
    /// Rebuilds `hmtx` from its WOFF2 transformed form (section 5.4), where left side bearings
    /// equal to the glyph's xMin may be omitted.
    /// </summary>
    private static byte[] ReconstructHmtx(byte[] transformed, int numGlyphs, int numberOfHMetrics, short[] xMins)
    {
        var reader = new ByteReader(transformed, 0);
        var flags = reader.ReadByte();
        var advances = new ushort[numberOfHMetrics];

        for (var index = 0; index < numberOfHMetrics; index++)
        {
            advances[index] = reader.ReadUInt16();
        }

        var bearings = new short[numGlyphs];

        for (var index = 0; index < numberOfHMetrics; index++)
        {
            bearings[index] = (flags & 1) != 0 ? xMins[index] : reader.ReadInt16();
        }

        for (var index = numberOfHMetrics; index < numGlyphs; index++)
        {
            bearings[index] = (flags & 2) != 0 ? xMins[index] : reader.ReadInt16();
        }

        var output = new BigEndianWriter();

        for (var index = 0; index < numGlyphs; index++)
        {
            if (index < numberOfHMetrics)
            {
                output.WriteUInt16(advances[index]);
            }

            output.WriteInt16(bearings[index]);
        }

        return output.ToArray();
    }

    private static short[] ReadGlyphXMins(byte[] glyf, byte[] loca, int numGlyphs, short indexFormat)
    {
        var xMins = new short[numGlyphs];

        int Offset(int glyph) => indexFormat == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(glyph * 2)) * 2
            : (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(glyph * 4));

        for (var glyph = 0; glyph < numGlyphs; glyph++)
        {
            var start = Offset(glyph);
            xMins[glyph] = Offset(glyph + 1) > start ? BinaryPrimitives.ReadInt16BigEndian(glyf.AsSpan(start + 2)) : (short)0;
        }

        return xMins;
    }

    /// <summary>
    /// Writes an sfnt: the offset table, the table records sorted by tag, and the 4-byte aligned
    /// tables with their checksums.
    /// </summary>
    private static byte[] BuildSfnt(uint flavor, IList<(uint Tag, byte[] Data)> tables)
    {
        var sorted = tables.OrderBy(static table => table.Tag).ToList();
        var numTables = (ushort)sorted.Count;
        var entrySelector = (ushort)Math.Floor(Math.Log2(Math.Max(1, (int)numTables)));
        var searchRange = (ushort)((1 << entrySelector) * 16);
        var output = new BigEndianWriter();

        output.WriteUInt32(flavor);
        output.WriteUInt16(numTables);
        output.WriteUInt16(searchRange);
        output.WriteUInt16(entrySelector);
        output.WriteUInt16((ushort)((numTables * 16) - searchRange));

        var offset = 12 + (16 * numTables);

        foreach (var (tag, data) in sorted)
        {
            output.WriteUInt32(tag);
            output.WriteUInt32(Checksum(data));
            output.WriteUInt32((uint)offset);
            output.WriteUInt32((uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }

        foreach (var (_, data) in sorted)
        {
            output.WriteBytes(data);

            for (var padding = data.Length; (padding & 3) != 0; padding++)
            {
                output.WriteByte(0);
            }
        }

        return output.ToArray();
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;

        for (var index = 0; index < data.Length; index += 4)
        {
            uint word = 0;

            for (var part = 0; part < 4; part++)
            {
                word = (word << 8) | (index + part < data.Length ? data[index + part] : (byte)0);
            }

            sum = unchecked(sum + word);
        }

        return sum;
    }

    /// <summary>A big-endian reader over a byte array.</summary>
    private struct ByteReader(byte[] data, int position)
    {
        public int Position { get; private set; } = position;

        public byte ReadByte() => data[Position++];

        public ushort ReadUInt16()
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(Position));
            Position += 2;
            return value;
        }

        public short ReadInt16() => (short)ReadUInt16();

        public uint ReadUInt32()
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(Position));
            Position += 4;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            var value = data.AsSpan(Position, count).ToArray();
            Position += count;
            return value;
        }

        /// <summary>WOFF2's variable-length unsigned integer (at most 5 bytes, 7 bits each).</summary>
        public uint ReadUIntBase128()
        {
            uint value = 0;

            for (var index = 0; index < 5; index++)
            {
                var part = ReadByte();

                if (index == 0 && part == 0x80)
                {
                    throw new InvalidDataException("A UIntBase128 value may not have leading zeros.");
                }

                value = checked((value << 7) | (uint)(part & 0x7F));

                if ((part & 0x80) == 0)
                {
                    return value;
                }
            }

            throw new InvalidDataException("A UIntBase128 value is longer than 5 bytes.");
        }
    }

    /// <summary>A big-endian writer.</summary>
    private sealed class BigEndianWriter
    {
        private readonly MemoryStream _stream = new();

        public void WriteByte(byte value) => _stream.WriteByte(value);

        public void WriteBytes(byte[] value) => _stream.Write(value);

        public void WriteUInt16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void WriteInt16(short value) => WriteUInt16((ushort)value);

        public void WriteUInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
