using TradeFix.Master.ViewModels;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Master.Tests;

/// <summary>
/// Regression coverage for a second real bug found while wiring up crop: drag/resize rebuilt a
/// bare <c>Transform2D</c> from scratch, silently resetting Crop (and everything else) back to
/// defaults on every mouse move. Also covers the "overscale and clip" math crop rendering
/// depends on, since a wrong sign/formula there would crop the wrong region without ever
/// throwing an exception — the kind of bug only a value-level test catches.
/// </summary>
public class SourceItemViewModelCropTests
{
    private static SourceDefinition MakeSource(Transform2D transform) => new()
    {
        Id = "src-1",
        Name = "Test",
        Type = SourceType.Image,
        Transform = transform
    };

    [Fact]
    public void CurrentTransform_PreservesCropAfterConstruction()
    {
        var crop = new CropBox { Left = 0.1, Top = 0.2, Right = 0.3, Bottom = 0.4 };
        var item = new SourceItemViewModel(MakeSource(new Transform2D { X = 10, Y = 20, Width = 100, Height = 50, Crop = crop }));

        var transform = item.CurrentTransform();

        Assert.Equal(0.1, transform.Crop.Left);
        Assert.Equal(0.2, transform.Crop.Top);
        Assert.Equal(0.3, transform.Crop.Right);
        Assert.Equal(0.4, transform.Crop.Bottom);
    }

    [Fact]
    public void CurrentTransform_PreservesCrop_AfterSimulatedDrag()
    {
        // The exact bug: MainViewModel.UpdateSourceTransform sets item.X/Y/Width/Height directly
        // (as a drag handler would) and then calls item.CurrentTransform() to build the network
        // update. If CurrentTransform ever goes back to a bare `new Transform2D { X, Y, W, H }`,
        // this assertion catches it immediately.
        var item = new SourceItemViewModel(MakeSource(new Transform2D
        {
            Width = 100,
            Height = 50,
            Crop = new CropBox { Left = 0.25, Top = 0.1, Right = 0.05, Bottom = 0.15 }
        }));

        // simulate a drag: only X/Y/Width/Height change, exactly like MainWindow's mouse handlers do
        item.X = 500;
        item.Y = 300;
        item.Width = 200;
        item.Height = 120;

        var transform = item.CurrentTransform();

        Assert.Equal(500, transform.X);
        Assert.Equal(300, transform.Y);
        Assert.Equal(0.25, transform.Crop.Left);
        Assert.Equal(0.1, transform.Crop.Top);
        Assert.Equal(0.05, transform.Crop.Right);
        Assert.Equal(0.15, transform.Crop.Bottom);
    }

    [Fact]
    public void NoCrop_ContentSizeMatchesBoxSize()
    {
        var item = new SourceItemViewModel(MakeSource(new Transform2D { Width = 400, Height = 300 }));

        Assert.Equal(400, item.ContentWidth, precision: 5);
        Assert.Equal(300, item.ContentHeight, precision: 5);
        Assert.Equal(0, item.ContentMargin.Left, precision: 5);
        Assert.Equal(0, item.ContentMargin.Top, precision: 5);
    }

    [Fact]
    public void SymmetricCrop_DoublesContentSize_AndCentersOffset()
    {
        // Cropping 25% off left+right (50% total) means the visible 50% must be blown up 2x to
        // fill the box — and shifted left by exactly the cropped-off region's rendered width.
        var item = new SourceItemViewModel(MakeSource(new Transform2D
        {
            Width = 200,
            Height = 200,
            Crop = new CropBox { Left = 0.25, Right = 0.25, Top = 0, Bottom = 0 }
        }));

        Assert.Equal(400, item.ContentWidth, precision: 5); // 200 / (1 - 0.5)
        Assert.Equal(-100, item.ContentMargin.Left, precision: 5); // -0.25 * 400
    }

    [Fact]
    public void OnlyLeftCrop_ShiftsContentLeft_WithoutChangingHeight()
    {
        var item = new SourceItemViewModel(MakeSource(new Transform2D { Width = 100, Height = 100, Crop = new CropBox { Left = 0.5 } }));

        Assert.Equal(200, item.ContentWidth, precision: 5); // 100 / (1 - 0.5)
        Assert.Equal(-100, item.ContentMargin.Left, precision: 5); // -0.5 * 200
        Assert.Equal(100, item.ContentHeight, precision: 5); // untouched — no vertical crop
        Assert.Equal(0, item.ContentMargin.Top, precision: 5);
    }
}
