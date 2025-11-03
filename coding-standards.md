# Coding Standards - generatedts

## C# Code Style

### General Principles

- **Target Framework**: .NET 10.0
- **Language Version**: C# 13 (latest features allowed)
- **Naming**: Follow .NET naming conventions
- **Null Safety**: Use nullable reference types (`#nullable enable`)

### Naming Conventions

```csharp
// Classes, Interfaces, Methods, Properties - PascalCase
public class AssemblyProcessor { }
public interface ITypeMapper { }
public void ProcessType() { }
public string TypeName { get; }

// Private fields - camelCase with underscore prefix
private readonly TypeMapper _typeMapper;
private readonly List<string> _warnings;

// Parameters, local variables - camelCase
public void MapType(Type type)
{
    var fullTypeName = GetFullTypeName(type);
}

// Constants - PascalCase
private const int MaxTypeDepth = 10;
```

### File Organization

Each file should contain:
1. Using statements (sorted alphabetically)
2. Single namespace declaration
3. Single class/interface/enum

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace GenerateDts;

public sealed class TypeMapper
{
    // Implementation
}
```

### Method Structure

- **Order**: Public methods first, then private methods
- **Size**: Keep methods focused and under 50 lines when possible
- **Comments**: Use XML documentation for public APIs

```csharp
/// <summary>
/// Maps a .NET type to its TypeScript representation.
/// </summary>
/// <param name="type">The .NET type to map</param>
/// <returns>The TypeScript type name</returns>
public string MapType(Type type)
{
    // Implementation
}
```

### Error Handling

- **Warnings**: Use `AddWarning()` for non-fatal issues
- **Exceptions**: Only for truly exceptional conditions
- **Validation**: Check preconditions early

```csharp
// Good - Warning for edge case
if (type.IsPointer)
{
    AddWarning($"Pointer type {type.Name} mapped to 'any'");
    return "any";
}

// Good - Early validation
if (string.IsNullOrWhiteSpace(fullTypeName))
{
    AddWarning($"Type {type} has no name - mapped to 'any'");
    return "any";
}
```

### LINQ and Functional Style

- Prefer LINQ for collections when it improves readability
- Use method chaining for transformations
- Avoid overly complex LINQ expressions

```csharp
// Good
var publicMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
    .Where(m => !m.IsSpecialName)
    .Select(ProcessMethod)
    .Where(m => m != null)
    .ToList();

// Avoid - Too complex
var result = type.GetMethods().Where(m => m.IsPublic && !m.IsSpecialName && m.GetParameters().Length > 0).Select(m => new { Name = m.Name, Params = m.GetParameters().Select(p => p.Name).ToList() }).ToList();
```

## TypeScript Output Style

### Declaration Structure

Follow this order in generated .d.ts files:

1. Namespace declaration
2. Enums
3. Interfaces
4. Classes
5. Exported functions (for static-only types)

```typescript
declare namespace System.Collections.Generic {
  // Enums first
  enum CollectionChangeAction { }

  // Interfaces next
  interface IEnumerable<T> { }

  // Classes
  class List<T> implements IEnumerable<T> { }

  // Static namespaces (for static-only types)
  namespace Enumerable {
    export function Range(start: int, count: int): IEnumerable<int>;
  }
}
```

### Type Naming

- **Fully qualified**: Always include namespace
- **Nested types**: Use underscore separator `Parent_Child`
- **Generic types**: Preserve type parameter names from .NET

```typescript
// Good
System.Collections.Generic.Dictionary<TKey, TValue>
System.Collections.Frozen.FrozenDictionary_AlternateLookup<TKey, TValue, TAlternate>

// Bad
Dictionary<TKey, TValue>  // Missing namespace
FrozenDictionary.AlternateLookup<...>  // Invalid nested syntax
```

### Member Declaration

```typescript
class Example {
  // Constructors first
  constructor(value: int);
  constructor(value: string);

  // Properties (readonly before mutable)
  readonly ReadOnlyProp: string;
  MutableProp: int;

  // Methods (instance before static)
  InstanceMethod(): void;
  static StaticMethod(): int;
}
```

### Readonly by Default

- Arrays → `ReadonlyArray<T>`
- Collections → Keep .NET types (List, Dictionary, etc.)
- Properties → Mark readonly when appropriate

```typescript
// Array parameters and returns
function GetItems(): ReadonlyArray<string>;

// Readonly properties
readonly Count: int;
```

## Metadata JSON Style

### Structure

```json
{
  "assemblyName": "System.Collections",
  "assemblyVersion": "10.0.0.0",
  "types": {
    "System.Collections.Generic.List`1": {
      "kind": "class",
      "isAbstract": false,
      "isSealed": false,
      "isStatic": false,
      "baseType": "System.Object",
      "interfaces": ["System.Collections.Generic.IList`1"],
      "members": {
        "Add(T)": {
          "kind": "method",
          "isVirtual": false,
          "isStatic": false,
          "accessibility": "public"
        }
      }
    }
  }
}
```

### Conventions

- **Indentation**: 2 spaces
- **Property order**: Alphabetical within each section
- **Type names**: Use .NET full names with backtick for generics
- **Member keys**: Include parameter types in method signatures

## Git Commit Style

### Commit Message Format

```
[Type] Brief description (50 chars or less)

Detailed explanation of changes, focusing on WHY rather than WHAT.
Include specific error codes fixed, files affected, and impact.

Implementation details:
- Bullet points for key changes
- Reference specific methods or classes
- Include before/after examples if helpful

Results:
- Error reduction statistics
- Validation results

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>
```

### Commit Types

- `Fix`: Bug fixes, error corrections
- `Feature`: New functionality
- `Refactor`: Code restructuring without behavior change
- `Docs`: Documentation updates
- `Test`: Test additions or modifications

### Examples

```
Fix TS2304: Namespace qualification for generic types

Removed special-case handling in MapGenericType() that returned bare
names like Dictionary<K,V> instead of fully qualified names.

Implementation:
- TypeMapper.cs: Removed List/Dictionary/HashSet special cases
- Now all generic types use GetFullTypeName() which includes namespace

Results:
- 34 TS2304 errors eliminated
- All generic type references now fully qualified

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>
```

## Testing Standards

### Local Testing

Always test locally before committing:

```bash
# Generate specific assembly
dotnet run -- path/to/assembly.dll --out-dir .tests/test-run

# Full BCL validation
node Scripts/validate.js

# Check specific error types
cd .tests/test-run && npx tsc --noEmit 2>&1 | grep TS2300
```

### Validation Criteria

✅ **Must Pass**:
- Zero TypeScript syntax errors (TS1xxx)
- All metadata files present
- Build succeeds

⚠️ **Expected**:
- Semantic errors for cross-assembly references
- Duplicate type warnings (TS6200) for branded types

❌ **Must Not Happen**:
- Type weakening (new `any` fallbacks)
- Lost type information (missing generics)
- Broken syntax in output

## Performance Guidelines

### Reflection Performance

- **Cache Type objects**: Don't repeatedly call `GetType()`
- **Batch operations**: Process types in collections when possible
- **Lazy evaluation**: Use `yield return` for large sequences

```csharp
// Good - Cache type lookup
var stringType = typeof(string);
if (type == stringType) { }

// Bad - Repeated reflection
if (type == typeof(string)) { }
if (type == typeof(string)) { }
```

### Output Generation

- **StringBuilder**: Use for string concatenation
- **Buffered writes**: Batch file I/O operations
- **Avoid allocations**: Reuse collections when possible

```csharp
// Good
var sb = new StringBuilder();
sb.Append("class ");
sb.Append(typeName);
return sb.ToString();

// Bad
var result = "class " + typeName;  // Multiple allocations
```

## Code Review Checklist

Before committing, verify:

- [ ] All warnings addressed or documented
- [ ] Full BCL validation passes (zero syntax errors)
- [ ] No type safety regressions
- [ ] Metadata files generated correctly
- [ ] Code follows naming conventions
- [ ] Comments explain WHY, not WHAT
- [ ] Commit message is descriptive
- [ ] Changes tested with specific edge cases
