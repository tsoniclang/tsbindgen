using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Core.Canon;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Synthesizes missing interface members for classes/structs.
/// Ensures all interface-required members exist on implementing types.
/// PURE - returns new SymbolGraph.
/// </summary>
public static class ExplicitImplSynthesizer
{
    public static SymbolGraph Synthesize(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("ExplicitImplSynthesizer", "Synthesizing missing interface members...");

        var classesAndStructs = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Class || t.Kind == TypeKind.Struct)
            .ToList();

        ctx.Log("ExplicitImplSynthesizer", $"Processing {classesAndStructs.Count} classes/structs");

        int totalSynthesized = 0;
        var updatedGraph = graph;

        foreach (var type in classesAndStructs)
        {
            var (newGraph, synthesizedCount) = SynthesizeForType(ctx, updatedGraph, type);
            updatedGraph = newGraph;
            totalSynthesized += synthesizedCount;
        }

        ctx.Log("ExplicitImplSynthesizer", $"Synthesized {totalSynthesized} interface members");
        return updatedGraph;
    }

    private static (SymbolGraph UpdatedGraph, int SynthesizedCount) SynthesizeForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type)
    {
        ctx.Log("ExplicitImplSynthesizer", $"Processing type {type.ClrFullName} with {type.Interfaces.Length} interfaces");

        // Collect all interface members required
        var requiredMembers = CollectInterfaceMembers(ctx, graph, type);

        ctx.Log("ExplicitImplSynthesizer", $"Found {requiredMembers.Methods.Count} required methods, {requiredMembers.Properties.Count} required properties");

        // Find which ones are missing
        var missing = FindMissingMembers(ctx, type, requiredMembers);

        if (missing.Count == 0)
        {
            ctx.Log("ExplicitImplSynthesizer", $"Type {type.ClrFullName} has all required members - nothing to synthesize");
            return (graph, 0);
        }

        ctx.Log("ExplicitImplSynthesizer", $"Type {type.ClrFullName} missing {missing.Count} interface members");

        // Synthesize the missing members
        var synthesizedMethods = new List<MethodSymbol>();
        var synthesizedProperties = new List<PropertySymbol>();

        foreach (var (iface, method) in missing.Methods)
        {
            var synthesized = SynthesizeMethod(ctx, type, iface, method);
            synthesizedMethods.Add(synthesized);
        }

        foreach (var (iface, property) in missing.Properties)
        {
            var synthesized = SynthesizeProperty(ctx, type, iface, property);
            synthesizedProperties.Add(synthesized);
        }

        // Add synthesized members to the type (immutably)
        var synthesizedCount = synthesizedMethods.Count + synthesizedProperties.Count;
        var updatedGraph = graph.WithUpdatedType(type.StableId.ToString(), t => t with
        {
            Members = t.Members with
            {
                Methods = t.Members.Methods.Concat(synthesizedMethods).ToImmutableArray(),
                Properties = t.Members.Properties.Concat(synthesizedProperties).ToImmutableArray()
            }
        });

        return (updatedGraph, synthesizedCount);
    }

    /// <summary>
    /// Determines if we will plan a view for the given interface.
    /// Only synthesize ViewOnly members for interfaces we will actually emit views for.
    /// </summary>
    private static bool WillPlanViewFor(BuildContext ctx, SymbolGraph graph, TypeSymbol type, TypeReference ifaceRef)
    {
        var iface = FindInterface(graph, ifaceRef);
        if (iface == null)
            return false; // Not in graph => no view => no synthesis

        // Interface is in the graph and we will emit a view for it
        return true;
    }

    private static InterfaceMembers CollectInterfaceMembers(BuildContext ctx, SymbolGraph graph, TypeSymbol type)
    {
        var methods = new List<(TypeReference Iface, MethodSymbol Method)>();
        var properties = new List<(TypeReference Iface, PropertySymbol Property)>();

        foreach (var ifaceRef in type.Interfaces)
        {
            // Gate synthesis: only process interfaces we will emit views for
            if (!WillPlanViewFor(ctx, graph, type, ifaceRef))
                continue; // No synthesis

            var iface = FindInterface(graph, ifaceRef);
            if (iface == null)
                continue; // External interface

            // Collect all methods and properties from this interface
            foreach (var method in iface.Members.Methods)
            {
                methods.Add((ifaceRef, method));
            }

            foreach (var property in iface.Members.Properties)
            {
                // Skip indexer properties - they should not be synthesized as interface members
                if (property.IndexParameters.Length > 0)
                    continue;

                properties.Add((ifaceRef, property));
            }
        }

        return new InterfaceMembers(methods, properties);
    }

    private static MissingMembers FindMissingMembers(BuildContext ctx, TypeSymbol type, InterfaceMembers required)
    {
        var missingMethods = new List<(TypeReference Iface, MethodSymbol Method)>();
        var missingProperties = new List<(TypeReference Iface, PropertySymbol Property)>();

        // Check each required method
        foreach (var (iface, method) in required.Methods)
        {
            var sig = ctx.CanonicalizeMethod(
                method.ClrName,
                method.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                GetTypeFullName(method.ReturnType));

            var exists = type.Members.Methods.Any(m =>
            {
                var mSig = ctx.CanonicalizeMethod(
                    m.ClrName,
                    m.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                    GetTypeFullName(m.ReturnType));
                return mSig == sig;
            });

            if (!exists)
            {
                missingMethods.Add((iface, method));
            }
        }

        // Check each required property
        foreach (var (iface, property) in required.Properties)
        {
            var indexParams = property.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();

            var sig = ctx.CanonicalizeProperty(
                property.ClrName,
                indexParams,
                GetTypeFullName(property.PropertyType));

            var exists = type.Members.Properties.Any(p =>
            {
                var pIndexParams = p.IndexParameters.Select(param => GetTypeFullName(param.Type)).ToList();
                var pSig = ctx.CanonicalizeProperty(
                    p.ClrName,
                    pIndexParams,
                    GetTypeFullName(p.PropertyType));
                return pSig == sig;
            });

            if (!exists)
            {
                missingProperties.Add((iface, property));
            }
        }

        return new MissingMembers(missingMethods, missingProperties);
    }

    private static MethodSymbol SynthesizeMethod(BuildContext ctx, TypeSymbol type, TypeReference iface, MethodSymbol method)
    {
        // Resolve to the declaring interface (not just the contributing interface)
        var memberCanonicalSig = ctx.CanonicalizeMethod(
            method.ClrName,
            method.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
            GetTypeFullName(method.ReturnType));

        var declaringInterface = InterfaceResolver.FindDeclaringInterface(
            iface,
            memberCanonicalSig,
            isMethod: true,
            ctx);

        // M5 FIX: Use interface member's StableId, mark as ViewOnly
        // EII members aren't accessible via the class in C#, only through the interface
        var stableId = method.StableId;

        ctx.Log("explicit-impl",
            $"eii: {type.StableId} {declaringInterface?.ToString() ?? iface.ToString()} " +
            $"{Plan.PhaseGate.FormatMemberStableId(stableId)} -> ViewOnly");

        // Create synthesized method symbol
        return new MethodSymbol
        {
            StableId = stableId,
            ClrName = method.ClrName,
            ReturnType = method.ReturnType,
            Parameters = method.Parameters,
            GenericParameters = method.GenericParameters,
            IsStatic = false,
            IsAbstract = false,
            IsVirtual = true,
            IsOverride = false,
            IsSealed = false,
            IsNew = false,
            Visibility = Visibility.Public,
            Provenance = MemberProvenance.ExplicitView,
            EmitScope = EmitScope.ViewOnly,
            SourceInterface = declaringInterface ?? iface
        };
    }

    private static PropertySymbol SynthesizeProperty(BuildContext ctx, TypeSymbol type, TypeReference iface, PropertySymbol property)
    {
        var indexParams = property.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();

        // Resolve to the declaring interface (not just the contributing interface)
        var memberCanonicalSig = ctx.CanonicalizeProperty(
            property.ClrName,
            indexParams,
            GetTypeFullName(property.PropertyType));

        var declaringInterface = InterfaceResolver.FindDeclaringInterface(
            iface,
            memberCanonicalSig,
            isMethod: false,
            ctx);

        // M5 FIX: Use interface property's StableId, mark as ViewOnly
        var stableId = property.StableId;

        ctx.Log("explicit-impl",
            $"eii: {type.StableId} {declaringInterface?.ToString() ?? iface.ToString()} " +
            $"{Plan.PhaseGate.FormatMemberStableId(stableId)} -> ViewOnly");

        return new PropertySymbol
        {
            StableId = stableId,
            ClrName = property.ClrName,
            PropertyType = property.PropertyType,
            IndexParameters = property.IndexParameters,
            HasGetter = property.HasGetter,
            HasSetter = property.HasSetter,
            IsStatic = false,
            IsVirtual = true,
            IsOverride = false,
            Visibility = Visibility.Public,
            Provenance = MemberProvenance.ExplicitView,
            EmitScope = EmitScope.ViewOnly,
            SourceInterface = declaringInterface ?? iface
        };
    }

    private static string GetSimpleInterfaceName(TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.Name.Replace("`", "_"),
            Model.Types.NestedTypeReference nested => nested.NestedName.Replace("`", "_"),
            _ => "Interface"
        };
    }

    private static string GetTypeFullName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            Model.Types.GenericParameterReference gp => gp.Name,
            Model.Types.ArrayTypeReference arr => $"{GetTypeFullName(arr.ElementType)}[]",
            Model.Types.PointerTypeReference ptr => $"{GetTypeFullName(ptr.PointeeType)}*",
            Model.Types.ByRefTypeReference byref => $"{GetTypeFullName(byref.ReferencedType)}&",
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static TypeSymbol? FindInterface(SymbolGraph graph, Model.Types.TypeReference typeRef)
    {
        var fullName = GetTypeFullName(typeRef);

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }

    private record InterfaceMembers(
        List<(TypeReference Iface, MethodSymbol Method)> Methods,
        List<(TypeReference Iface, PropertySymbol Property)> Properties);

    private record MissingMembers(
        List<(TypeReference Iface, MethodSymbol Method)> Methods,
        List<(TypeReference Iface, PropertySymbol Property)> Properties)
    {
        public int Count => Methods.Count + Properties.Count;
    }
}
