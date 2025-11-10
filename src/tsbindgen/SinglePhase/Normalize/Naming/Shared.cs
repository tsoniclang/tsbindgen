using System.Linq;
using tsbindgen.Core;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;
using tsbindgen.SinglePhase.Shape;

namespace tsbindgen.SinglePhase.Normalize.Naming;

/// <summary>
/// Shared utility functions for name computation and sanitization.
/// </summary>
internal static class Shared
{
    /// <summary>
    /// Compute the requested base name for a type (before reservation/numeric suffix).
    /// Handles nested types, generic arity, and reserved word sanitization.
    /// </summary>
    internal static string ComputeTypeRequestedBase(string clrName)
    {
        var baseName = clrName;

        // Handle nested types: Outer+Inner → Outer_Inner
        baseName = baseName.Replace('+', '_');

        // Handle generic arity: List`1 → List_1
        baseName = baseName.Replace('`', '_');

        // Remove other invalid TS identifier characters
        baseName = baseName.Replace('<', '_');
        baseName = baseName.Replace('>', '_');
        baseName = baseName.Replace('[', '_');
        baseName = baseName.Replace(']', '_');

        // Apply reserved word sanitization
        var sanitized = TypeScriptReservedWords.Sanitize(baseName);
        return sanitized.Sanitized;
    }

    /// <summary>
    /// Compute the base name for a method (handles operators and accessors).
    /// </summary>
    internal static string ComputeMethodBase(MethodSymbol method)
    {
        var name = method.ClrName;

        // Handle operators (map to policy-defined names)
        if (name.StartsWith("op_"))
        {
            var mapped = name switch
            {
                "op_Equality" => "equals",
                "op_Inequality" => "notEquals",
                "op_Addition" => "add",
                "op_Subtraction" => "subtract",
                "op_Multiply" => "multiply",
                "op_Division" => "divide",
                "op_Modulus" => "modulus",
                "op_BitwiseAnd" => "bitwiseAnd",
                "op_BitwiseOr" => "bitwiseOr",
                "op_ExclusiveOr" => "bitwiseXor",
                "op_LeftShift" => "leftShift",
                "op_RightShift" => "rightShift",
                "op_UnaryNegation" => "negate",
                "op_UnaryPlus" => "plus",
                "op_LogicalNot" => "not",
                "op_OnesComplement" => "complement",
                "op_Increment" => "increment",
                "op_Decrement" => "decrement",
                "op_True" => "isTrue",
                "op_False" => "isFalse",
                "op_GreaterThan" => "greaterThan",
                "op_LessThan" => "lessThan",
                "op_GreaterThanOrEqual" => "greaterThanOrEqual",
                "op_LessThanOrEqual" => "lessThanOrEqual",
                _ => name.Replace("op_", "operator_")
            };

            // Apply reserved word sanitization to operator names
            var sanitized = TypeScriptReservedWords.Sanitize(mapped);
            return sanitized.Sanitized;
        }

        // Accessors (get_, set_, add_, remove_) and regular methods use CLR name
        return SanitizeMemberName(name);
    }

    /// <summary>
    /// Sanitize a member name (remove invalid TS characters, handle reserved words).
    /// </summary>
    internal static string SanitizeMemberName(string name)
    {
        // Remove invalid TS identifier characters
        var cleaned = name.Replace('<', '_');
        cleaned = cleaned.Replace('>', '_');
        cleaned = cleaned.Replace('[', '_');
        cleaned = cleaned.Replace(']', '_');
        cleaned = cleaned.Replace('+', '_');

        // Apply reserved word sanitization
        var sanitized = TypeScriptReservedWords.Sanitize(cleaned);
        return sanitized.Sanitized;
    }

    /// <summary>
    /// Centralized function to compute requested base name for any member.
    /// Both class surface and view members use this to ensure consistency.
    /// Returns the base name that will be passed to Renamer (before style transform and numeric suffixes).
    /// </summary>
    internal static string RequestedBaseForMember(string clrName)
    {
        // Sanitize CLR name (remove invalid TS chars, handle reserved words)
        return SanitizeMemberName(clrName);
    }

    /// <summary>
    /// Check if a type name indicates compiler-generated code.
    /// Compiler-generated types have unspeakable names containing < or >
    /// Examples: "<Module>", "<PrivateImplementationDetails>", "<Name>e__FixedBuffer", "<>c__DisplayClass"
    /// </summary>
    internal static bool IsCompilerGenerated(string clrName)
    {
        return clrName.Contains('<') || clrName.Contains('>');
    }

    /// <summary>
    /// Find whether a view member is static by looking it up in the type's member collection.
    /// </summary>
    internal static bool FindMemberIsStatic(TypeSymbol type, ViewPlanner.ViewMember viewMember)
    {
        // Try to find the member in the type's members collection
        switch (viewMember.Kind)
        {
            case ViewPlanner.ViewMemberKind.Method:
                var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                return method?.IsStatic ?? false;

            case ViewPlanner.ViewMemberKind.Property:
                var property = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                return property?.IsStatic ?? false;

            case ViewPlanner.ViewMemberKind.Event:
                var evt = type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId));
                return evt?.IsStatic ?? false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Helper to get full type name from TypeReference.
    /// </summary>
    internal static string GetTypeReferenceName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }
}
