using System.Text.Json;
using TradeFix.Protocol.Messages;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Master.Services;

/// <summary>
/// In-memory authoritative project state: scenes and the sources within them. Master is the
/// single source of truth (spec section 39) — nodes only ever apply what this pushes to them.
/// Not yet persisted to disk/SQLite (Phase 2 still to do); lives for the app's lifetime.
/// </summary>
public sealed class ProjectState
{
    private readonly Dictionary<string, SourceDefinition> _sources = new();
    private readonly List<SceneDefinition> _scenes = [];
    private readonly object _lock = new();

    /// <summary>Structural changes (scene added/switched, source added/removed/reconfigured) —
    /// subscribers should do a full resync (send LOAD_SCENE).</summary>
    public event Action? Changed;

    /// <summary>A single source's transform changed (drag/resize) — cheap, high-frequency;
    /// subscribers should coalesce/rate-limit rather than resyncing the whole scene.</summary>
    public event Action<SourceDefinition>? SourceTransformChanged;

    public string ActiveSceneId { get; private set; }

    public ProjectState()
    {
        var mainScene = new SceneDefinition { Id = NewId(), Name = "Main", Order = 0 };
        _scenes.Add(mainScene);
        ActiveSceneId = mainScene.Id;
    }

    public IReadOnlyList<SceneDefinition> Scenes
    {
        get { lock (_lock) { return _scenes.ToList(); } }
    }

    public SceneDefinition AddScene(string name)
    {
        SceneDefinition scene;
        lock (_lock)
        {
            scene = new SceneDefinition { Id = NewId(), Name = name, Order = _scenes.Count };
            _scenes.Add(scene);
            ActiveSceneId = scene.Id;
        }

        Changed?.Invoke();
        return scene;
    }

    public void SwitchScene(string sceneId)
    {
        lock (_lock)
        {
            if (_scenes.All(s => s.Id != sceneId))
            {
                return;
            }

            ActiveSceneId = sceneId;
        }

        Changed?.Invoke();
    }

    public SourceDefinition AddSource(SourceType type, string name, JsonElement config, Transform2D? transform = null)
    {
        SourceDefinition source;
        lock (_lock)
        {
            source = new SourceDefinition
            {
                Id = NewId(),
                Name = name,
                Type = type,
                Config = config,
                Transform = transform ?? new Transform2D { X = 300, Y = 200, Width = 320, Height = 180 }
            };
            _sources[source.Id] = source;

            var scene = GetSceneUnlocked(ActiveSceneId);
            var sceneIndex = _scenes.IndexOf(scene);
            _scenes[sceneIndex] = scene with
            {
                Sources = [.. scene.Sources, new SceneSourceRef { SourceId = source.Id, Layer = scene.Sources.Count }]
            };
        }

        Changed?.Invoke();
        return source;
    }

    public void RemoveSource(string sourceId)
    {
        lock (_lock)
        {
            _sources.Remove(sourceId);
            for (var i = 0; i < _scenes.Count; i++)
            {
                _scenes[i] = _scenes[i] with { Sources = [.. _scenes[i].Sources.Where(r => r.SourceId != sourceId)] };
            }
        }

        Changed?.Invoke();
    }

    public void UpdateTransform(string sourceId, Transform2D transform)
    {
        SourceDefinition updated;
        lock (_lock)
        {
            if (!_sources.TryGetValue(sourceId, out var existing))
            {
                return;
            }

            updated = existing with { Transform = transform };
            _sources[sourceId] = updated;
        }

        SourceTransformChanged?.Invoke(updated);
    }

    public void UpdateConfig(string sourceId, JsonElement config)
    {
        lock (_lock)
        {
            if (!_sources.TryGetValue(sourceId, out var existing))
            {
                return;
            }

            _sources[sourceId] = existing with { Config = config };
        }

        Changed?.Invoke();
    }

    public LoadSceneDefinitionPayload BuildActiveScenePayload()
    {
        lock (_lock)
        {
            var scene = GetSceneUnlocked(ActiveSceneId);
            var sources = scene.Sources
                .Select(r => _sources.GetValueOrDefault(r.SourceId))
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

            return new LoadSceneDefinitionPayload { Scene = scene, Sources = sources };
        }
    }

    public IReadOnlyList<SourceDefinition> ActiveSceneSources
    {
        get
        {
            lock (_lock)
            {
                var scene = GetSceneUnlocked(ActiveSceneId);
                return scene.Sources
                    .Select(r => _sources.GetValueOrDefault(r.SourceId))
                    .Where(s => s is not null)
                    .Select(s => s!)
                    .ToList();
            }
        }
    }

    private SceneDefinition GetSceneUnlocked(string sceneId) => _scenes.First(s => s.Id == sceneId);

    private static string NewId() => Guid.NewGuid().ToString("n");
}
