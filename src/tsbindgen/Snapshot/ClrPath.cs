namespace tsbindgen.Snapshot;

/// <summary>
/// Structured representation of a CLR type's path.
/// Preserves exact nesting boundaries and generic arity without information loss.
/// </summary>
/// <remarks>
/// Example: "System.Console+Error`1" becomes:
///   Namespace: "System"
///   Segments: [("Console", 0), ("Error", 1)]
/// </remarks>
public readonly record struct ClrPath(
    string Namespace,
    IReadOnlyList<ClrSegment> Segments);

/// <summary>
/// A single segment in a CLR type path (either top-level or nested).
/// </summary>
/// <remarks>
/// Identifier preserves exact CLR name including any underscores.
/// GenericArity is 0 for non-generic types, otherwise 1..N.
/// </remarks>
public readonly record struct ClrSegment(
    string Identifier,
    int GenericArity);
