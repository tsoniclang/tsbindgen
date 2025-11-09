using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Plans handling of C# 'new' hidden members.
/// When a derived class hides a base member with 'new', we need to emit both:
/// - The base member (inherited)
/// - The derived member (with a suffix like "_new")
/// Uses the Renamer to reserve names with HiddenNewConflict reason.
/// </summary>
public static class HiddenMemberPlanner
{
    public static void Plan(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("HiddenMemberPlanner", "Planning C# 'new' hidden members...");

        var processedCount = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                processedCount += ProcessType(ctx, type);
            }
        }

        ctx.Log("HiddenMemberPlanner", $"Processed {processedCount} hidden members");
    }

    private static int ProcessType(BuildContext ctx, TypeSymbol type)
    {
        // Only process classes and structs that have a base type
        if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
            return 0;

        if (type.BaseType == null)
            return 0;

        var count = 0;
        // M5 FIX: Use correct scope format with type: prefix
        var typeScope = new TypeScope
        {
            TypeFullName = type.ClrFullName,
            IsStatic = false, // Will be set per member
            ScopeKey = $"type:{type.ClrFullName}" // Base scope, ReserveMemberName will add #static or #instance
        };

        // Process methods marked with IsNew
        foreach (var method in type.Members.Methods.Where(m => m.IsNew))
        {
            var suffix = ctx.Policy.Classes.HiddenMemberSuffix;
            var requestedName = method.ClrName + suffix;

            // Reserve the renamed version through the Renamer
            ctx.Renamer.ReserveMemberName(
                method.StableId,
                requestedName,
                typeScope with { IsStatic = method.IsStatic },
                "HiddenNewConflict",
                method.IsStatic,
                "HiddenMemberPlanner");

            count++;
        }

        // Process properties marked with IsNew (from PropertySymbol, checking getter/setter)
        // Note: PropertySymbol doesn't have IsNew field, so we'd need to check the underlying accessor
        // For now, we'll skip properties as they're less common to hide

        // Recursively process nested types
        foreach (var nested in type.NestedTypes)
        {
            count += ProcessType(ctx, nested);
        }

        return count;
    }
}

