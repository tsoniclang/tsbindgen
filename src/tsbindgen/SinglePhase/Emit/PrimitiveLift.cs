namespace tsbindgen.SinglePhase.Emit;

/// <summary>
/// Defines the primitive lifting rules for CLROf utility type.
/// This is the single source of truth for which primitives get lifted to their CLR types in generic contexts.
///
/// Contract (PG_GENERIC_PRIM_LIFT_001):
/// - Every primitive type used as a generic type argument is covered by these rules
/// - CLROf emitter uses these rules to generate the conditional type mapping
/// - TypeRefPrinter uses these rules to determine which concrete types to wrap with CLROf
/// - PhaseGate validator ensures all primitive type arguments are covered
/// </summary>
internal static class PrimitiveLift
{
    /// <summary>
    /// Primitive lifting rules: TypeScript primitive name → CLR full type name.
    /// Order matters for CLROf conditional type (more specific types first).
    /// </summary>
    internal static readonly (string TsName, string ClrFullName, string ClrSimpleName)[] Rules =
    {
        // Signed integers
        ("sbyte",   "System.SByte",   "SByte"),
        ("short",   "System.Int16",   "Int16"),
        ("int",     "System.Int32",   "Int32"),
        ("long",    "System.Int64",   "Int64"),
        ("int128",  "System.Int128",  "Int128"),   // .NET 7+ 128-bit signed integer
        ("nint",    "System.IntPtr",  "IntPtr"),

        // Unsigned integers
        ("byte",    "System.Byte",    "Byte"),
        ("ushort",  "System.UInt16",  "UInt16"),
        ("uint",    "System.UInt32",  "UInt32"),
        ("ulong",   "System.UInt64",  "UInt64"),
        ("uint128", "System.UInt128", "UInt128"),  // .NET 7+ 128-bit unsigned integer
        ("nuint",   "System.UIntPtr", "UIntPtr"),

        // Floating point
        ("half",    "System.Half",    "Half"),      // .NET 5+ 16-bit floating point
        ("float",   "System.Single",  "Single"),
        ("double",  "System.Double",  "Double"),
        ("decimal", "System.Decimal", "Decimal"),

        // Other
        ("char",    "System.Char",    "Char"),
        ("boolean", "System.Boolean", "Boolean_"),  // Note: emits as Boolean_ (reserved keyword)
        ("string",  "System.String",  "String_"),   // Note: emits as String_ (conflicts with TS String)
    };

    /// <summary>
    /// Check if a CLR type (by full name) is a liftable primitive.
    /// Used by PhaseGate validator to detect primitive type arguments.
    /// </summary>
    internal static bool IsLiftableClr(string clrFullName) =>
        Rules.Any(r => r.ClrFullName == clrFullName);

    /// <summary>
    /// Check if a TypeScript type name is a liftable primitive.
    /// Used by TypeRefPrinter to determine which concrete types to wrap with CLROf.
    /// </summary>
    internal static bool IsLiftableTs(string tsName) =>
        Rules.Any(r => r.TsName == tsName);

    /// <summary>
    /// Get the CLR simple name (for emission) for a given TS primitive.
    /// Returns null if not a liftable primitive.
    /// </summary>
    internal static string? GetClrSimpleName(string tsName) =>
        Rules.FirstOrDefault(r => r.TsName == tsName).ClrSimpleName;
}
