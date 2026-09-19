using System.Text;
using Content.StyleSheetify.Client.GifImage.Enums;
using Robust.Client.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.StyleSheetify.Client.GifImage;

public sealed class GifFrame
{
    public int DelayMs { get; set; }
    public float Delay => (float)DelayMs / 1000;
    public Texture FrameImage { get; set; }
}

public sealed class GifDecoder
{
    public short Width { get; private set; }
    public short Height { get; private set; }
    
    public IReadOnlyList<GifFrame> Frames => _frames;
    public bool IsDecoding { get; private set; }
    
    // Опциональное событие, вызываемое при добавлении каждого кадра
    public event Action<GifFrame>? OnFrameDecoded;

    private readonly List<GifFrame> _frames = new();
    private Rgba32[]? _globalColorTable;
    private Rgba32 _backgroundColor = new(0, 0, 0);
    private Image<Rgba32> _canvas = null!;

    public void DecodeHeader(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        
        if (new string(reader.ReadChars(3)) != "GIF")
            throw new Exception("Not a valid GIF file.");

        reader.ReadBytes(3); // specificationVersion

        Width = reader.ReadInt16();
        Height = reader.ReadInt16();

        var packed = reader.ReadByte();
        var bgColorIndex = reader.ReadByte();
        reader.ReadByte(); // aspectRatio

        var hasGlobalColorTable = (packed & 0x80) != 0;
        var globalColorTableSize = 1 << ((packed & 0x07) + 1);

        if (hasGlobalColorTable)
        {
            _globalColorTable = ReadColorTable(reader, globalColorTableSize);
            _backgroundColor = _globalColorTable[bgColorIndex];
        }

        _canvas = new Image<Rgba32>(Width, Height);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++) 
                _canvas[x, y] = _backgroundColor;
    }

    /// <summary>
    /// Асинхронная обработка всех кадров GIF. Постепенно добавляет их в _frames.
    /// </summary>
    public async Task ProcessFramesAsync(Stream stream, IClyde clyde, CancellationToken cancellationToken)
    {
        IsDecoding = true;
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        var delay = 0;
        var disposalMethod = GifDisposalMethod.Unspecified;
        var transIndex = -1;
        var hasTransparency = false;

        try
        {
            var stopGifRead = false;
            while (!cancellationToken.IsCancellationRequested && !stopGifRead)
            {
                var blockType = (GifBlockIntroducer)reader.ReadByte();
                switch (blockType)
                {
                    case GifBlockIntroducer.ImageDescriptor:
                        var frame = await DecodeImageFrameAsync(reader, delay, disposalMethod, transIndex, hasTransparency, clyde, cancellationToken);
                        _frames.Add(frame);
                        OnFrameDecoded?.Invoke(frame);
                        break;
                        
                    case GifBlockIntroducer.Extension:
                        var extLabel = (GifExtensionLabel)reader.ReadByte();
                        if (extLabel == GifExtensionLabel.GraphicControl)
                        {
                            reader.ReadByte(); // blockSize
                            var gcPacked = reader.ReadByte();

                            disposalMethod = (GifDisposalMethod)((gcPacked >> 2) & 0x07);
                            hasTransparency = (gcPacked & 0x01) != 0;

                            delay = reader.ReadInt16() * 10;
                            transIndex = reader.ReadByte();

                            if (reader.ReadByte() != 0x00)
                                throw new Exception("Unknown block terminator");
                        }
                        else
                        {
                            SkipExtension(reader);
                        }
                        break;
                        
                    case GifBlockIntroducer.Trailer:
                        stopGifRead = true;
                        break;
                        
                    default:
                        throw new Exception($"Unknown block introducer: {blockType}");
                }
            }
        }
        finally
        {
            IsDecoding = false;
        }
    }

    private async Task<GifFrame> DecodeImageFrameAsync(
        BinaryReader reader, int delay, GifDisposalMethod disposalMethod, int transIndex, bool hasTransparency, IClyde clyde, CancellationToken cancellationToken)
    {
        var left = reader.ReadInt16();
        var top = reader.ReadInt16();
        var width = reader.ReadInt16();
        var height = reader.ReadInt16();
        var packedImgData = reader.ReadByte();

        var hasLocalColorTable = (packedImgData & 0x80) != 0;
        var isInterlaced = (packedImgData & 0x40) != 0;
        
        if (isInterlaced)
            throw new NotSupportedException("Interlaced GIFs are not supported.");

        var colorTable = _globalColorTable!;
        if (hasLocalColorTable)
        {
            var localColorTableSize = 1 << ((packedImgData & 0x07) + 1);
            colorTable = ReadColorTable(reader, localColorTableSize);
        }

        int lzwMinCodeSize = reader.ReadByte();
        
        var compressedData = new List<byte>();
        while (true)
        {
            var blockSize = reader.ReadByte();
            if (blockSize == 0) break;
            compressedData.AddRange(reader.ReadBytes(blockSize));
        }

        // Тяжелая CPU-задача уходит в ThreadPool
        var indices = await Task.Run(() => 
            LZWHelper.DecompressLZW(compressedData.ToArray(), lzwMinCodeSize, width * height), cancellationToken);
        
        // После await возвращаемся в главный поток для безопасной работы с IClyde

        Image<Rgba32> canvasBefore = null;
        if (disposalMethod == GifDisposalMethod.RestoreToPrevious)
        {
            canvasBefore = _canvas.Clone();
        }

        for (var i = 0; i < indices.Length; i++)
        {
            var index = indices[i];
            var x = left + (i % width);
            var y = top + (i / width);

            if (x < Width && y < Height)
            {
                if (hasTransparency && index == transIndex) continue;
                _canvas[x, y] = colorTable[index];
            }
        }
      
        var frameTexture = clyde.LoadTextureFromImage(_canvas);
        
        var frame = new GifFrame
        {
            DelayMs = delay > 0 ? delay : 100,
            FrameImage = frameTexture
        };
        
        if (disposalMethod == GifDisposalMethod.RestoreToBackground)
        {
            for (int y = top; y < top + height && y < Height; y++)
                for (int x = left; x < left + width && x < Width; x++)
                    _canvas[x, y] = _backgroundColor;
        }
        else if (disposalMethod == GifDisposalMethod.RestoreToPrevious) 
        {
            _canvas.Dispose();
            _canvas = canvasBefore;
        }

        return frame;
    }

    private static void SkipExtension(BinaryReader reader)
    {
        while (true)
        {
            var blockSize = reader.ReadByte();
            if (blockSize == 0) break;
            reader.ReadBytes(blockSize);
        }
    }
    
    private static Rgba32[] ReadColorTable(BinaryReader reader, int colorCount)
    {
        var table = new Rgba32[colorCount];
        for (var i = 0; i < colorCount; i++)
        {
            var r = reader.ReadByte();
            var g = reader.ReadByte();
            var b = reader.ReadByte();
            table[i] = new Rgba32(r, g, b);
        }
        return table;
    }
}

public static class LZWHelper
{
    public static int[] DecompressLZW(byte[] data, int minCodeSize, int expectedPixels)
    {
        var clearCode = 1 << minCodeSize;
        var eoiCode = clearCode + 1;
        var codeSize = minCodeSize + 1;
        var maxCode = (1 << codeSize);
        var nextCode = eoiCode + 1;

        var dictionary = new int[4096][];
        InitializeDictionary(dictionary, clearCode);

        var output = new int[expectedPixels];
        var outIndex = 0;
        int bitBuffer = 0, bitsInBuffer = 0, byteIndex = 0;
        var prevCode = -1;

        while (outIndex < expectedPixels && byteIndex < data.Length)
        {
            while (bitsInBuffer < codeSize)
            {
                if (byteIndex < data.Length) { bitBuffer |= (data[byteIndex++] << bitsInBuffer); bitsInBuffer += 8; }
                else break;
            }
            if (bitsInBuffer < codeSize) break;

            var code = bitBuffer & ((1 << codeSize) - 1);
            bitBuffer >>= codeSize;
            bitsInBuffer -= codeSize;

            if (code == clearCode) {
                codeSize = minCodeSize + 1; maxCode = (1 << codeSize); nextCode = eoiCode + 1;
                InitializeDictionary(dictionary, clearCode); prevCode = -1; continue;
            }
            if (code == eoiCode) break;

            int[] entry;
            if (code < nextCode) entry = dictionary[code];
            else if (code == nextCode && prevCode != -1) {
                var prevEntry = dictionary[prevCode];
                entry = new int[prevEntry.Length + 1];
                Array.Copy(prevEntry, entry, prevEntry.Length);
                entry[prevEntry.Length] = prevEntry[0];
            }
            else throw new InvalidDataException("Invalid LZW data.");

            foreach (var pixelIndex in entry) { if (outIndex < expectedPixels) output[outIndex++] = pixelIndex; }

            if (prevCode != -1 && nextCode < 4096) {
                var prevEntry = dictionary[prevCode];
                var newEntry = new int[prevEntry.Length + 1];
                Array.Copy(prevEntry, newEntry, prevEntry.Length);
                newEntry[prevEntry.Length] = entry[0];
                dictionary[nextCode++] = newEntry;
                if (nextCode >= maxCode && codeSize < 12) { codeSize++; maxCode = (1 << codeSize); }
            }
            prevCode = code;
        }
        return output;
    }

    private static void InitializeDictionary(int[][] dictionary, int clearCode)
    {
        for (var i = 0; i < 4096; i++) dictionary[i] = null;
        for (var i = 0; i < clearCode; i++) dictionary[i] = new int[] { i };
    }
}