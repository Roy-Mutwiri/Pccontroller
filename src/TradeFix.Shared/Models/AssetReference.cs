namespace TradeFix.Shared.Models;

/// <summary>Metadata for a media asset (image/video/audio file). The file itself lives under
/// the project's assets/ directory on disk, never inside the database. See docs/OUTPUT.md and
/// spec section 16 (media synchronization uses <see cref="Sha256"/> to avoid redundant transfer).</summary>
public sealed record AssetReference
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string RelativePath { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public string? MimeType { get; init; }
}
