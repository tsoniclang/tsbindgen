namespace tsbindgen.Render.Output;

/// <summary>
/// Simplified representation of TypeScript types for debugging/comparison.
/// This captures what actually gets emitted to .d.ts files.
/// </summary>
public sealed record TypeScriptTypeList(
    string Namespace,
    IReadOnlyList<TypeScriptTypeEntry> Types);

/// <summary>
/// A single TypeScript type entry (class, interface, enum, or delegate).
/// </summary>
public sealed record TypeScriptTypeEntry(
    string Name,
    string Kind,
    bool IsNested,
    string? DeclaringType);
