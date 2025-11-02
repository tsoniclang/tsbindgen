# generatedts

A .NET tool that generates TypeScript declaration files (`.d.ts`) from .NET assemblies for use with the Tsonic compiler.

## Overview

`generatedts` uses reflection to analyze .NET assemblies and produces TypeScript declarations that follow Tsonic's interop rules. This allows TypeScript code compiled with Tsonic to properly type-check when using .NET libraries.

## Installation

Build the tool from source:

```bash
dotnet build
```

## Usage

### Basic Usage

```bash
generatedts <assembly-path>
```

Example:

```bash
generatedts /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.0/ref/net8.0/System.Text.Json.dll
# Creates:
#   ./System.Text.Json.d.ts
#   ./System.Text.Json.metadata.json
```

### Command-Line Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--namespaces` | `-n` | Comma-separated list of namespaces to include | All namespaces |
| `--out-dir` | `-o` | Output directory for generated file | `.` (current directory) |
| `--log` | `-l` | Path to write JSON log file | None |
| `--config` | `-c` | Path to configuration JSON file | None |

### Examples

**Filter specific namespaces:**

```bash
generatedts System.Text.Json.dll --namespaces System.Text.Json.Serialization
```

**Specify output directory:**

```bash
generatedts System.Net.Http.dll --out-dir ./declarations
```

**Generate with logging:**

```bash
generatedts System.IO.dll --log build.log.json
```

## Generated Output

The tool generates two files for each assembly:

1. **TypeScript declarations** (`.d.ts`) - TypeScript type definitions
2. **Metadata sidecar** (`.metadata.json`) - C# semantic information

### TypeScript Declarations

The `.d.ts` file contains TypeScript declarations with:

1. **Branded type aliases** for C# numeric types:
   ```typescript
   type int = number & { __brand: "int" };
   type decimal = number & { __brand: "decimal" };
   // ... etc
   ```

2. **Namespace declarations** matching .NET namespaces:
   ```typescript
   declare namespace System.Text.Json {
     class JsonSerializer {
       static Serialize<T>(value: T): string;
     }
   }
   ```

3. **Proper type mappings**:
   - `System.String` → `string`
   - `System.Int32` → `int`
   - `System.Boolean` → `boolean`
   - `Task<T>` → `Promise<T>`
   - `T[]` → `ReadonlyArray<T>`
   - `List<T>` → `List<T>`
   - `Nullable<T>` → `T | null`

### Metadata Sidecar Files

The `.metadata.json` file contains C# semantic information that TypeScript cannot express. This enables the Tsonic compiler to generate correct C# code, particularly for:

- **Virtual/override methods** - Required to correctly override base class methods
- **Abstract classes/methods** - Required to properly extend abstract types
- **Sealed classes/methods** - Prevents invalid inheritance
- **Static classes** - Type-level restrictions
- **Struct vs Class** - Value vs reference type semantics
- **Method accessibility** - Public, protected, private, internal modifiers

#### Example Structure

```json
{
  "assemblyName": "System.Text.Json",
  "assemblyVersion": "10.0.0.0",
  "types": {
    "System.Text.Json.JsonSerializer": {
      "kind": "class",
      "isAbstract": true,
      "isSealed": false,
      "isStatic": false,
      "baseType": null,
      "interfaces": [],
      "members": {
        "Serialize<T>(T)": {
          "kind": "method",
          "isVirtual": false,
          "isAbstract": false,
          "isSealed": false,
          "isOverride": false,
          "isStatic": true,
          "accessibility": "public"
        },
        "Deserialize<T>(string)": {
          "kind": "method",
          "isVirtual": false,
          "isAbstract": false,
          "isSealed": false,
          "isOverride": false,
          "isStatic": true,
          "accessibility": "public"
        }
      }
    }
  }
}
```

#### Metadata Fields

**Type-level fields:**
- `kind`: `"class"`, `"struct"`, `"interface"`, or `"enum"`
- `isAbstract`: True for abstract classes (excluding interfaces)
- `isSealed`: True for sealed classes (excluding value types and enums)
- `isStatic`: True for static classes
- `baseType`: Full name of base class (if any)
- `interfaces`: Array of implemented interface names
- `members`: Dictionary of member metadata keyed by signature

**Member-level fields:**
- `kind`: `"method"`, `"property"`, or `"constructor"`
- `isVirtual`: True if method can be overridden
- `isAbstract`: True for abstract methods
- `isSealed`: True if method prevents further overriding
- `isOverride`: True if method overrides a base method
- `isStatic`: True for static members
- `accessibility`: `"public"`, `"protected"`, `"private"`, `"internal"`, etc.

**Signature format:**
- Methods: `MethodName(Type1,Type2,...)` using C# type names
- Properties: `PropertyName`
- Constructors: `ctor(Type1,Type2,...)`

## Configuration File

You can provide a JSON configuration file to customize behavior:

```json
{
  "skipNamespaces": ["System.Internal"],
  "typeRenames": {
    "System.OldType": "NewType"
  },
  "skipMembers": [
    "System.String::InternalMethod"
  ]
}
```

Usage:

```bash
generatedts Assembly.dll --config config.json
```

## Log Output

When using `--log`, a JSON file is generated with:

```json
{
  "timestamp": "2025-11-01T13:03:38Z",
  "namespaces": ["System.Text.Json"],
  "typeCounts": {
    "classes": 40,
    "interfaces": 5,
    "enums": 10,
    "total": 55
  },
  "warnings": []
}
```

## Type Mapping Rules

The tool follows Tsonic's type mapping specification:

- **Classes** → TypeScript classes
- **Interfaces** → TypeScript interfaces
- **Enums** → TypeScript enums
- **Structs** → TypeScript classes
- **Static methods** → `static` methods
- **Properties** → TypeScript properties (with `readonly` when appropriate)
- **Generic types** → TypeScript generics `<T>`
- **Optional parameters** → `param?: Type`
- **Params arrays** → `...values: ReadonlyArray<T>`

## Excluded Members

The tool automatically skips:

- Private and internal members
- Compiler-generated types
- Common Object methods (`Equals`, `GetHashCode`, `ToString`, `GetType`, `ReferenceEquals`)
- Special-name members (property accessors, backing fields)

## Development

### Project Structure

```
generatedts/
├── Src/
│   ├── Program.cs              # CLI entry point
│   ├── AssemblyProcessor.cs    # Reflection and type/metadata extraction
│   ├── TypeMapper.cs           # C# to TypeScript type mapping
│   ├── DeclarationRenderer.cs  # TypeScript output generation
│   ├── TypeInfo.cs             # Data structures for declarations
│   ├── MetadataModel.cs        # Data structures for metadata
│   ├── SignatureFormatter.cs   # Method/property signature formatting
│   ├── MetadataWriter.cs       # JSON metadata serialization
│   ├── GeneratorConfig.cs      # Configuration support
│   └── GenerationLogger.cs     # Logging functionality
└── README.md
```

### Building

```bash
dotnet build
```

### Running

```bash
dotnet run --project Src -- <assembly-path> [options]
```

## Related Documentation

- [Tsonic Type Mappings](../tsonic/spec/04-type-mappings.md)
- [.NET Interop](../tsonic/spec/08-dotnet-interop.md)
- [.NET Declarations](../tsonic/spec/14-dotnet-declarations.md)

## License

See LICENSE file for details.
