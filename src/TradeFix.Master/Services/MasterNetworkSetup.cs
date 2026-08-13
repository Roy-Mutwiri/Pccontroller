using System.Diagnostics;
using System.IO;

namespace TradeFix.Master.Services;

/// <summary>
/// The one-click, in-app version of installer\Enable-MasterNetworking.ps1: reserves the control
/// server's URL ACLs and opens the firewall for the control and discovery ports, via a single
/// elevated PowerShell run (one UAC "Yes"). Windows requires admin rights for both operations —
/// there is no way around the UAC prompt — but everything else (writing the script, running it,
/// rebinding the listener afterward) is automatic, so a non-technical operator never touches
/// netsh or right-click-run-as-administrator.
/// </summary>
public static class MasterNetworkSetup
{
    /// <summary>Runs the elevated setup and returns whether it completed. False usually means the
    /// operator clicked No on the UAC prompt — safe to just ask again.</summary>
    public static async Task<bool> RunElevatedAsync(int controlPort, int discoveryPort)
    {
        // "Everyone" is a localized account name (e.g. "Jeder" on German Windows) — resolve it
        // from the well-known SID S-1-1-0 so the urlacl grant works on any display language.
        var script = $$"""
            $ErrorActionPreference = 'Continue'
            $everyone = (New-Object System.Security.Principal.SecurityIdentifier('S-1-1-0')).Translate([System.Security.Principal.NTAccount]).Value
            foreach ($path in @('ws','assets','media','audio')) {
                $url = "http://+:{{controlPort}}/$path/"
                netsh http delete urlacl url=$url | Out-Null
                netsh http add urlacl url=$url user="$everyone" | Out-Null
            }
            Remove-NetFirewallRule -DisplayName 'TradeFix Broadcast Master (control)' -ErrorAction SilentlyContinue
            Remove-NetFirewallRule -DisplayName 'TradeFix Broadcast Master (discovery)' -ErrorAction SilentlyContinue
            New-NetFirewallRule -DisplayName 'TradeFix Broadcast Master (control)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {{controlPort}} -Profile Any | Out-Null
            New-NetFirewallRule -DisplayName 'TradeFix Broadcast Master (discovery)' -Direction Inbound -Action Allow -Protocol UDP -LocalPort {{discoveryPort}} -Profile Any | Out-Null
            exit 0
            """;

        var scriptPath = Path.Combine(Path.GetTempPath(), $"tfx-enable-networking-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true, // required for the runas verb (UAC elevation)
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
            {
                return false;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // the operator clicked No on the UAC prompt
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch
            {
                // a leftover temp script is harmless
            }
        }
    }
}
