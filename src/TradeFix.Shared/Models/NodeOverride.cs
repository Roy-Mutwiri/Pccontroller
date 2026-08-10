using System.Text.Json;

namespace TradeFix.Shared.Models;

/// <summary>
/// An explicit, per-node divergence from the global source definition (spec section 40:
/// "Local Overrides"). Absence of an override for a node means it follows the global state.
/// </summary>
public sealed record NodeOverride
{
    public Transform2D? Transform { get; init; }
    public JsonElement? Config { get; init; }
}
