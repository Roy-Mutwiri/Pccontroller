using System.Text.Json;

namespace TradeFix.Shared.Models;

public sealed record SourceFilter(string Id, string Type, JsonElement Config, bool Enabled = true);
