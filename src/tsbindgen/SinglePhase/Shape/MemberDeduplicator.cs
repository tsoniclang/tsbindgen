using System.Collections.Immutable;
using tsbindgen.Core.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Final deduplication pass to remove any duplicate members that may have been
/// introduced by multiple Shape passes (BaseOverloadAdder, ExplicitImplSynthesizer, etc.).
/// Keeps the first occurrence of each unique StableId.
/// </summary>
public static class MemberDeduplicator
{
    /// <summary>
    /// Deduplicate all members in the graph by StableId.
    /// PURE - returns new graph with duplicates removed.
    /// </summary>
    public static SymbolGraph Deduplicate(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("MemberDeduplicator", "Deduplicating members by StableId...");

        var updatedNamespaces = ImmutableArray.CreateBuilder<NamespaceSymbol>();
        int totalDuplicatesRemoved = 0;

        foreach (var ns in graph.Namespaces)
        {
            var updatedTypes = ImmutableArray.CreateBuilder<TypeSymbol>();

            foreach (var type in ns.Types)
            {
                // Deduplicate methods
                var uniqueMethods = DeduplicateByStableId(type.Members.Methods, out var methodDupes);
                totalDuplicatesRemoved += methodDupes;

                // Deduplicate properties
                var uniqueProperties = DeduplicateByStableId(type.Members.Properties, out var propDupes);
                totalDuplicatesRemoved += propDupes;

                // Deduplicate fields
                var uniqueFields = DeduplicateByStableId(type.Members.Fields, out var fieldDupes);
                totalDuplicatesRemoved += fieldDupes;

                // Deduplicate events
                var uniqueEvents = DeduplicateByStableId(type.Members.Events, out var eventDupes);
                totalDuplicatesRemoved += eventDupes;

                // Deduplicate constructors
                var uniqueCtors = DeduplicateByStableId(type.Members.Constructors, out var ctorDupes);
                totalDuplicatesRemoved += ctorDupes;

                // If any duplicates were found, create new type with deduplicated members
                if (methodDupes > 0 || propDupes > 0 || fieldDupes > 0 || eventDupes > 0 || ctorDupes > 0)
                {
                    var newMembers = new TypeMembers
                    {
                        Methods = uniqueMethods,
                        Properties = uniqueProperties,
                        Fields = uniqueFields,
                        Events = uniqueEvents,
                        Constructors = uniqueCtors
                    };

                    var newType = type with { Members = newMembers };
                    updatedTypes.Add(newType);

                    if (methodDupes + propDupes + fieldDupes + eventDupes + ctorDupes > 0)
                    {
                        ctx.Log("MemberDeduplicator", $"Removed {methodDupes + propDupes + fieldDupes + eventDupes + ctorDupes} duplicates from {type.ClrFullName}");
                    }
                }
                else
                {
                    updatedTypes.Add(type);
                }
            }

            var newNs = ns with { Types = updatedTypes.ToImmutable() };
            updatedNamespaces.Add(newNs);
        }

        ctx.Log("MemberDeduplicator", $"Total duplicates removed: {totalDuplicatesRemoved}");

        return new SymbolGraph
        {
            Namespaces = updatedNamespaces.ToImmutable(),
            SourceAssemblies = graph.SourceAssemblies
        };
    }

    /// <summary>
    /// Deduplicate a collection of members by StableId, keeping the first occurrence.
    /// </summary>
    private static ImmutableArray<T> DeduplicateByStableId<T>(
        ImmutableArray<T> members,
        out int duplicatesRemoved) where T : class
    {
        // Extract StableId using reflection (all member symbols have a StableId property)
        var stableIdProperty = typeof(T).GetProperty("StableId");
        if (stableIdProperty == null)
        {
            duplicatesRemoved = 0;
            return members;
        }

        var seen = new HashSet<StableId>();
        var unique = ImmutableArray.CreateBuilder<T>();

        foreach (var member in members)
        {
            var stableId = (StableId)stableIdProperty.GetValue(member)!;

            if (!seen.Contains(stableId))
            {
                seen.Add(stableId);
                unique.Add(member);
            }
        }

        duplicatesRemoved = members.Length - unique.Count;
        return unique.ToImmutable();
    }
}
