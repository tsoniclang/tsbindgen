using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;

namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// Plans stable, deterministic emission order.
/// Ensures reproducible .d.ts files across runs.
/// Uses Renamer.GetFinalTypeName() for stable sorting.
/// </summary>
public sealed class EmitOrderPlanner
{
    private readonly BuildContext _ctx;

    public EmitOrderPlanner(BuildContext ctx)
    {
        _ctx = ctx;
    }

    public EmitOrder PlanOrder(SymbolGraph graph)
    {
        _ctx.Log("EmitOrderPlanner", "Planning deterministic emission order...");

        var orderedNamespaces = new List<NamespaceEmitOrder>();

        foreach (var ns in graph.Namespaces.OrderBy(n => n.Name))
        {
            var orderedTypes = OrderTypes(ns.Types);
            orderedNamespaces.Add(new NamespaceEmitOrder
            {
                Namespace = ns,
                OrderedTypes = orderedTypes
            });
        }

        _ctx.Log("EmitOrderPlanner", $"Ordered {orderedNamespaces.Count} namespaces");

        return new EmitOrder
        {
            Namespaces = orderedNamespaces
        };
    }

    private List<TypeEmitOrder> OrderTypes(IReadOnlyList<TypeSymbol> types)
    {
        var result = new List<TypeEmitOrder>();

        // Sort types by:
        // 1. Kind (Enum < Delegate < Interface < Struct < Class < StaticNamespace)
        // 2. Final TS name from Renamer (for stable diffs when names change)
        // 3. Arity (for overloaded generic types)

        // Get namespace scope for name resolution (assuming all types in same namespace)
        var nsScope = types.Count > 0 ? new SinglePhase.Renaming.NamespaceScope
        {
            Namespace = types[0].Namespace,
            IsInternal = true,
            ScopeKey = $"ns:{types[0].Namespace}:internal"
        } : null;

        var sorted = types.OrderBy(t => GetKindSortOrder(t.Kind))
                          .ThenBy(t => nsScope != null ? _ctx.Renamer.GetFinalTypeName(t.StableId, nsScope) : t.ClrName)
                          .ThenBy(t => t.Arity)
                          .ToList();

        foreach (var type in sorted)
        {
            // Recursively order nested types
            var orderedNested = type.NestedTypes.Length > 0
                ? OrderTypes(type.NestedTypes)
                : new List<TypeEmitOrder>();

            // Order members within the type
            var orderedMembers = OrderMembers(type);

            result.Add(new TypeEmitOrder
            {
                Type = type,
                OrderedMembers = orderedMembers,
                OrderedNestedTypes = orderedNested
            });
        }

        return result;
    }

    private MemberEmitOrder OrderMembers(TypeSymbol type)
    {
        // Sort members by:
        // 1. Kind (Constructor < Field < Property < Event < Method)
        // 2. IsStatic (instance first, then static)
        // 3. Final TS name from Renamer (for stable diffs)
        // 4. Canonical signature (for overloads)

        var orderedConstructors = type.Members.Constructors
            .OrderBy(c => c.IsStatic)
            .ThenBy(c => c.StableId.CanonicalSignature)
            .ToList();

        var orderedFields = type.Members.Fields
            .OrderBy(f => f.IsStatic)
            .ThenBy(f => {
                var scope = new SinglePhase.Renaming.TypeScope
                {
                    TypeFullName = type.ClrFullName,
                    IsStatic = f.IsStatic,
                    ScopeKey = $"type:{type.ClrFullName}#{(f.IsStatic ? "static" : "instance")}"
                };
                return _ctx.Renamer.GetFinalMemberName(f.StableId, scope, f.IsStatic);
            })
            .ToList();

        var orderedProperties = type.Members.Properties
            .OrderBy(p => p.IsStatic)
            .ThenBy(p => {
                var scope = new SinglePhase.Renaming.TypeScope
                {
                    TypeFullName = type.ClrFullName,
                    IsStatic = p.IsStatic,
                    ScopeKey = $"type:{type.ClrFullName}#{(p.IsStatic ? "static" : "instance")}"
                };
                return _ctx.Renamer.GetFinalMemberName(p.StableId, scope, p.IsStatic);
            })
            .ThenBy(p => p.StableId.CanonicalSignature)
            .ToList();

        var orderedEvents = type.Members.Events
            .OrderBy(e => e.IsStatic)
            .ThenBy(e => {
                var scope = new SinglePhase.Renaming.TypeScope
                {
                    TypeFullName = type.ClrFullName,
                    IsStatic = e.IsStatic,
                    ScopeKey = $"type:{type.ClrFullName}#{(e.IsStatic ? "static" : "instance")}"
                };
                return _ctx.Renamer.GetFinalMemberName(e.StableId, scope, e.IsStatic);
            })
            .ToList();

        var orderedMethods = type.Members.Methods
            .OrderBy(m => m.IsStatic)
            .ThenBy(m => {
                var scope = new SinglePhase.Renaming.TypeScope
                {
                    TypeFullName = type.ClrFullName,
                    IsStatic = m.IsStatic,
                    ScopeKey = $"type:{type.ClrFullName}#{(m.IsStatic ? "static" : "instance")}"
                };
                return _ctx.Renamer.GetFinalMemberName(m.StableId, scope, m.IsStatic);
            })
            .ThenBy(m => m.Arity)
            .ThenBy(m => m.StableId.CanonicalSignature)
            .ToList();

        return new MemberEmitOrder
        {
            Constructors = orderedConstructors,
            Fields = orderedFields,
            Properties = orderedProperties.ToImmutableArray(),
            Events = orderedEvents.ToImmutableArray(),
            Methods = orderedMethods
        };
    }

    private int GetKindSortOrder(Model.Symbols.TypeKind kind)
    {
        return kind switch
        {
            Model.Symbols.TypeKind.Enum => 0,
            Model.Symbols.TypeKind.Delegate => 1,
            Model.Symbols.TypeKind.Interface => 2,
            Model.Symbols.TypeKind.Struct => 3,
            Model.Symbols.TypeKind.Class => 4,
            Model.Symbols.TypeKind.StaticNamespace => 5,
            _ => 999
        };
    }
}

/// <summary>
/// Planned emission order for the entire graph.
/// </summary>
public sealed record EmitOrder
{
    public required IReadOnlyList<NamespaceEmitOrder> Namespaces { get; init; }
}

/// <summary>
/// Planned emission order for a single namespace.
/// </summary>
public sealed record NamespaceEmitOrder
{
    public required NamespaceSymbol Namespace { get; init; }
    public required IReadOnlyList<TypeEmitOrder> OrderedTypes { get; init; }
}

/// <summary>
/// Planned emission order for a single type.
/// </summary>
public sealed record TypeEmitOrder
{
    public required TypeSymbol Type { get; init; }
    public required MemberEmitOrder OrderedMembers { get; init; }
    public required IReadOnlyList<TypeEmitOrder> OrderedNestedTypes { get; init; }
}

/// <summary>
/// Planned emission order for members within a type.
/// </summary>
public sealed record MemberEmitOrder
{
    public required IReadOnlyList<Model.Symbols.MemberSymbols.ConstructorSymbol> Constructors { get; init; }
    public required IReadOnlyList<Model.Symbols.MemberSymbols.FieldSymbol> Fields { get; init; }
    public required IReadOnlyList<Model.Symbols.MemberSymbols.PropertySymbol> Properties { get; init; }
    public required IReadOnlyList<Model.Symbols.MemberSymbols.EventSymbol> Events { get; init; }
    public required IReadOnlyList<Model.Symbols.MemberSymbols.MethodSymbol> Methods { get; init; }
}
