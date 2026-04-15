using System.Text;

namespace MarkItDown.Core.Utilities;

/// <summary>
/// Parser for CHM (Microsoft Compiled HTML Help) files.
/// Reads the ITSF binary container, walks the ITSP B-tree directory, and exposes
/// file extraction for both section 0 (uncompressed) and section 1 (LZX-compressed) content.
/// </summary>
internal sealed class ChmParser
{
    // ── Constants ────────────────────────────────────────────────────────────

    private static readonly byte[] ItsfMagic = "ITSF"u8.ToArray();
    private static readonly byte[] ItspMagic = "ITSP"u8.ToArray();
    private static readonly byte[] PmglMagic = "PMGL"u8.ToArray();
    private static readonly byte[] LzxcMagic = "LZXC"u8.ToArray();

    // ── Fields ───────────────────────────────────────────────────────────────

    private readonly byte[] _data;

    // ITSF section table: two sections (0 = uncompressed, 1 = LZX-compressed).
    private long _section0Offset;

    // ITSP directory info.
    private long _directoryOffset;
    private int _blockSize;
    private int _firstPmglBlock;

    // Directory: maps lower-case file path → (section, offset, length).
    private readonly Dictionary<string, ChmEntry> _files = new(StringComparer.OrdinalIgnoreCase);

    // LZX decompression parameters (populated lazily on first section-1 read).
    private int _windowBits;
    private long _totalUncompressedSize;
    private long _resetIntervalSize;
    private long[] _compressedOffsets = [];
    private bool _lzxReady;
    private byte[]? _decompressedSection1;

    // ── Constructor ───────────────────────────────────────────────────────────

    private ChmParser(byte[] data)
    {
        _data = data;
    }

    // ── Public factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a CHM file from <paramref name="data"/>.
    /// Throws <see cref="InvalidDataException"/> if the data is not a valid CHM.
    /// </summary>
    public static ChmParser Parse(byte[] data)
    {
        var parser = new ChmParser(data);
        parser.ParseItsf();
        parser.ParseDirectory();
        return parser;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Document title extracted from the <c>#SYSTEM</c> metadata chunk, or <c>null</c>.</summary>
    public string? Title { get; private set; }

    /// <summary>All HTML/HTM file paths found in the directory (skips internal entries).</summary>
    public IEnumerable<string> HtmlFiles =>
        _files.Keys
              .Where(k => !k.StartsWith("::") && !k.StartsWith("#") && !k.StartsWith("$") &&
                          (k.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
                           k.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
              .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the raw bytes for the named file from the CHM.
    /// Returns <c>null</c> if the entry is not found.
    /// </summary>
    public byte[]? ReadFile(string name)
    {
        if (!_files.TryGetValue(name, out var entry))
            return null;

        if (entry.Section == 0)
            return ReadSection0(entry.Offset, entry.Length);

        EnsureLzxReady();
        return ReadSection1(entry.Offset, entry.Length);
    }

    // ── ITSF / ITSP parsing ──────────────────────────────────────────────────

    private void ParseItsf()
    {
        if (_data.Length < 88)
            throw new InvalidDataException("File is too small to be a valid CHM.");

        if (!StartsWith(_data, 0, ItsfMagic))
            throw new InvalidDataException("Not a CHM file (missing ITSF signature).");

        int version = ReadI32(_data, 4);
        if (version < 2 || version > 3)
            throw new InvalidDataException($"Unsupported ITSF version {version}.");

        // ITSF v2 header is 88 bytes; v3 is 96 bytes.
        // After two 16-byte GUIDs at [24..55]:
        //   [56..63] = offset of unnamed pre-directory section (ignored)
        //   [64..71] = length of unnamed pre-directory section (ignored)
        //   [72..79] = offset of the ITSP directory
        //   [80..87] = length of the ITSP directory
        //   [88..95] = content (section-0) start offset  [v3 only]
        long dirOffset = ReadI64(_data, 72);
        long dirLen = ReadI64(_data, 80);

        _directoryOffset = dirOffset;

        // Section-0 content (uncompressed files) starts immediately after the directory
        // for v2; for v3 the exact offset is stored at byte 88.
        _section0Offset = version >= 3 && _data.Length >= 96
            ? ReadI64(_data, 88)
            : dirOffset + dirLen;

        ParseItsp();
    }

    private void ParseItsp()
    {
        int at = (int)_directoryOffset;
        if (at + 84 > _data.Length || !StartsWith(_data, at, ItspMagic))
            throw new InvalidDataException("ITSP directory header not found.");

        // version = ReadI32(_data, at + 4); // typically 1
        _blockSize = ReadI32(_data, at + 16);
        _firstPmglBlock = ReadI32(_data, at + 32);  // first PMGL leaf block index (+28 is the PMGI root)

        // Blocks start immediately after the 84-byte ITSP header.
        // (The block array starts at directoryOffset + headerSize.)
        // ITSP header size is at at + 8.
        int headerSize = ReadI32(_data, at + 8);
        _directoryOffset += headerSize;  // now points to block 0
    }

    private void ParseDirectory()
    {
        int block = _firstPmglBlock;
        while (block >= 0)
        {
            int blockStart = (int)_directoryOffset + block * _blockSize;
            if (blockStart + 20 > _data.Length || !StartsWith(_data, blockStart, PmglMagic))
                break;

            int quickRefSize = ReadI32(_data, blockStart + 4);
            int nextBlock = ReadI32(_data, blockStart + 16);  // +12 = prev chunk, +16 = next chunk
            // 16-byte PMGL header; entries start at blockStart + 20.
            // Quick-ref section occupies the last `quickRefSize + 2` bytes of the block.
            int dataEnd = blockStart + _blockSize - quickRefSize - 2;
            int pos = blockStart + 20;

            while (pos < dataEnd)
            {
                // Read entry: name, section, offset, length (all variable-length encoded).
                int nameLen = ReadVarInt(_data, ref pos);
                if (nameLen <= 0 || pos + nameLen > _data.Length) break;

                string name = Encoding.UTF8.GetString(_data, pos, nameLen);
                pos += nameLen;

                int section = ReadVarInt(_data, ref pos);
                long offset = ReadVarLong(_data, ref pos);
                long length = ReadVarLong(_data, ref pos);

                _files[name] = new ChmEntry(section, offset, length);
            }

            block = nextBlock;
        }

        // Extract title from the #SYSTEM metadata file.
        Title = ReadSystemTitle();
    }

    // ── #SYSTEM parsing ──────────────────────────────────────────────────────

    private string? ReadSystemTitle()
    {
        var systemBytes = ReadFile("#SYSTEM");
        if (systemBytes is null || systemBytes.Length < 4) return null;

        // #SYSTEM version (4 bytes), then a series of tag-length-value records.
        int pos = 4;
        while (pos + 4 <= systemBytes.Length)
        {
            int tag = ReadU16(systemBytes, pos);
            int length = ReadU16(systemBytes, pos + 2);
            pos += 4;

            if (pos + length > systemBytes.Length) break;

            if (tag == 3 && length > 0)  // tag 3 = window title
            {
                // Null-terminated ANSI string.
                int nullIdx = Array.IndexOf(systemBytes, (byte)0, pos, length);
                int strLen = nullIdx >= 0 ? nullIdx - pos : length;
                return Encoding.Default.GetString(systemBytes, pos, strLen);
            }

            pos += length;
        }

        return null;
    }

    // ── File reading ──────────────────────────────────────────────────────────

    private byte[] ReadSection0(long offset, long length)
    {
        long start = _section0Offset + offset;
        int len = (int)Math.Min(length, _data.Length - start);
        if (len <= 0) return [];

        var result = new byte[len];
        Array.Copy(_data, (int)start, result, 0, len);
        return result;
    }

    private byte[] ReadSection1(long offset, long length)
    {
        _decompressedSection1 ??= LzxDecoder.Decompress(
            GetCompressedContent(),
            _windowBits,
            _totalUncompressedSize,
            _resetIntervalSize,
            _compressedOffsets);

        long end = offset + length;
        if (end > _decompressedSection1.Length) end = _decompressedSection1.Length;
        int len = (int)(end - offset);
        if (len <= 0) return [];

        var result = new byte[len];
        Array.Copy(_decompressedSection1, (int)offset, result, 0, len);
        return result;
    }

    private byte[] GetCompressedContent()
        => ReadFile("::DataSpace/Storage/MSCompressed/Content") ?? [];

    // ── LZX initialisation ───────────────────────────────────────────────────

    private void EnsureLzxReady()
    {
        if (_lzxReady) return;

        // Read ControlData: ::DataSpace/Storage/MSCompressed/ControlData
        // Layout: [0..3] = size (DWORD count), [4..7] = 'LZXC', [8..11] = version,
        //         [12..15] = resetInterval (in 0x8000 blocks), [16..19] = windowSize (in 0x8000 blocks)
        const string controlPath = "::DataSpace/Storage/MSCompressed/ControlData";
        var ctrl = ReadFile(controlPath);
        if (ctrl is not null && ctrl.Length >= 20 && StartsWith(ctrl, 4, LzxcMagic))
        {
            int resetIntervalBlocks = ReadI32(ctrl, 12);
            int windowSizeBlocks = ReadI32(ctrl, 16);

            // Convert to bytes: multiply by 0x8000 = 32768.
            _resetIntervalSize = (long)resetIntervalBlocks * 0x8000;
            int windowSizeBytes = windowSizeBlocks * 0x8000;

            // Window bits = log2(windowSizeBytes).
            _windowBits = 0;
            int tmp = windowSizeBytes;
            while (tmp > 1) { tmp >>= 1; _windowBits++; }
            if (_windowBits < 15) _windowBits = 15;
            if (_windowBits > 21) _windowBits = 21;
        }
        else
        {
            // Fallback: common defaults.
            _windowBits = 16;
            _resetIntervalSize = 0x8000;
        }

        // Read ResetTable. The GUID in the Transform path varies across CHM files,
        // so locate the entry dynamically instead of using a hardcoded path.
        const string resetTableSuffix = "/InstanceData/ResetTable";
        const string transformPrefix = "::DataSpace/Storage/MSCompressed/Transform/";
        string? resetTablePath = _files.Keys.FirstOrDefault(k =>
            k.StartsWith(transformPrefix, StringComparison.OrdinalIgnoreCase) &&
            k.EndsWith(resetTableSuffix, StringComparison.OrdinalIgnoreCase));

        var rt = resetTablePath is not null ? ReadFile(resetTablePath) : null;
        if (rt is not null && rt.Length >= 28)
        {
            // version     = ReadI32(rt, 0);  // 2
            int numEntries = ReadI32(rt, 4);
            // entrySize   = ReadI32(rt, 8);  // 8
            // tableOffset = ReadI32(rt, 12); // offset to first entry (usually 40)
            _totalUncompressedSize = ReadI64(rt, 16);
            // compressedSize = ReadI64(rt, 24);
            // block_len describes the 32 KB frame granularity used by the reset table.
            // Real CHM files commonly expose one table entry per frame even when the
            // actual LZX reset interval is larger (for example, ControlData can report
            // 64 KB while ResetTable entries still appear every 32 KB). Keep the
            // ControlData-derived reset interval when present.
            if (_resetIntervalSize <= 0)
                _resetIntervalSize = ReadI64(rt, 32);

            int tableOffset = ReadI32(rt, 12);
            _compressedOffsets = new long[numEntries];
            for (int i = 0; i < numEntries && tableOffset + (i + 1) * 8 <= rt.Length; i++)
                _compressedOffsets[i] = ReadI64(rt, tableOffset + i * 8);
        }
        else
        {
            _totalUncompressedSize = 0;
            _compressedOffsets = [];
        }

        _lzxReady = true;
    }

    // ── Binary reading helpers ────────────────────────────────────────────────

    private static bool StartsWith(byte[] data, int offset, byte[] magic)
    {
        if (offset + magic.Length > data.Length) return false;
        for (int i = 0; i < magic.Length; i++)
            if (data[offset + i] != magic[i]) return false;
        return true;
    }

    private static int ReadI32(byte[] data, int offset) =>
        (int)((uint)data[offset]
            | ((uint)data[offset + 1] << 8)
            | ((uint)data[offset + 2] << 16)
            | ((uint)data[offset + 3] << 24));

    private static long ReadI64(byte[] data, int offset)
    {
        uint lo = (uint)data[offset]
               | ((uint)data[offset + 1] << 8)
               | ((uint)data[offset + 2] << 16)
               | ((uint)data[offset + 3] << 24);
        uint hi = (uint)data[offset + 4]
               | ((uint)data[offset + 5] << 8)
               | ((uint)data[offset + 6] << 16)
               | ((uint)data[offset + 7] << 24);
        return (long)((ulong)lo | ((ulong)hi << 32));
    }

    private static int ReadU16(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8);

    /// <summary>Reads a variable-length encoded integer (ENCINT) from <paramref name="data"/> at <paramref name="pos"/>.</summary>
    private static int ReadVarInt(byte[] data, ref int pos)
    {
        int value = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return value;
    }

    /// <summary>Reads a variable-length encoded long integer (ENCINT) from <paramref name="data"/> at <paramref name="pos"/>.</summary>
    private static long ReadVarLong(byte[] data, ref int pos)
    {
        long value = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            value = (value << 7) | (byte)(b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return value;
    }

    // ── Inner types───────────────────────────────────────────────────────────

    private readonly record struct ChmEntry(int Section, long Offset, long Length);
}
