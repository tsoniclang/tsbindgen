using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Plan.Validation;

/// <summary>
/// Shared validation functions.
/// </summary>
internal static class Shared
{
    internal static bool IsTypeScriptReservedWord(string name)
    {
        var reservedWords = new HashSet<string>
        {
            "break", "case", "catch", "class", "const", "continue", "debugger", "default",
            "delete", "do", "else", "enum", "export", "extends", "false", "finally",
            "for", "function", "if", "import", "in", "instanceof", "new", "null",
            "return", "super", "switch", "this", "throw", "true", "try", "typeof",
            "var", "void", "while", "with", "as", "implements", "interface", "let",
            "package", "private", "protected", "public", "static", "yield", "any",
            "boolean", "number", "string", "symbol", "abstract", "async", "await",
            "constructor", "declare", "from", "get", "is", "module", "namespace",
            "of", "readonly", "require", "set", "type"
        };

        return reservedWords.Contains(name.ToLowerInvariant());
    }

    internal static bool IsValidTypeScriptIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Must start with letter, _, or $
        if (!char.IsLetter(name[0]) && name[0] != '_' && name[0] != '$')
            return false;

        // Subsequent characters can be letters, digits, _, or $
        for (int i = 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_' && name[i] != '$')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Check if a conformance mismatch would break TypeScript assignability.
    /// Returns true if the class method signature is NOT assignable to the interface method in TS.
    /// </summary>
    internal static bool IsRepresentableConformanceBreak(MethodSymbol classMethod, MethodSymbol ifaceMethod)
    {
        // Erase both methods to TypeScript signatures
        var classSig = TsErase.EraseMember(classMethod);
        var ifaceSig = TsErase.EraseMember(ifaceMethod);

        // Check if class method is assignable to interface method
        // If assignable, this is NOT a representable break (benign difference)
        // If not assignable, this IS a representable break (real TS error)
        return !TsAssignability.IsMethodAssignable(classSig, ifaceSig);
    }

    internal static string GetPropertyTypeString(PropertySymbol property)
    {
        // Get a string representation of the property type for comparison
        // This is a simple comparison - if types have the same full name, they're considered the same
        return GetTypeFullName(property.PropertyType);
    }

    /// <summary>
    /// Check if an interface exists in the symbol graph.
    /// Returns true if the interface is being generated (in graph), false if external.
    /// </summary>
    internal static bool IsInterfaceInGraph(SymbolGraph graph, TypeReference ifaceRef)
    {
        var ifaceFullName = GetTypeFullName(ifaceRef);
        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Any(t => t.ClrFullName == ifaceFullName && t.Kind == TypeKind.Interface);
    }

    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            GenericParameterReference gp => gp.Name,
            ArrayTypeReference arr => $"{GetTypeFullName(arr.ElementType)}[]",
            PointerTypeReference ptr => $"{GetTypeFullName(ptr.PointeeType)}*",
            ByRefTypeReference byref => $"{GetTypeFullName(byref.ReferencedType)}&",
            NestedTypeReference nested => nested.FullReference.FullName,
            PlaceholderTypeReference placeholder => placeholder.DebugName,
            _ => "unknown"
        };
    }
}
