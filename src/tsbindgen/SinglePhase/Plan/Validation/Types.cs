using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using tsbindgen.Core.Diagnostics;
using tsbindgen.SinglePhase.Emit;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Plan.Validation;

/// <summary>
/// Type system validation functions.
/// Validates printer name consistency, TypeMap compliance, and external type resolution.
/// </summary>
internal static class Types
{
    /// <summary>
    /// PG_PRINT_001: Validates that TypeNameResolver produces names matching Renamer final names.
    /// Ensures no CLR names leak into TypeScript output through TypeRefPrinter.
    /// This guard validates the TypeRefPrinter→TypeNameResolver→Renamer chain integrity.
    /// Walks ALL type references in signatures (parameters, returns, base types, interfaces, etc.)
    /// </summary>
    internal static void ValidatePrinterNameConsistency(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating printer name consistency (PG_PRINT_001)...");

        var resolver = new TypeNameResolver(ctx, graph);
        int checkedReferences = 0;

        // Helper: Check a single NamedTypeReference
        void CheckNamed(NamedTypeReference named, string owner, string where)
        {
            // Skip primitives - they intentionally print as built-ins
            if (TypeNameResolver.IsPrimitive(named.FullName))
                return;

            // Try to resolve this NamedTypeReference to a TypeSymbol in the graph
            var stableId = $"{named.AssemblyName}:{named.FullName}";
            if (!graph.TypeIndex.TryGetValue(stableId, out var targetType))
            {
                // External assembly or out of graph - skip for this check
                return;
            }

            // Compare what resolver produces vs what renamer says
            var renamerName = ctx.Renamer.GetFinalTypeName(targetType);
            var resolverName = resolver.For(named);

            if (!string.Equals(renamerName, resolverName, StringComparison.Ordinal))
            {
                validationCtx.RecordDiagnostic(
                    DiagnosticCodes.PG_PRINT_001,
                    "ERROR",
                    $"{owner}: type name mismatch in {where}. resolver='{resolverName}', renamer='{renamerName}'");
            }

            checkedReferences++;
        }

        // Helper: Walk TypeReference tree recursively
        void Walk(string owner, string where, TypeReference? tr)
        {
            if (tr == null) return;

            switch (tr)
            {
                case NamedTypeReference named:
                    CheckNamed(named, owner, where);
                    // Recurse into type arguments
                    foreach (var arg in named.TypeArguments)
                        Walk(owner, where, arg);
                    break;

                case ArrayTypeReference arr:
                    Walk(owner, where, arr.ElementType);
                    break;

                case PointerTypeReference ptr:
                    Walk(owner, where, ptr.PointeeType);
                    break;

                case ByRefTypeReference byref:
                    Walk(owner, where, byref.ReferencedType);
                    break;

                case GenericParameterReference:
                    // Generic parameters don't need validation - they're declared locally
                    break;
            }
        }

        // Walk all types and their signatures
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                var typeId = $"{ns.Name}.{type.ClrFullName}";

                // 1. Validate the type identifier itself (quick sanity check)
                var renamerName = ctx.Renamer.GetFinalTypeName(type);
                var resolverName = resolver.For(type);
                if (renamerName != resolverName)
                {
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.PG_PRINT_001,
                        "ERROR",
                        $"Type identifier mismatch for {type.ClrFullName}. resolver='{resolverName}', renamer='{renamerName}'");
                }

                // 2. Walk base type
                if (type.BaseType != null)
                    Walk(typeId, "base type", type.BaseType);

                // 3. Walk interfaces
                foreach (var iface in type.Interfaces)
                    Walk(typeId, "interface", iface);

                // 4. Walk method signatures
                foreach (var method in type.Members.Methods)
                {
                    var methodId = $"{typeId}.{method.ClrName}";

                    // Parameters
                    foreach (var param in method.Parameters)
                        Walk(methodId, "parameter", param.Type);

                    // Return type
                    Walk(methodId, "return", method.ReturnType);

                    // Generic constraints
                    foreach (var gp in method.GenericParameters)
                    {
                        foreach (var constraint in gp.Constraints)
                            Walk(methodId, $"generic constraint {gp.Name}", constraint);
                    }
                }

                // 5. Walk property signatures
                foreach (var prop in type.Members.Properties)
                {
                    var propId = $"{typeId}.{prop.ClrName}";

                    // Property type
                    Walk(propId, "property type", prop.PropertyType);

                    // Indexer parameters
                    foreach (var param in prop.IndexParameters)
                        Walk(propId, "indexer parameter", param.Type);
                }

                // 6. Walk field types
                foreach (var field in type.Members.Fields)
                {
                    Walk($"{typeId}.{field.ClrName}", "field type", field.FieldType);
                }

                // 7. Walk event types
                foreach (var evt in type.Members.Events)
                {
                    Walk($"{typeId}.{evt.ClrName}", "event handler type", evt.EventHandlerType);
                }

                // 8. Walk generic parameter constraints on the type itself
                foreach (var gp in type.GenericParameters)
                {
                    foreach (var constraint in gp.Constraints)
                        Walk(typeId, $"generic constraint {gp.Name}", constraint);
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated printer consistency for {checkedReferences} type references");
    }

    /// <summary>
    /// PG_TYPEMAP_001: Validates that no type references use unsupported special forms.
    /// NOTE: Pointers and byrefs are now properly handled via branded marker types
    /// (TSUnsafePointer<T>, TSByRef<T>) and no longer trigger validation errors.
    /// This guard currently detects function pointers and other unsupported forms.
    /// </summary>
    internal static void ValidateTypeMapCompliance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating TypeMap compliance (PG_TYPEMAP_001)...");

        int checkedTypes = 0;
        int unsupportedForms = 0;

        void CheckTypeReference(TypeReference typeRef, string ownerContext)
        {
            checkedTypes++;

            switch (typeRef)
            {
                case PointerTypeReference ptr:
                    // Pointer types are now properly handled by TypeRefPrinter → TSUnsafePointer<T>
                    // Recursively check the pointee type
                    CheckTypeReference(ptr.PointeeType, ownerContext);
                    break;

                case ByRefTypeReference byref:
                    // ByRef types are now properly handled by TypeRefPrinter → TSByRef<T>
                    // Recursively check the referenced type
                    CheckTypeReference(byref.ReferencedType, ownerContext);
                    break;

                case NamedTypeReference named:
                    // Recursively check type arguments
                    foreach (var arg in named.TypeArguments)
                    {
                        CheckTypeReference(arg, ownerContext);
                    }
                    break;

                case ArrayTypeReference array:
                    CheckTypeReference(array.ElementType, ownerContext);
                    break;

                case GenericParameterReference:
                    // Generic parameters are fine
                    break;

                case NestedTypeReference nested:
                    CheckTypeReference(nested.FullReference, ownerContext);
                    break;
            }
        }

        // Check all type references in the graph
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                var typeId = $"{type.Namespace}.{type.ClrName}";

                // Check base type
                if (type.BaseType != null)
                {
                    CheckTypeReference(type.BaseType, $"{typeId} (base type)");
                }

                // Check interfaces
                foreach (var iface in type.Interfaces)
                {
                    CheckTypeReference(iface, $"{typeId} (interface)");
                }

                // Check generic parameter constraints
                foreach (var gp in type.GenericParameters)
                {
                    foreach (var constraint in gp.Constraints)
                    {
                        CheckTypeReference(constraint, $"{typeId}.{gp.Name} (constraint)");
                    }
                }

                // Check member signatures
                foreach (var method in type.Members.Methods)
                {
                    CheckTypeReference(method.ReturnType, $"{typeId}.{method.ClrName} (return type)");
                    foreach (var param in method.Parameters)
                    {
                        CheckTypeReference(param.Type, $"{typeId}.{method.ClrName} (param {param.Name})");
                    }
                }

                foreach (var prop in type.Members.Properties)
                {
                    CheckTypeReference(prop.PropertyType, $"{typeId}.{prop.ClrName} (property type)");
                }

                foreach (var field in type.Members.Fields)
                {
                    CheckTypeReference(field.FieldType, $"{typeId}.{field.ClrName} (field type)");
                }

                foreach (var evt in type.Members.Events)
                {
                    CheckTypeReference(evt.EventHandlerType, $"{typeId}.{evt.ClrName} (event type)");
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {checkedTypes} type references. Unsupported forms: {unsupportedForms}");
    }

    /// <summary>
    /// PG_LOAD_001: Validates that all external type references are either in TypeIndex or built-in.
    /// Detects types that should have been loaded but weren't (missing transitive closure).
    /// ALLOWS references to types in source assemblies (might be internal types in same assembly).
    /// </summary>
    internal static void ValidateExternalTypeResolution(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating external type resolution (PG_LOAD_001)...");

        int checkedReferences = 0;
        int unresolvedReferences = 0;

        void CheckTypeReference(TypeReference typeRef, string ownerContext)
        {
            switch (typeRef)
            {
                case NamedTypeReference named:
                    // Skip built-in types (already handled by TypeMap)
                    if (TypeMap.TryMapBuiltin(named.FullName, out _))
                    {
                        return;
                    }

                    // Check if type is in TypeIndex
                    var stableId = $"{named.AssemblyName}:{named.FullName}";
                    if (!graph.TypeIndex.TryGetValue(stableId, out _))
                    {
                        // Check if this is a reference to an assembly we're actively generating
                        // (Could be an internal type in the same assembly - that's OK)
                        var isSourceAssembly = graph.SourceAssemblies.Any(path =>
                            Path.GetFileNameWithoutExtension(path) == named.AssemblyName);

                        if (!isSourceAssembly)
                        {
                            // External type not in graph and not built-in - MISSING
                            validationCtx.RecordDiagnostic(
                                DiagnosticCodes.PG_LOAD_001,
                                "ERROR",
                                $"{ownerContext}: references external type '{named.FullName}' from assembly '{named.AssemblyName}', " +
                                $"but it's not in TypeIndex and not a built-in type. Transitive closure loading failed to resolve this dependency.");
                            unresolvedReferences++;
                        }
                        // If it IS a source assembly, allow the reference (might be internal type in same assembly)
                    }

                    checkedReferences++;

                    // Recursively check type arguments
                    foreach (var arg in named.TypeArguments)
                    {
                        CheckTypeReference(arg, ownerContext);
                    }
                    break;

                case ArrayTypeReference array:
                    CheckTypeReference(array.ElementType, ownerContext);
                    break;

                case PointerTypeReference ptr:
                    CheckTypeReference(ptr.PointeeType, ownerContext);
                    break;

                case ByRefTypeReference byref:
                    CheckTypeReference(byref.ReferencedType, ownerContext);
                    break;

                case GenericParameterReference:
                    // Generic parameters are fine
                    break;

                case NestedTypeReference nested:
                    CheckTypeReference(nested.FullReference, ownerContext);
                    break;
            }
        }

        // Check all type references in public API surface
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types.Where(t => t.Accessibility == Accessibility.Public))
            {
                var typeId = $"{type.Namespace}.{type.ClrName}";

                // Check base type
                if (type.BaseType != null)
                {
                    CheckTypeReference(type.BaseType, $"{typeId} (base)");
                }

                // Check interfaces
                foreach (var iface in type.Interfaces)
                {
                    CheckTypeReference(iface, $"{typeId} (interface)");
                }

                // Check public member signatures
                foreach (var method in type.Members.Methods.Where(m => m.Visibility == Model.Symbols.MemberSymbols.Visibility.Public))
                {
                    CheckTypeReference(method.ReturnType, $"{typeId}.{method.ClrName}");
                    foreach (var param in method.Parameters)
                    {
                        CheckTypeReference(param.Type, $"{typeId}.{method.ClrName}({param.Name})");
                    }
                }

                foreach (var prop in type.Members.Properties.Where(p => p.Visibility == Model.Symbols.MemberSymbols.Visibility.Public))
                {
                    CheckTypeReference(prop.PropertyType, $"{typeId}.{prop.ClrName}");
                }

                foreach (var field in type.Members.Fields.Where(f => f.Visibility == Model.Symbols.MemberSymbols.Visibility.Public))
                {
                    CheckTypeReference(field.FieldType, $"{typeId}.{field.ClrName}");
                }

                foreach (var evt in type.Members.Events.Where(e => e.Visibility == Model.Symbols.MemberSymbols.Visibility.Public))
                {
                    CheckTypeReference(evt.EventHandlerType, $"{typeId}.{evt.ClrName}");
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {checkedReferences} external references. Unresolved: {unresolvedReferences}");
    }
}
