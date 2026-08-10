using TradeFix.Shared.Enums;

namespace TradeFix.Shared.Models;

/// <summary>
/// Maps a logical, device-category source (Camera, Microphone, WindowCapture, DisplayCapture)
/// to a concrete local device/window on one specific node. Spec section 6/19/20/21.
/// </summary>
public sealed record DeviceMapping
{
    public required string LogicalSourceId { get; init; }
    public required string NodeId { get; init; }
    public required SourceType DeviceType { get; init; }

    /// <summary>Stable local identifier (device path / window handle key / process name pattern).</summary>
    public required string DeviceIdentifier { get; init; }
    public required string DeviceDisplayName { get; init; }
    public bool IsConnected { get; init; } = true;
}
