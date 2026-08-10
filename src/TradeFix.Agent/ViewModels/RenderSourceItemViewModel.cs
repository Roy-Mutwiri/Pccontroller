using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Agent.ViewModels;

public sealed partial class RenderSourceItemViewModel : ObservableObject
{
    public string Id { get; }
    public SourceType Type { get; }
    public bool IsText => Type == SourceType.Text;
    public bool IsImage => Type == SourceType.Image;
    public bool IsColor => Type == SourceType.Background;
    public bool IsLive { get; private set; }
    public string? AssetHash { get; private set; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private string _colorHex = "#3E8EF7";
    [ObservableProperty] private string _textContent = string.Empty;
    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private BitmapSource? _liveFrame;

    // Mirrors SourceItemViewModel on the Master side — same crop semantics (0-1 fractions),
    // same "overscale and clip" rendering technique, kept in sync via the same Transform2D.Crop
    // that drag/resize already broadcasts.
    [ObservableProperty] private double _cropLeft;
    [ObservableProperty] private double _cropTop;
    [ObservableProperty] private double _cropRight;
    [ObservableProperty] private double _cropBottom;

    public double ContentWidth => Width / Math.Max(0.05, 1 - CropLeft - CropRight);
    public double ContentHeight => Height / Math.Max(0.05, 1 - CropTop - CropBottom);
    public Thickness ContentMargin => new(-CropLeft * ContentWidth, -CropTop * ContentHeight, 0, 0);

    public RenderSourceItemViewModel(SourceDefinition source)
    {
        Id = source.Id;
        Type = source.Type;
        Apply(source);
    }

    public void Apply(SourceDefinition source)
    {
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

            IsLive = source.Config.TryGetProperty("live", out var liveProp) && liveProp.ValueKind == JsonValueKind.True;
        }
    }

    public void ApplyTransform(Transform2D transform)
    {
        X = transform.X;
        Y = transform.Y;
        Width = transform.Width;
        Height = transform.Height;
        CropLeft = transform.Crop.Left;
        CropTop = transform.Crop.Top;
        CropRight = transform.Crop.Right;
        CropBottom = transform.Crop.Bottom;
    }

    /// <summary>Decodes a JPEG frame into a displayable, cross-thread-safe bitmap. Deliberately
    /// safe to call from ANY thread (not just the UI thread): <see cref="BitmapCacheOption.OnLoad"/>
    /// plus <see cref="Freezable.Freeze"/> produces an immutable bitmap that doesn't need dispatcher
    /// affinity — decoding here off the UI thread, in <see cref="LiveFramePump"/>, is what stops a
    /// large (quality/resolution now maxed out on request) JPEG's decode cost from blocking the
    /// render window or the network thread that received it.</summary>
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
}
