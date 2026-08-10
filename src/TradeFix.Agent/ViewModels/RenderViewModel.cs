using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TradeFix.Protocol.Messages;
using TradeFix.Shared.Models;

namespace TradeFix.Agent.ViewModels;

/// <summary>Mirrors the Master's active scene: a live collection of sources, replaced wholesale
/// on LOAD_SCENE and patched in place on UPDATE_SOURCE (drag/resize).</summary>
public sealed partial class RenderViewModel : ObservableObject
{
    public ObservableCollection<RenderSourceItemViewModel> Sources { get; } = [];

    [ObservableProperty] private bool _hasContent;

    public void ApplyScene(LoadSceneDefinitionPayload scene)
    {
        Sources.Clear();
        foreach (var source in scene.Sources)
        {
            Sources.Add(new RenderSourceItemViewModel(source));
        }

        HasContent = Sources.Count > 0;
    }

    public void ApplySourceUpdate(SourceDefinition source)
    {
        var existing = Sources.FirstOrDefault(s => s.Id == source.Id);
        if (existing is not null)
        {
            existing.ApplyTransform(source.Transform);
        }
        else
        {
            Sources.Add(new RenderSourceItemViewModel(source));
            HasContent = true;
        }
    }

    public void ApplyAssetPath(string sourceId, string localPath)
    {
        var existing = Sources.FirstOrDefault(s => s.Id == sourceId);
        if (existing is not null)
        {
            existing.ImagePath = localPath;
        }
    }

    public void ApplyLiveFrame(string sourceId, byte[] jpegBytes)
    {
        var existing = Sources.FirstOrDefault(s => s.Id == sourceId);
        existing?.ApplyLiveFrame(jpegBytes);
    }
}
