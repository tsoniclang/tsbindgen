using System.Text.Json;
using System.Text.Json.Serialization;
using GenerateDts.Model;

namespace GenerateDts.Diagnostics;

public sealed class GenerationLogger
{
    public string CreateLog(ProcessedAssembly assembly)
    {
        var log = new LogData
        {
            Timestamp = DateTime.UtcNow,
            Namespaces = assembly.Namespaces.Select(ns => ns.Name).ToList(),
            TypeCounts = new TypeCounts
            {
                Classes = assembly.Namespaces.Sum(ns => ns.Types.OfType<ClassDeclaration>().Count()),
                Interfaces = assembly.Namespaces.Sum(ns => ns.Types.OfType<InterfaceDeclaration>().Count()),
                Enums = assembly.Namespaces.Sum(ns => ns.Types.OfType<EnumDeclaration>().Count()),
                Total = assembly.Namespaces.Sum(ns => ns.Types.Count)
            },
            Warnings = assembly.Warnings.ToList()
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        return JsonSerializer.Serialize(log, options);
    }

    private sealed class LogData
    {
        public DateTime Timestamp { get; set; }
        public List<string> Namespaces { get; set; } = new();
        public TypeCounts TypeCounts { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    private sealed class TypeCounts
    {
        public int Classes { get; set; }
        public int Interfaces { get; set; }
        public int Enums { get; set; }
        public int Total { get; set; }
    }
}
