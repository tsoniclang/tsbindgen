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

        // DEBUG: Check for duplicates in existing members (should never happen)
        var methodDuplicates = type.Members.Methods
            .GroupBy(m => m.StableId)
            .Where(g => g.Count() > 1)
            .ToList();

        var propertyDuplicates = type.Members.Properties
            .GroupBy(p => p.StableId)
            .Where(g => g.Count() > 1)
            .ToList();

        if (methodDuplicates.Any() || propertyDuplicates.Any())
        {
            var details = string.Join("\n",
                methodDuplicates.Select(g => $"  Method {g.Key}: {g.Count()} duplicates")
                .Concat(propertyDuplicates.Select(g => $"  Property {g.Key}: {g.Count()} duplicates")));

            throw new InvalidOperationException(
                $"ExplicitImplSynthesizer: Type {type.ClrFullName} already has duplicate members BEFORE synthesis:\n{details}\n" +
                $"This indicates a loader bug or earlier pass adding duplicates.");
        }

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

        // DEDUPLICATION: Multiple interfaces may require the same member (e.g., ICollection.CopyTo and IList.CopyTo)
        // Keep only the first synthesis of each unique StableId (deterministic)
        var uniqueMethods = synthesizedMethods.GroupBy(m => m.StableId).Select(g => g.First()).ToList();
        var uniqueProperties = synthesizedProperties.GroupBy(p => p.StableId).Select(g => g.First()).ToList();

        if (synthesizedMethods.Count != uniqueMethods.Count)
        {
            ctx.Log("ExplicitImplSynthesizer",
                $"Deduplicated {synthesizedMethods.Count - uniqueMethods.Count} duplicate methods " +
                $"(multiple interfaces required same member)");
        }

        if (synthesizedProperties.Count != uniqueProperties.Count)
        {
            ctx.Log("ExplicitImplSynthesizer",
                $"Deduplicated {synthesizedProperties.Count - uniqueProperties.Count} duplicate properties " +
                $"(multiple interfaces required same member)");
        }

        synthesizedMethods = uniqueMethods;
        synthesizedProperties = uniqueProperties;

        // VALIDATION: Check for duplicates WITHIN the synthesized list (should be none after deduplication)
        var methodStableIdGroups = synthesizedMethods.GroupBy(m => m.StableId).Where(g => g.Count() > 1).ToList();
        var propertyStableIdGroups = synthesizedProperties.GroupBy(p => p.StableId).Where(g => g.Count() > 1).ToList();

        if (methodStableIdGroups.Any() || propertyStableIdGroups.Any())
        {
            var details = string.Join("\n",
                methodStableIdGroups.Select(g => $"  Method: {g.Key} ({g.Count()} copies)")
                .Concat(propertyStableIdGroups.Select(g => $"  Property: {g.Key} ({g.Count()} copies)")));

            throw new InvalidOperationException(
                $"ExplicitImplSynthesizer: Synthesized list contains INTERNAL duplicates for {type.ClrFullName}:\n{details}\n" +
                $"This indicates multiple interfaces required the same member.");
        }

        // VALIDATION: Check if adding these members would create duplicates with existing
        var existingMethodStableIds = type.Members.Methods.Select(m => m.StableId).ToHashSet();
        var existingPropertyStableIds = type.Members.Properties.Select(p => p.StableId).ToHashSet();

        var duplicateMethodsToAdd = synthesizedMethods.Where(m => existingMethodStableIds.Contains(m.StableId)).ToList();
        var duplicatePropertiesToAdd = synthesizedProperties.Where(p => existingPropertyStableIds.Contains(p.StableId)).ToList();

        if (duplicateMethodsToAdd.Any() || duplicatePropertiesToAdd.Any())
        {
            var details = string.Join("\n",
                duplicateMethodsToAdd.Select(m => $"  Method: {m.StableId}")
                .Concat(duplicatePropertiesToAdd.Select(p => $"  Property: {p.StableId}")));

            throw new InvalidOperationException(
                $"ExplicitImplSynthesizer: Attempting to add duplicate members to {type.ClrFullName}:\n{details}\n" +
                $"This would create duplicates with existing. Check FindMissingMembers logic.");
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
        // FIX: Compare by StableId directly instead of re-canonicalizing signatures
        // After canonical format change, re-canonicalizing causes mismatches
        foreach (var (iface, method) in required.Methods)
        {
            var exists = type.Members.Methods.Any(m => m.StableId.Equals(method.StableId));

            if (!exists)
            {
                missingMethods.Add((iface, method));
            }
        }

        // Check each required property
        // FIX: Compare by StableId directly instead of re-canonicalizing signatures
        foreach (var (iface, property) in required.Properties)
        {
            var exists = type.Members.Properties.Any(p => p.StableId.Equals(property.StableId));

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
            $"{Plan.Validation.Scopes.FormatMemberStableId(stableId)} -> ViewOnly");

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
            $"{Plan.Validation.Scopes.FormatMemberStableId(stableId)} -> ViewOnly");

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
