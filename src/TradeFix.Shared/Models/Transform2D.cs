namespace TradeFix.Shared.Models;

public sealed record Transform2D
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 100;
    public double Height { get; init; } = 100;
    public double RotationDegrees { get; init; }
    public double ScaleX { get; init; } = 1.0;
    public double ScaleY { get; init; } = 1.0;
    public double Opacity { get; init; } = 1.0;
    public int ZIndex { get; init; }
    public bool Visible { get; init; } = true;
    public CropBox Crop { get; init; } = new();
}

/// <summary>Each value is a 0–1 fraction of the source's own content width/height to trim from
/// that edge (resolution-independent — works the same whether the underlying image/capture is
/// 640x360 or 3840x2160). Left+Right and Top+Bottom should each stay below 1.</summary>
public sealed record CropBox
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; }
    public double Bottom { get; init; }
}
