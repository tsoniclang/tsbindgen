using System.Text;
using System.Text.Json;
using GenerateDts.Model;

namespace GenerateDts.Metadata;

/// <summary>
/// Serializes AssemblyMetadata to JSON format.
/// </summary>
public sealed class MetadataWriter
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Serializes assembly metadata to JSON string.
    /// </summary>
    public string SerializeMetadata(AssemblyMetadata metadata)
    {
        return JsonSerializer.Serialize(metadata, _jsonOptions);
    }

    /// <summary>
    /// Writes assembly metadata to a JSON file.
    /// </summary>
    public async Task WriteMetadataAsync(AssemblyMetadata metadata, string outputPath)
    {
        var json = SerializeMetadata(metadata);
        await File.WriteAllTextAsync(outputPath, json, Encoding.UTF8);
    }
}
