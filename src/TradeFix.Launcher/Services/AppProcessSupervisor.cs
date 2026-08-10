using System.Diagnostics;
using System.IO;

namespace TradeFix.Launcher.Services;

/// <summary>
/// Starts/stops the sibling Master or Agent app based on the selected <see cref="LauncherRole"/>.
/// Expects the installed layout the installer script lays out:
/// <c>TradeFix Broadcast\Launcher\TradeFix.Launcher.exe</c> next to
/// <c>TradeFix Broadcast\Master\TradeFix.Master.exe</c> and
/// <c>TradeFix Broadcast\Agent\TradeFix.Agent.exe</c> — resolved relative to this exe's own
/// directory so it works regardless of where the whole folder was installed.
///
/// Launched processes are genuinely independent OS processes (<c>UseShellExecute = true</c>), not
/// child processes tied to the Launcher's lifetime — closing the Launcher (its tray icon) must
/// never take down an already-running Master/Agent; only an explicit role switch stops it.
/// </summary>
public sealed class AppProcessSupervisor : IDisposable
{
    private Process? _current;

    public LauncherRole? RunningRole { get; private set; }

    /// <summary>Fires when the app this supervisor started exits on its own (e.g. the operator
    /// closed its window) — not fired when <see cref="StopCurrent"/> was called deliberately.</summary>
    public event Action? ProcessExitedUnexpectedly;

    /// <param name="baseDirectory">Defaults to this exe's own directory in production; overridable
    /// so the path-resolution logic can be unit-tested against a temp folder instead of depending
    /// on a real installed layout.</param>
    public static string? ResolveExePath(LauncherRole role, string? baseDirectory = null)
    {
        var (folderName, exeName) = role switch
        {
            LauncherRole.Master => ("Master", "TradeFix.Master.exe"),
            LauncherRole.RenderNode => ("Agent", "TradeFix.Agent.exe"),
            _ => (null, null)
        };

        if (folderName is null || exeName is null)
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "..", folderName, exeName));
        return File.Exists(candidate) ? candidate : null;
    }

    /// <returns>true if the process actually started.</returns>
    public bool Start(LauncherRole role)
    {
        var exePath = ResolveExePath(role);
        if (exePath is null)
        {
            return false;
        }

        StopCurrent(expected: true);

        var process = Process.Start(new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(exePath),
            UseShellExecute = true
        });

        if (process is null)
        {
            return false;
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            var wasThisProcess = ReferenceEquals(_current, process);
            if (wasThisProcess)
            {
                _current = null;
                RunningRole = null;
                ProcessExitedUnexpectedly?.Invoke();
            }
        };

        _current = process;
        RunningRole = role;
        return true;
    }

    /// <summary>Gracefully asks the current app to close (falling back to a hard kill if it
    /// doesn't within a few seconds) — used when explicitly switching roles. <paramref name="expected"/>
    /// suppresses <see cref="ProcessExitedUnexpectedly"/> for this deliberate stop.</summary>
    public void StopCurrent(bool expected = true)
    {
        if (_current is not { HasExited: false } process)
        {
            _current = null;
            RunningRole = null;
            return;
        }

        if (expected)
        {
            process.EnableRaisingEvents = false;
        }

        try
        {
            process.CloseMainWindow();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort — it may have already exited between the check above and here
        }

        _current = null;
        RunningRole = null;
    }

    /// <summary>Releases this object's own handle to the tracked process without killing it — the
    /// Launcher shutting down must not take an independently-running Master/Agent down with it.</summary>
    public void Dispose()
    {
        if (_current is not null)
        {
            _current.EnableRaisingEvents = false;
            _current.Dispose();
            _current = null;
        }
    }
}
