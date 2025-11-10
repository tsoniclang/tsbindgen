using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Renaming;

namespace tsbindgen.SinglePhase.Normalize.Naming;

/// <summary>
/// Name application functions - apply reserved names from Renamer to symbol graph.
/// Phase 2: Transforms symbol graph by setting TsEmitName properties.
/// </summary>
internal static class Application
{
    /// <summary>
    /// Apply reserved names to entire symbol graph.
    /// Transforms graph by copying and updating TsEmitName on types and members.
    /// Returns new graph with updated names.
    /// </summary>
    internal static SymbolGraph ApplyNamesToGraph(BuildContext ctx, SymbolGraph graph)
    {
        var updatedNamespaces = graph.Namespaces.Select(ns => ApplyNamesToNamespace(ctx, ns)).ToImmutableArray();
        return (graph with { Namespaces = updatedNamespaces }).WithIndices();
    }

    /// <summary>
    /// Apply reserved names to a namespace.
    /// Skips compiler-generated types.
    /// Returns new namespace with updated types.
    /// </summary>
    private static NamespaceSymbol ApplyNamesToNamespace(BuildContext ctx, NamespaceSymbol ns)
    {
        var nsScope = ScopeFactory.Namespace(ns.Name, NamespaceArea.Internal);

        var updatedTypes = ns.Types.Select(t =>
        {
            if (Shared.IsCompilerGenerated(t.ClrName))
                return t;

            return ApplyNamesToType(ctx, t, nsScope);
        }).ToImmutableArray();

        return ns with { Types = updatedTypes };
    }

    /// <summary>
    /// Apply reserved names to a type.
    /// Gets TsEmitName from Renamer and applies to type and all members.
    /// Returns new type with updated names.
    /// </summary>
    private static TypeSymbol ApplyNamesToType(BuildContext ctx, TypeSymbol type, NamespaceScope nsScope)
    {
        // Base scope (unused here - kept for signature compatibility)
        var typeScope = ScopeFactory.ClassBase(type);

        // Get TsEmitName from Renamer
        var tsEmitName = ctx.Renamer.GetFinalTypeName(type);

        // Update members
        // M5 FIX: Pass declaringType so ViewOnly members can use view scopes
        var updatedMembers = ApplyNamesToMembers(ctx, type, type.Members, typeScope);

        // Return new type with updated names
        return type with
        {
            TsEmitName = tsEmitName,
            Members = updatedMembers
        };
    }

    /// <summary>
    /// Apply reserved names to type members.
    /// M5 FIX: ViewOnly members use view scope, others use class scope.
    /// Handles methods, properties, fields, events.
    /// Returns new TypeMembers with updated TsEmitName on all members.
    /// </summary>
    private static TypeMembers ApplyNamesToMembers(BuildContext ctx, TypeSymbol declaringType, TypeMembers members, TypeScope typeScope)
    {
        // M5 FIX: ViewOnly members use view scope, others use class scope
        var updatedMethods = members.Methods.Select(m =>
        {
            string tsEmitName;
            if (m.EmitScope == EmitScope.ViewOnly && m.SourceInterface != null)
            {
                var interfaceStableId = ScopeFactory.GetInterfaceStableId(m.SourceInterface);
                var viewScope = ScopeFactory.ViewSurface(declaringType, interfaceStableId, m.IsStatic);
                tsEmitName = ctx.Renamer.GetFinalMemberName(m.StableId, viewScope);
            }
            else
            {
                var classScope = ScopeFactory.ClassSurface(declaringType, m.IsStatic);
                tsEmitName = ctx.Renamer.GetFinalMemberName(m.StableId, classScope);
            }
            return m with { TsEmitName = tsEmitName };
        }).ToImmutableArray();

        var updatedProperties = members.Properties.Select(p =>
        {
            string tsEmitName;
            if (p.EmitScope == EmitScope.ViewOnly && p.SourceInterface != null)
            {
                var interfaceStableId = ScopeFactory.GetInterfaceStableId(p.SourceInterface);
                var viewScope = ScopeFactory.ViewSurface(declaringType, interfaceStableId, p.IsStatic);
                tsEmitName = ctx.Renamer.GetFinalMemberName(p.StableId, viewScope);
            }
            else
            {
                var classScope = ScopeFactory.ClassSurface(declaringType, p.IsStatic);
                tsEmitName = ctx.Renamer.GetFinalMemberName(p.StableId, classScope);
            }
            return p with { TsEmitName = tsEmitName };
        }).ToImmutableArray();

        // Fields are always ClassSurface, use class scope
        var updatedFields = members.Fields.Select(f =>
        {
            var fieldScope = ScopeFactory.ClassSurface(declaringType, f.IsStatic);
            var tsEmitName = ctx.Renamer.GetFinalMemberName(f.StableId, fieldScope);
            return f with { TsEmitName = tsEmitName };
        }).ToImmutableArray();

        // Events are always ClassSurface, use class scope
        var updatedEvents = members.Events.Select(e =>
        {
            var eventScope = ScopeFactory.ClassSurface(declaringType, e.IsStatic);
            var tsEmitName = ctx.Renamer.GetFinalMemberName(e.StableId, eventScope);
            return e with { TsEmitName = tsEmitName };
        }).ToImmutableArray();

        return members with
        {
            Methods = updatedMethods.ToImmutableArray(),
            Properties = updatedProperties.ToImmutableArray(),
            Fields = updatedFields,
            Events = updatedEvents
        };
    }
}
