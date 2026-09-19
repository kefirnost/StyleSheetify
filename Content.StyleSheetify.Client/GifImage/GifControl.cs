using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.StyleSheetify.Client.GifImage;

public sealed partial class GifControl : Control
{
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IResourceManager _resourceManager = default!;
    
    private int _curFrame;
    private float _curFrameTime;
    
    private GifDecoder? _gifDecoder;
    private CancellationTokenSource? _loadCts;
    private MemoryStream? _gifStream;
    
    public TextureRect DisplayRect { get; }
    public int AnimationFrameCount { get; private set; } = 1;
    
    public ResPath GifPath
    {
        get => field;
        set
        {
            field = value;
            _ = LoadGifAsync(value);
        }
    }

    public void LoadFromStream(Stream stream)
    {
        _ = LoadFromStreamAsync(stream);
    }
    
    public GifControl()
    {
        IoCManager.InjectDependencies(this);
        DisplayRect = new TextureRect()
        {
            Stretch = TextureRect.StretchMode.KeepAspectCentered, 
        };
        AddChild(DisplayRect);
    }

    private async Task LoadGifAsync(ResPath path)
    {
        CancelLoading();
        
        AnimationFrameCount = 1;
        _curFrame = 0;
        _curFrameTime = 0;
        
        try
        {
            using var originalStream = _resourceManager.ContentFileRead(path);
            _gifStream = new MemoryStream();
            
            // АСИНХРОННОЕ копирование. Главный поток не блокируется ни на мгновение.
            await originalStream.CopyToAsync(_gifStream);
            _gifStream.Position = 0;
            
            _gifDecoder = new GifDecoder();
            _gifDecoder.DecodeHeader(_gifStream);
            
            // Создаем текстуру уже после того, как узнали размеры из заголовка
            DisplayRect.Texture = _clyde.CreateBlankTexture<Rgba32>(new Vector2i(_gifDecoder.Width, _gifDecoder.Height));
            
            StartBackgroundDecoding(_gifStream);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load GIF from {path}: {ex}");
        }
    }

    private async Task LoadFromStreamAsync(Stream stream)
    {
        CancelLoading();
        
        AnimationFrameCount = 1;
        _curFrame = 0;
        _curFrameTime = 0;

        try
        {
            _gifStream = new MemoryStream();
            await stream.CopyToAsync(_gifStream);
            _gifStream.Position = 0;

            _gifDecoder = new GifDecoder();
            _gifDecoder.DecodeHeader(_gifStream);
            DisplayRect.Texture = _clyde.CreateBlankTexture<Rgba32>(new Vector2i(_gifDecoder.Width, _gifDecoder.Height));
            
            StartBackgroundDecoding(_gifStream);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load GIF from stream: {ex}");
        }
    }

    private void CancelLoading()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        
        _gifStream?.Dispose();
        _gifStream = null;
    }

    private void StartBackgroundDecoding(Stream stream)
    {
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        
        _ = ProcessFramesLoopAsync(stream, token);
    }

    private async Task ProcessFramesLoopAsync(Stream stream, CancellationToken token)
    {
        try
        {
            await _gifDecoder!.ProcessFramesAsync(stream, _clyde, token);
        }
        catch (OperationCanceledException) 
        { 
        }
        catch (Exception ex)
        {
            Logger.Error($"Error decoding GIF: {ex}");
        }
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        
        if(_gifDecoder is null) 
            return;
        
        int totalFrames = _gifDecoder.Frames.Count;
        if (totalFrames == 0) 
            return;
        
        if (totalFrames > AnimationFrameCount)
            AnimationFrameCount = totalFrames;

        var oldFrame = _curFrame;

        _curFrameTime -= args.DeltaSeconds;
        
        while (_curFrameTime < GetDelay(_curFrame))
        {
            int nextFrame = (_curFrame + 1) % AnimationFrameCount;
            
            if (_gifDecoder.IsDecoding && nextFrame >= totalFrames)
            {
                break;
            }

            _curFrame = nextFrame;
            _curFrameTime += GetDelay(_curFrame);
        }
        
        if (_curFrame != oldFrame)
        {
            var gifFrame = GetGifFrame(_curFrame);
            if (gifFrame != null)
                DisplayRect.Texture = gifFrame.FrameImage;
        }
    }

    public float GetDelay(int frame)
    {
        return GetGifFrame(frame)?.Delay ?? 0.5f;
    }

    public GifFrame? GetGifFrame(int frame)
    {
        var gifCount = _gifDecoder?.Frames.Count ?? 0;
        if(gifCount == 0) 
            return null;
        
        if (frame >= gifCount) 
            return null;
        
        return _gifDecoder!.Frames[frame];
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            CancelLoading();
        }
    }
}