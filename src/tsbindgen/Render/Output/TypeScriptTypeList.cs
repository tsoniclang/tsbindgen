namespace tsbindgen.Render.Output;

/// <summary>
/// Simplified representation of TypeScript types for debugging/comparison.
/// This captures what actually gets emitted to .d.ts files.
/// Matches the snapshot.json structure (flat list of types with tsEmitName).
/// </summary>
public sealed record TypeScriptTypeList(
    string Namespace,
    IReadOnlyList<TypeScriptTypeEntry> Types);

/// <summary>
/// A single TypeScript type entry (class, interface, enum, or delegate).
/// Uses tsEmitName which includes $ separator for nested types (e.g., "Delegate$InvocationListEnumerator_1").
/// </summary>
public sealed record TypeScriptTypeEntry(
    string TsEmitName,  // The TypeScript emission name (matches snapshot.json tsEmitName)
    string Kind,
    IReadOnlyList<TypeScriptMemberEntry> Members);

/// <summary>
/// A single member (method, property, field, or event) in a TypeScript type.
/// </summary>
public sealed record TypeScriptMemberEntry(
    string Name,
    string Kind, // "method", "property", "field", "event"
    bool IsStatic,
    string? EmitScope); // "ClassSurface", "StaticSurface", "ViewOnly", or null
