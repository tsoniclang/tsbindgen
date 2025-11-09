using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Renaming;

/// <summary>
/// Centralized scope construction for SymbolRenamer lookups.
/// Ensures emitters use identical scope keys as NameReservation.
///
/// CANONICAL SCOPE FORMATS (do not deviate):
/// - Namespace (public):  ns:{NamespaceName}:public
/// - Namespace (internal): ns:{NamespaceName}:internal
/// - Class members:       type:{TypeFullName}#instance or type:{TypeFullName}#static
/// - View members:        view:{TypeStableId}:{InterfaceStableId}#instance or #static
///
/// M5 CRITICAL: View members MUST be looked up with ViewScope(), not ClassInstance()/ClassStatic().
/// </summary>
public static class RenamerScopes
{
    /// <summary>
    /// Creates namespace scope for public type names.
    /// Format: "ns:{Namespace}:public"
    /// </summary>
    public static NamespaceScope NamespacePublic(string ns)
    {
        return new NamespaceScope
        {
            Namespace = ns,
            IsInternal = false,
            ScopeKey = $"ns:{ns}:public"
        };
    }

    /// <summary>
    /// Creates namespace scope for internal type names.
    /// Format: "ns:{Namespace}:internal"
    /// </summary>
    public static NamespaceScope NamespaceInternal(string ns)
    {
        return new NamespaceScope
        {
            Namespace = ns,
            IsInternal = true,
            ScopeKey = $"ns:{ns}:internal"
        };
    }

    /// <summary>
    /// Creates class-surface scope for instance members.
    /// Format: "type:{TypeFullName}#instance"
    ///
    /// Use for: Instance members with EmitScope.ClassSurface
    /// </summary>
    public static TypeScope ClassInstance(TypeSymbol type)
    {
        return new TypeScope
        {
            TypeFullName = type.ClrFullName,
            IsStatic = false,
            ScopeKey = $"type:{type.ClrFullName}#instance"
        };
    }

    /// <summary>
    /// Creates class-surface scope for static members.
    /// Format: "type:{TypeFullName}#static"
    ///
    /// Use for: Static members with EmitScope.StaticSurface or EmitScope.ClassSurface
    /// </summary>
    public static TypeScope ClassStatic(TypeSymbol type)
    {
        return new TypeScope
        {
            TypeFullName = type.ClrFullName,
            IsStatic = true,
            ScopeKey = $"type:{type.ClrFullName}#static"
        };
    }

    /// <summary>
    /// Creates view scope for explicit interface view members.
    /// Format: "view:{TypeStableId}:{InterfaceStableId}#instance" or "#static"
    ///
    /// Use for: Members with EmitScope.ViewOnly inside ExplicitView
    ///
    /// M5 FIX: This is what emitters were missing - they were using ClassInstance()/ClassStatic()
    /// for view members, causing PG_NAME_004 collisions.
    /// </summary>
    public static TypeScope ViewScope(TypeSymbol type, string interfaceStableId, bool isStatic)
    {
        return new TypeScope
        {
            TypeFullName = type.ClrFullName,
            IsStatic = isStatic,
            ScopeKey = $"view:{type.StableId}:{interfaceStableId}#{(isStatic ? "static" : "instance")}"
        };
    }

    /// <summary>
    /// Extracts interface StableId from TypeReference (same logic as ViewPlanner).
    /// Returns assembly-qualified identifier for grouping/merging.
    /// </summary>
    public static string GetInterfaceStableId(TypeReference ifaceRef)
    {
        return ifaceRef switch
        {
            NamedTypeReference named => $"{named.AssemblyName}:{named.FullName}",
            NestedTypeReference nested => GetInterfaceStableId(nested.DeclaringType) + "+" + nested.NestedName,
            _ => ifaceRef.ToString() ?? "unknown"
        };
    }
}
