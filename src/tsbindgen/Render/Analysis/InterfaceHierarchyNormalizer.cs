using tsbindgen.Config;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Analysis;

/// <summary>
/// Resolves TS2430 errors caused by generic→non-generic interface inheritance.
///
/// Category A errors: Generic interface extends non-generic counterpart
/// Examples:
/// - IEnumerator_1&lt;T&gt; extends IEnumerator (return type T vs Object)
/// - IEnumerable_1&lt;T&gt; extends IEnumerable (return type T vs Object)
/// - IList_1&lt;T&gt; extends IList (property/indexer shape conflicts)
///
/// TypeScript cannot reconcile T vs Object in covariant positions.
/// This pass removes the extends relationship and optionally fans in parent members.
///
/// Strategy:
/// 1. Identify generic interfaces extending non-generic counterparts (same base name)
/// 2. Remove the problematic extends edge
/// 3. Optionally fan in parent members (if needed for completeness)
/// </summary>
public static class InterfaceHierarchyNormalizer
{
    /// <summary>
    /// Breaks generic→non-generic inheritance edges in interfaces and structs.
    /// Processes both interfaces and structs that implement interfaces.
    /// </summary>
    public static NamespaceModel Apply(
        NamespaceModel model,
        IReadOnlyDictionary<string, NamespaceModel> allModels,
        AnalysisContext ctx)
    {
        var updatedTypes = model.Types.Select(type =>
        {
            // Process interfaces and structs that implement interfaces
            if (type.Kind != TypeKind.Interface && type.Kind != TypeKind.Struct)
                return type;

            // Only process types that implement other interfaces
            if (type.Implements.Count == 0)
                return type;

            return BreakGenericNonGenericInheritance(type, ctx);
        }).ToList();

        return model with { Types = updatedTypes };
    }

    /// <summary>
    /// Removes extends edges where a generic interface extends its non-generic counterpart.
    /// </summary>
    private static TypeModel BreakGenericNonGenericInheritance(
        TypeModel type,
        AnalysisContext ctx)
    {
        // Only apply to generic interfaces
        if (type.GenericParameters.Count == 0)
            return type;

        var filteredImplements = new List<TypeReference>();
        var removedAny = false;

        foreach (var parent in type.Implements)
        {
            if (IsNonGenericCounterpart(type, parent))
            {
                removedAny = true;
            }
            else
            {
                filteredImplements.Add(parent);
            }
        }

        // No changes needed
        if (!removedAny)
            return type;

        return type with { Implements = filteredImplements };
    }

    /// <summary>
    /// Checks if parent is the non-generic counterpart of child.
    /// Examples:
    /// - IEnumerator_1 → IEnumerator (True, even across namespaces)
    /// - IEnumerable_1 → IEnumerable (True, even across namespaces)
    /// - IList_1 → IList (True)
    /// - IList_1 → ICollection (False - different base name)
    /// </summary>
    private static bool IsNonGenericCounterpart(TypeModel child, TypeReference parent)
    {
        // Parent must be non-generic
        if (parent.GenericArgs.Count > 0)
            return false;

        // Check if parent name matches child without generic arity suffix
        // Child: IEnumerator_1 → Base: IEnumerator
        // Child: IList_1 → IList
        // Note: We allow cross-namespace matches (IEnumerator_1 in System.Collections.Generic
        // extends IEnumerator in System.Collections)
        var childBaseName = RemoveGenericAritySuffix(child.ClrName);
        return parent.TypeName == childBaseName;
    }

    /// <summary>
    /// Removes generic arity suffix from a CLR type name.
    /// CLR uses backtick notation for generics.
    /// Examples:
    /// - IEnumerator`1 → IEnumerator
    /// - IDictionary`2 → IDictionary
    /// - Foo → Foo (no change)
    /// </summary>
    private static string RemoveGenericAritySuffix(string typeName)
    {
        var backtickIndex = typeName.LastIndexOf('`');
        if (backtickIndex == -1)
            return typeName;

        // Check if what follows is a number (generic arity)
        var suffix = typeName.Substring(backtickIndex + 1);
        if (int.TryParse(suffix, out _))
        {
            return typeName.Substring(0, backtickIndex);
        }

        return typeName;
    }
}
