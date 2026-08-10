namespace TradeFix.Shared.Enums;

/// <summary>
/// Whether a source's definition can be reproduced identically on every node (Logical),
/// or whether it requires a per-node <see cref="Models.DeviceMapping"/> (DeviceMapped).
/// See docs/SOURCE_SYSTEM.md section "Synchronization Model".
/// </summary>
public enum SourceCategory
{
    Logical,
    DeviceMapped
}
