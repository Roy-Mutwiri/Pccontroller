using CommunityToolkit.Mvvm.ComponentModel;

namespace TradeFix.Master.ViewModels;

/// <summary>
/// One transient notification in the Master's toast stack (bottom-right of MainWindow) — used
/// for node online/offline events so the operator notices them without watching the log panel.
/// Lifetime is driven by MainViewModel: shown → after a few seconds <see cref="IsClosing"/>
/// flips (XAML fade-out animation runs) → removed shortly after.
/// </summary>
public sealed partial class ToastViewModel : ObservableObject
{
    public required string Message { get; init; }

    /// <summary>True for "good news" (node online) — accents the toast green vs warning amber.</summary>
    public required bool IsPositive { get; init; }

    [ObservableProperty] private bool _isClosing;
}
