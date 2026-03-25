namespace MarkItDown.Core.Utilities;

/// <summary>
/// LZX decompressor for CHM (Microsoft Compiled HTML Help) files.
/// Mirrors libmspack's frame/block decoding semantics closely enough for
/// CHM section-1 streams, but deliberately omits the E8 translation step.
/// </summary>
internal static class LzxDecoder
{
    private const int FrameSize = 0x8000;
    private const int NumChars = 256;
    private const int NumPrimaryLengths = 7;
    private const int NumSecondaryLengths = 249;
    private const int MinMatch = 2;

    private static readonly int[] PositionSlotsByWindowBits = [30, 32, 34, 36, 38, 42, 50];
    private static readonly byte[] ExtraBits = BuildExtraBits(50);
    private static readonly uint[] PositionBase = BuildPositionBase(50);

    private enum BlockType
    {
        Invalid = 0,
        Verbatim = 1,
        Aligned = 2,
        Uncompressed = 3,
    }

    /// <summary>
    /// Decompresses the full CHM section-1 content stream.
    /// </summary>
    public static byte[] Decompress(
        byte[] compressedContent,
        int windowBits,
        long totalUncompressedSize,
        long resetIntervalSize,
        long[] compressedOffsets)
    {
        if (totalUncompressedSize <= 0 || compressedContent.Length == 0)
            return [];

        if (totalUncompressedSize > int.MaxValue)
            throw new InvalidDataException("CHM section-1 is too large to decompress into a single byte array.");

        var output = new byte[(int)totalUncompressedSize];
        DecompressStream(
            compressedContent,
            compressedContent.Length,
            windowBits,
            output.Length,
            checked((int)Math.Min(Math.Max(resetIntervalSize, 0), int.MaxValue)),
            compressedOffsets,
            output,
            0);

        return output;
    }

    private static void DecompressStream(
        byte[] input,
        int inputLength,
        int windowBits,
        int outputSize,
        int resetIntervalSize,
        long[] compressedOffsets,
        byte[] output,
        int outputOffset)
    {
        int windowSize = 1 << windowBits;
        int numPositionSlots = GetNumPositionSlots(windowBits);
        int numMainSymbols = NumChars + (numPositionSlots << 3);
        int resetIntervalFrames = resetIntervalSize > 0 ? Math.Max(1, resetIntervalSize / FrameSize) : 0;
        int startOffset = compressedOffsets.Length > 0
            ? checked((int)Math.Clamp(compressedOffsets[0], 0L, inputLength))
            : 0;

        var window = new byte[windowSize];
        var mainLens = new byte[numMainSymbols];
        var lengthLens = new byte[NumSecondaryLengths];
        var alignedLens = new byte[8];

        var br = new BitReader(input, startOffset, inputLength - startOffset);

        int outPos = 0;
        int frame = 0;
        int framePos = 0;
        int windowPos = 0;
        uint r0 = 1, r1 = 1, r2 = 1;
        bool headerRead = false;
        int blockRemaining = 0;
        int blockLength = 0;
        BlockType blockType = BlockType.Invalid;
        HuffmanDecoder? mainTree = null;
        HuffmanDecoder? lengthTree = null;
        HuffmanDecoder? alignedTree = null;
        bool lengthTreeEmpty = true;

        while (outPos < outputSize)
        {
            if (resetIntervalFrames > 0 && (frame % resetIntervalFrames) == 0)
            {
                r0 = r1 = r2 = 1;
                headerRead = false;
                blockRemaining = 0;
                blockLength = 0;
                blockType = BlockType.Invalid;
                mainTree = null;
                lengthTree = null;
                alignedTree = null;
                lengthTreeEmpty = true;
                Array.Clear(mainLens, 0, mainLens.Length);
                Array.Clear(lengthLens, 0, lengthLens.Length);
            }

            if (!headerRead)
            {
                if (br.ReadBits(1) != 0)
                {
                    br.ReadBits(16);
                    br.ReadBits(16);
                }

                headerRead = true;
            }

            int frameSize = Math.Min(FrameSize, outputSize - outPos);
            int bytesTodo = frameSize;
            int frameStart = framePos;

            while (bytesTodo > 0)
            {
                if (blockRemaining == 0)
                {
                    if (blockType == BlockType.Uncompressed && (blockLength & 1) != 0)
                        br.SkipByte();

                    blockType = (BlockType)br.ReadBits(3);
                    blockLength = blockRemaining = (int)((br.ReadBits(16) << 8) | br.ReadBits(8));

                    switch (blockType)
                    {
                        case BlockType.Aligned:
                            for (int i = 0; i < alignedLens.Length; i++)
                                alignedLens[i] = (byte)br.ReadBits(3);
                            alignedTree = HuffmanDecoder.Create(alignedLens, alignedLens.Length, allowEmpty: false);
                            goto case BlockType.Verbatim;

                        case BlockType.Verbatim:
                            ReadLengths(ref br, mainLens, 0, NumChars);
                            ReadLengths(ref br, mainLens, NumChars, numMainSymbols);
                            mainTree = HuffmanDecoder.Create(mainLens, numMainSymbols, allowEmpty: false);

                            ReadLengths(ref br, lengthLens, 0, NumSecondaryLengths);
                            lengthTree = HuffmanDecoder.Create(lengthLens, NumSecondaryLengths, allowEmpty: true);
                            lengthTreeEmpty = lengthTree.IsEmpty;
                            break;

                        case BlockType.Uncompressed:
                            br.AlignForUncompressed();
                            r0 = br.ReadUInt32LE();
                            r1 = br.ReadUInt32LE();
                            r2 = br.ReadUInt32LE();
                            break;

                        default:
                            throw new InvalidDataException($"Unknown LZX block type {(int)blockType} at outPos={outPos}.");
                    }
                }

                int thisRun = Math.Min(blockRemaining, bytesTodo);
                bytesTodo -= thisRun;
                blockRemaining -= thisRun;

                switch (blockType)
                {
                    case BlockType.Verbatim:
                    case BlockType.Aligned:
                        thisRun = DecodeCompressedRun(
                            ref br,
                            blockType,
                            thisRun,
                            frameStart,
                            outPos,
                            window,
                            ref windowPos,
                            windowSize,
                            mainTree!,
                            lengthTree,
                            lengthTreeEmpty,
                            alignedTree,
                            ref r0,
                            ref r1,
                            ref r2);

                        if (thisRun < 0)
                        {
                            int overrun = -thisRun;
                            if (overrun > blockRemaining)
                                throw new InvalidDataException("LZX match overran the end of the current block.");

                            blockRemaining -= overrun;
                        }
                        break;

                    case BlockType.Uncompressed:
                        br.ReadBytes(window, windowPos, thisRun);
                        windowPos += thisRun;
                        break;

                    default:
                        throw new InvalidDataException("Invalid LZX block state.");
                }
            }

            br.AlignToWordBoundary();
            CopyFrame(window, framePos, frameSize, output, outputOffset + outPos);

            outPos += frameSize;
            frame++;

            framePos += frameSize;
            if (framePos == windowSize)
                framePos = 0;

            if (windowPos == windowSize)
                windowPos = 0;
        }
    }

    private static int DecodeCompressedRun(
        ref BitReader br,
        BlockType blockType,
        int thisRun,
        int frameStart,
        int totalOutputSoFar,
        byte[] window,
        ref int windowPos,
        int windowSize,
        HuffmanDecoder mainTree,
        HuffmanDecoder? lengthTree,
        bool lengthTreeEmpty,
        HuffmanDecoder? alignedTree,
        ref uint r0,
        ref uint r1,
        ref uint r2)
    {
        while (thisRun > 0)
        {
            int mainElement = mainTree.Decode(ref br);
            if (mainElement < NumChars)
            {
                window[windowPos++] = (byte)mainElement;
                thisRun--;
                continue;
            }

            mainElement -= NumChars;

            int matchLength = mainElement & NumPrimaryLengths;
            if (matchLength == NumPrimaryLengths)
            {
                if (lengthTreeEmpty || lengthTree is null)
                    throw new InvalidDataException("LZX LENGTH symbol required but the length tree is empty.");

                matchLength += lengthTree.Decode(ref br);
            }

            matchLength += MinMatch;

            uint matchOffset = (uint)(mainElement >> 3);
            switch (matchOffset)
            {
                case 0:
                    matchOffset = r0;
                    break;

                case 1:
                    matchOffset = r1;
                    r1 = r0;
                    r0 = matchOffset;
                    break;

                case 2:
                    matchOffset = r2;
                    r2 = r0;
                    r0 = matchOffset;
                    break;

                default:
                    {
                        int posSlot = checked((int)matchOffset);
                        if ((uint)posSlot >= (uint)PositionBase.Length)
                            throw new InvalidDataException($"LZX position slot {posSlot} is out of range.");

                        int extra = posSlot >= 36 ? 17 : ExtraBits[posSlot];
                        matchOffset = PositionBase[posSlot] - 2;

                        if (extra >= 3 && blockType == BlockType.Aligned)
                        {
                            if (extra > 3)
                                matchOffset += br.ReadBits(extra - 3) << 3;

                            if (alignedTree is null)
                                throw new InvalidDataException("Aligned-offset block is missing its aligned tree.");

                            matchOffset += (uint)alignedTree.Decode(ref br);
                        }
                        else if (extra > 0)
                        {
                            matchOffset += br.ReadBits(extra);
                        }

                        r2 = r1;
                        r1 = r0;
                        r0 = matchOffset;
                        break;
                    }
            }

            if (matchOffset == 0)
                throw new InvalidDataException("LZX produced an invalid zero match offset.");

            if (windowPos + matchLength > windowSize)
                throw new InvalidDataException("LZX match ran past the end of the window.");

            int bytesDecodedInFrame = windowPos - frameStart;
            long decodedSoFar = totalOutputSoFar + bytesDecodedInFrame;
            if (matchOffset > windowPos && matchOffset > decodedSoFar)
                throw new InvalidDataException("LZX match offset points before the start of the decoded stream.");

            int runDest = windowPos;
            int remaining = matchLength;

            if (matchOffset > windowPos)
            {
                int wrapDistance = checked((int)matchOffset) - windowPos;
                if (wrapDistance > windowSize)
                    throw new InvalidDataException("LZX match offset exceeded the window size.");

                int runSrc = windowSize - wrapDistance;
                if (wrapDistance < remaining)
                {
                    remaining -= wrapDistance;
                    while (wrapDistance-- > 0)
                        window[runDest++] = window[runSrc++];

                    runSrc = 0;
                }

                while (remaining-- > 0)
                    window[runDest++] = window[runSrc++];
            }
            else
            {
                int runSrc = runDest - checked((int)matchOffset);
                while (remaining-- > 0)
                    window[runDest++] = window[runSrc++];
            }

            thisRun -= matchLength;
            windowPos += matchLength;
        }

        return thisRun;
    }

    private static void CopyFrame(byte[] window, int framePos, int frameSize, byte[] output, int outputPos)
    {
        if (framePos + frameSize <= window.Length)
        {
            Array.Copy(window, framePos, output, outputPos, frameSize);
            return;
        }

        int first = window.Length - framePos;
        Array.Copy(window, framePos, output, outputPos, first);
        Array.Copy(window, 0, output, outputPos + first, frameSize - first);
    }

    private static void ReadLengths(ref BitReader br, byte[] lens, int from, int to)
    {
        var preLens = new byte[20];
        for (int i = 0; i < preLens.Length; i++)
            preLens[i] = (byte)br.ReadBits(4);

        var preTree = HuffmanDecoder.Create(preLens, preLens.Length, allowEmpty: false);

        int pos = from;
        while (pos < to)
        {
            int sym = preTree.Decode(ref br);
            switch (sym)
            {
                case 17:
                    {
                        int count = (int)br.ReadBits(4) + 4;
                        while (count-- > 0 && pos < to)
                            lens[pos++] = 0;
                        break;
                    }

                case 18:
                    {
                        int count = (int)br.ReadBits(5) + 20;
                        while (count-- > 0 && pos < to)
                            lens[pos++] = 0;
                        break;
                    }

                case 19:
                    {
                        int count = (int)br.ReadBits(1) + 4;
                        int nextSym = preTree.Decode(ref br);
                        int newLen = lens[pos] - nextSym;
                        if (newLen < 0)
                            newLen += 17;

                        while (count-- > 0 && pos < to)
                            lens[pos++] = (byte)newLen;
                        break;
                    }

                default:
                    {
                        int newLen = lens[pos] - sym;
                        if (newLen < 0)
                            newLen += 17;

                        lens[pos++] = (byte)newLen;
                        break;
                    }
            }
        }
    }

    private static int GetNumPositionSlots(int windowBits)
    {
        if (windowBits < 15 || windowBits > 21)
            throw new InvalidDataException($"Unsupported LZX window size 2^{windowBits}.");

        return PositionSlotsByWindowBits[windowBits - 15];
    }

    private static byte[] BuildExtraBits(int count)
    {
        var result = new byte[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = i switch
            {
                < 4 => 0,
                < 36 => (byte)((i / 2) - 1),
                _ => 17,
            };
        }

        return result;
    }

    private static uint[] BuildPositionBase(int count)
    {
        var result = new uint[count];
        uint next = 0;
        for (int i = 0; i < count; i++)
        {
            result[i] = next;
            next += 1u << ExtraBits[i];
        }

        return result;
    }

    private struct BitReader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _pos;
        private ulong _buffer;
        private int _bits;

        public BitReader(byte[] data, int offset, int length)
        {
            _data = data;
            _pos = offset;
            _end = offset + length;
            _buffer = 0;
            _bits = 0;
        }

        public uint ReadBits(int count)
        {
            if (count == 0)
                return 0;

            EnsureBits(count);
            uint value = (uint)((_buffer >> (_bits - count)) & ((1UL << count) - 1));
            _bits -= count;
            if (_bits == 0)
            {
                _buffer = 0;
            }
            else
            {
                _buffer &= (1UL << _bits) - 1;
            }

            return value;
        }

        public uint ReadBit() => ReadBits(1);

        public void AlignForUncompressed()
        {
            _buffer = 0;
            _bits = 0;
        }

        public void AlignToWordBoundary()
        {
            if (_bits > 0)
            {
                EnsureBits(16);
                int excess = _bits & 15;
                if (excess != 0)
                    RemoveBits(excess);
            }
        }

        public void SkipByte()
        {
            if (_pos >= _end)
                throw new InvalidDataException("Unexpected end of LZX input while skipping uncompressed padding.");

            _pos++;
        }

        public uint ReadUInt32LE()
        {
            uint b0 = ReadRawByte();
            uint b1 = ReadRawByte();
            uint b2 = ReadRawByte();
            uint b3 = ReadRawByte();
            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }

        public void ReadBytes(byte[] destination, int destinationOffset, int count)
        {
            if (_pos + count > _end)
                throw new InvalidDataException("Unexpected end of LZX input while reading an uncompressed block.");

            Array.Copy(_data, _pos, destination, destinationOffset, count);
            _pos += count;
        }

        private uint ReadRawByte()
        {
            if (_pos >= _end)
                throw new InvalidDataException("Unexpected end of LZX input.");

            return _data[_pos++];
        }

        private void EnsureBits(int count)
        {
            while (_bits < count)
            {
                if (_pos + 1 < _end)
                {
                    uint word = (uint)(_data[_pos] | (_data[_pos + 1] << 8));
                    _buffer = (_buffer << 16) | word;
                    _bits += 16;
                    _pos += 2;
                }
                else if (_pos < _end)
                {
                    _buffer = (_buffer << 8) | _data[_pos++];
                    _bits += 8;
                }
                else
                {
                    _buffer <<= 16;
                    _bits += 16;
                }
            }
        }

        private void RemoveBits(int count)
        {
            EnsureBits(count);
            _bits -= count;
            if (_bits == 0)
            {
                _buffer = 0;
            }
            else
            {
                _buffer &= (1UL << _bits) - 1;
            }
        }
    }

    private sealed class HuffmanDecoder
    {
        private const int MaxCodeLength = 16;

        private readonly int[] _firstCode = new int[MaxCodeLength + 1];
        private readonly int[] _counts = new int[MaxCodeLength + 1];
        private readonly int[] _firstSymbol = new int[MaxCodeLength + 1];
        private readonly int[] _symbols;

        private HuffmanDecoder(int[] symbols, bool isEmpty)
        {
            _symbols = symbols;
            IsEmpty = isEmpty;
        }

        public bool IsEmpty { get; }

        public static HuffmanDecoder Create(byte[] lengths, int symbolCount, bool allowEmpty)
        {
            var counts = new int[MaxCodeLength + 1];
            int nonZero = 0;
            for (int i = 0; i < symbolCount; i++)
            {
                int len = lengths[i];
                if (len > MaxCodeLength)
                    throw new InvalidDataException($"LZX Huffman code length {len} exceeds the supported maximum.");

                if (len != 0)
                {
                    counts[len]++;
                    nonZero++;
                }
            }

            if (nonZero == 0)
            {
                if (!allowEmpty)
                    throw new InvalidDataException("LZX encountered an empty Huffman tree.");

                return new HuffmanDecoder([], isEmpty: true);
            }

            var firstCode = new int[MaxCodeLength + 1];
            var firstSymbol = new int[MaxCodeLength + 1];
            int code = 0;
            int symbolIndex = 0;
            for (int len = 1; len <= MaxCodeLength; len++)
            {
                code = (code + counts[len - 1]) << 1;
                firstCode[len] = code;
                firstSymbol[len] = symbolIndex;
                symbolIndex += counts[len];
            }

            var nextIndex = new int[MaxCodeLength + 1];
            Array.Copy(firstSymbol, nextIndex, nextIndex.Length);

            var symbols = new int[nonZero];
            for (int symbol = 0; symbol < symbolCount; symbol++)
            {
                int len = lengths[symbol];
                if (len != 0)
                    symbols[nextIndex[len]++] = symbol;
            }

            var decoder = new HuffmanDecoder(symbols, isEmpty: false);
            Array.Copy(firstCode, decoder._firstCode, firstCode.Length);
            Array.Copy(counts, decoder._counts, counts.Length);
            Array.Copy(firstSymbol, decoder._firstSymbol, firstSymbol.Length);
            return decoder;
        }

        public int Decode(ref BitReader br)
        {
            int code = 0;
            for (int len = 1; len <= MaxCodeLength; len++)
            {
                code = (code << 1) | (int)br.ReadBit();
                int delta = code - _firstCode[len];
                if ((uint)delta < (uint)_counts[len])
                    return _symbols[_firstSymbol[len] + delta];
            }

            throw new InvalidDataException("Invalid Huffman code in LZX stream.");
        }
    }
}
