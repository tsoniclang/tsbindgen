using tsbindgen.Core.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Normalize;

/// <summary>
/// Reserves all TypeScript names through the central Renamer.
/// Runs after Shape phase, before Plan phase.
/// </summary>
public static class NameReservation
{
    /// <summary>
    /// Reserve all type and member names in the symbol graph.
    /// This is the ONLY place where names are reserved - all other components
    /// must use Renamer.GetFinal*() to retrieve names.
    /// </summary>
    public static void ReserveAllNames(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("NameReservation: Reserving all TypeScript names...");

        int typesReserved = 0;
        int membersReserved = 0;

        foreach (var ns in graph.Namespaces)
        {
            // Create namespace scope
            var nsScope = new NamespaceScope
            {
                ScopeKey = $"ns:{ns.Name}",
                Namespace = ns.Name,
                IsInternal = true // Internal scope (facade scope handled separately)
            };

            foreach (var type in ns.Types)
            {
                // Reserve type name
                ReserveTypeName(ctx, type, nsScope);
                typesReserved++;

                // Reserve all member names
                membersReserved += ReserveMemberNames(ctx, type);
            }
        }

        ctx.Log($"NameReservation: Reserved {typesReserved} type names, {membersReserved} member names");
    }

    private static void ReserveTypeName(BuildContext ctx, TypeSymbol type, NamespaceScope nsScope)
    {
        // Use CLR name as requested name (will be transformed by Renamer)
        var requested = type.ClrFullName.Split('.').Last(); // Get simple name

        ctx.Renamer.ReserveTypeName(
            stableId: type.StableId,
            requested: requested,
            scope: nsScope,
            reason: "TypeDeclaration",
            decisionSource: "NameReservation");
    }

    private static int ReserveMemberNames(BuildContext ctx, TypeSymbol type)
    {
        // Create type scope
        var typeScope = new TypeScope
        {
            ScopeKey = $"type:{type.ClrFullName}",
            TypeFullName = type.ClrFullName,
            IsStatic = false // Will be overridden for static members
        };

        int count = 0;

        // Reserve methods
        foreach (var method in type.Members.Methods)
        {
            ReserveMethodName(ctx, method, typeScope);
            count++;
        }

        // Reserve properties
        foreach (var property in type.Members.Properties)
        {
            ReservePropertyName(ctx, property, typeScope);
            count++;
        }

        // Reserve fields
        foreach (var field in type.Members.Fields)
        {
            ReserveFieldName(ctx, field, typeScope);
            count++;
        }

        // Reserve events
        foreach (var ev in type.Members.Events)
        {
            ReserveEventName(ctx, ev, typeScope);
            count++;
        }

        // Reserve constructors
        foreach (var ctor in type.Members.Constructors)
        {
            ReserveConstructorName(ctx, ctor, typeScope);
            count++;
        }

        return count;
    }

    private static void ReserveMethodName(BuildContext ctx, MethodSymbol method, TypeScope typeScope)
    {
        // Determine reason based on provenance
        var reason = method.Provenance switch
        {
            MemberProvenance.Original => "MethodDeclaration",
            MemberProvenance.FromInterface => "InterfaceMember",
            MemberProvenance.Synthesized => "SynthesizedMember",
            MemberProvenance.HiddenNew => "HiddenNewMember",
            MemberProvenance.BaseOverload => "BaseOverload",
            _ => "Unknown"
        };

        ctx.Renamer.ReserveMemberName(
            stableId: method.StableId,
            requested: method.ClrName,
            scope: typeScope,
            reason: reason,
            isStatic: method.IsStatic,
            decisionSource: "NameReservation");
    }

    private static void ReservePropertyName(BuildContext ctx, PropertySymbol property, TypeScope typeScope)
    {
        var reason = property.Provenance switch
        {
            MemberProvenance.Original => property.IsIndexer ? "IndexerProperty" : "PropertyDeclaration",
            MemberProvenance.FromInterface => "InterfaceProperty",
            MemberProvenance.Synthesized => "SynthesizedProperty",
            _ => "Unknown"
        };

        ctx.Renamer.ReserveMemberName(
            stableId: property.StableId,
            requested: property.ClrName,
            scope: typeScope,
            reason: reason,
            isStatic: property.IsStatic,
            decisionSource: "NameReservation");
    }

    private static void ReserveFieldName(BuildContext ctx, FieldSymbol field, TypeScope typeScope)
    {
        var reason = field.IsConst ? "ConstantField" : "FieldDeclaration";

        ctx.Renamer.ReserveMemberName(
            stableId: field.StableId,
            requested: field.ClrName,
            scope: typeScope,
            reason: reason,
            isStatic: field.IsStatic,
            decisionSource: "NameReservation");
    }

    private static void ReserveEventName(BuildContext ctx, EventSymbol ev, TypeScope typeScope)
    {
        ctx.Renamer.ReserveMemberName(
            stableId: ev.StableId,
            requested: ev.ClrName,
            scope: typeScope,
            reason: "EventDeclaration",
            isStatic: ev.IsStatic,
            decisionSource: "NameReservation");
    }

    private static void ReserveConstructorName(BuildContext ctx, ConstructorSymbol ctor, TypeScope typeScope)
    {
        ctx.Renamer.ReserveMemberName(
            stableId: ctor.StableId,
            requested: "constructor", // TypeScript always uses "constructor"
            scope: typeScope,
            reason: "ConstructorDeclaration",
            isStatic: ctor.IsStatic, // static constructors exist
            decisionSource: "NameReservation");
    }
}
