using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeFix.Common;
using TradeFix.Network.Client;

namespace TradeFix.Agent.Services;

/// <summary>
/// Persists the paired session token encrypted at rest via Windows DPAPI (current-user scope),
/// per spec section 29 ("store credentials securely", "no default universal password"). DPAPI
/// keys are tied to the Windows user account, so the file is unreadable outside this account.
/// </summary>
public static class CredentialStore
{
    private static string FilePath => Path.Combine(AppPaths.DataRoot("Agent"), "credentials.bin");

    public static AgentCredentials? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(FilePath);
            var decrypted = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AgentCredentials>(Encoding.UTF8.GetString(decrypted));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(AgentCredentials credentials)
    {
        var json = JsonSerializer.Serialize(credentials);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, encrypted);
    }

    public static void Clear()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
