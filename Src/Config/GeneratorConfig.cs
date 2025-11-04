using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenerateDts.Config;

public sealed class GeneratorConfig
{
    [JsonPropertyName("skipNamespaces")]
    public List<string> SkipNamespaces { get; set; } = new();

    [JsonPropertyName("typeRenames")]
    public Dictionary<string, string> TypeRenames { get; set; } = new();

    [JsonPropertyName("skipMembers")]
    public List<string> SkipMembers { get; set; } = new();

    public static async Task<GeneratorConfig> LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        var config = JsonSerializer.Deserialize<GeneratorConfig>(json);
        return config ?? new GeneratorConfig();
    }
}
