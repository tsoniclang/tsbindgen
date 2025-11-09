using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Core.Canon;
using tsbindgen.Core.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Synthesizes missing interface members for classes/structs.
/// Ensures all interface-required members exist on implementing types.
/// </summary>
public static class ExplicitImplSynthesizer
{
    public static void Synthesize(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("ExplicitImplSynthesizer", "Synthesizing missing interface members...");

        var classesAndStructs = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Class || t.Kind == TypeKind.Struct)
            .ToList();

        ctx.Log("ExplicitImplSynthesizer", $"Processing {classesAndStructs.Count} classes/structs");

        int totalSynthesized = 0;

        foreach (var type in classesAndStructs)
        {
            var synthesizedCount = SynthesizeForType(ctx, graph, type);
            totalSynthesized += synthesizedCount;
        }

        ctx.Log("ExplicitImplSynthesizer", $"Synthesized {totalSynthesized} interface members");
    }

    private static int SynthesizeForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type)
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
            return 0;
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

        // Add synthesized members to the type
        var updatedMembers = new TypeMembers
        {
            Methods = type.Members.Methods.Concat(synthesizedMethods).ToImmutableArray(),
            Properties = type.Members.Properties.Concat(synthesizedProperties).ToImmutableArray(),
            Fields = type.Members.Fields,
            Events = type.Members.Events,
            Constructors = type.Members.Constructors
        };

        // Update type (using reflection)
        var membersProperty = typeof(TypeSymbol).GetProperty(nameof(TypeSymbol.Members));
        membersProperty!.SetValue(type, updatedMembers);

        return synthesizedMethods.Count + synthesizedProperties.Count;
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
        // Synthesize with explicit view suffix if policy says so
        var strategy = ctx.Policy.Classes.SynthesizeExplicitImpl;

        string requestedName = method.ClrName;

        if (strategy == Core.Policy.ExplicitImplStrategy.SynthesizeWithSuffix)
        {
            var ifaceName = GetSimpleInterfaceName(iface);
            requestedName = $"{method.ClrName}_{ifaceName}";
        }

        // Reserve the name through renamer
        var typeScope = new TypeScope
        {
            TypeFullName = type.ClrFullName,
            IsStatic = false,
            ScopeKey = $"{type.ClrFullName}#instance"
        };

        var stableId = new MemberStableId
        {
            AssemblyName = type.StableId.AssemblyName,
            DeclaringClrFullName = type.ClrFullName,
            MemberName = requestedName,
            CanonicalSignature = ctx.CanonicalizeMethod(
                requestedName,
                method.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                GetTypeFullName(method.ReturnType))
        };

        ctx.Renamer.ReserveMemberName(
            stableId,
            requestedName,
            typeScope,
            "InterfaceSynthesis",
            isStatic: false);

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

        // Create synthesized method symbol
        return new MethodSymbol
        {
            StableId = stableId,
            ClrName = requestedName,
            ReturnType = method.ReturnType,
            Parameters = method.Parameters,
            GenericParameters = method.GenericParameters,
            IsStatic = false,
            IsAbstract = type.IsAbstract,
            IsVirtual = true,
            IsOverride = false,
            IsSealed = false,
            IsNew = false,
            Visibility = Visibility.Public,
            Provenance = MemberProvenance.Synthesized,
            EmitScope = EmitScope.ClassSurface,
            SourceInterface = declaringInterface ?? iface
        };
    }

    private static PropertySymbol SynthesizeProperty(BuildContext ctx, TypeSymbol type, TypeReference iface, PropertySymbol property)
    {
        var strategy = ctx.Policy.Classes.SynthesizeExplicitImpl;

        string requestedName = property.ClrName;

        if (strategy == Core.Policy.ExplicitImplStrategy.SynthesizeWithSuffix)
        {
            var ifaceName = GetSimpleInterfaceName(iface);
            requestedName = $"{property.ClrName}_{ifaceName}";
        }

        var indexParams = property.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();

        var stableId = new MemberStableId
        {
            AssemblyName = type.StableId.AssemblyName,
            DeclaringClrFullName = type.ClrFullName,
            MemberName = requestedName,
            CanonicalSignature = ctx.CanonicalizeProperty(requestedName, indexParams, GetTypeFullName(property.PropertyType))
        };

        var typeScope = new TypeScope
        {
            TypeFullName = type.ClrFullName,
            IsStatic = false,
            ScopeKey = $"{type.ClrFullName}#instance"
        };

        ctx.Renamer.ReserveMemberName(
            stableId,
            requestedName,
            typeScope,
            "InterfaceSynthesis",
            isStatic: false);

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

        return new PropertySymbol
        {
            StableId = stableId,
            ClrName = requestedName,
            PropertyType = property.PropertyType,
            IndexParameters = property.IndexParameters,
            HasGetter = property.HasGetter,
            HasSetter = property.HasSetter,
            IsStatic = false,
            IsVirtual = true,
            IsOverride = false,
            Visibility = Visibility.Public,
            Provenance = MemberProvenance.Synthesized,
            EmitScope = EmitScope.ClassSurface,
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
