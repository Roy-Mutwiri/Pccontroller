# Node System

Implemented in `TradeFix.Network` (state machine, registry, pairing) and `TradeFix.Master`/
`TradeFix.Agent` (UI). This is the Phase 1 deliverable.

## Connection states

`TradeFix.Shared.Enums.NodeConnectionState`: `Connecting → Pairing` (first time) or
`Connecting → (auth) → Online` (reconnect with stored credentials) `→ Syncing → Synced`.
`Warning`/`Error`/`Offline` cover degraded/lost connections. Phase 1 drives nodes to `Online`;
`Syncing`/`Synced` are wired into the state enum now but only become meaningful once Phase 2
(project state sync) exists — right now a re-authenticated node is set to `Syncing` and stays
there, which is honest: there is no state to sync yet, so it never claims `Synced`.

## Node dashboard

`NodeRegistry` (`TradeFix.Network`) is the Master's live, in-memory, authoritative view of every
node (itself included, registered as role `Master`). It fires `NodeChanged`/`NodeRemoved` events;
`TradeFix.Master.ViewModels.MainViewModel` subscribes and marshals updates onto the UI thread.
Persisted *identity* (which nodes have ever paired) lives separately in SQLite
(`PairedNodeRepository`) — the registry is runtime state, rebuilt from scratch each Master launch.

## Pairing (spec section 8)

Originally this required typing three separate values into the Agent (Master IP, port, pairing
code) — in practice this was a real source of operator error (e.g. pasting the wrong PC's own
Tailscale IP by mistake). It's now a single **connect code**:

1. Operator clicks "Pair New Node" in the Master UI. Master auto-detects its own best-reachable
   address (`NetworkAddressHelper` — prefers a Tailscale address, then a private LAN address, then
   loopback) and combines it with a freshly issued `PairingService.IssueCode()` code (30-minute
   expiry, stored via `PairingCodeRepository`) into one string via `ConnectCode.Format`:
   `{code}@{host}:{port}`, e.g. `TRADE-8391@100.116.30.51:8791`. A "Copy" button puts it on the
   clipboard.
2. Operator pastes that single string into the Agent's "Connect to Master" box on the other PC and
   clicks Connect — nothing else to type. `ConnectCode.TryParse` extracts the code/host/port;
   the Agent connects to that host/port, and as soon as the Master signals it needs pairing, the
   Agent automatically submits the parsed code (`MainViewModel.BeginConnect`) — no second manual
   step. A fallback manual code-entry box still exists (still visible while state is `Pairing`) in
   case the parsed code failed (e.g. it expired between copy and paste).
3. Agent sends `PAIR_REQUEST`; Master atomically validates-and-consumes the code
   (`PairingCodeRepository.TryConsume` — a transaction, so the same code can't be redeemed twice
   even under a race), issues a new `nodeId` + session token, stores the token's SHA-256 hash, and
   registers the node.
4. Agent persists the returned credentials locally, encrypted at rest via Windows DPAPI
   (`CredentialStore`, current-user scope) — never plaintext on disk. It also remembers the
   Master's host/port (`AgentSettingsStore`). **On every future launch, if a Master is already
   known, the Agent reconnects automatically** — the "paste a connect code" screen only appears
   once, on first-ever setup for that PC.

## Reconnection (spec section 27)

`AgentConnection` runs a loop: connect → (pair or auth) → heartbeat-until-disconnected → on any
failure, exponential backoff (`ReconnectPolicy`: 1s, 2s, 4s, 8s, 16s, capped at 16s) → retry. A
node that already has stored credentials never needs to re-pair after a disconnect; it just
re-authenticates.

## Metrics (spec section 7/31)

`INodeMetricsProvider` / `BasicNodeMetricsProvider`:
- **CPU**: Windows Performance Counter `Processor / % Processor Time / _Total`.
- **RAM**: `GlobalMemoryStatusEx` (Win32, via P/Invoke) — `dwMemoryLoad` is the OS's own computed
  system-wide memory pressure percentage.
- **GPU**: Windows "GPU Engine" performance counter category, summing `Utilization Percentage`
  across `engtype_3D` instances. Best-effort — see [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md).
- **FPS / latency**: placeholders (`0`) until Phase 3 (rendering) and real round-trip timing exist;
  not fabricated.

## Simulation Mode (spec section 47)

`TradeFix.Network.Simulation.NodeSimulator` spins up a fake node that goes through the *exact
same* `AgentConnection` state machine as a real Agent — pairing, auth, heartbeat — over an
in-process transport (`InProcessMessageTransport`) instead of a real WebSocket. This is how Phase 1
was verified without physical PC2/PC3 hardware (see `tests/TradeFix.Network.Tests` and the
"Add Simulated Node" button in the Master dashboard). It is not a separate mock implementation —
if the real protocol logic has a bug, the simulator hits it too.

## First-Time Network Setup (real PC2/PC3 deployment)

The Master listens via `HttpListener` on `http://+:{port}/ws/` (all interfaces) so LAN peers can
reach it. On Windows, binding a wildcard prefix requires **either**:
- running the Master elevated (as Administrator), or
- a one-time URL ACL reservation:
  ```
  netsh http add urlacl url=http://+:8791/ws/ user=DOMAIN\username
  ```

If neither is available, `MasterHost` catches the failure and falls back to localhost-only
binding automatically (logged as a warning, surfaced in the dashboard) — **render nodes on other
PCs will not be able to connect** in that fallback mode, but the Master itself doesn't crash.

Windows Firewall will also prompt to allow the Master's inbound connections on first run; allow it
for Private networks. TradeFix never creates a firewall rule without explicit, per-action user
consent (spec section 29/49) — in this project's own setup, a rule scoped to exactly TCP 8791 was
added only after asking.

## When the PCs aren't on the same physical network

The pairing/heartbeat protocol only cares about IP reachability — it has no concept of "LAN" vs.
"VPN." If PC1/PC2/PC3 can't be plugged into the same router/Wi-Fi (e.g. same building, different
networks, or genuinely different locations), the recommended fix is a **mesh VPN** (Tailscale or
ZeroTier), not exposing the Master's port to the public internet:

1. Install Tailscale on all three PCs, sign in with the same account on each.
2. Each PC gets a stable virtual IP (e.g. `100.x.y.z`) that works regardless of physical network.
3. On the Agent, type that Tailscale IP into "Master IP address" instead of a local LAN IP —
   nothing else changes; `AgentConnection`/`WebSocketTransportFactory` have no idea the address
   isn't on a physical LAN.

No application code change was needed to support this — confirmed by connecting to a running
Master over its Tailscale address and getting the same response as over `localhost`. The
`http://+:{port}/ws/` wildcard bind (see above) already covers whatever virtual network adapter
Tailscale creates; the existing firewall rule (scoped to the port, `-Profile Any`) covers it too.

If a pre-existing VPN is already running on a node for unrelated reasons (e.g. a personal
privacy/streaming VPN), it doesn't help here — those route traffic to a remote exit server rather
than creating a shared network between the PCs — but it also generally doesn't conflict with
Tailscale running alongside it. If connectivity is flaky, try disabling the unrelated VPN while
running a broadcast.

## Not yet implemented

Version-compatibility gating (spec section 28: "PC3 ⚠ UPDATE REQUIRED"), remote-desktop
diagnostic view (section 25), emergency controls beyond "Add Simulated Node" (section 26), and
group/global/selected-node command modes (section 41) all require Phase 2+ state sync to be
meaningful and are not implemented yet.
