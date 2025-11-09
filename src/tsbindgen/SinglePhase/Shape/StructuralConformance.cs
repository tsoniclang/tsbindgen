using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Core.Renaming;
using tsbindgen.SinglePhase.Load;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;
using tsbindgen.SinglePhase.Normalize;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Analyzes structural conformance for interfaces.
/// For each interface that cannot be structurally implemented on the class surface,
/// synthesizes ViewOnly members that will appear in explicit views (As_IInterface properties).
/// </summary>
public static class StructuralConformance
{
    public static SymbolGraph Analyze(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("StructuralConformance", "Analyzing structural conformance and synthesizing ViewOnly members...");

        var classesAndStructs = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Class || t.Kind == TypeKind.Struct)
            .ToList();

        int totalViewOnlyMembers = 0;

        // Build new namespaces immutably
        var updatedNamespaces = graph.Namespaces.Select(ns =>
        {
            var updatedTypes = ns.Types.Select(type =>
            {
                if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
                    return type;

                var (updatedType, synthesizedCount) = AnalyzeType(ctx, graph, type);
                totalViewOnlyMembers += synthesizedCount;
                return updatedType;
            }).ToImmutableArray();

            return ns with { Types = updatedTypes };
        }).ToImmutableArray();

        ctx.Log("StructuralConformance", $"Synthesized {totalViewOnlyMembers} ViewOnly members across {classesAndStructs.Count} types");

        return (graph with { Namespaces = updatedNamespaces }).WithIndices();
    }

    /// <summary>
    /// Determines if we will plan a view for the given interface.
    /// Only synthesize ViewOnly members for interfaces we will actually emit views for.
    /// </summary>
    private static bool WillPlanViewFor(BuildContext ctx, SymbolGraph graph, TypeSymbol type, TypeReference ifaceRef)
    {
        var ifaceName = GetTypeFullName(ifaceRef);
        var iface = FindInterface(graph, ifaceRef);
        if (iface == null)
        {
            return false; // Not in graph => no view => no synthesis
        }

        // Interface is in the graph and we will emit a view for it
        return true;
    }

    private static (TypeSymbol UpdatedType, int SynthesizedCount) AnalyzeType(
        BuildContext ctx,
        SymbolGraph graph,
        TypeSymbol type)
    {
        if (type.Interfaces.Length == 0)
            return (type, 0);

        var viewOnlyMethods = new List<MethodSymbol>();
        var viewOnlyProperties = new List<PropertySymbol>();
        var interfacesNeedingViews = new HashSet<string>(); // Track which interfaces need views

        // Track synthesized signatures to prevent duplicates across multiple interfaces
        var synthesizedMethodSigs = new HashSet<string>();
        var synthesizedPropertySigs = new HashSet<string>();

        // Build class representable surface once (excludes existing ViewOnly members)
        var classSurface = BuildClassSurface(ctx, type);

        ctx.Log("StructuralConformance", $"Analyzing {type.ClrFullName} with {type.Interfaces.Length} interfaces");

        // For each implemented interface, build substituted surface and compare
        foreach (var ifaceRef in type.Interfaces)
        {
            // Gate synthesis: only create ViewOnly members for interfaces we will emit views for
            if (!WillPlanViewFor(ctx, graph, type, ifaceRef))
            {
                ctx.Log("StructuralConformance", $"Skipping interface {GetTypeFullName(ifaceRef)} (no view will be planned)");
                continue; // DO NOT synthesize; DO NOT record a view
            }

            var iface = FindInterface(graph, ifaceRef);
            if (iface == null)
            {
                ctx.Log("StructuralConformance", $"Skipping external interface {GetTypeFullName(ifaceRef)}");
                continue; // External interface
            }

            // Build substituted interface surface (flattened + type args substituted)
            var interfaceSurface = BuildInterfaceSurface(ctx, graph, ifaceRef, iface);

            int missingMembers = 0;

            // Check each interface method
            foreach (var (ifaceMethod, declaringIface) in interfaceSurface.Methods)
            {
                // Check if already on class surface
                if (classSurface.HasMethod(ifaceMethod))
                    continue;

                // Check if already synthesized for a different interface
                var methodSig = ctx.CanonicalizeMethod(
                    ifaceMethod.ClrName,
                    ifaceMethod.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                    GetTypeFullName(ifaceMethod.ReturnType));

                if (synthesizedMethodSigs.Contains(methodSig))
                {
                    ctx.Log("StructuralConformance", $"Skipping duplicate synthesis of {ifaceMethod.ClrName} (already synthesized for another interface)");
                    continue;
                }

                // Synthesize ViewOnly method
                var viewOnlyMethod = SynthesizeViewOnlyMethod(ctx, type, ifaceMethod, declaringIface);
                viewOnlyMethods.Add(viewOnlyMethod);
                synthesizedMethodSigs.Add(methodSig);
                interfacesNeedingViews.Add(GetTypeFullName(ifaceRef));
                missingMembers++;
            }

            // Check each interface property
            foreach (var (ifaceProperty, declaringIface) in interfaceSurface.Properties)
            {
                // Skip indexer properties - they're intentionally omitted and handled by IndexerPlanner
                if (ifaceProperty.IsIndexer)
                    continue;

                // Check if already on class surface
                if (classSurface.HasProperty(ifaceProperty))
                    continue;

                // Check if already synthesized for a different interface
                var indexParams = ifaceProperty.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();
                var propertySig = ctx.CanonicalizeProperty(
                    ifaceProperty.ClrName,
                    indexParams,
                    GetTypeFullName(ifaceProperty.PropertyType));

                if (synthesizedPropertySigs.Contains(propertySig))
                {
                    ctx.Log("StructuralConformance", $"Skipping duplicate synthesis of {ifaceProperty.ClrName} (already synthesized for another interface)");
                    continue;
                }

                // Synthesize ViewOnly property
                var viewOnlyProperty = SynthesizeViewOnlyProperty(ctx, type, ifaceProperty, declaringIface);
                viewOnlyProperties.Add(viewOnlyProperty);
                synthesizedPropertySigs.Add(propertySig);
                interfacesNeedingViews.Add(GetTypeFullName(ifaceRef));
                missingMembers++;
            }

            if (missingMembers > 0)
            {
                ctx.Log("StructuralConformance", $"Interface {iface.ClrFullName} needs {missingMembers} ViewOnly members on {type.ClrFullName}");
            }
        }

        if (viewOnlyMethods.Count == 0 && viewOnlyProperties.Count == 0)
            return (type, 0);

        // Add ViewOnly members to type immutably
        var updatedType = type
            .WithAddedMethods(viewOnlyMethods)
            .WithAddedProperties(viewOnlyProperties);

        ctx.Log("StructuralConformance", $"Added {viewOnlyMethods.Count} ViewOnly methods, {viewOnlyProperties.Count} ViewOnly properties to {type.ClrFullName}");

        return (updatedType, viewOnlyMethods.Count + viewOnlyProperties.Count);
    }

    private static ClassSurface BuildClassSurface(BuildContext ctx, TypeSymbol type)
    {
        // Build representable surface: members that can appear on class surface in TypeScript
        // Exclude ViewOnly members (we're checking if class surface satisfies interface)
        var methods = type.Members.Methods
            .Where(m => m.EmitScope != EmitScope.ViewOnly && IsRepresentable(m))
            .ToList();

        var properties = type.Members.Properties
            .Where(p => p.EmitScope != EmitScope.ViewOnly && IsRepresentable(p))
            .ToList();

        return new ClassSurface(methods, properties, ctx);
    }

    private static InterfaceSurface BuildInterfaceSurface(
        BuildContext ctx,
        SymbolGraph graph,
        TypeReference closedIfaceRef,
        TypeSymbol ifaceSymbol)
    {
        // Build flattened interface surface with type arguments substituted
        // Returns (member after substitution, declaring interface)

        var methods = new List<(MethodSymbol Method, TypeReference DeclaringIface)>();
        var properties = new List<(PropertySymbol Property, TypeReference DeclaringIface)>();

        // Get all members from interface (already flattened by InterfaceInliner)
        foreach (var method in ifaceSymbol.Members.Methods)
        {
            // Find declaring interface for this member
            var memberSig = ctx.CanonicalizeMethod(
                method.ClrName,
                method.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                GetTypeFullName(method.ReturnType));

            var declaringIface = InterfaceResolver.FindDeclaringInterface(
                closedIfaceRef,
                memberSig,
                isMethod: true,
                ctx);

            // Substitute type parameters if this is a closed generic interface
            var substitutedMethod = SubstituteMethodTypeParameters(method, closedIfaceRef);

            methods.Add((substitutedMethod, declaringIface ?? closedIfaceRef));
        }

        foreach (var property in ifaceSymbol.Members.Properties)
        {
            // Skip indexer properties - they should not be synthesized as interface members
            if (property.IndexParameters.Length > 0)
                continue;

            var indexParams = property.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();
            var memberSig = ctx.CanonicalizeProperty(
                property.ClrName,
                indexParams,
                GetTypeFullName(property.PropertyType));

            var declaringIface = InterfaceResolver.FindDeclaringInterface(
                closedIfaceRef,
                memberSig,
                isMethod: false,
                ctx);

            var substitutedProperty = SubstitutePropertyTypeParameters(property, closedIfaceRef);

            properties.Add((substitutedProperty, declaringIface ?? closedIfaceRef));
        }

        return new InterfaceSurface(methods, properties);
    }

    private static MethodSymbol SubstituteMethodTypeParameters(MethodSymbol method, TypeReference closedIfaceRef)
    {
        // TODO: Implement proper type parameter substitution
        // For now, return method as-is (will work for non-generic or already-substituted cases)
        // Full implementation would use InterfaceMemberSubstitution.SubstituteTypeReference
        return method;
    }

    private static PropertySymbol SubstitutePropertyTypeParameters(PropertySymbol property, TypeReference closedIfaceRef)
    {
        // TODO: Implement proper type parameter substitution
        // For now, return property as-is
        return property;
    }

    private static MethodSymbol SynthesizeViewOnlyMethod(
        BuildContext ctx,
        TypeSymbol type,
        MethodSymbol ifaceMethod,
        TypeReference declaringInterface)
    {
        // Create StableId for this synthesized member
        var stableId = new MemberStableId
        {
            AssemblyName = type.StableId.AssemblyName,
            DeclaringClrFullName = type.ClrFullName,
            MemberName = ifaceMethod.ClrName,
            CanonicalSignature = ctx.CanonicalizeMethod(
                ifaceMethod.ClrName,
                ifaceMethod.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                GetTypeFullName(ifaceMethod.ReturnType))
        };

        return new MethodSymbol
        {
            StableId = stableId,
            ClrName = ifaceMethod.ClrName,
            ReturnType = ifaceMethod.ReturnType,
            Parameters = ifaceMethod.Parameters,
            GenericParameters = ifaceMethod.GenericParameters,
            IsStatic = false,
            IsAbstract = false,
            IsVirtual = true,
            IsOverride = false,
            IsSealed = false,
            Visibility = Visibility.Public,
            Provenance = MemberProvenance.ExplicitView,
            EmitScope = EmitScope.ViewOnly,
            SourceInterface = declaringInterface
        };
    }

    private static PropertySymbol SynthesizeViewOnlyProperty(
        BuildContext ctx,
        TypeSymbol type,
        PropertySymbol ifaceProperty,
        TypeReference declaringInterface)
    {
        var indexParams = ifaceProperty.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();

        var stableId = new MemberStableId
        {
            AssemblyName = type.StableId.AssemblyName,
            DeclaringClrFullName = type.ClrFullName,
            MemberName = ifaceProperty.ClrName,
            CanonicalSignature = ctx.CanonicalizeProperty(
                ifaceProperty.ClrName,
                indexParams,
                GetTypeFullName(ifaceProperty.PropertyType))
        };

        return new PropertySymbol
        {
            StableId = stableId,
            ClrName = ifaceProperty.ClrName,
            PropertyType = ifaceProperty.PropertyType,
            IndexParameters = ifaceProperty.IndexParameters,
            HasGetter = ifaceProperty.HasGetter,
            HasSetter = ifaceProperty.HasSetter,
            IsStatic = false,
            IsVirtual = true,
            IsOverride = false,
            Visibility = Visibility.Public,
            Provenance = MemberProvenance.ExplicitView,
            EmitScope = EmitScope.ViewOnly,
            SourceInterface = declaringInterface
        };
    }

    private static bool IsRepresentable(MethodSymbol method)
    {
        // Check if method can be represented in TypeScript
        // Exclude: byref parameters, pointer types, etc.
        // For now, accept everything (full implementation would check parameters/return)
        return true;
    }

    private static bool IsRepresentable(PropertySymbol property)
    {
        // Check if property can be represented in TypeScript
        return true;
    }

    private static TypeSymbol? FindInterface(SymbolGraph graph, TypeReference typeRef)
    {
        var fullName = GetTypeFullName(typeRef);

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }

    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            GenericParameterReference gp => gp.Name,
            ArrayTypeReference arr => $"{GetTypeFullName(arr.ElementType)}[]",
            PointerTypeReference ptr => $"{GetTypeFullName(ptr.PointeeType)}*",
            ByRefTypeReference byref => $"{GetTypeFullName(byref.ReferencedType)}&",
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private record ClassSurface(
        List<MethodSymbol> Methods,
        List<PropertySymbol> Properties,
        BuildContext Ctx)
    {
        public bool HasMethod(MethodSymbol method)
        {
            var sig = Ctx.CanonicalizeMethod(
                method.ClrName,
                method.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                GetTypeFullName(method.ReturnType));

            return Methods.Any(m =>
            {
                var mSig = Ctx.CanonicalizeMethod(
                    m.ClrName,
                    m.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                    GetTypeFullName(m.ReturnType));
                return mSig == sig;
            });
        }

        public bool HasProperty(PropertySymbol property)
        {
            var indexParams = property.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();
            var sig = Ctx.CanonicalizeProperty(
                property.ClrName,
                indexParams,
                GetTypeFullName(property.PropertyType));

            return Properties.Any(p =>
            {
                var pIndexParams = p.IndexParameters.Select(param => GetTypeFullName(param.Type)).ToList();
                var pSig = Ctx.CanonicalizeProperty(
                    p.ClrName,
                    pIndexParams,
                    GetTypeFullName(p.PropertyType));
                return pSig == sig;
            });
        }
    }

    private record InterfaceSurface(
        List<(MethodSymbol Method, TypeReference DeclaringIface)> Methods,
        List<(PropertySymbol Property, TypeReference DeclaringIface)> Properties);
}
