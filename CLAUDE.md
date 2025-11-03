# CLAUDE.md

This file provides guidance to Claude Code when working with the generatedts project.

## Critical Guidelines

### NEVER ACT WITHOUT EXPLICIT USER APPROVAL

**YOU MUST ALWAYS ASK FOR PERMISSION BEFORE:**

- Making architectural decisions or changes
- Implementing new features or functionality
- Modifying type mapping rules or generation logic
- Changing metadata structure or output format
- Adding new dependencies or packages
- Modifying reflection or MetadataLoadContext usage patterns

**ONLY make changes AFTER the user explicitly approves.** When you identify issues or potential improvements, explain them clearly and wait for the user's decision. Do NOT assume what the user wants or make "helpful" changes without permission.

### ANSWER QUESTIONS AND STOP

**CRITICAL RULE**: If the user asks you a question - whether as part of a larger text or just the question itself - you MUST:

1. **Answer ONLY that question**
2. **STOP your response completely**
3. **DO NOT continue with any other tasks or implementation**
4. **DO NOT proceed with previous tasks**
5. **Wait for the user's next instruction**

This applies to ANY question, even if it seems like part of a larger task or discussion.

### NEVER USE AUTOMATED SCRIPTS FOR FIXES

**🚨 CRITICAL RULE: NEVER EVER attempt automated fixes via scripts or mass updates. 🚨**

- **NEVER** create scripts to automate replacements (PowerShell, bash, Python, etc.)
- **NEVER** use sed, awk, grep, or other text processing tools for bulk changes
- **NEVER** write code that modifies multiple files automatically
- **ALWAYS** make changes manually using the Edit tool
- **Even if there are hundreds of similar changes, do them ONE BY ONE**

Automated scripts break syntax in unpredictable ways and destroy codebases.

### WORKING DIRECTORIES

**IMPORTANT**: Never create temporary files in the project root or Src/ directories. Use dedicated gitignored directories for different purposes.

#### .tests/ Directory (Test Output Capture)

**Purpose:** Save validation run output for analysis without re-running

**Usage:**
```bash
# Create directory (gitignored)
mkdir -p .tests

# Run validation with tee - shows output AND saves to file
node Scripts/validate.js | tee .tests/validation-$(date +%s).txt

# Run TypeScript compiler directly with tee
npx tsc --project /tmp/generatedts-validation | tee .tests/tsc-$(date +%s).txt

# Analyze saved output later without re-running:
grep "TS2416" .tests/validation-*.txt
tail -50 .tests/validation-*.txt
grep -A10 "System.Collections" .tests/tsc-*.txt
```

**Benefits:**
- See validation output in real-time (unlike `>` redirection)
- Analyze errors without expensive re-runs (validation takes 2-3 minutes)
- Keep historical validation results for comparison
- Search across multiple validation runs

**Key Rule:** ALWAYS use `tee` for validation output, NEVER plain redirection (`>` or `2>&1`)

#### .analysis/ Directory (Research & Documentation)

**Purpose:** Keep analysis artifacts separate from source code

**Usage:**
```bash
# Create directory (gitignored)
mkdir -p .analysis

# Use for:
# - Error analysis reports
# - Type mapping investigations
# - Assembly analysis output
# - Performance profiling results
# - Architecture documentation
# - Session status reports
# - Bug fix impact analysis
```

**Benefits:**
- Keeps analysis work separate from source code
- Allows iterative analysis without cluttering repository
- Safe place for comprehensive documentation
- Gitignored - no risk of committing debug artifacts

**Note:** All directories (`.tests/`, `.analysis/`) should be added to `.gitignore`

## Session Startup

### First Steps When Starting a Session

When you begin working on this project, you MUST:

1. **Read this entire CLAUDE.md file** to understand the project conventions
2. **Read STATUS.md** for current project state and metrics
3. **Read coding-standards.md** for C# style guidelines
4. **Review recent .analysis/ reports** to understand recent work
5. **Check git status** to see uncommitted work

Only after reading these documents should you proceed with implementation tasks.

## Project Overview

**generatedts** is a .NET tool that generates TypeScript declaration files (.d.ts) and metadata sidecars (.metadata.json) from .NET assemblies using reflection.

### Purpose

Enable TypeScript code in the Tsonic compiler to reference .NET BCL types with full IDE support and type safety.

### Key Features

- Generates TypeScript declarations from any .NET assembly
- Creates metadata sidecars with CLR-specific information
- Handles .NET 10 BCL assemblies including System.Private.CoreLib
- Uses MetadataLoadContext for assemblies that can't be loaded normally
- Validates output with TypeScript compiler (tsc)

## Architecture

### Three-File System

Every .NET assembly generates two companion files:

1. **TypeScript Declarations** (`*.d.ts`)
   - Standard TypeScript type definitions
   - Namespaces map to C# namespaces
   - Classes, interfaces, enums, delegates
   - Generic types with proper constraints
   - Branded numeric types (int, decimal, etc.)

2. **Metadata Sidecars** (`*.metadata.json`)
   - CLR-specific information (virtual/override, static, ref/out)
   - Used by Tsonic compiler for correct C# code generation
   - Tracks intentional omissions (indexers, generic static members)
   - Full type signatures for ambiguous cases

### Code Organization

```
Src/generatedts/                 # C# implementation
├── Program.cs                    # CLI entry point
├── AssemblyProcessor.cs          # Reflection and type extraction
├── TypeMapper.cs                 # C# → TypeScript type mapping
├── DeclarationRenderer.cs        # TypeScript output generation
├── MetadataAssemblyLoader.cs     # MetadataLoadContext handling
└── TypeInfo.cs                   # Data structures

Scripts/
└── validate.js                   # Full BCL validation script

.analysis/                        # Generated analysis reports
├── session-status-report-*.md
├── remaining-errors-comprehensive.md
└── boolean-fix-impact.md
```

## Critical Implementation Patterns

### MetadataLoadContext Type Comparisons

**CRITICAL**: System.Reflection.MetadataLoadContext types CANNOT be compared with `typeof()`:

```csharp
// ❌ WRONG - Fails for MetadataLoadContext types
if (type == typeof(bool)) return "boolean";

// ✅ CORRECT - Use name-based comparisons
if (type.FullName == "System.Boolean") return "boolean";
```

**Why**: MetadataLoadContext loads assemblies in isolation. The `Type` objects it returns are different instances from `typeof()` results, so `==` comparisons always fail.

**Impact**: The boolean→number bug (fixed in commit dcf59e3) was caused by this exact issue.

### Type Safety Principles

**NO WEAKENING ALLOWED**: All fixes must maintain or improve type safety:

✅ **Acceptable**:
- Omitting types that can't be represented (documented in metadata)
- Using stricter types (`readonly T[]` instead of `T[]`)
- Adding method overloads for interface compatibility
- Skipping generic static members (TypeScript limitation)

❌ **NOT Acceptable**:
- Mapping all unknown types to `any`
- Removing type parameters
- Weakening return types
- Removing required properties

### Known .NET/TypeScript Impedance Mismatches

1. **Property Covariance** (625 TS2416 errors, 48% of total)
   - C# allows properties to return more specific types than interfaces require
   - TypeScript doesn't support property overloads (unlike methods)
   - Status: Documented limitation, safe to ignore or use type assertions

2. **Array Interface Implementation** (392 TS2420 errors, 30%)
   - We map `IEnumerable<T>` → `ReadonlyArray<T>` for ergonomics
   - .NET classes don't implement array methods (length, concat, etc.)
   - Status: Design decision, use `.ToArray()` when array methods needed

3. **Type-Forwarding Assemblies** (138 of 233 TS2694 errors)
   - Many .NET assemblies in shared runtime forward types to System.Private.*
   - These generate empty .d.ts files (only branded numeric types)
   - Status: Architectural limitation, low priority

4. **Generic Static Members** (~44 errors)
   - C# allows `static T DefaultValue` in `class List<T>`
   - TypeScript doesn't support this
   - Status: Intentionally skipped, tracked in metadata

5. **Indexers** (~90 instances)
   - C# indexers with different parameter types cause duplicate identifiers
   - Status: Intentionally skipped from declarations, tracked in metadata

## Type Mapping Rules (Tsonic Conventions)

### Primitive Types → Branded Types

```typescript
// All C# numeric types get branded type aliases
type int = number & { __brand: "int" };
type uint = number & { __brand: "uint" };
type byte = number & { __brand: "byte" };
type decimal = number & { __brand: "decimal" };
// etc.

// Usage in generated code
class List_1<T> {
    readonly Count: int;  // Not just 'number'
}
```

### Collections → ReadonlyArray

```csharp
// C#: IEnumerable<T>, ICollection<T>, IList<T>
// TypeScript: ReadonlyArray<T>
```

### Tasks → Promises

```csharp
// C#: Task<T>
// TypeScript: Promise<T>
```

### Nullable → Union

```csharp
// C#: int?
// TypeScript: int | null
```

### Namespaces Preserved

```csharp
// C#: System.Collections.Generic.List<T>
// TypeScript: System.Collections.Generic.List_1<T>
```

### Generic Arity in Names

```typescript
// C# uses backtick: List`1
// TypeScript uses underscore: List_1
```

## Validation Workflow

### Running Validation

```bash
# Full validation (2-3 minutes)
node Scripts/validate.js

# With output capture for later analysis
node Scripts/validate.js | tee .tests/validation-$(date +%s).txt
```

### Validation Steps

1. Cleans `/tmp/generatedts-validation`
2. Generates all 55 BCL assemblies
3. Creates `index.d.ts` with triple-slash references
4. Creates `tsconfig.json`
5. Runs TypeScript compiler (`tsc`)
6. Reports error breakdown

### Success Criteria

- ✅ **Zero syntax errors (TS1xxx)** - All output is valid TypeScript
- ✅ **All assemblies generate** - No generation failures
- ✅ **All metadata files present** - Each .d.ts has matching .metadata.json
- ⚠️ **Semantic errors acceptable** - TS2xxx errors are expected (cross-assembly refs, known limitations)

### Error Categories

```
TS1xxx - Syntax errors (CRITICAL - must be zero)
TS2xxx - Semantic errors (expected, prioritized by count/impact)
TS6200 - Duplicate type aliases (expected for branded types)
```

## Common Tasks

### Generating Declarations for an Assembly

```bash
dotnet run --project Src/generatedts.csproj -- \
  /path/to/Assembly.dll \
  --out-dir output/
```

### Adding a New BCL Assembly to Validation

1. Edit `Scripts/validate.js`
2. Add assembly name to `BCL_ASSEMBLIES` array
3. Run validation to verify generation
4. Update STATUS.md with new assembly count

### Investigating Type Mapping Issues

1. Generate single assembly: `dotnet run -- path/to/Assembly.dll --out-dir /tmp/test`
2. Inspect output: `cat /tmp/test/Assembly.d.ts`
3. Check metadata: `cat /tmp/test/Assembly.metadata.json`
4. Validate: `npx tsc --noEmit /tmp/test/Assembly.d.ts`

### Analyzing Validation Errors

```bash
# Run validation with capture
node Scripts/validate.js 2>&1 | tee .tests/run.txt

# Count errors by type
grep "error TS" .tests/run.txt | sed 's/.*error \(TS[0-9]*\).*/\1/' | sort | uniq -c | sort -rn

# Find specific error examples
grep "TS2416" .tests/run.txt | head -20

# See errors for specific file
grep "System.Collections.Generic.d.ts" .tests/run.txt
```

## Build Commands

```bash
# Build project
dotnet build Src/generatedts.csproj

# Run tool
dotnet run --project Src/generatedts.csproj -- <args>

# Validate all BCL assemblies
node Scripts/validate.js

# Capture validation output
node Scripts/validate.js | tee .tests/validation-$(date +%s).txt
```

## Git Workflow

### Branch Strategy

1. **Work on feature branches**: `feature/feature-name` or `fix/bug-name`
2. **Commit frequently**: Small, focused commits
3. **Clear commit messages**: Follow format in coding-standards.md
4. **Push regularly**: Keep remote in sync

### Commit Message Format

```
<type>: <subject>

<body>

<footer>
```

**Types**: feat, fix, docs, refactor, test, chore

**Example**:
```
fix: Use name-based type comparisons for MetadataLoadContext compatibility

Changed MapPrimitiveType() to use type.FullName comparisons instead of
typeof() because MetadataLoadContext types are different instances.

Fixes #123
```

## Progress Tracking

### Current Status (as of 2025-11-03)

- **55 BCL assemblies** generated
- **96.1% error reduction** (32,912 → 1,298 errors)
- **Zero syntax errors** (TS1xxx)
- **Type safety: 9.6/10**
- **Production ready** for internal use
- **External use**: Needs user documentation (1-2 days)

### Error Distribution (1,298 total)

```
 625 TS2416 (48%) - Property/method type variance
 392 TS2420 (30%) - Interface implementation gaps
 233 TS2694 (18%) - Missing type references
  55 TS6200 ( 4%) - Branded types (by design)
  48 other  (<1%) - Minor edge cases
```

### Known Limitations

1. **Property Covariance** (625 errors) - TypeScript limitation, use type assertions
2. **Array Interface Implementation** (300 errors) - Design decision, use `.ToArray()`
3. **Type-Forwarding Assemblies** (138 errors) - .NET architecture artifact
4. **Intentional Omissions** - Indexers (~90), generic static members (~44)

See `.analysis/remaining-errors-comprehensive.md` for complete details.

## Recent Major Fixes

### Boolean Mapping Bug Fix (commit dcf59e3) ⭐ CRITICAL

**Impact**: -910 errors (-41.4%)

**Problem**: `typeof(bool)` comparisons fail for MetadataLoadContext types, causing all boolean properties to be typed as `number`.

**Solution**: Changed to name-based comparisons using `type.FullName`.

**Lesson**: Always use name-based type comparisons when working with MetadataLoadContext.

### Type-Forwarding Discovery (commit 6a24dac)

**Finding**: Many .NET assemblies in shared runtime are type-forwarding only (no actual types).

**Impact**: Adding 6 assemblies only reduced TS2694 by 2 errors (instead of expected -120+).

**Root Cause**: Type-forwarding assemblies reference types that live in System.Private.* or ref packs.

**Decision**: Accept current state rather than implement complex dual-path system.

## When You Get Stuck

If you encounter issues:

1. **STOP immediately** - Don't implement workarounds without approval
2. **Explain the issue clearly** - Show what's blocking you
3. **Analyze root cause** - Use .analysis/ directory for investigation
4. **Propose solutions** - Suggest approaches with trade-offs
5. **Wait for user decision** - Don't proceed without explicit approval

## Key Files to Reference

- **STATUS.md** - Current project state and metrics
- **coding-standards.md** - C# style guidelines
- **.analysis/remaining-errors-comprehensive.md** - Complete error catalog
- **.analysis/session-status-report-*.md** - Recent session work
- **Scripts/validate.js** - BCL assembly validation script

## Remember

1. **Type safety first** - Never weaken types without approval
2. **MetadataLoadContext requires name-based comparisons** - Never use `typeof()`
3. **Validation is expensive** - Always capture output with `tee`
4. **Document limitations** - Known issues go in metadata
5. **Ask before changing** - Get user approval for all decisions
6. **Semantic errors are expected** - Focus on zero syntax errors
