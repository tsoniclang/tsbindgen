using System.Collections.Generic;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Analyzes static-side inheritance issues.
/// Detects when static members conflict with instance members from the class hierarchy.
/// TypeScript doesn't allow the static side of a class to extend the static side of the base class,
/// which can cause TS2417 errors.
/// </summary>
public static class StaticSideAnalyzer
{
    public static void Analyze(BuildContext ctx, SymbolGraph graph)
    {
        var action = ctx.Policy.StaticSide.Action;
        ctx.Log("StaticSideAnalyzer", $"Analyzing static-side inheritance (Action: {action})...");

        var classes = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Class && t.BaseType != null)
            .ToList();

        int issuesFound = 0;
        int renamedCount = 0;

        foreach (var derivedClass in classes)
        {
            var (issues, renamed) = AnalyzeClass(ctx, graph, derivedClass, action);
            issuesFound += issues;
            renamedCount += renamed;
        }

        if (issuesFound > 0)
        {
            ctx.Log("StaticSideAnalyzer", $"Found {issuesFound} static-side inheritance issues");

            if (action == Core.Policy.StaticSideAction.AutoRename)
            {
                ctx.Log("StaticSideAnalyzer", $"Renamed {renamedCount} conflicting static members");
            }
            else if (action == Core.Policy.StaticSideAction.Analyze)
            {
                ctx.Log("StaticSideAnalyzer", "These can be resolved via renaming or explicit views if needed");
            }
        }
        else
        {
            ctx.Log("StaticSideAnalyzer", "No static-side issues detected");
        }
    }

    private static (int issues, int renamed) AnalyzeClass(BuildContext ctx, SymbolGraph graph, TypeSymbol derivedClass, Core.Policy.StaticSideAction action)
    {
        var baseClass = FindBaseClass(graph, derivedClass);
        if (baseClass == null)
            return (0, 0);

        // Get static members from both classes
        var derivedStatics = derivedClass.Members.Methods
            .Where(m => m.IsStatic)
            .Concat(derivedClass.Members.Properties.Where(p => p.IsStatic).Select(p => (object)p))
            .Concat(derivedClass.Members.Fields.Where(f => f.IsStatic).Select(f => (object)f))
            .ToList();

        var baseStatics = baseClass.Members.Methods
            .Where(m => m.IsStatic)
            .Concat(baseClass.Members.Properties.Where(p => p.IsStatic).Select(p => (object)p))
            .Concat(baseClass.Members.Fields.Where(f => f.IsStatic).Select(f => (object)f))
            .ToList();

        if (derivedStatics.Count == 0 && baseStatics.Count == 0)
            return (0, 0);

        // Check for conflicts
        var issues = new List<string>();
        var renamedCount = 0;

        // In TypeScript, the static side doesn't automatically inherit from base
        // This can cause issues when:
        // 1. Derived has static members with same names as base but different signatures
        // 2. Derived attempts to override static members (not allowed in TS)

        var derivedStaticNames = GetStaticMemberNames(derivedStatics);
        var baseStaticNames = GetStaticMemberNames(baseStatics);

        var conflicts = derivedStaticNames.Intersect(baseStaticNames).OrderBy(name => name).ToList();

        if (conflicts.Count > 0)
        {
            foreach (var conflictName in conflicts)
            {
                var diagnostic = $"Static member '{conflictName}' in {derivedClass.ClrFullName} conflicts with base class {baseClass.ClrFullName}";

                if (action == Core.Policy.StaticSideAction.Error)
                {
                    ctx.Diagnostics.Error(
                        Core.Diagnostics.DiagnosticCodes.StaticSideInheritanceIssue,
                        diagnostic);
                }
                else if (action == Core.Policy.StaticSideAction.AutoRename)
                {
                    // Rename the conflicting static member in derived class
                    var renamed = RenameConflictingStatic(ctx, derivedClass, derivedStatics, conflictName);
                    renamedCount += renamed;

                    if (renamed > 0)
                    {
                        ctx.Log("StaticSideAnalyzer", $"Renamed static member '{conflictName}' in {derivedClass.ClrFullName}");
                    }
                }
                else
                {
                    ctx.Diagnostics.Warning(
                        Core.Diagnostics.DiagnosticCodes.StaticSideInheritanceIssue,
                        diagnostic);
                }

                issues.Add(diagnostic);
            }
        }

        return (issues.Count, renamedCount);
    }

    private static int RenameConflictingStatic(BuildContext ctx, TypeSymbol derivedClass, List<object> derivedStatics, string conflictName)
    {
        // Find all static members with this name in the derived class
        var conflictingMembers = derivedStatics
            .Where(m => GetMemberName(m) == conflictName)
            .ToList();

        if (conflictingMembers.Count == 0)
            return 0;

        var typeScope = new SinglePhase.Renaming.TypeScope
        {
            TypeFullName = derivedClass.ClrFullName,
            IsStatic = true,
            ScopeKey = $"{derivedClass.ClrFullName}#static"
        };

        // Rename each conflicting member using Renamer
        foreach (var member in conflictingMembers)
        {
            switch (member)
            {
                case Model.Symbols.MemberSymbols.MethodSymbol method:
                    ctx.Renamer.ReserveMemberName(
                        method.StableId,
                        $"{method.ClrName}_static",
                        typeScope,
                        "StaticSideNameCollision",
                        isStatic: true);
                    break;

                case Model.Symbols.MemberSymbols.PropertySymbol property:
                    ctx.Renamer.ReserveMemberName(
                        property.StableId,
                        $"{property.ClrName}_static",
                        typeScope,
                        "StaticSideNameCollision",
                        isStatic: true);
                    break;

                case Model.Symbols.MemberSymbols.FieldSymbol field:
                    ctx.Renamer.ReserveMemberName(
                        field.StableId,
                        $"{field.ClrName}_static",
                        typeScope,
                        "StaticSideNameCollision",
                        isStatic: true);
                    break;
            }
        }

        return conflictingMembers.Count;
    }

    private static string GetMemberName(object member)
    {
        return member switch
        {
            Model.Symbols.MemberSymbols.MethodSymbol m => m.ClrName,
            Model.Symbols.MemberSymbols.PropertySymbol p => p.ClrName,
            Model.Symbols.MemberSymbols.FieldSymbol f => f.ClrName,
            _ => string.Empty
        };
    }

    private static HashSet<string> GetStaticMemberNames(List<object> members)
    {
        var names = new HashSet<string>();

        foreach (var member in members)
        {
            switch (member)
            {
                case Model.Symbols.MemberSymbols.MethodSymbol m:
                    names.Add(m.ClrName);
                    break;
                case Model.Symbols.MemberSymbols.PropertySymbol p:
                    names.Add(p.ClrName);
                    break;
                case Model.Symbols.MemberSymbols.FieldSymbol f:
                    names.Add(f.ClrName);
                    break;
            }
        }

        return names;
    }

    private static TypeSymbol? FindBaseClass(SymbolGraph graph, TypeSymbol derivedClass)
    {
        if (derivedClass.BaseType == null)
            return null;

        var baseFullName = GetTypeFullName(derivedClass.BaseType);

        // Skip System.Object and System.ValueType
        if (baseFullName == "System.Object" || baseFullName == "System.ValueType")
            return null;

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == baseFullName && t.Kind == TypeKind.Class);
    }

    private static string GetTypeFullName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }
}
