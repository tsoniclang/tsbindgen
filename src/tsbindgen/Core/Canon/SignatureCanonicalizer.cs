using System.Text;

namespace tsbindgen.Core.Canon;

/// <summary>
/// Creates stable, collision-free canonical signatures for methods and properties.
/// Used for:
/// - Overload deduplication
/// - Bindings/metadata correlation
/// - Interface surface matching
/// </summary>
public static class SignatureCanonicalizer
{
    /// <summary>
    /// Create a canonical signature for a method.
    /// Format: "(param1Type,param2Type,...):ReturnType"
    /// NOTE: Method name is NOT included here - it's stored separately in MemberStableId.MemberName
    /// and concatenated in MemberStableId.ToString()
    /// </summary>
    public static string CanonicalizeMethod(
        string methodName,
        IReadOnlyList<string> parameterTypes,
        string returnType)
    {
        var sb = new StringBuilder();
        // BUG FIX: Do NOT append methodName here - it's already in MemberStableId.MemberName
        // Previous code: sb.Append(methodName); caused "AddNewAddNew" duplication
        sb.Append('(');

        for (int i = 0; i < parameterTypes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(NormalizeTypeName(parameterTypes[i]));
        }

        sb.Append(')');
        sb.Append(':');
        sb.Append(NormalizeTypeName(returnType));

        return sb.ToString();
    }

    /// <summary>
    /// Create a canonical signature for a property.
    /// Format: "[param1Type,param2Type,...]:PropertyType" (for indexers) or ":PropertyType" (for regular properties)
    /// NOTE: Property name is NOT included here - it's stored separately in MemberStableId.MemberName
    /// </summary>
    public static string CanonicalizeProperty(
        string propertyName,
        IReadOnlyList<string> indexParameterTypes,
        string propertyType)
    {
        var sb = new StringBuilder();
        // BUG FIX: Do NOT append propertyName here - it's already in MemberStableId.MemberName

        if (indexParameterTypes.Count > 0)
        {
            sb.Append('[');
            for (int i = 0; i < indexParameterTypes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(NormalizeTypeName(indexParameterTypes[i]));
            }
            sb.Append(']');
        }

        sb.Append(':');
        sb.Append(NormalizeTypeName(propertyType));

        return sb.ToString();
    }

    /// <summary>
    /// Create a canonical signature for a field.
    /// Format: ":FieldType"
    /// NOTE: Field name is NOT included here - it's stored separately in MemberStableId.MemberName
    /// </summary>
    public static string CanonicalizeField(string fieldName, string fieldType)
    {
        // BUG FIX: Do NOT include fieldName here - it's already in MemberStableId.MemberName
        return $":{NormalizeTypeName(fieldType)}";
    }

    /// <summary>
    /// Create a canonical signature for an event.
    /// Format: ":DelegateType"
    /// NOTE: Event name is NOT included here - it's stored separately in MemberStableId.MemberName
    /// </summary>
    public static string CanonicalizeEvent(string eventName, string delegateType)
    {
        // BUG FIX: Do NOT include eventName here - it's already in MemberStableId.MemberName
        return $":{NormalizeTypeName(delegateType)}";
    }

    /// <summary>
    /// Normalize a type name for signature matching.
    /// Handles generic arity, nested types, etc.
    /// </summary>
    private static string NormalizeTypeName(string typeName)
    {
        // Remove whitespace
        var normalized = typeName.Replace(" ", "");

        // Normalize generic backtick to underscore (List`1 -> List_1)
        normalized = normalized.Replace('`', '_');

        // TODO: More sophisticated normalization for:
        // - Array types (Int32[] -> Int32[])
        // - Nullable types (Int32? -> Nullable<Int32>)
        // - ByRef types (Int32& -> ByRef<Int32>)
        // - Pointer types (Int32* -> Pointer<Int32>)

        return normalized;
    }

    /// <summary>
    /// Extract method signature from a canonical signature.
    /// Useful for debugging and diagnostics.
    /// NOTE: After the AddNew bug fix, canonical signatures no longer include the method name.
    /// Format is now: "(param1,param2):ReturnType" instead of "MethodName(param1,param2):ReturnType"
    /// </summary>
    public static (string? name, string[] parameters, string returnType) ParseMethodSignature(
        string canonicalSignature)
    {
        var parenIndex = canonicalSignature.IndexOf('(');
        var closeParenIndex = canonicalSignature.IndexOf(')');
        var colonIndex = canonicalSignature.IndexOf(':', closeParenIndex);

        // BUG FIX: Canonical signature no longer includes method name
        // Return null for name since it's not in the signature anymore
        var name = parenIndex > 0 ? canonicalSignature[..parenIndex] : null;
        var paramsStr = canonicalSignature[(parenIndex + 1)..closeParenIndex];
        var returnType = canonicalSignature[(colonIndex + 1)..];

        var parameters = string.IsNullOrEmpty(paramsStr)
            ? Array.Empty<string>()
            : paramsStr.Split(',');

        return (name, parameters, returnType);
    }
}
