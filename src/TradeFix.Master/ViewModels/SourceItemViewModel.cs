using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Master.ViewModels;

public sealed partial class SourceItemViewModel : ObservableObject
{
    public string Id { get; }
    public SourceType Type { get; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private string _colorHex = "#3E8EF7";
    [ObservableProperty] private string _textContent = string.Empty;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private BitmapSource? _liveFrame;

    // 0-1 fractions — see TradeFix.Shared.Models.CropBox. Edited as 0-100% in the Properties panel.
    [ObservableProperty] private double _cropLeft;
    [ObservableProperty] private double _cropTop;
    [ObservableProperty] private double _cropRight;
    [ObservableProperty] private double _cropBottom;

    // Capture-only: editable after creation via the Properties panel (MasterHost restarts the
    // capture at the new settings rather than requiring delete + re-add).
    [ObservableProperty] private int _captureFps = 12;
    [ObservableProperty] private int _captureMaxDimension = 3840;
    [ObservableProperty] private int _captureQuality = 100;
    [ObservableProperty] private bool _captureIncludeAudio = true;

    public bool IsText => Type == SourceType.Text;
    public bool IsImage => Type == SourceType.Image;
    public bool IsColor => Type == SourceType.Background;
    public bool IsCapture => Type == SourceType.DisplayCapture;
    public bool HasCrop => IsImage || IsCapture;

    /// <summary>Drives the on-canvas drag handles (see MainWindow.xaml's crop-handle Rectangles) —
    /// only shown while this exact source is selected, so unrelated boxes don't sprout handles.</summary>
    public bool ShowCropHandles => HasCrop && IsSelected;

    public string? AssetHash { get; private set; }

    /// <summary>Width/Margin the underlying Image element needs so that, once clipped to the
    /// box, only the cropped region is visible — the standard "overscale and clip" technique
    /// (avoids needing a custom Geometry/Transform in XAML).</summary>
    public double ContentWidth => Width / Math.Max(0.05, 1 - CropLeft - CropRight);
    public double ContentHeight => Height / Math.Max(0.05, 1 - CropTop - CropBottom);
    public Thickness ContentMargin => new(-CropLeft * ContentWidth, -CropTop * ContentHeight, 0, 0);

    public SourceItemViewModel(SourceDefinition source)
    {
        Id = source.Id;
        Type = source.Type;
        Apply(source);
    }

    public void Apply(SourceDefinition source)
    {
        Name = source.Name;
        X = source.Transform.X;
        Y = source.Transform.Y;
        Width = source.Transform.Width;
        Height = source.Transform.Height;
        CropLeft = source.Transform.Crop.Left;
        CropTop = source.Transform.Crop.Top;
        CropRight = source.Transform.Crop.Right;
        CropBottom = source.Transform.Crop.Bottom;

        if (source.Config.ValueKind == JsonValueKind.Object)
        {
            if (source.Config.TryGetProperty("color", out var colorProp) && colorProp.ValueKind == JsonValueKind.String)
            {
                ColorHex = colorProp.GetString() ?? ColorHex;
            }

            if (source.Config.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
            {
                TextContent = textProp.GetString() ?? TextContent;
            }

            if (source.Config.TryGetProperty("assetHash", out var hashProp) && hashProp.ValueKind == JsonValueKind.String)
            {
                AssetHash = hashProp.GetString();
            }

            if (source.Config.TryGetProperty("fps", out var fpsProp) && fpsProp.ValueKind == JsonValueKind.Number)
            {
                CaptureFps = fpsProp.GetInt32();
            }

            if (source.Config.TryGetProperty("maxDimension", out var dimProp) && dimProp.ValueKind == JsonValueKind.Number)
            {
                CaptureMaxDimension = dimProp.GetInt32();
            }

            if (source.Config.TryGetProperty("quality", out var qualityProp) && qualityProp.ValueKind == JsonValueKind.Number)
            {
                CaptureQuality = qualityProp.GetInt32();
            }

            if (source.Config.TryGetProperty("audio", out var audioProp) &&
                (audioProp.ValueKind == JsonValueKind.True || audioProp.ValueKind == JsonValueKind.False))
            {
                CaptureIncludeAudio = audioProp.GetBoolean();
            }
        }
    }

    public Transform2D CurrentTransform() => new()
    {
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        Crop = new CropBox { Left = CropLeft, Top = CropTop, Right = CropRight, Bottom = CropBottom }
    };

    /// <summary>Decodes a JPEG frame into a displayable, cross-thread-safe bitmap. Deliberately
    /// safe to call from ANY thread: <see cref="BitmapCacheOption.OnLoad"/> plus
    /// <see cref="System.Windows.Freezable.Freeze"/> produces an immutable bitmap that doesn't
    /// need dispatcher affinity — decoding here off the UI thread, in
    /// <see cref="TradeFix.Master.Services.LiveFramePump"/>, is what stops a large (quality/
    /// resolution now maxed out on request) JPEG's decode cost from blocking Master's own capture
    /// loop, which would otherwise delay broadcasting the next frame to every node.</summary>
    public static BitmapSource DecodeJpeg(byte[] jpegBytes)
    {
        using var stream = new System.IO.MemoryStream(jpegBytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Assigns an already-decoded frame. Must be called on the UI thread (this is the
    /// cheap part — just a property assignment that raises PropertyChanged for data binding).</summary>
    public void ApplyLiveFrame(BitmapSource decoded) => LiveFrame = decoded;

    partial void OnWidthChanged(double value)
    {
        OnPropertyChanged(nameof(ContentWidth));
        OnPropertyChanged(nameof(ContentMargin));
    }

    partial void OnHeightChanged(double value)
    {
        OnPropertyChanged(nameof(ContentHeight));
        OnPropertyChanged(nameof(ContentMargin));
    }

    partial void OnCropLeftChanged(double value)
    {
        OnPropertyChanged(nameof(ContentWidth));
        OnPropertyChanged(nameof(ContentMargin));
    }

    partial void OnCropRightChanged(double value) => OnPropertyChanged(nameof(ContentWidth));

    partial void OnCropTopChanged(double value)
    {
        OnPropertyChanged(nameof(ContentHeight));
        OnPropertyChanged(nameof(ContentMargin));
    }

    partial void OnCropBottomChanged(double value) => OnPropertyChanged(nameof(ContentHeight));

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(ShowCropHandles));
}
