# Claude Code Guidelines for generatedts

## Project Overview

**generatedts** is a .NET tool that generates TypeScript declaration files (.d.ts) from .NET assemblies, specifically targeting the .NET Base Class Library (BCL). It uses reflection to analyze .NET types and produces TypeScript declarations that follow Tsonic's type mapping conventions.

## Key Project Conventions

### 1. Temporary Files and Testing

**IMPORTANT**: Use designated directories for different types of temporary files:

#### `.tests/` - Test Outputs and Validation Scripts
- All temporary validation scripts, test outputs, and experimental code
- Example: `.tests/validation-run-1/`, `.tests/indexer-test/`

#### `.analysis/` - Intermediate Analysis Files
- Reports, analysis results, error breakdowns that you want to share with the user
- Example: `.analysis/error-breakdown.md`, `.analysis/indexer-analysis.txt`

**Both directories are git-ignored.**

**Examples**:
```bash
# Good - Test outputs
dotnet run -- assembly.dll --out-dir .tests/validation-run-1

# Good - Analysis reports
tsc --noEmit 2>&1 | grep TS2300 > .analysis/ts2300-errors.txt

# Bad - Do NOT use /tmp/ or relative paths
dotnet run -- assembly.dll --out-dir /tmp/validation-run-1
```

### 2. Type Safety Principles

The core mission is generating type-safe TypeScript declarations without weakening type information. Acceptable fallbacks to `any`:

- **Pointers** (`*`): TypeScript has no equivalent
- **Unnamed types**: Function pointers and compiler-generated types
- **System.Object**: Intentionally maps to `any` per Tsonic semantics

All other types must preserve full type information including:
- Branded numeric types (int, long, decimal, etc.)
- Generic parameters
- Namespace qualification
- Delegate signatures as function types

### 3. Metadata Sidecar Files

Every generated `.d.ts` file has a corresponding `.metadata.json` file containing:
- Assembly name and version
- Type kind (class, interface, struct, enum)
- Member metadata (methods, properties, constructors)
- Accessibility, virtuality, and other modifiers

**Purpose**: Enables round-trip tooling and runtime interop without requiring .NET reflection.

### 4. Validation Workflow

Standard validation uses the `Scripts/validate.js` script:

```bash
node Scripts/validate.js
```

This:
1. Generates .d.ts files for all 39 BCL assemblies
2. Creates index.d.ts with triple-slash references
3. Validates metadata file presence
4. Runs TypeScript compiler
5. Reports error breakdown

**Success criteria**:
- ✅ Zero TypeScript syntax errors (TS1xxx)
- ✅ All metadata files present
- ⚠️ Semantic errors (TS2xxx) expected due to cross-assembly references

### 5. Error Reduction Strategy

When fixing TypeScript errors, prioritize in this order:

1. **Syntax errors (TS1xxx)**: MUST be zero
2. **High-impact semantic errors**:
   - TS2304: Cannot find name (missing imports/namespaces)
   - TS2863: Cannot extend 'any' (broken inheritance)
   - TS2300: Duplicate identifier (name conflicts)
3. **Cross-assembly references**: Expected, lower priority
4. **Interface compatibility**: Requires design decisions

### 6. Tsonic Type Mapping Rules

Key mappings from C# to TypeScript:

| C# Type | TypeScript Type | Notes |
|---------|----------------|-------|
| `int`, `long`, etc. | Branded types (`int`, `long`) | Intersection with number |
| `Task<T>` | `Promise<T>` | Async interop |
| `T[]` | `ReadonlyArray<T>` | Immutable by default |
| Delegates | Function types | `(args) => ReturnType` |
| `object` | `any` | Intentional for dynamic behavior |
| Nested types | Parent_Child | Underscore separator |

### 7. Common Pitfalls

**Nested Types**: C# uses `+` for nested types (e.g., `Parent+Child`). We emit as `Parent_Child` to avoid TypeScript conflicts. Full names must include namespace: `Namespace.Parent_Child`.

**Delegates**: Skip emitting class declarations for delegates. They only exist as function type aliases via TypeMapper.

**Generic Type Parameters**: Static members cannot reference class type parameters in TypeScript. Skip such static members with warnings.

**Indexers**: C# indexer properties (multiple `Item` properties with different parameter types) need special handling to avoid duplicate identifiers.

### 8. Git Workflow

- **Main branch**: Protected, requires PR
- **Feature branches**: Use descriptive names like `feature/fix-indexer-handling`
- **Commit messages**: End with Claude Code attribution:
  ```
  Feature description

  Details...

  🤖 Generated with [Claude Code](https://claude.com/claude-code)

  Co-Authored-By: Claude <noreply@anthropic.com>
  ```

### 9. Project Structure

```
generatedts/
├── Src/                    # Source code
│   ├── Program.cs          # CLI entry point
│   ├── AssemblyProcessor.cs # Reflection & type extraction
│   ├── TypeMapper.cs       # C# → TypeScript type mapping
│   ├── DeclarationRenderer.cs # .d.ts output generation
│   ├── MetadataAssemblyLoader.cs # MetadataLoadContext handling
│   ├── TypeInfo.cs         # Data structures
│   └── GeneratorConfig.cs  # Configuration support
├── Scripts/
│   └── validate.js         # BCL validation script
├── .tests/                 # Test outputs & validation scripts (git-ignored)
├── .analysis/              # Analysis reports to share with user (git-ignored)
├── claude.md               # This file - Claude Code guidelines
└── coding-standards.md     # Code style guidelines
```

### 10. Development Cycle

When implementing fixes:

1. **Create todo list**: Use TodoWrite tool to track tasks
2. **Analyze the issue**: Read relevant code, check error patterns
3. **Implement fix**: Edit source files
4. **Test locally**: Generate specific assemblies to .tests/ directory
5. **Full validation**: Run `node Scripts/validate.js`
6. **Update todos**: Mark tasks as completed
7. **Commit**: Use git with proper attribution
8. **Create PR**: Push to feature branch, use `gh pr create`

### 11. Performance Notes

- **Parallel processing**: AssemblyProcessor can process types in parallel (future optimization)
- **MetadataLoadContext**: Required for System.Private.CoreLib and other runtime assemblies
- **Output size**: System.Private.CoreLib generates ~27K lines; full BCL is ~500K+ lines

### 12. Current Status (as of 2025-11-03)

**Error Reduction Progress**:
- Initial: 32,912 errors
- Current: 3,888 errors
- Reduction: 88.1%

**Remaining Issues**:
- TS2300 (115): Duplicate Item properties (indexers)
- TS2416/TS2420 (1,431): Interface compatibility
- TS2315/TS2694 (2,039): Cross-assembly references

**Next Priorities**:
1. Fix duplicate indexer properties
2. Address interface inheritance conflicts
3. Optimize cross-assembly type resolution
