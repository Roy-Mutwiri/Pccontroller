using System.Diagnostics;
using System.Text.Json;

namespace TradeFix.Network;

public enum TailscaleBackendState
{
    NotInstalled,
    NeedsLogin,
    Stopped,
    Running,
    Unknown
}

public sealed record TailscaleStatus(TailscaleBackendState State, bool SelfOnline);

/// <summary>
/// Local Tailscale health: is it installed, signed in, and actually connected? Cross-network
/// setups (a render node in a different building than the Master) depend entirely on Tailscale
/// being up on BOTH ends, and "someone signed out of / quit Tailscale" looks identical to "the
/// Master app is broken" from the other PC — nodes just cycle Connecting → Offline. Both apps
/// use this to detect that state, silently reconnect a merely-stopped Tailscale
/// (<see cref="TryStartAsync"/> needs no admin rights — the CLI talks to the already-elevated
/// service), and tell the operator in plain words when only a human can act (sign-in).
/// </summary>
public static class TailscaleHealth
{
    public static string? FindExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            "tailscale.exe"
        };
        return candidates.FirstOrDefault(p => p == "tailscale.exe" || File.Exists(p));
    }

    public static async Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var exe = FindExe();
        if (exe is null)
        {
            return new TailscaleStatus(TailscaleBackendState.NotInstalled, false);
        }

        string json;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "status", "--json" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return new TailscaleStatus(TailscaleBackendState.NotInstalled, false);
            }

            json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new TailscaleStatus(TailscaleBackendState.NotInstalled, false);
        }
        catch
        {
            return new TailscaleStatus(TailscaleBackendState.Unknown, false);
        }

        return Parse(json);
    }

    /// <summary>Split out from <see cref="GetStatusAsync"/> so the JSON shapes the real CLI emits
    /// can be unit-tested without Tailscale installed.</summary>
    public static TailscaleStatus Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var backend = doc.RootElement.TryGetProperty("BackendState", out var stateElement)
                ? stateElement.GetString()
                : null;

            var state = backend switch
            {
                "Running" or "Starting" => TailscaleBackendState.Running,
                "Stopped" => TailscaleBackendState.Stopped,
                "NeedsLogin" or "NoState" or "NeedsMachineAuth" => TailscaleBackendState.NeedsLogin,
                _ => TailscaleBackendState.Unknown
            };

            var selfOnline = doc.RootElement.TryGetProperty("Self", out var self)
                && self.ValueKind == JsonValueKind.Object
                && self.TryGetProperty("Online", out var online)
                && online.ValueKind == JsonValueKind.True;

            return new TailscaleStatus(state, selfOnline);
        }
        catch
        {
            return new TailscaleStatus(TailscaleBackendState.Unknown, false);
        }
    }

    /// <summary>How this PC's ACTIVE peer connections are being carried: direct (fast) vs bounced
    /// through a Tailscale relay server (DERP — works everywhere but adds hundreds of ms and
    /// limited bandwidth; happens when NAT traversal fails on either end). Relayed active peers
    /// are why "it works but lags badly" — surfaced to the operator instead of left invisible.</summary>
    public sealed record TailscalePathSummary(int DirectPeers, int RelayedPeers);

    public static async Task<TailscalePathSummary> GetPeerPathsAsync(CancellationToken cancellationToken = default)
    {
        var exe = FindExe();
        if (exe is null)
        {
            return new TailscalePathSummary(0, 0);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "status", "--json" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return new TailscalePathSummary(0, 0);
            }

            var json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return ParsePeerPaths(json);
        }
        catch
        {
            return new TailscalePathSummary(0, 0);
        }
    }

    /// <summary>Split out for unit testing against real CLI JSON. A peer counts only while
    /// Active (traffic currently flowing); CurAddr empty on an active peer means every packet is
    /// detouring through the relay named in Relay.</summary>
    public static TailscalePathSummary ParsePeerPaths(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object)
            {
                return new TailscalePathSummary(0, 0);
            }

            int direct = 0, relayed = 0;
            foreach (var peer in peers.EnumerateObject())
            {
                var active = peer.Value.TryGetProperty("Active", out var activeElement)
                    && activeElement.ValueKind == JsonValueKind.True;
                if (!active)
                {
                    continue;
                }

                var hasDirectAddress = peer.Value.TryGetProperty("CurAddr", out var curAddr)
                    && curAddr.GetString() is { Length: > 0 };
                if (hasDirectAddress)
                {
                    direct++;
                }
                else
                {
                    relayed++;
                }
            }

            return new TailscalePathSummary(direct, relayed);
        }
        catch
        {
            return new TailscalePathSummary(0, 0);
        }
    }

    /// <summary>Reconnects a signed-in-but-stopped Tailscale (someone hit "Disconnect" or the
    /// service came up disconnected). Returns false when it can't — most importantly when the
    /// account is signed out, which only an interactive login fixes.</summary>
    public static async Task<bool> TryStartAsync(CancellationToken cancellationToken = default)
    {
        var exe = FindExe();
        if (exe is null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "up", "--timeout=25s" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(35), cancellationToken);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Kicks off the interactive sign-in: <c>tailscale login</c> opens the account page
    /// in the default browser. Fire-and-forget — completion shows up in the next status poll.</summary>
    public static void OpenLoginFlow()
    {
        var exe = FindExe();
        if (exe is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "login" },
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // best-effort — the banner keeps showing what's wrong either way
        }
    }
}
