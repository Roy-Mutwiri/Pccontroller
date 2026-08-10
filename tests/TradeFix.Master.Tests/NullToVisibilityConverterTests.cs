using System.Windows;
using TradeFix.Master.Converters;

namespace TradeFix.Master.Tests;

/// <summary>
/// Regression test for a real bug found during manual testing: the converter checked
/// <c>value as string</c>, so binding it to any non-string object (like the Properties panel's
/// <c>SelectedSource</c>) always evaluated as "empty" and collapsed — regardless of whether
/// something was actually selected. The entire Properties panel was permanently invisible as a
/// result. Covers both the plain and Invert paths, and both string and non-string bindings.
/// </summary>
public class NullToVisibilityConverterTests
{
    private readonly NullToVisibilityConverter _converter = new();

    private sealed record SomeObject(string Name);

    [Fact]
    public void NonNullObject_IsVisible()
    {
        var result = _converter.Convert(new SomeObject("selected source"), typeof(Visibility), null, null!);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void NullObject_IsCollapsed()
    {
        var result = _converter.Convert(null, typeof(Visibility), null, null!);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void NonNullObject_WithInvert_IsCollapsed()
    {
        var result = _converter.Convert(new SomeObject("selected source"), typeof(Visibility), "Invert", null!);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void NullObject_WithInvert_IsVisible()
    {
        var result = _converter.Convert(null, typeof(Visibility), "Invert", null!);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void NonEmptyString_IsVisible()
    {
        var result = _converter.Convert("TRADE-1234", typeof(Visibility), null, null!);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void EmptyString_IsCollapsed()
    {
        var result = _converter.Convert(string.Empty, typeof(Visibility), null, null!);
        Assert.Equal(Visibility.Collapsed, result);
    }
}
