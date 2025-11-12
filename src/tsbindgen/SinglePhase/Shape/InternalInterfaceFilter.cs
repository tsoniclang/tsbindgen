using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// FIX F: Filters internal interfaces from type interface lists.
/// Internal interfaces are BCL implementation details that aren't publicly accessible
/// but appear in reflection metadata.
/// </summary>
public static class InternalInterfaceFilter
{
    /// <summary>
    /// Known internal interface patterns from BCL.
    /// These are interfaces that exist in metadata but aren't meant for public consumption.
    /// </summary>
    private static readonly HashSet<string> InternalPatterns = new(StringComparer.Ordinal)
    {
        "Internal",              // IValueTupleInternal, ITupleInternal, IImmutableDictionaryInternal_2, IInternalStringEqualityComparer
        "Debugger",              // IDebuggerDisplay
        "ParseAndFormatInfo",    // IBinaryIntegerParseAndFormatInfo_1, IBinaryFloatParseAndFormatInfo_1
        "Runtime",               // IRuntimeAlgorithm
        "StateMachineBox",       // IStateMachineBoxAwareAwaiter
        "SecurePooled",          // ISecurePooledObjectUser
        "BuiltInJson",           // IBuiltInJsonTypeInfoResolver
        "DeferredDisposable",    // IDeferredDisposable
    };

    /// <summary>
    /// Explicit list of internal interfaces that don't match simple patterns.
    /// Use FullName (CLR form with backtick and namespace).
    /// </summary>
    private static readonly HashSet<string> ExplicitInternalInterfaces = new(StringComparer.Ordinal)
    {
        "System.Runtime.Intrinsics.ISimdVector`2",         // ISimdVector_2<TSelf, T>
        "System.IUtfChar`1",                               // IUtfChar_1<TSelf>
        "System.Collections.Immutable.IStrongEnumerator`1", // IStrongEnumerator_1<T>
        "System.Collections.Immutable.IStrongEnumerable`2", // IStrongEnumerable_2<TKey, TValue>
        "System.Runtime.CompilerServices.ITaskAwaiter",     // ITaskAwaiter
        "System.Collections.Immutable.IImmutableArray",     // IImmutableArray
    };

    /// <summary>
    /// Filter internal interfaces from a type's interface list.
    /// Returns a new TypeSymbol with internal interfaces removed.
    /// </summary>
    public static TypeSymbol FilterInterfaces(BuildContext ctx, TypeSymbol type)
    {
        if (type.Interfaces.Length == 0)
            return type;

        var filtered = new List<TypeReference>();
        var removedCount = 0;

        foreach (var iface in type.Interfaces)
        {
            if (IsInternalInterface(iface))
            {
                removedCount++;
                ctx.Log("InternalFilter", $"Removed internal interface '{GetInterfaceName(iface)}' from '{type.ClrFullName}'");
            }
            else
            {
                filtered.Add(iface);
            }
        }

        if (removedCount == 0)
            return type;

        return type with { Interfaces = filtered.ToImmutableArray() };
    }

    /// <summary>
    /// Filter internal interfaces from all types in a graph.
    /// Returns a new SymbolGraph with internal interfaces removed.
    /// </summary>
    public static SymbolGraph FilterGraph(BuildContext ctx, SymbolGraph graph)
    {
        var filteredNamespaces = new List<NamespaceSymbol>();
        var totalRemoved = 0;

        foreach (var ns in graph.Namespaces)
        {
            var filteredTypes = new List<TypeSymbol>();

            foreach (var type in ns.Types)
            {
                var beforeCount = type.Interfaces.Length;
                var filtered = FilterInterfaces(ctx, type);
                var afterCount = filtered.Interfaces.Length;
                totalRemoved += (beforeCount - afterCount);

                filteredTypes.Add(filtered);
            }

            filteredNamespaces.Add(ns with { Types = filteredTypes.ToImmutableArray() });
        }

        ctx.Log("InternalFilter", $"Removed {totalRemoved} internal interfaces across {graph.Namespaces.Length} namespaces");

        return graph with { Namespaces = filteredNamespaces.ToImmutableArray() };
    }

    /// <summary>
    /// Check if a type reference represents an internal interface.
    /// </summary>
    private static bool IsInternalInterface(TypeReference typeRef)
    {
        var fullName = GetFullName(typeRef);
        if (string.IsNullOrEmpty(fullName))
            return false;

        // Check explicit list first (uses FullName with namespace)
        if (ExplicitInternalInterfaces.Contains(fullName))
            return true;

        var name = GetInterfaceName(typeRef);
        if (string.IsNullOrEmpty(name))
            return false;

        // Check patterns (ONLY on simple name, NOT full name to avoid false positives)
        foreach (var pattern in InternalPatterns)
        {
            if (name.Contains(pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Get the display name from a type reference (for logging).
    /// </summary>
    private static string GetInterfaceName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.Name,
            NestedTypeReference nested => nested.FullReference.Name,
            GenericParameterReference => "",
            ArrayTypeReference => "",
            PointerTypeReference => "",
            ByRefTypeReference => "",
            _ => ""
        };
    }

    /// <summary>
    /// Get the full CLR name (with namespace and backtick arity) from a type reference.
    /// </summary>
    private static string GetFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => ""
        };
    }
}
