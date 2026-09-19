using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
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
    public TextureRect DisplayRect { get; }
    public int AnimationFrameCount { get; private set; } = 1;
    
    public ResPath GifPath
    {
        get => field;
        set
        {
            AnimationFrameCount = 1;
            _gifDecoder = GifDecoder.Decode(_resourceManager.ContentFileRead(value), CancellationToken.None);
            DisplayRect.Texture = _clyde.CreateBlankTexture<Rgba32>(new Vector2i(_gifDecoder.Width, _gifDecoder.Height));
            field = value;
        }
    }

    public void LoadFromStream(Stream stream)
    {
        AnimationFrameCount = 1;
        _gifDecoder = GifDecoder.Decode(stream, CancellationToken.None);
        DisplayRect.Texture = _clyde.CreateBlankTexture<Rgba32>(new Vector2i(_gifDecoder.Width, _gifDecoder.Height));
    }
    
    public GifControl()
    {
        IoCManager.InjectDependencies(this);
        DisplayRect = new  TextureRect()
        {
            Stretch = TextureRect.StretchMode.KeepAspectCentered, 
        };
        AddChild(DisplayRect);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        
        if(_gifDecoder is null) 
            return;
        
        if(_gifDecoder.Frames.Count > AnimationFrameCount)
            AnimationFrameCount = _gifDecoder.Frames.Count;
        
        var oldFrame = _curFrame;

        _curFrameTime -= args.DeltaSeconds;
        while (_curFrameTime < GetDelay(_curFrame))
        {
            _curFrame = (_curFrame + 1) % AnimationFrameCount;
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
        
        if(gifCount > AnimationFrameCount)
            AnimationFrameCount = gifCount;

        if (frame > AnimationFrameCount) 
            return null;
        
        return _gifDecoder!.Frames[frame];
    }
}