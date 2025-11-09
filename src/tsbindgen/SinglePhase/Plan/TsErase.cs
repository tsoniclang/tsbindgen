using System.Collections.Generic;
using System.Linq;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// Erases CLR-specific details to produce TypeScript-level signatures.
/// Used for assignability checking in PhaseGate validation.
/// </summary>
public static class TsErase
{
    /// <summary>
    /// Erase a method to its TypeScript signature representation.
    /// Removes CLR-specific modifiers (ref/out) and simplifies types.
    /// </summary>
    public static TsMethodSignature EraseMember(MethodSymbol method)
    {
        return new TsMethodSignature(
            Name: method.TsEmitName,
            Arity: method.Arity,
            Parameters: method.Parameters.Select(p => EraseType(p.Type)).ToList(),
            ReturnType: EraseType(method.ReturnType));
    }

    /// <summary>
    /// Erase a property to its TypeScript signature representation.
    /// </summary>
    public static TsPropertySignature EraseMember(PropertySymbol property)
    {
        return new TsPropertySignature(
            Name: property.TsEmitName,
            PropertyType: EraseType(property.PropertyType),
            IsReadonly: !property.HasSetter);
    }

    /// <summary>
    /// Erase type to TypeScript-level representation.
    /// Maps CLR types to their TypeScript equivalents.
    /// </summary>
    public static TsTypeShape EraseType(TypeReference typeRef)
    {
        return typeRef switch
        {
            // Named types - check if constructed generic or simple named type
            NamedTypeReference named when named.TypeArguments.Count > 0 =>
                // Constructed generic - erase to application with argument shapes
                new TsTypeShape.GenericApplication(
                    new TsTypeShape.Named(named.FullName),
                    named.TypeArguments.Select(EraseType).ToList()),

            NamedTypeReference named =>
                // Simple named type - keep full name for comparison
                new TsTypeShape.Named(named.FullName),

            // Nested types - use full reference name
            NestedTypeReference nested => new TsTypeShape.Named(nested.FullReference.FullName),

            // Generic parameters - keep parameter name
            GenericParameterReference gp => new TsTypeShape.TypeParameter(gp.Name),

            // Array types - erase to readonly array
            ArrayTypeReference arr => new TsTypeShape.Array(EraseType(arr.ElementType)),

            // Pointer/ByRef types - erase to element type (TS doesn't support these)
            PointerTypeReference ptr => EraseType(ptr.PointeeType),
            ByRefTypeReference byref => EraseType(byref.ReferencedType),

            // Fallback - use string representation
            _ => new TsTypeShape.Unknown(typeRef.ToString() ?? "unknown")
        };
    }
}

/// <summary>
/// TypeScript-level method signature (after CLR erasure).
/// </summary>
public sealed record TsMethodSignature(
    string Name,
    int Arity,
    List<TsTypeShape> Parameters,
    TsTypeShape ReturnType);

/// <summary>
/// TypeScript-level property signature (after CLR erasure).
/// </summary>
public sealed record TsPropertySignature(
    string Name,
    TsTypeShape PropertyType,
    bool IsReadonly);

/// <summary>
/// TypeScript type shape (simplified type representation).
/// </summary>
public abstract record TsTypeShape
{
    public sealed record Named(string FullName) : TsTypeShape;
    public sealed record TypeParameter(string Name) : TsTypeShape;
    public sealed record Array(TsTypeShape ElementType) : TsTypeShape;
    public sealed record GenericApplication(TsTypeShape GenericType, List<TsTypeShape> TypeArguments) : TsTypeShape;
    public sealed record Unknown(string Description) : TsTypeShape;
}
