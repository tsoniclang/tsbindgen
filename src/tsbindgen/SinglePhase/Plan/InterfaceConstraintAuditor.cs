using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Core.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// Audits constructor constraint loss per (Type, Interface) pair.
/// Prevents duplicate PG_CT_001 diagnostics for view members.
///
/// M4/M5 Fix: Constructor-constraint loss is assessed ONCE per implemented interface,
/// not per cloned view member.
/// </summary>
public static class InterfaceConstraintAuditor
{
    /// <summary>
    /// Audit all (Type, Interface) pairs and return findings.
    /// </summary>
    public static InterfaceConstraintFindings Audit(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("InterfaceConstraintAuditor", "Auditing constructor constraints per (Type, Interface) pair...");

        var findings = ImmutableArray.CreateBuilder<InterfaceConstraintFinding>();
        int pairsChecked = 0;
        int findingsCreated = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Only check types that implement interfaces
                if (type.Interfaces.Length == 0)
                    continue;

                foreach (var ifaceRef in type.Interfaces)
                {
                    pairsChecked++;

                    // Resolve interface type in graph
                    var iface = ResolveInterface(graph, ifaceRef);
                    if (iface == null)
                        continue;

                    // Check if interface has generic parameters with constructor constraints
                    var finding = CheckInterfaceConstraints(ctx, graph, type, iface, ifaceRef);
                    if (finding != null)
                    {
                        findings.Add(finding);
                        findingsCreated++;
                    }
                }
            }
        }

        ctx.Log("InterfaceConstraintAuditor", $"Checked {pairsChecked} (Type, Interface) pairs, created {findingsCreated} findings");

        return new InterfaceConstraintFindings
        {
            Findings = findings.ToImmutable()
        };
    }

    /// <summary>
    /// Check if an interface implementation has constructor constraint loss.
    /// Returns a finding if `new()` constraint is present on interface generic params.
    /// </summary>
    private static InterfaceConstraintFinding? CheckInterfaceConstraints(
        BuildContext ctx,
        SymbolGraph graph,
        TypeSymbol implementingType,
        TypeSymbol interfaceType,
        TypeReference interfaceReference)
    {
        // Only check if interface has generic parameters
        if (interfaceType.GenericParameters.Length == 0)
            return null;

        // Check each generic parameter for constructor constraint
        foreach (var gp in interfaceType.GenericParameters)
        {
            // Check if has constructor constraint (new())
            if ((gp.SpecialConstraints & GenericParameterConstraints.DefaultConstructor) != 0)
            {
                // Constructor constraint loss detected
                return new InterfaceConstraintFinding
                {
                    ImplementingTypeStableId = implementingType.StableId,
                    InterfaceStableId = interfaceType.StableId,
                    LossKind = ConstraintLossKind.ConstructorConstraintLoss,
                    GenericParameterName = gp.Name,
                    TypeFullName = implementingType.ClrFullName,
                    InterfaceFullName = interfaceType.ClrFullName
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve interface TypeSymbol from TypeReference.
    /// </summary>
    private static TypeSymbol? ResolveInterface(SymbolGraph graph, TypeReference ifaceRef)
    {
        var fullName = GetTypeReferenceName(ifaceRef);

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }

    /// <summary>
    /// Get full type name from TypeReference.
    /// </summary>
    private static string GetTypeReferenceName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }
}

/// <summary>
/// Collection of interface constraint findings.
/// </summary>
public sealed record InterfaceConstraintFindings
{
    public required ImmutableArray<InterfaceConstraintFinding> Findings { get; init; }
}

/// <summary>
/// Single finding for a (Type, Interface) pair with constructor constraint loss.
/// </summary>
public sealed record InterfaceConstraintFinding
{
    public required StableId ImplementingTypeStableId { get; init; }
    public required StableId InterfaceStableId { get; init; }
    public required ConstraintLossKind LossKind { get; init; }
    public required string GenericParameterName { get; init; }
    public required string TypeFullName { get; init; }
    public required string InterfaceFullName { get; init; }
}

/// <summary>
/// Kind of constraint loss.
/// </summary>
public enum ConstraintLossKind
{
    None,
    ConstructorConstraintLoss
}
