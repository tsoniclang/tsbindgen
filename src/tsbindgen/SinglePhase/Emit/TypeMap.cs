using System;
using System.Collections.Generic;

namespace tsbindgen.SinglePhase.Emit;

/// <summary>
/// Maps CLR built-in types to TypeScript types.
/// This short-circuits graph lookups for primitives and special types.
/// CRITICAL: Must be checked BEFORE TypeIndex lookup to avoid PG_LOAD_001 false positives.
/// </summary>
public static class TypeMap
{
    /// <summary>
    /// Try to map a CLR type to a TypeScript built-in type.
    /// Returns true if this is a known built-in that doesn't need graph lookup.
    /// </summary>
    public static bool TryMapBuiltin(string fullName, out string tsType)
    {
        switch (fullName)
        {
            // Void
            case "System.Void":
                tsType = "void";
                return true;

            // Boolean
            case "System.Boolean":
                tsType = "boolean";
                return true;

            // String
            case "System.String":
                tsType = "string";
                return true;

            // Object (map to any for maximum compatibility)
            case "System.Object":
                tsType = "any";
                return true;

            // Signed integers (branded types)
            case "System.SByte":
                tsType = "sbyte";
                return true;
            case "System.Int16":
                tsType = "short";
                return true;
            case "System.Int32":
                tsType = "int";
                return true;
            case "System.Int64":
                tsType = "long";
                return true;
            case "System.Int128":
                tsType = "int128";
                return true;
            case "System.IntPtr":
                tsType = "nint";
                return true;

            // Unsigned integers (branded types)
            case "System.Byte":
                tsType = "byte";
                return true;
            case "System.UInt16":
                tsType = "ushort";
                return true;
            case "System.UInt32":
                tsType = "uint";
                return true;
            case "System.UInt64":
                tsType = "ulong";
                return true;
            case "System.UInt128":
                tsType = "uint128";
                return true;
            case "System.UIntPtr":
                tsType = "nuint";
                return true;

            // Floating point (branded types)
            case "System.Half":
                tsType = "half";
                return true;
            case "System.Single":
                tsType = "float";
                return true;
            case "System.Double":
                tsType = "double";
                return true;
            case "System.Decimal":
                tsType = "decimal";
                return true;

            // Char (map to string - TypeScript doesn't have char)
            case "System.Char":
                tsType = "string";
                return true;

            // Array base type
            case "System.Array":
                tsType = "any[]";
                return true;

            // Value type base
            case "System.ValueType":
                tsType = "any";
                return true;

            // Enum base
            case "System.Enum":
                tsType = "number";
                return true;

            // Delegate base
            case "System.Delegate":
            case "System.MulticastDelegate":
                tsType = "Function";
                return true;

            default:
                tsType = default!;
                return false;
        }
    }

    /// <summary>
    /// Checks if a CLR type is an unsupported special form.
    /// These require special handling or substitution.
    /// </summary>
    public static bool IsUnsupportedSpecialForm(string fullName, bool isPointer, bool isByRef, bool isFunctionPointer)
    {
        return isPointer || isByRef || isFunctionPointer;
    }

    /// <summary>
    /// Gets TypeScript type for an unsupported special form.
    /// Only call this if IsUnsupportedSpecialForm returns true.
    /// </summary>
    public static string MapUnsupportedSpecialForm(string fullName, bool isPointer, bool isByRef, bool isFunctionPointer, bool allowUnsafeMaps)
    {
        if (!allowUnsafeMaps)
        {
            throw new InvalidOperationException(
                $"Unsupported special form: {fullName} (isPointer={isPointer}, isByRef={isByRef}, isFunctionPointer={isFunctionPointer}). " +
                $"Use --allow-unsafe-maps to substitute with 'any'.");
        }

        // When unsafe maps are allowed, substitute with 'any'
        return "any";
    }

    /// <summary>
    /// Checks if a type is a known primitive that should use branded type syntax.
    /// These are emitted as type aliases in the preamble of each file.
    /// </summary>
    public static bool IsBrandedPrimitive(string fullName)
    {
        return fullName switch
        {
            "System.SByte" => true,
            "System.Byte" => true,
            "System.Int16" => true,
            "System.UInt16" => true,
            "System.Int32" => true,
            "System.UInt32" => true,
            "System.Int64" => true,
            "System.UInt64" => true,
            "System.Int128" => true,
            "System.UInt128" => true,
            "System.IntPtr" => true,
            "System.UIntPtr" => true,
            "System.Half" => true,
            "System.Single" => true,
            "System.Double" => true,
            "System.Decimal" => true,
            _ => false
        };
    }
}
