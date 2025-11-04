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

    // Naming transform options (CLI-only, not from config file)
    [JsonIgnore]
    public NameTransformOption NamespaceNames { get; set; } = NameTransformOption.None;

    [JsonIgnore]
    public NameTransformOption ClassNames { get; set; } = NameTransformOption.None;

    [JsonIgnore]
    public NameTransformOption InterfaceNames { get; set; } = NameTransformOption.None;

    [JsonIgnore]
    public NameTransformOption MethodNames { get; set; } = NameTransformOption.None;

    [JsonIgnore]
    public NameTransformOption PropertyNames { get; set; } = NameTransformOption.None;

    [JsonIgnore]
    public NameTransformOption EnumMemberNames { get; set; } = NameTransformOption.None;

    [JsonIgnore]
    public NameTransformOption BindingNames { get; set; } = NameTransformOption.None;

    public static async Task<GeneratorConfig> LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        var config = JsonSerializer.Deserialize<GeneratorConfig>(json);
        return config ?? new GeneratorConfig();
    }
}
