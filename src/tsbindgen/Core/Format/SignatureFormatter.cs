using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Renaming;

namespace tsbindgen.Core.Format;

/// <summary>
/// Unified formatting for type/member signatures in diagnostics and error messages.
/// HARDENING: Ensures all error messages use consistent, readable formatting.
/// Single source of truth for signature formatting across the pipeline.
/// </summary>
public static class SignatureFormatter
{
    /// <summary>
    /// Format a method signature for diagnostics.
    /// Format: AssemblyName:DeclaringType::MethodName(param1, param2, ...):ReturnType
    /// </summary>
    public static string FormatMethod(MethodSymbol method)
    {
        var paramTypes = string.Join(", ", method.Parameters.Select(p => p.Type.ToString()));
        var returnType = method.ReturnType.ToString();

        return $"{method.StableId.AssemblyName}:{method.StableId.DeclaringClrFullName}::" +
               $"{method.ClrName}({paramTypes}): {returnType}";
    }

    /// <summary>
    /// Format a property signature for diagnostics.
    /// Format: AssemblyName:DeclaringType::PropertyName: PropertyType
    /// </summary>
    public static string FormatProperty(PropertySymbol property)
    {
        return $"{property.StableId.AssemblyName}:{property.StableId.DeclaringClrFullName}::" +
               $"{property.ClrName}: {property.PropertyType}";
    }

    /// <summary>
    /// Format a field signature for diagnostics.
    /// Format: AssemblyName:DeclaringType::FieldName: FieldType
    /// </summary>
    public static string FormatField(FieldSymbol field)
    {
        return $"{field.StableId.AssemblyName}:{field.StableId.DeclaringClrFullName}::" +
               $"{field.ClrName}: {field.FieldType}";
    }

    /// <summary>
    /// Format a MemberStableId for diagnostics.
    /// Format: AssemblyName:DeclaringType::MemberName{CanonicalSignature}
    /// Avoids duplicating member name if already in canonical signature.
    /// </summary>
    public static string FormatMemberStableId(MemberStableId id)
    {
        // Avoid duplicating member name if already in CanonicalSignature
        var sig = id.CanonicalSignature.StartsWith(id.MemberName + "(", System.StringComparison.Ordinal)
            ? id.CanonicalSignature
            : $"{id.MemberName}{id.CanonicalSignature}";

        return $"{id.AssemblyName}:{id.DeclaringClrFullName}::{sig}";
    }

    /// <summary>
    /// Format a scope key for diagnostics.
    /// Adds context about scope type for readability.
    /// </summary>
    public static string FormatScopeKey(string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            return "<empty>";

        if (scopeKey.StartsWith("type:", StringComparison.Ordinal))
            return $"{scopeKey} (class scope)";

        if (scopeKey.StartsWith("view:", StringComparison.Ordinal))
            return $"{scopeKey} (view scope)";

        if (scopeKey.StartsWith("ns:", StringComparison.Ordinal))
            return $"{scopeKey} (namespace scope)";

        return scopeKey;
    }

    /// <summary>
    /// Format a rename decision for diagnostics.
    /// Shows requested -> final name transformation.
    /// </summary>
    public static string FormatRenameDecision(string requested, string final)
    {
        return requested == final
            ? $"'{requested}' (no transform)"
            : $"'{requested}' → '{final}'";
    }

    /// <summary>
    /// Format a type reference for diagnostics.
    /// Shows assembly-qualified name.
    /// </summary>
    public static string FormatTypeReference(SinglePhase.Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            SinglePhase.Model.Types.NamedTypeReference named =>
                $"{named.AssemblyName}:{named.FullName}",
            SinglePhase.Model.Types.NestedTypeReference nested =>
                $"{FormatTypeReference(nested.DeclaringType)}+{nested.NestedName}",
            SinglePhase.Model.Types.ArrayTypeReference array =>
                $"{FormatTypeReference(array.ElementType)}[]",
            SinglePhase.Model.Types.PointerTypeReference pointer =>
                $"{FormatTypeReference(pointer.PointeeType)}*",
            SinglePhase.Model.Types.ByRefTypeReference byRef =>
                $"ref {FormatTypeReference(byRef.ReferencedType)}",
            SinglePhase.Model.Types.GenericParameterReference genericParam =>
                genericParam.Name,
            _ => typeRef.ToString() ?? "unknown"
        };
    }
}
