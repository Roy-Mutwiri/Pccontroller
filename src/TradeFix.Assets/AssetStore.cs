using System.Security.Cryptography;

namespace TradeFix.Assets;

/// <summary>
/// Content-addressed local file cache: every asset is identified by the SHA-256 hash of its
/// bytes (spec section 16 — "do not transfer the same file repeatedly"). Used identically on
/// Master (source of truth for files added locally) and on each Agent (cache of files fetched
/// from Master). Not yet wired to resumable/chunked transfer (spec section 5/38) — files are
/// fetched whole over HTTP; fine for the image sizes this is used for today.
/// </summary>
public sealed class AssetStore
{
    private readonly string _directory;

    public AssetStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Copies a local file into the store, keyed by its content hash. Returns the hash.
    /// If a file with that hash already exists, does not copy again.</summary>
    public string SaveFile(string sourceFilePath)
    {
        var hash = ComputeHash(sourceFilePath);
        var destination = PathFor(hash, Path.GetExtension(sourceFilePath));
        if (!File.Exists(destination))
        {
            File.Copy(sourceFilePath, destination, overwrite: true);
        }

        return hash;
    }

    /// <summary>Writes already-downloaded bytes into the store under the given hash. Verifies
    /// the bytes actually hash to the claimed value before accepting them.</summary>
    public bool TrySaveBytes(string hash, string extension, byte[] bytes)
    {
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        File.WriteAllBytes(PathFor(hash, extension), bytes);
        return true;
    }

    public string? GetFilePath(string hash)
    {
        var match = Directory.EnumerateFiles(_directory, $"{hash}.*").FirstOrDefault();
        return match;
    }

    public bool Contains(string hash) => GetFilePath(hash) is not null;

    private string PathFor(string hash, string extension) => Path.Combine(_directory, $"{hash}{extension}");

    private static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
