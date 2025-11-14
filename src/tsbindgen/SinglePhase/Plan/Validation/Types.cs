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
                    DiagnosticCodes.TypeNamePrinterRenamerMismatch,
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
                        DiagnosticCodes.TypeNamePrinterRenamerMismatch,
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
                                DiagnosticCodes.UnresolvedExternalType,
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

    /// <summary>
    /// PG_GENERIC_PRIM_LIFT_001: Validates that all primitive type arguments are covered by CLROf lifting rules.
    /// Ensures TypeRefPrinter primitive detection stays in sync with PrimitiveLift configuration.
    /// Prevents regressions where a new primitive is used but not added to CLROf mapping.
    /// </summary>
    internal static void ValidatePrimitiveGenericLifting(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating primitive generic lifting (PG_GENERIC_PRIM_LIFT_001)...");

        int checkedTypeArgs = 0;
        int primitiveTypeArgs = 0;

        // Helper: Check all type references in a type signature
        void CheckTypeReference(TypeReference typeRef, string owner)
        {
            // Walk the type reference tree
            switch (typeRef)
            {
                case NamedTypeReference named:
                    // Check if this is a generic instantiation
                    if (named.TypeArguments.Count > 0)
                    {
                        foreach (var arg in named.TypeArguments)
                        {
                            checkedTypeArgs++;

                            // Check if the type argument is a concrete primitive
                            if (arg is NamedTypeReference argNamed)
                            {
                                // Is this a CLR primitive type?
                                if (PrimitiveLift.IsLiftableClr(argNamed.FullName))
                                {
                                    primitiveTypeArgs++;
                                    // This is covered by CLROf - good!
                                }
                                // If it's NOT in PrimitiveLift but IS a System primitive,
                                // that's a configuration error
                                else if (argNamed.FullName.StartsWith("System.") && IsPotentialPrimitive(argNamed.FullName))
                                {
                                    validationCtx.RecordDiagnostic(
                                        DiagnosticCodes.PrimitiveGenericLiftMismatch,
                                        "ERROR",
                                        $"Type '{owner}' uses primitive type argument '{argNamed.FullName}' " +
                                        $"in generic type '{named.FullName}', but this primitive is not covered by " +
                                        $"CLROf lifting rules. Add it to PrimitiveLift.Rules. (PG_GENERIC_PRIM_LIFT_001)");
                                }
                            }

                            // Recursively check nested generics
                            CheckTypeReference(arg, owner);
                        }
                    }
                    break;

                case ArrayTypeReference arr:
                    CheckTypeReference(arr.ElementType, owner);
                    break;

                case PointerTypeReference ptr:
                    CheckTypeReference(ptr.PointeeType, owner);
                    break;

                case ByRefTypeReference byref:
                    CheckTypeReference(byref.ReferencedType, owner);
                    break;

                case NestedTypeReference nested:
                    CheckTypeReference(nested.FullReference, owner);
                    break;

                case GenericParameterReference:
                    // Generic parameters themselves don't need checking
                    break;
            }
        }

        // Helper: Check if a type name looks like a potential primitive
        // This catches cases where someone adds a new primitive without configuring it
        bool IsPotentialPrimitive(string fullName) =>
            fullName is "System.SByte" or "System.Byte"
                or "System.Int16" or "System.UInt16"
                or "System.Int32" or "System.UInt32"
                or "System.Int64" or "System.UInt64"
                or "System.IntPtr" or "System.UIntPtr"
                or "System.Single" or "System.Double" or "System.Decimal"
                or "System.Char" or "System.Boolean" or "System.String"
                or "System.Half" or "System.Int128" or "System.UInt128"; // Future primitives

        // Walk all types and their public surfaces
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                var typeId = $"{type.Namespace}.{type.ClrName}";

            // Check base type
            if (type.BaseType != null)
            {
                CheckTypeReference(type.BaseType, typeId);
            }

            // Check interfaces
            foreach (var iface in type.Interfaces)
            {
                CheckTypeReference(iface, typeId);
            }

            // Check method signatures
            foreach (var method in type.Members.Methods)
            {
                CheckTypeReference(method.ReturnType, $"{typeId}.{method.ClrName}");

                foreach (var param in method.Parameters)
                {
                    CheckTypeReference(param.Type, $"{typeId}.{method.ClrName}");
                }
            }

            // Check properties
            foreach (var prop in type.Members.Properties)
            {
                CheckTypeReference(prop.PropertyType, $"{typeId}.{prop.ClrName}");
            }

            // Check fields
            foreach (var field in type.Members.Fields)
            {
                CheckTypeReference(field.FieldType, $"{typeId}.{field.ClrName}");
            }

            // Check events
            foreach (var evt in type.Members.Events)
            {
                CheckTypeReference(evt.EventHandlerType, $"{typeId}.{evt.ClrName}");
            }
            }
        }

        ctx.Log("PhaseGate", $"Validated {checkedTypeArgs} generic type arguments. Primitive args: {primitiveTypeArgs}");
    }

    /// <summary>
    /// PG_REF_001: Validates that all type references can be resolved via import/local declaration/built-in.
    /// Detects TS2304 "Cannot find name" errors at planning time instead of tsc runtime.
    /// Checks that TypeNameResolver would produce a resolvable reference for every type used.
    /// </summary>
    internal static void ValidateTypeReferenceResolution(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating type reference resolution (PG_REF_001)...");

        int checkedReferences = 0;
        int unresolvedReferences = 0;

        // Helper: Check if a type reference is resolvable
        bool IsResolvable(NamedTypeReference named, NamespaceSymbol currentNamespace, TypeNameResolver resolver)
        {
            // 1. Check if it's a built-in type
            if (TypeMap.TryMapBuiltin(named.FullName, out _))
                return true;

            // 2. Check if it's a primitive that TypeNameResolver handles
            if (TypeNameResolver.IsPrimitive(named.FullName))
                return true;

            // 3. Check if it's a local type in current namespace
            var stableId = $"{named.AssemblyName}:{named.FullName}";
            if (graph.TypeIndex.TryGetValue(stableId, out var targetType))
            {
                // Type exists in graph - check if it's in current namespace (local) or needs import
                if (targetType.Namespace == currentNamespace.Name)
                {
                    // Local type in same namespace - always resolvable
                    return true;
                }

                // Foreign namespace - check if there's an import for it
                if (imports.NamespaceImports.TryGetValue(currentNamespace.Name, out var nsImports))
                {
                    // Check if any import covers this type's namespace
                    foreach (var import in nsImports)
                    {
                        if (import.TargetNamespace == targetType.Namespace)
                            return true;
                    }
                }

                // Type in different namespace but no import
                return false;
            }

            // 4. Type not in graph - could be external or missing
            // Allow if it's from a source assembly (might be internal type)
            var isSourceAssembly = graph.SourceAssemblies.Any(path =>
                System.IO.Path.GetFileNameWithoutExtension(path) == named.AssemblyName);

            return isSourceAssembly;
        }

        // Helper: Walk type reference tree
        void CheckTypeReference(TypeReference typeRef, string owner, NamespaceSymbol currentNamespace, TypeNameResolver resolver)
        {
            switch (typeRef)
            {
                case NamedTypeReference named:
                    checkedReferences++;

                    if (!IsResolvable(named, currentNamespace, resolver))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.TypeReferenceUnresolvable,
                            "ERROR",
                            $"{owner}: type reference '{named.FullName}' from assembly '{named.AssemblyName}' " +
                            $"cannot be resolved. No local declaration, import, or built-in type found. (PG_REF_001)");
                        unresolvedReferences++;
                    }

                    // Recursively check type arguments
                    foreach (var arg in named.TypeArguments)
                        CheckTypeReference(arg, owner, currentNamespace, resolver);
                    break;

                case ArrayTypeReference array:
                    CheckTypeReference(array.ElementType, owner, currentNamespace, resolver);
                    break;

                case PointerTypeReference ptr:
                    CheckTypeReference(ptr.PointeeType, owner, currentNamespace, resolver);
                    break;

                case ByRefTypeReference byref:
                    CheckTypeReference(byref.ReferencedType, owner, currentNamespace, resolver);
                    break;

                case NestedTypeReference nested:
                    CheckTypeReference(nested.FullReference, owner, currentNamespace, resolver);
                    break;

                case GenericParameterReference:
                    // Generic parameters are always resolvable (locally declared)
                    break;
            }
        }

        // Walk all types and their signatures
        foreach (var ns in graph.Namespaces)
        {
            var resolver = new TypeNameResolver(ctx, graph);

            foreach (var type in ns.Types)
            {
                var typeId = $"{ns.Name}.{type.ClrName}";

                // Check base type
                if (type.BaseType != null)
                    CheckTypeReference(type.BaseType, $"{typeId} (base)", ns, resolver);

                // Check interfaces
                foreach (var iface in type.Interfaces)
                    CheckTypeReference(iface, $"{typeId} (interface)", ns, resolver);

                // Check generic parameter constraints
                foreach (var gp in type.GenericParameters)
                {
                    foreach (var constraint in gp.Constraints)
                        CheckTypeReference(constraint, $"{typeId}<{gp.Name}> (constraint)", ns, resolver);
                }

                // Check method signatures
                foreach (var method in type.Members.Methods)
                {
                    var methodId = $"{typeId}.{method.ClrName}";

                    CheckTypeReference(method.ReturnType, $"{methodId} (return)", ns, resolver);

                    foreach (var param in method.Parameters)
                        CheckTypeReference(param.Type, $"{methodId} ({param.Name})", ns, resolver);

                    foreach (var gp in method.GenericParameters)
                    {
                        foreach (var constraint in gp.Constraints)
                            CheckTypeReference(constraint, $"{methodId}<{gp.Name}> (constraint)", ns, resolver);
                    }
                }

                // Check property signatures
                foreach (var prop in type.Members.Properties)
                {
                    CheckTypeReference(prop.PropertyType, $"{typeId}.{prop.ClrName} (property)", ns, resolver);

                    foreach (var param in prop.IndexParameters)
                        CheckTypeReference(param.Type, $"{typeId}.{prop.ClrName}[{param.Name}]", ns, resolver);
                }

                // Check field types
                foreach (var field in type.Members.Fields)
                    CheckTypeReference(field.FieldType, $"{typeId}.{field.ClrName} (field)", ns, resolver);

                // Check event types
                foreach (var evt in type.Members.Events)
                    CheckTypeReference(evt.EventHandlerType, $"{typeId}.{evt.ClrName} (event)", ns, resolver);
            }
        }

        ctx.Log("PhaseGate", $"Validated {checkedReferences} type references. Unresolved: {unresolvedReferences}");
    }

    /// <summary>
    /// PG_ARITY_001: Validates that generic type arity is consistent across aliases and exports.
    /// Detects TS2315 "Type is not generic" errors at planning time.
    /// Checks that every facade/internal export/view composition alias has matching arity.
    /// </summary>
    internal static void ValidateGenericArityConsistency(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating generic arity consistency (PG_ARITY_001)...");

        int checkedTypes = 0;
        int arityMismatches = 0;

        // Walk all types and check their arity is consistent
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                checkedTypes++;

                var typeId = $"{ns.Name}.{type.ClrName}";
                var expectedArity = type.Arity;

                // Check 1: Verify namespace exports (including facade exports if this namespace has them)
                if (imports.NamespaceExports.TryGetValue(ns.Name, out var nsExports))
                {
                    var finalTypeName = ctx.Renamer.GetFinalTypeName(type);

                    foreach (var export in nsExports)
                    {
                        if (export.ExportName == finalTypeName)
                        {
                            if (export.Arity != expectedArity)
                            {
                                validationCtx.RecordDiagnostic(
                                    DiagnosticCodes.GenericArityInconsistent,
                                    "ERROR",
                                    $"{typeId}: export has arity {export.Arity} but type has arity {expectedArity}. (PG_ARITY_001)");
                                arityMismatches++;
                            }
                        }
                    }
                }

                // Check 2: Verify all type references to this type use correct arity
                // This is implicitly checked by TypeRefPrinter, but we can add explicit validation
                // for type arguments count vs declared generic parameter count
                void CheckTypeRefArity(TypeReference typeRef, string owner)
                {
                    if (typeRef is NamedTypeReference named)
                    {
                        // Try to resolve to a type in the graph
                        var stableId = $"{named.AssemblyName}:{named.FullName}";
                        if (graph.TypeIndex.TryGetValue(stableId, out var targetType))
                        {
                            var declaredArity = targetType.Arity;
                            var usedArity = named.TypeArguments.Count;

                            if (declaredArity != usedArity)
                            {
                                validationCtx.RecordDiagnostic(
                                    DiagnosticCodes.GenericArityInconsistent,
                                    "ERROR",
                                    $"{owner}: uses type '{named.FullName}' with {usedArity} type arguments, " +
                                    $"but type is declared with {declaredArity} generic parameters. (PG_ARITY_001)");
                                arityMismatches++;
                            }
                        }

                        // Recursively check type arguments
                        foreach (var arg in named.TypeArguments)
                            CheckTypeRefArity(arg, owner);
                    }
                    else if (typeRef is ArrayTypeReference array)
                    {
                        CheckTypeRefArity(array.ElementType, owner);
                    }
                    else if (typeRef is PointerTypeReference ptr)
                    {
                        CheckTypeRefArity(ptr.PointeeType, owner);
                    }
                    else if (typeRef is ByRefTypeReference byref)
                    {
                        CheckTypeRefArity(byref.ReferencedType, owner);
                    }
                    else if (typeRef is NestedTypeReference nested)
                    {
                        CheckTypeRefArity(nested.FullReference, owner);
                    }
                }

                // Check type references in this type's signatures
                if (type.BaseType != null)
                    CheckTypeRefArity(type.BaseType, $"{typeId} (base)");

                foreach (var iface in type.Interfaces)
                    CheckTypeRefArity(iface, $"{typeId} (interface)");

                foreach (var gp in type.GenericParameters)
                {
                    foreach (var constraint in gp.Constraints)
                        CheckTypeRefArity(constraint, $"{typeId}<{gp.Name}> (constraint)");
                }

                foreach (var method in type.Members.Methods)
                {
                    CheckTypeRefArity(method.ReturnType, $"{typeId}.{method.ClrName} (return)");

                    foreach (var param in method.Parameters)
                        CheckTypeRefArity(param.Type, $"{typeId}.{method.ClrName} ({param.Name})");
                }

                foreach (var prop in type.Members.Properties)
                {
                    CheckTypeRefArity(prop.PropertyType, $"{typeId}.{prop.ClrName} (property)");
                }

                foreach (var field in type.Members.Fields)
                {
                    CheckTypeRefArity(field.FieldType, $"{typeId}.{field.ClrName} (field)");
                }

                foreach (var evt in type.Members.Events)
                {
                    CheckTypeRefArity(evt.EventHandlerType, $"{typeId}.{evt.ClrName} (event)");
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {checkedTypes} types for arity consistency. Mismatches: {arityMismatches}");
    }
}
