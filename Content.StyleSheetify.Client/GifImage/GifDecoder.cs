using System.Text;
using Content.StyleSheetify.Client.GifImage.Enums;
using Robust.Client.Graphics;
using Robust.Shared.IoC;
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

    private readonly List<GifFrame> _frames = new();
    private Rgba32[]? _globalColorTable;
    private Rgba32 _backgroundColor = new(0,0,0);
    
    public static GifDecoder Decode(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(3)) != "GIF")
        {
            reader.Dispose();
            throw new Exception("Not a valid GIF file.");
        }
        
        var specificationVersion = reader.ReadBytes(3);
        
        var decoder = new GifDecoder
        {
            Width = reader.ReadInt16(),
            Height = reader.ReadInt16()
        };
        
        var packed = reader.ReadByte();
        var bgColorIndex = reader.ReadByte();
        var aspectRatio = reader.ReadByte(); 
        
        var hasGlobalColorTable = (packed & 0x80) != 0;
        var globalColorTableSize = 1 << ((packed & 0x07) + 1);
        
        if (hasGlobalColorTable)
        {
            decoder._globalColorTable = ReadColorTable(reader, globalColorTableSize);
            decoder._backgroundColor = decoder._globalColorTable[bgColorIndex];
        }
        
        decoder._canvas = new Image<Rgba32>(decoder.Width, decoder.Height);
        
        for (var y = 0; y < decoder.Height; y++)
            for (var x = 0; x < decoder.Width; x++) 
                decoder._canvas[x, y] = decoder._backgroundColor;
        
        var delay = 0;
        var disposalMethod = GifDisposalMethod.Unspecified;
        var transIndex = -1;
        var hasTransparency = false;
        var clyde = IoCManager.Resolve<IClyde>();

        try
        {
            var stopGifRead = false;

            while (!cancellationToken.IsCancellationRequested && !stopGifRead)
            {
                switch ((GifBlockIntroducer)reader.ReadByte())
                {
                    case GifBlockIntroducer.ImageDescriptor:
                        decoder.DecodeImageFrame(reader, delay, disposalMethod, transIndex,
                            hasTransparency, clyde);
                        break;
                    case GifBlockIntroducer.Extension:
                        if ((GifExtensionLabel)reader.ReadByte() == GifExtensionLabel.GraphicControl)
                        {
                            var blockSize = reader.ReadByte();
                            var gcPacked = reader.ReadByte();

                            disposalMethod = (GifDisposalMethod)((gcPacked >> 2) & 0x07);
                            hasTransparency = (gcPacked & 0x01) != 0;

                            delay = reader.ReadInt16() * 10; // 1/100th sec to ms
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
                        throw new Exception($"Unknown block introducer");
                }
            }
        }
        finally
        {
            reader.Dispose();
        }
        return decoder;
    }

    private Image<Rgba32> _canvas;

    private void DecodeImageFrame(BinaryReader reader, int delay, GifDisposalMethod disposalMethod, int transIndex, bool hasTransparency, IClyde clyde)
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

        var indices = LZWHelper.DecompressLZW(compressedData.ToArray(), lzwMinCodeSize, width * height);
        
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
                // If pixel is transparent, don't overwrite the canvas
                if (hasTransparency && index == transIndex) continue;

                _canvas[x, y] = colorTable[index];
            }
        }
      
        _frames.Add(new GifFrame
        {
            DelayMs = delay > 0 ? delay : 100,
            FrameImage = clyde.LoadTextureFromImage(_canvas)
        });
        
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
    }

    private static void SkipExtension(BinaryReader reader)
    {
        while (true)
        {
            var blockSize = reader.ReadByte();
            if (blockSize == 0) break;
            reader.ReadBytes(blockSize);
            //reader.BaseStream.Position += blockSize;
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

public sealed class SimplePool<T>
{
    private readonly T[] _buffer;
    public int Length { get; private set; }
    
    private Queue<int> _free = [];
    
    private readonly Func<T> _factory;
    
    public SimplePool(int limit, Func<T> factory, bool init = false)
    {
        _buffer = new T[limit];
        _factory = factory;

        if(!init) return;
        
        for (var i = 0; i < limit; i++)
        {
            _buffer[i] = factory();
        }
    }
    
    public SimplePoolEntry Take()
    {
        if (!_free.TryDequeue(out var index))
            index = Length++;
        
        var objectSelected = _buffer[index];
        if (objectSelected == null)
        {
            objectSelected = _factory();
            _buffer[index] = objectSelected;
        }
        
        return new SimplePoolEntry(index, objectSelected);
    }

    public void Free(SimplePoolEntry entry)
    {
        _free.Enqueue(entry.Index);
    }

    public void Clear()
    {
        Length = 0;
        _free.Clear();
    }
    
    public sealed class SimplePoolEntry
    {
        public SimplePoolEntry(int index, T value)
        {
            Index = index;
            Value = value;
        }

        public int Index { get; }
        public T Value { get; }
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