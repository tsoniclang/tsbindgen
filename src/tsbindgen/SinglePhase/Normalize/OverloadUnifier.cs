using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Normalize;

/// <summary>
/// Unifies method overloads that differ only in ways TypeScript can't distinguish.
/// Runs after Plan phase, before PhaseGate.
///
/// Problem: C# allows overloads differing by ref/out modifiers or generic constraints.
/// TypeScript doesn't support overload disambiguation by these features.
///
/// Solution: Group methods by TypeScript erasure key (name, arity, param-count),
/// pick the "widest" signature, mark narrower ones as EmitScope.Omitted.
/// </summary>
public static class OverloadUnifier
{
    /// <summary>
    /// Unify overloads in the symbol graph.
    /// PURE - returns new graph with unified overloads.
    /// </summary>
    public static SymbolGraph UnifyOverloads(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("OverloadUnifier", "Unifying method overloads...");

        int totalUnified = 0;
        int typesProcessed = 0;

        var updatedNamespaces = graph.Namespaces.Select(ns =>
        {
            var updatedTypes = ns.Types.Select(type =>
            {
                var (updatedType, unifiedCount) = UnifyTypeOverloads(type);
                totalUnified += unifiedCount;
                if (unifiedCount > 0)
                {
                    typesProcessed++;
                }
                return updatedType;
            }).ToImmutableArray();

            return ns with { Types = updatedTypes };
        }).ToImmutableArray();

        ctx.Log("OverloadUnifier", $"Unified {totalUnified} overloads across {typesProcessed} types");

        return graph with { Namespaces = updatedNamespaces };
    }

    /// <summary>
    /// Unify overloads within a single type.
    /// Returns updated type and count of unified methods.
    /// </summary>
    private static (TypeSymbol updatedType, int unifiedCount) UnifyTypeOverloads(TypeSymbol type)
    {
        // Group methods by TypeScript erasure key
        var methodGroups = type.Members.Methods
            .Where(m => m.EmitScope == EmitScope.ClassSurface || m.EmitScope == EmitScope.StaticSurface)
            .GroupBy(m => ComputeErasureKey(m))
            .Where(g => g.Count() > 1) // Only process groups with collisions
            .ToList();

        if (methodGroups.Count == 0)
        {
            return (type, 0); // No unification needed
        }

        int unifiedCount = 0;
        var updatedMethods = type.Members.Methods.ToList();

        foreach (var group in methodGroups)
        {
            // Find the widest signature in the group
            var widestMethod = SelectWidestSignature(group.ToList());

            // Mark narrower signatures as Omitted
            foreach (var method in group)
            {
                if (method.StableId != widestMethod.StableId)
                {
                    var index = updatedMethods.FindIndex(m => m.StableId == method.StableId);
                    if (index >= 0)
                    {
                        updatedMethods[index] = method with { EmitScope = EmitScope.Omitted };
                        unifiedCount++;
                    }
                }
            }
        }

        var updatedMembers = type.Members with
        {
            Methods = updatedMethods.ToImmutableArray()
        };

        return (type with { Members = updatedMembers }, unifiedCount);
    }

    /// <summary>
    /// Compute TypeScript erasure key for a method.
    /// Methods with the same erasure key cannot be distinguished in TypeScript.
    /// Key format: "name|arity|paramCount"
    /// </summary>
    private static string ComputeErasureKey(MethodSymbol method)
    {
        var name = method.TsEmitName;
        var arity = method.Arity; // Generic parameter count
        var paramCount = method.Parameters.Length;

        return $"{name}|{arity}|{paramCount}";
    }

    /// <summary>
    /// Select the widest signature from a group of overloads.
    /// Widest = most permissive in TypeScript context.
    ///
    /// Preference order:
    /// 1. Fewer ref/out parameters (TypeScript doesn't support ref/out)
    /// 2. Fewer generic constraints (TypeScript has weaker constraint system)
    /// 3. First in declaration order (stable tie-breaker)
    /// </summary>
    private static MethodSymbol SelectWidestSignature(List<MethodSymbol> overloads)
    {
        if (overloads.Count == 1)
        {
            return overloads[0];
        }

        // Score each method (lower score = wider signature)
        var scored = overloads.Select(m => new
        {
            Method = m,
            RefOutCount = CountRefOutParameters(m),
            ConstraintCount = CountGenericConstraints(m)
        }).ToList();

        // Sort by: fewer ref/out, fewer constraints, stable order
        var widest = scored
            .OrderBy(s => s.RefOutCount)
            .ThenBy(s => s.ConstraintCount)
            .ThenBy(s => s.Method.StableId.ToString()) // Stable tie-breaker
            .First();

        return widest.Method;
    }

    /// <summary>
    /// Count ref and out parameters in a method.
    /// TypeScript doesn't support these, so prefer methods without them.
    /// </summary>
    private static int CountRefOutParameters(MethodSymbol method)
    {
        return method.Parameters.Count(p => p.IsRef || p.IsOut);
    }

    /// <summary>
    /// Count generic constraints on method type parameters.
    /// TypeScript has weaker constraint system, so prefer fewer constraints.
    /// </summary>
    private static int CountGenericConstraints(MethodSymbol method)
    {
        return method.GenericParameters.Sum(gp => gp.Constraints.Length);
    }
}
