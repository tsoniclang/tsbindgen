using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core.Diagnostics;
using tsbindgen.SinglePhase.Emit;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Plan.Validation;

/// <summary>
/// Import/Export validation functions.
/// Validates public API surface, import completeness, and export completeness.
/// </summary>
internal static class ImportExport
{
    /// <summary>
    /// PG_API_001/002: Validates that public API surface doesn't reference non-emitted/internal types.
    /// This is the SEMANTIC validator - catches "public API exposes internal type" errors.
    /// Must run BEFORE PG_IMPORT_001 because it's more fundamental.
    /// </summary>
    internal static void ValidatePublicApiSurface(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating public API surface (PG_API_001/002)...");

        int checkedTypes = 0;
        int apiViolations = 0;

        // Helper: Check if a type will be emitted
        bool IsEmitted(TypeSymbol type)
        {
            return type.Accessibility == Accessibility.Public;
        }

        // Helper: Check if a type is exported from its namespace
        bool IsExported(TypeSymbol type, ImportPlan plan)
        {
            if (!plan.NamespaceExports.TryGetValue(type.Namespace, out var exports))
                return false;

            var finalName = ctx.Renamer.GetFinalTypeName(type);
            return exports.Any(e => e.ExportName == finalName);
        }

        // Helper: Check a single type reference in public API
        void CheckApiTypeReference(TypeSymbol ownerType, string location, NamedTypeReference named)
        {
            // Skip primitives
            if (TypeNameResolver.IsPrimitive(named.FullName))
                return;

            // Try to resolve to in-graph type
            var stableId = $"{named.AssemblyName}:{named.FullName}";
            if (!graph.TypeIndex.TryGetValue(stableId, out var referencedType))
            {
                // External type - skip (handled separately)
                return;
            }

            // Check if referenced type is emitted and exported
            var isEmitted = IsEmitted(referencedType);
            var isExported = IsExported(referencedType, imports);

            if (!isEmitted || !isExported)
            {
                var finalName = ctx.Renamer.GetFinalTypeName(referencedType);
                validationCtx.RecordDiagnostic(
                    DiagnosticCodes.PG_API_001,
                    "ERROR",
                    $"{ownerType.Namespace}.{ownerType.ClrName} exposes non-emitted type '{finalName}' " +
                    $"at {location} (emitted={isEmitted}, exported={isExported}, " +
                    $"accessibility={referencedType.Accessibility})");
                apiViolations++;
            }
        }

        // Helper: Walk TypeReference tree
        void Walk(TypeSymbol owner, string location, TypeReference? tr)
        {
            if (tr == null) return;

            switch (tr)
            {
                case NamedTypeReference named:
                    CheckApiTypeReference(owner, location, named);
                    // Recurse into type arguments
                    foreach (var arg in named.TypeArguments)
                        Walk(owner, location, arg);
                    break;

                case ArrayTypeReference arr:
                    Walk(owner, location, arr.ElementType);
                    break;

                case PointerTypeReference ptr:
                    Walk(owner, location, ptr.PointeeType);
                    break;

                case ByRefTypeReference byref:
                    Walk(owner, location, byref.ReferencedType);
                    break;

                case GenericParameterReference:
                    // Generic parameters are declared locally
                    break;
            }
        }

        // Walk all PUBLIC, EMITTED types
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types.Where(IsEmitted))
            {
                // Walk base type
                if (type.BaseType != null)
                    Walk(type, "base type", type.BaseType);

                // Walk interfaces
                foreach (var iface in type.Interfaces)
                    Walk(type, "interface", iface);

                // Walk method signatures (public only)
                foreach (var method in type.Members.Methods.Where(m => m.Visibility == Visibility.Public))
                {
                    foreach (var param in method.Parameters)
                        Walk(type, $"method '{method.ClrName}' parameter '{param.Name}'", param.Type);
                    Walk(type, $"method '{method.ClrName}' return", method.ReturnType);

                    // PG_API_002: Generic constraints
                    foreach (var gp in method.GenericParameters)
                    {
                        foreach (var constraint in gp.Constraints)
                            Walk(type, $"method '{method.ClrName}' generic constraint <{gp.Name}>", constraint);
                    }
                }

                // Walk property signatures (public only)
                foreach (var prop in type.Members.Properties.Where(p => p.Visibility == Visibility.Public))
                {
                    Walk(type, $"property '{prop.ClrName}' type", prop.PropertyType);
                    foreach (var param in prop.IndexParameters)
                        Walk(type, $"property '{prop.ClrName}' indexer parameter", param.Type);
                }

                // Walk field types (public only)
                foreach (var field in type.Members.Fields.Where(f => f.Visibility == Visibility.Public))
                    Walk(type, $"field '{field.ClrName}' type", field.FieldType);

                // Walk event types (public only)
                foreach (var evt in type.Members.Events.Where(e => e.Visibility == Visibility.Public))
                    Walk(type, $"event '{evt.ClrName}' handler type", evt.EventHandlerType);

                // PG_API_002: Type-level generic constraints
                foreach (var gp in type.GenericParameters)
                {
                    foreach (var constraint in gp.Constraints)
                        Walk(type, $"generic constraint <{gp.Name}>", constraint);
                }

                checkedTypes++;
            }
        }

        ctx.Log("PhaseGate", $"Validated {checkedTypes} public types. API violations: {apiViolations}");
    }

    /// <summary>
    /// PG_IMPORT_001: Validates that every foreign type used in signatures has a corresponding import.
    /// Walks all type references and ensures they're either:
    /// - Declared in the current namespace
    /// - Primitives (boolean, string, etc.)
    /// - External types
    /// - Imported from another namespace
    /// </summary>
    internal static void ValidateImportCompleteness(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating import completeness (PG_IMPORT_001)...");

        int checkedNamespaces = 0;
        int missingImports = 0;

        // Helper: Check if type is imported
        bool IsImported(string namespaceName, string tsTypeName)
        {
            if (!imports.NamespaceImports.TryGetValue(namespaceName, out var importStatements))
                return false;

            return importStatements.Any(stmt =>
                stmt.TypeImports.Any(ti => ti.TypeName == tsTypeName || ti.Alias == tsTypeName));
        }

        // Helper: Check if type is declared locally AND will be emitted
        bool IsDeclaredAndEmitted(NamespaceSymbol ns, string clrFullName)
        {
            var type = ns.Types.FirstOrDefault(t => t.ClrFullName == clrFullName);
            if (type == null) return false;

            // Type must be public and not omitted to be emitted
            // Internal/private types won't be in the output even if they're in the namespace
            return type.Accessibility == Accessibility.Public;
        }

        // Helper: Walk type references and check imports
        void CheckTypeReference(NamespaceSymbol ns, NamedTypeReference named, string owner, string where)
        {
            // Skip primitives
            if (TypeNameResolver.IsPrimitive(named.FullName))
                return;

            // Check if it's declared in this namespace AND will be emitted
            if (IsDeclaredAndEmitted(ns, named.FullName))
                return;

            // Check if it's in the graph
            var stableId = $"{named.AssemblyName}:{named.FullName}";
            if (!graph.TypeIndex.TryGetValue(stableId, out var targetType))
            {
                // External type - skip (handled by external imports)
                return;
            }

            // It's a foreign type from another namespace in the graph
            // Check if it's imported
            var tsName = ctx.Renamer.GetFinalTypeName(targetType);
            if (!IsImported(ns.Name, tsName))
            {
                validationCtx.RecordDiagnostic(
                    DiagnosticCodes.PG_IMPORT_001,
                    "ERROR",
                    $"{owner}: type '{tsName}' used in {where} but not imported (from {targetType.Namespace})");
                missingImports++;
            }
        }

        // Helper: Walk TypeReference tree
        void Walk(NamespaceSymbol ns, string owner, string where, TypeReference? tr)
        {
            if (tr == null) return;

            switch (tr)
            {
                case NamedTypeReference named:
                    CheckTypeReference(ns, named, owner, where);
                    // Recurse into type arguments
                    foreach (var arg in named.TypeArguments)
                        Walk(ns, owner, where, arg);
                    break;

                case ArrayTypeReference arr:
                    Walk(ns, owner, where, arr.ElementType);
                    break;

                case PointerTypeReference ptr:
                    Walk(ns, owner, where, ptr.PointeeType);
                    break;

                case ByRefTypeReference byref:
                    Walk(ns, owner, where, byref.ReferencedType);
                    break;

                case GenericParameterReference:
                    // Generic parameters are declared locally
                    break;
            }
        }

        // Walk all namespaces and check type references
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                var typeId = $"{ns.Name}.{type.ClrFullName}";

                // Walk base type
                if (type.BaseType != null)
                    Walk(ns, typeId, "base type", type.BaseType);

                // Walk interfaces
                foreach (var iface in type.Interfaces)
                    Walk(ns, typeId, "interface", iface);

                // Walk method signatures
                foreach (var method in type.Members.Methods)
                {
                    var methodId = $"{typeId}.{method.ClrName}";
                    foreach (var param in method.Parameters)
                        Walk(ns, methodId, "parameter", param.Type);
                    Walk(ns, methodId, "return", method.ReturnType);
                    foreach (var gp in method.GenericParameters)
                        foreach (var constraint in gp.Constraints)
                            Walk(ns, methodId, $"generic constraint {gp.Name}", constraint);
                }

                // Walk property signatures
                foreach (var prop in type.Members.Properties)
                {
                    var propId = $"{typeId}.{prop.ClrName}";
                    Walk(ns, propId, "property type", prop.PropertyType);
                    foreach (var param in prop.IndexParameters)
                        Walk(ns, propId, "indexer parameter", param.Type);
                }

                // Walk field types
                foreach (var field in type.Members.Fields)
                    Walk(ns, $"{typeId}.{field.ClrName}", "field type", field.FieldType);

                // Walk event types
                foreach (var evt in type.Members.Events)
                    Walk(ns, $"{typeId}.{evt.ClrName}", "event handler type", evt.EventHandlerType);

                // Walk generic parameter constraints
                foreach (var gp in type.GenericParameters)
                    foreach (var constraint in gp.Constraints)
                        Walk(ns, typeId, $"generic constraint {gp.Name}", constraint);
            }

            checkedNamespaces++;
        }

        ctx.Log("PhaseGate", $"Validated imports for {checkedNamespaces} namespaces. Missing imports: {missingImports}");
    }

    /// <summary>
    /// PG_EXPORT_001: Validates that every imported type is actually exported by its source namespace.
    /// Prevents TS2694 "Namespace has no exported member" errors.
    /// </summary>
    internal static void ValidateExportCompleteness(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating export completeness (PG_EXPORT_001)...");

        int checkedImports = 0;
        int missingExports = 0;

        foreach (var (namespaceName, importStatements) in imports.NamespaceImports)
        {
            foreach (var importStmt in importStatements)
            {
                // Get the exports from the target namespace
                if (!imports.NamespaceExports.TryGetValue(importStmt.TargetNamespace, out var exports))
                {
                    // Target namespace has no exports at all
                    foreach (var typeImport in importStmt.TypeImports)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_EXPORT_001,
                            "ERROR",
                            $"{namespaceName}: imports '{typeImport.TypeName}' from {importStmt.TargetNamespace}, but target namespace has no exports");
                        missingExports++;
                    }
                    continue;
                }

                // Check each imported type is actually exported
                var exportedNames = new HashSet<string>(exports.Select(e => e.ExportName));

                foreach (var typeImport in importStmt.TypeImports)
                {
                    var importedName = typeImport.TypeName;

                    if (!exportedNames.Contains(importedName))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_EXPORT_001,
                            "ERROR",
                            $"{namespaceName}: imports '{importedName}' from {importStmt.TargetNamespace}, but it's not exported. " +
                            $"Available exports: {string.Join(", ", exportedNames.Take(5))}{(exportedNames.Count > 5 ? "..." : "")}");
                        missingExports++;
                    }

                    checkedImports++;
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {checkedImports} imports. Missing exports: {missingExports}");
    }
}
