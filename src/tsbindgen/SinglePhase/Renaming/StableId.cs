namespace tsbindgen.SinglePhase.Renaming;

/// <summary>
/// Immutable identity for types and members BEFORE any name transformations.
/// Used as the key for rename decisions and for bindings back to CLR.
/// </summary>
public abstract record StableId
{
    /// <summary>
    /// Assembly name where the symbol originates.
    /// </summary>
    public required string AssemblyName { get; init; }
}

/// <summary>
/// Stable identity for a type.
/// </summary>
public sealed record TypeStableId : StableId
{
    /// <summary>
    /// Full CLR type name (e.g., "System.Collections.Generic.List`1").
    /// </summary>
    public required string ClrFullName { get; init; }

    public override string ToString() => $"{AssemblyName}:{ClrFullName}";
}

/// <summary>
/// Stable identity for a member (method, property, field, event).
/// Equality is based on semantic identity (excluding MetadataToken).
/// </summary>
public sealed record MemberStableId : StableId
{
    /// <summary>
    /// Full CLR name of the declaring type.
    /// </summary>
    public required string DeclaringClrFullName { get; init; }

    /// <summary>
    /// Member name as it appears in CLR metadata.
    /// </summary>
    public required string MemberName { get; init; }

    /// <summary>
    /// Canonical signature that uniquely identifies this member among overloads.
    /// For methods: includes parameter types and return type.
    /// For properties: includes parameter types (for indexers).
    /// For fields/events: typically empty or just the type.
    /// </summary>
    public required string CanonicalSignature { get; init; }

    /// <summary>
    /// Optional metadata token for exact CLR correlation.
    /// NOT included in equality comparison (semantic identity only).
    /// </summary>
    public int? MetadataToken { get; init; }

    public override string ToString() =>
        $"{AssemblyName}:{DeclaringClrFullName}::{MemberName}{CanonicalSignature}";

    // Override equality to exclude MetadataToken (semantic equality)
    public bool Equals(MemberStableId? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return AssemblyName == other.AssemblyName
            && DeclaringClrFullName == other.DeclaringClrFullName
            && MemberName == other.MemberName
            && CanonicalSignature == other.CanonicalSignature;
        // MetadataToken intentionally excluded from equality
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(AssemblyName, DeclaringClrFullName, MemberName, CanonicalSignature);
        // MetadataToken intentionally excluded from hash
    }
}
