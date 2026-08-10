namespace TradeFix.Protocol;

/// <summary>
/// Full catalog of control-channel message types. Connection-lifecycle types are implemented in
/// Phase 1; state/scene/source/media types are defined now (per spec section 9) so the protocol
/// contract is stable, and implemented incrementally in later phases per docs/DEVELOPMENT_PLAN.md.
/// </summary>
public enum CommandType
{
    // --- Connection lifecycle (Phase 1) ---
    Hello,
    PairRequest,
    PairResponse,
    AuthRequest,
    AuthResponse,
    Heartbeat,
    NodeStatus,
    Ping,
    Pong,
    Error,
    Disconnect,

    // --- Project / scene / source state (Phase 2+) ---
    LoadProject,
    LoadScene,
    AddSource,
    RemoveSource,
    UpdateSource,
    MoveSource,
    ResizeSource,
    SetVisibility,
    SetOpacity,
    SetLayer,

    // --- Media (Phase 5/7) ---
    PlayMedia,
    PauseMedia,
    StopMedia,
    SeekMedia,
    SetAudioVolume,
    MuteAudio,
    UnmuteAudio,

    // --- Output (Phase 8) ---
    StartOutput,
    StopOutput,
    RestartRenderer,

    // --- Synchronization (Phase 2) ---
    SyncState,
    RequestStatus
}
