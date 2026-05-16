using System.Text.Json;
using System.Text.Json.Serialization;

namespace KusPus.Persistence.Json;

/// <summary>
/// Single source of truth for <c>settings.json</c> serialization. The spec example
/// in TECH_SPEC §9.1 shows camelCase property names and PascalCase enum strings
/// (e.g. <c>"LeftCtrl"</c>). This options object enforces both.
/// </summary>
internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters =
        {
            // Enum members serialise as their declared names (identity, no policy).
            new JsonStringEnumConverter(),
        },
    };
}
