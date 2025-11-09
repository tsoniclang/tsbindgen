using System.Collections.Generic;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Resolves return-type conflicts in overloads.
/// TypeScript doesn't support method overloads that differ only in return type.
/// This component detects such conflicts and marks non-representative overloads as ViewOnly.
/// </summary>
public static class OverloadReturnConflictResolver
{
    public static void Resolve(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("OverloadReturnConflictResolver", "Resolving return-type conflicts...");

        var allTypes = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .ToList();

        int totalResolved = 0;

        foreach (var type in allTypes)
        {
            var resolved = ResolveForType(ctx, type);
            totalResolved += resolved;
        }

        ctx.Log("OverloadReturnConflictResolver", $"Resolved {totalResolved} return-type conflicts");
    }

    private static int ResolveForType(BuildContext ctx, TypeSymbol type)
    {
        // Group methods by signature excluding return type, sorted for deterministic iteration
        var methodGroups = type.Members.Methods
            .GroupBy(m => GetSignatureWithoutReturn(ctx, m))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key)
            .ToList();

        int resolved = 0;

        foreach (var group in methodGroups)
        {
            var methods = group.ToList();

            // Check if they have different return types
            var returnTypes = methods.Select(m => GetTypeFullName(m.ReturnType)).Distinct().ToList();

            if (returnTypes.Count <= 1)
                continue; // Same return type, no conflict

            // Return-type conflict detected
            ctx.Log("OverloadReturnConflictResolver", $"Return-type conflict in {type.ClrFullName}.{methods[0].ClrName} - {returnTypes.Count} return types");

            // Select a representative method to keep on the class surface
            // Prefer non-void, prefer immutable returns (no ref/out parameters)
            var representative = SelectRepresentative(methods);

            // Mark others as ViewOnly
            foreach (var method in methods)
            {
                if (method != representative)
                {
                    MarkAsViewOnly(method);
                }
            }

            resolved++;
        }

        // Do the same for properties (indexers can have return-type conflicts), sorted for deterministic iteration
        var propertyGroups = type.Members.Properties
            .Where(p => p.IsIndexer)
            .GroupBy(p => GetPropertySignatureWithoutReturn(ctx, p))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in propertyGroups)
        {
            var properties = group.ToList();

            var propertyTypes = properties.Select(p => GetTypeFullName(p.PropertyType)).Distinct().ToList();

            if (propertyTypes.Count <= 1)
                continue;

            ctx.Log("OverloadReturnConflictResolver", $"Property type conflict in {type.ClrFullName} indexers - {propertyTypes.Count} types");

            var representative = properties.First(); // Simple: keep first

            foreach (var property in properties)
            {
                if (property != representative)
                {
                    MarkPropertyAsViewOnly(property);
                }
            }

            resolved++;
        }

        return resolved;
    }

    private static string GetSignatureWithoutReturn(BuildContext ctx, MethodSymbol method)
    {
        // Signature: "MethodName(param1Type,param2Type,...)"
        // Exclude return type and accessor kind
        var paramTypes = method.Parameters.Select(p => GetTypeFullName(p.Type)).ToList();
        return $"{method.ClrName}({string.Join(",", paramTypes)})";
    }

    private static string GetPropertySignatureWithoutReturn(BuildContext ctx, PropertySymbol property)
    {
        // Signature: "this[param1Type,param2Type,...]|accessor=get/set/both/none"
        // Accessor kind is important - getters and setters should not conflict with each other
        var paramTypes = property.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();

        var accessor = (property.HasGetter, property.HasSetter) switch
        {
            (true, true) => "both",
            (true, false) => "get",
            (false, true) => "set",
            _ => "none"
        };

        return $"this[{string.Join(",", paramTypes)}]|accessor={accessor}";
    }

    private static MethodSymbol SelectRepresentative(List<MethodSymbol> methods)
    {
        // Selection criteria (in order of preference):
        // 1. Prefer non-void returns (more informative)
        // 2. Prefer no ref/out parameters (immutable)
        // 3. Prefer first in list (deterministic)

        var nonVoid = methods.Where(m => GetTypeFullName(m.ReturnType) != "System.Void").ToList();

        if (nonVoid.Count > 0)
        {
            // Prefer methods without ref/out parameters
            var immutable = nonVoid.Where(m => !m.Parameters.Any(p => p.IsRef || p.IsOut)).ToList();

            if (immutable.Count > 0)
                return immutable.First();

            return nonVoid.First();
        }

        // All void - just pick first
        return methods.First();
    }

    private static void MarkAsViewOnly(MethodSymbol method)
    {
        var emitScopeProperty = typeof(MethodSymbol).GetProperty(nameof(MethodSymbol.EmitScope));
        emitScopeProperty!.SetValue(method, EmitScope.ViewOnly);
    }

    private static void MarkPropertyAsViewOnly(PropertySymbol property)
    {
        var emitScopeProperty = typeof(PropertySymbol).GetProperty(nameof(PropertySymbol.EmitScope));
        emitScopeProperty!.SetValue(property, EmitScope.ViewOnly);
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
}
