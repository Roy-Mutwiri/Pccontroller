namespace TradeFix.Protocol;

public static class ProtocolVersion
{
    /// <summary>Wire protocol version. Bumped only on breaking changes to <see cref="Envelope"/>
    /// or command semantics — independent of application (Master/Agent) version numbers.
    /// See docs/PROTOCOL.md.</summary>
    public const int Current = 1;
}
