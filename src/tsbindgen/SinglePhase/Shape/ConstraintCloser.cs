using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Closes generic constraints for TypeScript.
/// Computes final constraint sets by combining base constraints with any additional requirements.
/// Handles constraint merging strategies (Intersection, Union, etc.) according to policy.
/// </summary>
public static class ConstraintCloser
{
    public static SymbolGraph Close(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("ConstraintCloser", "Closing generic constraints...");

        // Step 1: Resolve raw constraint types into TypeReferences
        var updatedGraph = ResolveAllConstraints(ctx, graph);

        var allTypes = updatedGraph.Namespaces
            .SelectMany(ns => ns.Types)
            .ToList();

        int totalClosed = 0;

        foreach (var type in allTypes)
        {
            // Close type-level generic parameters
            if (type.GenericParameters.Length > 0)
            {
                foreach (var gp in type.GenericParameters)
                {
                    CloseConstraints(ctx, gp);
                    totalClosed++;
                }
            }

            // Close method-level generic parameters
            foreach (var method in type.Members.Methods)
            {
                if (method.GenericParameters.Length > 0)
                {
                    foreach (var gp in method.GenericParameters)
                    {
                        CloseConstraints(ctx, gp);
                        totalClosed++;
                    }
                }
            }
        }

        ctx.Log("ConstraintCloser", $"Closed {totalClosed} generic parameter constraints");
        return updatedGraph;
    }

    /// <summary>
    /// Resolve raw System.Type constraints into TypeReferences.
    /// Uses the memoized TypeReferenceFactory with cycle detection.
    /// PURE - returns new SymbolGraph.
    /// </summary>
    private static SymbolGraph ResolveAllConstraints(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("ConstraintCloser", "Resolving constraint types...");

        // Create TypeReferenceFactory for constraint resolution
        var typeFactory = new Load.TypeReferenceFactory(ctx);
        int totalResolved = 0;
        var updatedGraph = graph;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                bool typeNeedsUpdate = false;
                ImmutableArray<GenericParameterSymbol> updatedTypeGenericParams = type.GenericParameters;
                ImmutableArray<MethodSymbol> updatedMethods = type.Members.Methods;

                // Resolve type-level generic parameter constraints
                if (type.GenericParameters.Length > 0)
                {
                    var typeGenericParamsBuilder = ImmutableArray.CreateBuilder<GenericParameterSymbol>();

                    foreach (var gp in type.GenericParameters)
                    {
                        if (gp.RawConstraintTypes != null && gp.RawConstraintTypes.Length > 0)
                        {
                            var constraintsBuilder = ImmutableArray.CreateBuilder<TypeReference>();

                            foreach (var rawType in gp.RawConstraintTypes)
                            {
                                // Uses memoized factory with cycle detection
                                var resolved = typeFactory.Create(rawType);
                                constraintsBuilder.Add(resolved);
                                totalResolved++;
                            }

                            // Create updated GenericParameterSymbol with resolved constraints
                            var updatedGp = gp with { Constraints = constraintsBuilder.ToImmutable() };
                            typeGenericParamsBuilder.Add(updatedGp);
                            typeNeedsUpdate = true;
                        }
                        else
                        {
                            typeGenericParamsBuilder.Add(gp);
                        }
                    }

                    updatedTypeGenericParams = typeGenericParamsBuilder.ToImmutable();
                }

                // Resolve method-level generic parameter constraints
                if (type.Members.Methods.Length > 0)
                {
                    var methodsBuilder = ImmutableArray.CreateBuilder<MethodSymbol>();

                    foreach (var method in type.Members.Methods)
                    {
                        if (method.GenericParameters.Length > 0)
                        {
                            var methodGenericParamsBuilder = ImmutableArray.CreateBuilder<GenericParameterSymbol>();
                            bool methodNeedsUpdate = false;

                            foreach (var gp in method.GenericParameters)
                            {
                                if (gp.RawConstraintTypes != null && gp.RawConstraintTypes.Length > 0)
                                {
                                    var constraintsBuilder = ImmutableArray.CreateBuilder<TypeReference>();

                                    foreach (var rawType in gp.RawConstraintTypes)
                                    {
                                        var resolved = typeFactory.Create(rawType);
                                        constraintsBuilder.Add(resolved);
                                        totalResolved++;
                                    }

                                    // Create updated GenericParameterSymbol with resolved constraints
                                    var updatedGp = gp with { Constraints = constraintsBuilder.ToImmutable() };
                                    methodGenericParamsBuilder.Add(updatedGp);
                                    methodNeedsUpdate = true;
                                }
                                else
                                {
                                    methodGenericParamsBuilder.Add(gp);
                                }
                            }

                            if (methodNeedsUpdate)
                            {
                                var updatedMethod = method with { GenericParameters = methodGenericParamsBuilder.ToImmutable() };
                                methodsBuilder.Add(updatedMethod);
                                typeNeedsUpdate = true;
                            }
                            else
                            {
                                methodsBuilder.Add(method);
                            }
                        }
                        else
                        {
                            methodsBuilder.Add(method);
                        }
                    }

                    updatedMethods = methodsBuilder.ToImmutable();
                }

                // Update the type if any changes were made
                if (typeNeedsUpdate)
                {
                    updatedGraph = updatedGraph.WithUpdatedType(type.StableId.ToString(), t => t with
                    {
                        GenericParameters = updatedTypeGenericParams,
                        Members = t.Members with
                        {
                            Methods = updatedMethods
                        }
                    });
                }
            }
        }

        ctx.Log("ConstraintCloser", $"Resolved {totalResolved} constraint types");
        return updatedGraph;
    }

    private static void CloseConstraints(BuildContext ctx, GenericParameterSymbol gp)
    {
        // In C#, generic constraints can be:
        // 1. Type constraints (interfaces, classes)
        // 2. Special constraints (struct, class, new())
        //
        // For TypeScript:
        // - Type constraints map directly using intersection types if multiple
        // - Special constraints are documented in metadata but don't affect TS signature

        if (gp.Constraints.Length == 0)
            return; // No constraints to close

        // Policy determines how to merge multiple constraints
        var strategy = ctx.Policy.Constraints.MergeStrategy;

        switch (strategy)
        {
            case Core.Policy.ConstraintMergeStrategy.Intersection:
                // TypeScript uses intersection automatically with "T extends A & B & C"
                // No additional work needed - the printer will handle this
                ctx.Log("ConstraintCloser", $"{gp.Name} has {gp.Constraints.Length} constraints (intersection)");
                break;

            case Core.Policy.ConstraintMergeStrategy.Union:
                // Would need to change constraints to a union, but TypeScript doesn't support this syntax
                ctx.Diagnostics.Warning(
                    Core.Diagnostics.DiagnosticCodes.UnsupportedConstraintMerge,
                    $"Union constraint merge not supported in TypeScript for {gp.Name}");
                break;

            case Core.Policy.ConstraintMergeStrategy.PreferLeft:
                // Keep only the first constraint
                ctx.Log("ConstraintCloser", $"{gp.Name} using first constraint only (PreferLeft)");
                // Would need to mutate the GenericParameterSymbol to keep only first constraint
                // Since constraints are IReadOnlyList, we'd need reflection here
                // For now, document the strategy
                break;
        }

        // Check for constraint compatibility
        ValidateConstraints(ctx, gp);
    }

    private static void ValidateConstraints(BuildContext ctx, GenericParameterSymbol gp)
    {
        // Check for incompatible constraints
        // For example: both "struct" and "class" special constraints

        if ((gp.SpecialConstraints & GenericParameterConstraints.ValueType) != 0 &&
            (gp.SpecialConstraints & GenericParameterConstraints.ReferenceType) != 0)
        {
            ctx.Diagnostics.Warning(
                Core.Diagnostics.DiagnosticCodes.IncompatibleConstraints,
                $"Generic parameter {gp.Name} has both 'struct' and 'class' constraints");
        }

        // Check for circular constraints (T : U, U : T)
        // This would require building a constraint graph - complex analysis
        // For now, rely on C# compiler to catch these

        // Validate that constraints are representable in TypeScript
        foreach (var constraint in gp.Constraints)
        {
            if (!IsTypeScriptRepresentable(constraint))
            {
                ctx.Diagnostics.Warning(
                    Core.Diagnostics.DiagnosticCodes.UnrepresentableConstraint,
                    $"Constraint on {gp.Name} uses type {GetTypeFullName(constraint)} which may not be representable in TypeScript");
            }
        }
    }

    private static bool IsTypeScriptRepresentable(TypeReference typeRef)
    {
        // Most type references are representable
        // Exceptions:
        // - Pointer types (mapped to underlying type, loses semantics)
        // - ByRef types (mapped to underlying type)

        return typeRef switch
        {
            PointerTypeReference => false,
            ByRefTypeReference => false,
            _ => true
        };
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
            PlaceholderTypeReference placeholder => placeholder.DebugName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }
}
