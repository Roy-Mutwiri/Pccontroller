using CommunityToolkit.Mvvm.ComponentModel;

namespace TradeFix.Master.ViewModels;

public sealed partial class SceneItemViewModel(string id, string name) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;

    [ObservableProperty] private bool _isActive;
}
