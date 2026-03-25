using System.Collections.Concurrent;
using System.Threading;
using CHMsharp;

namespace EbookScanner.Core.Extractors;

internal readonly record struct ChmObjectLocation(
    string Path,
    int ContentSection,
    long Offset,
    int Length);

internal interface IChmCompressedObjectReaderFactory
{
    IChmCompressedObjectReader Create(
        string filePath,
        ulong dataOffset,
        IReadOnlyDictionary<string, ChmObjectLocation> entries);
}

internal interface IChmCompressedObjectReader : IDisposable
{
    bool TryRead(ChmObjectLocation entry, out byte[] data);
}

public sealed partial class ChmMetadataExtractor
{
    private sealed class ChmSharpCompressedObjectReaderFactory : IChmCompressedObjectReaderFactory
    {
        public IChmCompressedObjectReader Create(
            string filePath,
            ulong dataOffset,
            IReadOnlyDictionary<string, ChmObjectLocation> entries) =>
            ChmSharpCompressedObjectReader.TryCreate(filePath, dataOffset, entries, out var reader)
                ? reader
                : NullChmCompressedObjectReader.Instance;
    }

    private sealed class NullChmCompressedObjectReader : IChmCompressedObjectReader
    {
        public static readonly NullChmCompressedObjectReader Instance = new();

        public void Dispose()
        {
        }

        public bool TryRead(ChmObjectLocation entry, out byte[] data)
        {
            data = [];
            return false;
        }
    }

    private sealed class ChmSharpCompressedObjectReader : IChmCompressedObjectReader
    {
        private const int CachedBlockCount = 5;

        private readonly FileStream _stream;
        private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
        private ChmFileInfo _file;
        private bool _disposed;

        private ChmSharpCompressedObjectReader(FileStream stream, ChmFileInfo file)
        {
            _stream = stream;
            _file = file;
        }

        public static bool TryCreate(
            string filePath,
            ulong dataOffset,
            IReadOnlyDictionary<string, ChmObjectLocation> entries,
            out ChmSharpCompressedObjectReader reader)
        {
            reader = null!;

            if (!TryGetSectionZeroEntry(entries, NormalizeInternalPath(Storage.CHMU_RESET_TABLE), out var resetTableEntry) ||
                !TryGetSectionZeroEntry(entries, NormalizeInternalPath(Storage.CHMU_LZXC_CONTROLDATA), out var controlEntry) ||
                !TryGetSectionZeroEntry(entries, NormalizeInternalPath(Storage.CHMU_CONTENT), out var contentEntry))
            {
                return false;
            }

            FileStream? stream = null;

            try
            {
                stream = File.OpenRead(filePath);
                if (!TryReadSectionZeroObjectBytes(stream, dataOffset, resetTableEntry, out var resetTableBytes) ||
                    !TryReadSectionZeroObjectBytes(stream, dataOffset, controlEntry, out var controlBytes))
                {
                    stream.Dispose();
                    return false;
                }

                if (!TryParseCompressionParameters(resetTableBytes, controlBytes, out var resetTable, out var controlData))
                {
                    stream.Dispose();
                    return false;
                }

                uint halfWindow = controlData.windowSize / 2;
                uint resetBlockCount = halfWindow == 0
                    ? 0
                    : (controlData.resetInterval / halfWindow) * controlData.windowsPerReset;
                if (resetBlockCount == 0)
                {
                    stream.Dispose();
                    return false;
                }

                var file = new ChmFileInfo
                {
                    fd = stream,
                    mutex = new Mutex(),
                    lzx_mutex = new Mutex(),
                    cache_mutex = new Mutex(),
                    data_offset = dataOffset,
                    rt_unit = ToUnitInfo(resetTableEntry),
                    cn_unit = ToUnitInfo(contentEntry),
                    reset_table = resetTable,
                    compression_enabled = true,
                    window_size = controlData.windowSize,
                    reset_interval = controlData.resetInterval,
                    reset_blkcount = resetBlockCount,
                    lzx_last_block = -1,
                    cache_num_blocks = CachedBlockCount,
                    cache_blocks = new byte[CachedBlockCount][],
                    cache_block_indices = new ulong[CachedBlockCount],
                };

                reader = new ChmSharpCompressedObjectReader(stream, file);
                return true;
            }
            catch
            {
                stream?.Dispose();
                return false;
            }
        }

        public bool TryRead(ChmObjectLocation entry, out byte[] data)
        {
            data = [];
            if (_disposed || entry.ContentSection == 0 || entry.Length <= 0)
                return false;

            if (_cache.TryGetValue(entry.Path, out var cached))
            {
                data = cached;
                return true;
            }

            var buffer = new byte[entry.Length];
            long read = RetrieveObject(ToUnitInfo(entry), buffer);
            if (read != entry.Length)
                return false;

            data = buffer;
            _cache[entry.Path] = buffer;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_file.lzx_state is not null)
                Lzx.LZXteardown(_file.lzx_state);

            _stream.Dispose();
            _file.mutex?.Dispose();
            _file.lzx_mutex?.Dispose();
            _file.cache_mutex?.Dispose();
        }

        private long RetrieveObject(ChmUnitInfo unitInfo, byte[] buffer)
        {
            if (_file.fd is null)
                return 0;

            if (unitInfo.space == Storage.CHM_UNCOMPRESSED)
                return Storage.FetchBytes(ref _file, ref buffer, _file.data_offset + unitInfo.start, (long)unitInfo.length);

            if (!_file.compression_enabled)
                return 0;

            long remaining = (long)unitInfo.length;
            ulong address = 0;
            ulong bufferOffset = 0;
            long total = 0;

            while (remaining > 0)
            {
                long swath = Lzxc.DecompressRegion(ref _file, ref buffer, bufferOffset, unitInfo.start + address, remaining);
                if (swath <= 0)
                    return total;

                total += swath;
                remaining -= swath;
                address += (ulong)swath;
                bufferOffset += (ulong)swath;
            }

            return total;
        }

        private static bool TryParseCompressionParameters(
            byte[] resetTableBytes,
            byte[] controlBytes,
            out chmLzxcResetTable resetTable,
            out chmLzxcControlData controlData)
        {
            resetTable = default;
            controlData = default;

            uint resetPos = 0;
            uint resetRemain = (uint)resetTableBytes.Length;
            if (!Lzxc.UnmarshalLzxcResetTable(ref resetTableBytes, ref resetPos, ref resetRemain, ref resetTable) ||
                resetTable.block_len == 0 ||
                resetTable.block_count == 0)
            {
                return false;
            }

            uint controlPos = 0;
            uint controlRemain = (uint)controlBytes.Length;
            if (!Lzxc.UnmarshalLzxcControlData(ref controlBytes, ref controlPos, ref controlRemain, ref controlData) ||
                controlData.windowSize < 2 ||
                controlData.resetInterval == 0)
            {
                return false;
            }

            return true;
        }

        private static bool TryGetSectionZeroEntry(
            IReadOnlyDictionary<string, ChmObjectLocation> entries,
            string path,
            out ChmObjectLocation entry)
        {
            if (entries.TryGetValue(path, out entry) && entry.ContentSection == 0 && entry.Length > 0)
                return true;

            entry = default;
            return false;
        }

        private static bool TryReadSectionZeroObjectBytes(
            FileStream stream,
            ulong dataOffset,
            ChmObjectLocation entry,
            out byte[] data)
        {
            data = [];

            long absoluteOffset = (long)dataOffset + entry.Offset;
            if (absoluteOffset < 0 || absoluteOffset + entry.Length > stream.Length)
                return false;

            stream.Seek(absoluteOffset, SeekOrigin.Begin);
            data = new byte[entry.Length];
            stream.ReadExactly(data);
            return true;
        }

        private static ChmUnitInfo ToUnitInfo(ChmObjectLocation entry) =>
            new()
            {
                path = entry.Path,
                space = entry.ContentSection,
                start = (ulong)entry.Offset,
                length = (ulong)entry.Length,
            };
    }
}
