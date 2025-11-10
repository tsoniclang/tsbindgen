using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core.Diagnostics;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Shape;
using static tsbindgen.Core.TypeScriptReservedWords;

namespace tsbindgen.SinglePhase.Plan.Validation;

/// <summary>
/// Name-related validation functions.
/// Validates final names from Renamer, import aliases, identifier sanitization,
/// overload collisions, and class surface uniqueness.
/// </summary>
internal static class Names
{
    /// <summary>
    /// Validates that all types and members have final names from Renamer.
    /// Checks for duplicate names within namespace and type scopes.
    /// ClassSurface members only - ViewOnly members may have duplicate names in different views.
    /// </summary>
    internal static void ValidateFinalNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating final names from Renamer...");

        int totalTypes = 0;
        int totalMembers = 0;
        int duplicateTypes = 0;
        int duplicateMembers = 0;

        // Validate type names (namespace scope)
        foreach (var ns in graph.Namespaces)
        {
            var typeNamesInNamespace = new HashSet<string>();

            foreach (var type in ns.Types)
            {
                totalTypes++;

                // Get final name from Renamer
                var finalName = ctx.Renamer.GetFinalTypeName(type);

                if (string.IsNullOrWhiteSpace(finalName))
                {
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.ValidationFailed,
                        "ERROR",
                        $"Type {type.ClrFullName} has no final name from Renamer");
                    continue;
                }

                // Check for duplicates within namespace
                if (!typeNamesInNamespace.Add(finalName))
                {
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.DuplicateMember,
                        "ERROR",
                        $"Duplicate final type name '{finalName}' in namespace {ns.Name}");
                    duplicateTypes++;
                }
            }
        }

        // Validate member names (type scope, separated by static vs instance)
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                var instanceMemberNames = new HashSet<string>();
                var staticMemberNames = new HashSet<string>();

                // Validate method names (ClassSurface only - ViewOnly members may have duplicate names in different views)
                foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface))
                {
                    totalMembers++;

                    var methodScope = ScopeFactory.ClassSurface(type, method.IsStatic);
                    var finalName = ctx.Renamer.GetFinalMemberName(method.StableId, methodScope);

                    if (string.IsNullOrWhiteSpace(finalName))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Method {method.ClrName} in {type.ClrFullName} has no final name from Renamer");
                        continue;
                    }

                    // Add to appropriate scope
                    var scopeSet = method.IsStatic ? staticMemberNames : instanceMemberNames;
                    var scopeName = method.IsStatic ? "static" : "instance";

                    // Methods can have overloads, so we use signature for uniqueness
                    var signature = $"{finalName}({method.Parameters.Length})";

                    if (!scopeSet.Add(signature))
                    {
                        // This is a warning, not an error (overloads are allowed)
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.AmbiguousOverload,
                            "WARNING",
                            $"Duplicate {scopeName} method signature '{signature}' in {type.ClrFullName} (on class surface)");
                    }
                }

                // Validate property names (ClassSurface only - ViewOnly members may have duplicate names in different views)
                foreach (var property in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface))
                {
                    totalMembers++;

                    var propertyScope = ScopeFactory.ClassSurface(type, property.IsStatic);
                    var finalName = ctx.Renamer.GetFinalMemberName(property.StableId, propertyScope);

                    if (string.IsNullOrWhiteSpace(finalName))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Property {property.ClrName} in {type.ClrFullName} has no final name from Renamer");
                        continue;
                    }

                    // Add to appropriate scope
                    var scopeSet = property.IsStatic ? staticMemberNames : instanceMemberNames;
                    var scopeName = property.IsStatic ? "static" : "instance";

                    if (!scopeSet.Add(finalName))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.DuplicateMember,
                            "ERROR",
                            $"Duplicate {scopeName} property name '{finalName}' in {type.ClrFullName} (on class surface)");
                        duplicateMembers++;
                    }
                }

                // Validate field names (ClassSurface only - ViewOnly members may have duplicate names in different views)
                foreach (var field in type.Members.Fields.Where(f => f.EmitScope == EmitScope.ClassSurface))
                {
                    totalMembers++;

                    var fieldScope = ScopeFactory.ClassSurface(type, field.IsStatic);
                    var finalName = ctx.Renamer.GetFinalMemberName(field.StableId, fieldScope);

                    if (string.IsNullOrWhiteSpace(finalName))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Field {field.ClrName} in {type.ClrFullName} has no final name from Renamer");
                        continue;
                    }

                    // Add to appropriate scope
                    var scopeSet = field.IsStatic ? staticMemberNames : instanceMemberNames;
                    var scopeName = field.IsStatic ? "static" : "instance";

                    if (!scopeSet.Add(finalName))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.DuplicateMember,
                            "ERROR",
                            $"Duplicate {scopeName} field name '{finalName}' in {type.ClrFullName} (on class surface)");
                        duplicateMembers++;
                    }
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {totalTypes} type names and {totalMembers} member names from Renamer");

        if (duplicateTypes > 0 || duplicateMembers > 0)
        {
            ctx.Log("PhaseGate", $"Found {duplicateTypes} duplicate types, {duplicateMembers} duplicate members");
        }
    }

    /// <summary>
    /// Validates that import aliases don't collide after Renamer resolution.
    /// Checks that all aliased names are unique within their import scope.
    /// </summary>
    internal static void ValidateAliases(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating import aliases...");

        int totalAliases = 0;
        int aliasCollisions = 0;

        foreach (var (ns, importList) in imports.NamespaceImports)
        {
            var aliasesInScope = new HashSet<string>();
            var typeNamesInScope = new HashSet<string>();

            var namespaceScope = new NamespaceScope
            {
                Namespace = ns,
                IsInternal = true,
                ScopeKey = ns
            };

            foreach (var import in importList)
            {
                // Each import statement contains multiple type imports
                foreach (var typeImport in import.TypeImports)
                {
                    var effectiveName = typeImport.Alias ?? typeImport.TypeName;

                    // Check if this import uses an alias
                    if (!string.IsNullOrWhiteSpace(typeImport.Alias))
                    {
                        totalAliases++;

                        // Check for alias collisions
                        if (!aliasesInScope.Add(typeImport.Alias))
                        {
                            validationCtx.RecordDiagnostic(
                                DiagnosticCodes.NameConflictUnresolved,
                                "ERROR",
                                $"Import alias '{typeImport.Alias}' collides in namespace {ns}");
                            aliasCollisions++;
                        }
                    }

                    // Check that imported type names don't collide with each other
                    if (!typeNamesInScope.Add(effectiveName))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.NameConflictUnresolved,
                            "WARNING",
                            $"Imported type name '{effectiveName}' appears multiple times in namespace {ns}");
                    }
                }
            }

            // Also check that imported names don't collide with local types
            var localNamespace = graph.Namespaces.FirstOrDefault(n => n.Name == ns);
            if (localNamespace != null)
            {
                foreach (var localType in localNamespace.Types)
                {
                    var localTypeName = ctx.Renamer.GetFinalTypeName(localType);

                    if (typeNamesInScope.Contains(localTypeName))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.NameConflictUnresolved,
                            "WARNING",
                            $"Imported type name '{localTypeName}' in namespace {ns} collides with local type {localType.ClrFullName}");
                    }
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {totalAliases} import aliases");

        if (aliasCollisions > 0)
        {
            ctx.Log("PhaseGate", $"Found {aliasCollisions} alias collisions");
        }
    }

    /// <summary>
    /// PhaseGate Hardening M1: Validate all identifiers are properly sanitized.
    /// Checks that all TypeScript reserved words have been escaped with underscore suffix.
    /// Catches every unsanitized TS reserved word before emit (types, members, parameters, view members).
    /// </summary>
    internal static void ValidateIdentifiers(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M1: Validating identifier sanitization...");

        int totalIdentifiersChecked = 0;
        int unsanitizedCount = 0;

        foreach (var ns in graph.Namespaces)
        {
            // Check namespace name
            if (!string.IsNullOrWhiteSpace(ns.Name))
            {
                totalIdentifiersChecked++;
                CheckIdentifier(ctx, validationCtx, "namespace", ns.Name, ns.Name, ns.Name, ref unsanitizedCount);
            }

            foreach (var type in ns.Types)
            {
                // Get the final emitted name from Renamer
                var emittedTypeName = ctx.Renamer.GetFinalTypeName(type);

                // Check type name
                totalIdentifiersChecked++;
                CheckIdentifier(ctx, validationCtx, "type", type.ClrFullName, type.StableId.ToString(), emittedTypeName, ref unsanitizedCount);

                // Check type parameters
                foreach (var tp in type.GenericParameters)
                {
                    totalIdentifiersChecked++;
                    // Type parameters are emitted as-is (like regular identifiers), so sanitize them
                    var sanitizedTpName = SanitizeParameterName(tp.Name);
                    CheckIdentifier(ctx, validationCtx, "type parameter", $"{type.ClrFullName}.{tp.Name}", type.StableId.ToString(), sanitizedTpName, ref unsanitizedCount);
                }

                // Check methods (ClassSurface only - ViewOnly members checked in view loop below)
                foreach (var method in type.Members.Methods)
                {
                    // Skip private/internal members that won't be emitted
                    if (method.Visibility != Visibility.Public)
                        continue;

                    // Skip ViewOnly members - they're checked in the view members loop below
                    if (method.EmitScope == EmitScope.ViewOnly)
                        continue;

                    var methodScope = ScopeFactory.ClassSurface(type, method.IsStatic);
                    var emittedMethodName = ctx.Renamer.GetFinalMemberName(method.StableId, methodScope);

                    totalIdentifiersChecked++;
                    CheckIdentifier(ctx, validationCtx, "method", $"{type.ClrFullName}::{method.ClrName}", method.StableId.ToString(), emittedMethodName, ref unsanitizedCount);

                    // Check method parameters
                    int paramIndex = 0;
                    foreach (var param in method.Parameters)
                    {
                        totalIdentifiersChecked++;
                        // Parameters are sanitized using SanitizeParameterName (reserved words get "_" suffix)
                        var sanitizedParamName = SanitizeParameterName(param.Name);
                        CheckIdentifier(ctx, validationCtx, "parameter", $"{type.ClrFullName}::{method.ClrName}", $"{method.StableId}#param{paramIndex}", sanitizedParamName, ref unsanitizedCount);
                        paramIndex++;
                    }

                    // Check method type parameters
                    foreach (var tp in method.GenericParameters)
                    {
                        totalIdentifiersChecked++;
                        var sanitizedTpName = SanitizeParameterName(tp.Name);
                        CheckIdentifier(ctx, validationCtx, "method type parameter", $"{type.ClrFullName}::{method.ClrName}.{tp.Name}", method.StableId.ToString(), sanitizedTpName, ref unsanitizedCount);
                    }
                }

                // Check properties (ClassSurface only - ViewOnly members checked in view loop below)
                foreach (var property in type.Members.Properties)
                {
                    if (property.Visibility != Visibility.Public)
                        continue;

                    // Skip ViewOnly members - they're checked in the view members loop below
                    if (property.EmitScope == EmitScope.ViewOnly)
                        continue;

                    var propertyScope = ScopeFactory.ClassSurface(type, property.IsStatic);
                    var emittedPropertyName = ctx.Renamer.GetFinalMemberName(property.StableId, propertyScope);

                    totalIdentifiersChecked++;
                    CheckIdentifier(ctx, validationCtx, "property", $"{type.ClrFullName}::{property.ClrName}", property.StableId.ToString(), emittedPropertyName, ref unsanitizedCount);

                    // Check indexer parameters
                    int indexerParamIndex = 0;
                    foreach (var param in property.IndexParameters)
                    {
                        totalIdentifiersChecked++;
                        var sanitizedParamName = SanitizeParameterName(param.Name);
                        CheckIdentifier(ctx, validationCtx, "indexer parameter", $"{type.ClrFullName}::{property.ClrName}", $"{property.StableId}#param{indexerParamIndex}", sanitizedParamName, ref unsanitizedCount);
                        indexerParamIndex++;
                    }
                }

                // Check fields
                foreach (var field in type.Members.Fields)
                {
                    if (field.Visibility != Visibility.Public)
                        continue;

                    // Fields are always ClassSurface (no ViewOnly fields)
                    var fieldScope = ScopeFactory.ClassSurface(type, field.IsStatic);
                    var emittedFieldName = ctx.Renamer.GetFinalMemberName(field.StableId, fieldScope);

                    totalIdentifiersChecked++;
                    CheckIdentifier(ctx, validationCtx, "field", $"{type.ClrFullName}::{field.ClrName}", field.StableId.ToString(), emittedFieldName, ref unsanitizedCount);
                }

                // Check events
                foreach (var evt in type.Members.Events)
                {
                    if (evt.Visibility != Visibility.Public)
                        continue;

                    // Events are always ClassSurface (no ViewOnly events)
                    var eventScope = ScopeFactory.ClassSurface(type, evt.IsStatic);
                    var emittedEventName = ctx.Renamer.GetFinalMemberName(evt.StableId, eventScope);

                    totalIdentifiersChecked++;
                    CheckIdentifier(ctx, validationCtx, "event", $"{type.ClrFullName}::{evt.ClrName}", evt.StableId.ToString(), emittedEventName, ref unsanitizedCount);
                }

                // Check view members
                foreach (var view in type.ExplicitViews)
                {
                    // Check view property name
                    totalIdentifiersChecked++;
                    CheckIdentifier(ctx, validationCtx, "view property", $"{type.ClrFullName}.{view.ViewPropertyName}", type.StableId.ToString(), view.ViewPropertyName, ref unsanitizedCount);

                    // Check each view member's emitted name (to catch mismatches or synthetic entries)
                    // Get interface StableId for this view (needed for ViewSurface scope)
                    var interfaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);

                    foreach (var viewMember in view.ViewMembers)
                    {
                        string emittedMemberName;
                        string memberOwner;

                        switch (viewMember.Kind)
                        {
                            case ViewPlanner.ViewMemberKind.Method:
                                var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                                if (method != null)
                                {
                                    var methodScope = ScopeFactory.ViewSurface(type, interfaceStableId, method.IsStatic);
                                    emittedMemberName = ctx.Renamer.GetFinalMemberName(method.StableId, methodScope);
                                    memberOwner = $"{type.ClrFullName}::{method.ClrName} (in view {view.ViewPropertyName})";
                                    totalIdentifiersChecked++;
                                    CheckIdentifier(ctx, validationCtx, "view method", memberOwner, method.StableId.ToString(), emittedMemberName, ref unsanitizedCount);
                                }
                                break;

                            case ViewPlanner.ViewMemberKind.Property:
                                var property = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                                if (property != null)
                                {
                                    var propertyScope = ScopeFactory.ViewSurface(type, interfaceStableId, property.IsStatic);
                                    emittedMemberName = ctx.Renamer.GetFinalMemberName(property.StableId, propertyScope);
                                    memberOwner = $"{type.ClrFullName}::{property.ClrName} (in view {view.ViewPropertyName})";
                                    totalIdentifiersChecked++;
                                    CheckIdentifier(ctx, validationCtx, "view property member", memberOwner, property.StableId.ToString(), emittedMemberName, ref unsanitizedCount);
                                }
                                break;

                            case ViewPlanner.ViewMemberKind.Event:
                                var evt = type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId));
                                if (evt != null)
                                {
                                    var eventScope = ScopeFactory.ViewSurface(type, interfaceStableId, evt.IsStatic);
                                    emittedMemberName = ctx.Renamer.GetFinalMemberName(evt.StableId, eventScope);
                                    memberOwner = $"{type.ClrFullName}::{evt.ClrName} (in view {view.ViewPropertyName})";
                                    totalIdentifiersChecked++;
                                    CheckIdentifier(ctx, validationCtx, "view event", memberOwner, evt.StableId.ToString(), emittedMemberName, ref unsanitizedCount);
                                }
                                break;
                        }
                    }
                }
            }
        }

        ctx.Log("[PG]", $"M1: Checked {totalIdentifiersChecked} identifiers, found {unsanitizedCount} unsanitized reserved words");
    }

    /// <summary>
    /// PhaseGate Hardening M2: Validate no duplicate erased TS signatures in same surface.
    /// Checks class surface and each explicit view separately.
    /// Groups by (Name, Arity, ErasedParameterTypes, IsStatic).
    /// </summary>
    internal static void ValidateOverloadCollisions(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M2: Validating overload collisions...");

        int totalCollisions = 0;
        int totalSurfacesChecked = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Check class surface
                totalSurfacesChecked++;
                var classSurfaceCollisions = CheckSurfaceForCollisions(ctx, validationCtx, type, "class surface",
                    type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface && m.Visibility == Visibility.Public).ToList(),
                    type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface && p.Visibility == Visibility.Public).ToList());
                totalCollisions += classSurfaceCollisions;

                // Check each explicit view separately
                foreach (var view in type.ExplicitViews)
                {
                    totalSurfacesChecked++;

                    // Get interface StableId for this view
                    var interfaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);

                    // Collect ViewOnly members for this view
                    var viewMethods = view.ViewMembers
                        .Where(vm => vm.Kind == ViewPlanner.ViewMemberKind.Method)
                        .Select(vm => type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(vm.StableId)))
                        .Where(m => m != null)
                        .Cast<MethodSymbol>()
                        .ToList();

                    var viewProperties = view.ViewMembers
                        .Where(vm => vm.Kind == ViewPlanner.ViewMemberKind.Property)
                        .Select(vm => type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(vm.StableId)))
                        .Where(p => p != null)
                        .Cast<PropertySymbol>()
                        .ToList();

                    var viewSurfaceCollisions = CheckSurfaceForCollisions(ctx, validationCtx, type,
                        $"view {view.ViewPropertyName}", viewMethods, viewProperties, interfaceStableId);
                    totalCollisions += viewSurfaceCollisions;
                }
            }
        }

        ctx.Log("[PG]", $"M2: Checked {totalSurfacesChecked} surfaces, found {totalCollisions} signature collisions");
    }

    /// <summary>
    /// M5: Validate that class surface has no duplicate emitted names after deduplication.
    /// PG_NAME_005: Catches any duplicates that slipped through ClassSurfaceDeduplicator.
    /// </summary>
    internal static void ValidateClassSurfaceUniqueness(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M5: Validating class surface uniqueness...");

        int duplicates = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Only check classes and structs
                if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
                    continue;

                // Group class-surface properties by emitted name (camelCase)
                var propertyGroups = type.Members.Properties
                    .Where(p => p.EmitScope == EmitScope.ClassSurface)
                    .GroupBy(p => ApplyCamelCase(p.ClrName))
                    .Where(g => g.Count() > 1)
                    .ToList();

                foreach (var group in propertyGroups)
                {
                    duplicates++;
                    var members = string.Join(", ", group.Select(p => p.ClrName));
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.PG_NAME_005,
                        "ERROR",
                        $"Duplicate property name on class surface\n" +
                        $"  type:          {type.ClrFullName}\n" +
                        $"  emitted name:  {group.Key}\n" +
                        $"  members:       {members}\n" +
                        $"  reason:        ClassSurfaceDeduplicator should have resolved this");
                }
            }
        }

        ctx.Log("[PG]", $"M5: Class surface uniqueness - {duplicates} duplicate property names");
    }

    // ================================================================================
    // Helper Functions
    // ================================================================================

    private static void CheckIdentifier(BuildContext ctx, ValidationContext validationCtx, string symbolKind, string owner, string stableId, string emittedName, ref int unsanitizedCount)
    {
        if (string.IsNullOrWhiteSpace(emittedName))
            return;

        // Check if the emitted name is a TypeScript reserved word and doesn't have the trailing underscore
        if (IsReservedWord(emittedName) && !emittedName.EndsWith("_"))
        {
            unsanitizedCount++;
            validationCtx.RecordDiagnostic(
                DiagnosticCodes.PG_ID_001,
                "ERROR",
                $"Reserved identifier not sanitized\n" +
                $"  where:   {symbolKind}\n" +
                $"  owner:   {owner}\n" +
                $"  stable:  {stableId}\n" +
                $"  name:    {emittedName}  →  {emittedName}_  (expected suffix \"_\")");
        }
    }

    private static int CheckSurfaceForCollisions(
        BuildContext ctx,
        ValidationContext validationCtx,
        TypeSymbol type,
        string surfaceName,
        List<MethodSymbol> methods,
        List<PropertySymbol> properties,
        string? interfaceStableId = null)
    {
        int collisionCount = 0;

        // Group methods by erased signature key: (Name, IsStatic, ErasedSignature)
        // Methods and properties are in separate namespaces, so we check them separately
        var methodGroups = methods
            .GroupBy(m =>
            {
                var methodScope = interfaceStableId != null
                    ? ScopeFactory.ViewSurface(type, interfaceStableId, m.IsStatic)
                    : ScopeFactory.ClassSurface(type, m.IsStatic);
                var finalName = ctx.Renamer.GetFinalMemberName(m.StableId, methodScope);
                var erasedParams = string.Join(", ", m.Parameters.Select(p => EraseTypeToString(p.Type)));
                var erasedReturn = EraseTypeToString(m.ReturnType);
                return (finalName, m.IsStatic, erasedParams, erasedReturn);
            })
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in methodGroups)
        {
            var (name, isStatic, erasedParams, erasedReturn) = group.Key;
            var members = group.ToList();
            var scopeName = isStatic ? "static" : "instance";
            var erasedSignature = $"{name}({erasedParams}): {erasedReturn}";

            // Build member list with StableIds
            var memberDetails = string.Join("\n", members.Select(m =>
                $"    - stable: {m.StableId}\n" +
                $"      clr:    {type.ClrFullName}::{m.ClrName}"));

            validationCtx.RecordDiagnostic(
                DiagnosticCodes.PG_OV_001,
                "ERROR",
                $"Duplicate erased signature in {surfaceName}\n" +
                $"  type:      {type.ClrFullName}\n" +
                $"  scope:     {scopeName}\n" +
                $"  signature: {erasedSignature}\n" +
                $"  members:\n{memberDetails}");

            collisionCount++;
        }

        // Properties don't have overloads in TypeScript, but we can still check for duplicates
        // Group by (Name, IsStatic) only
        var propertyGroups = properties
            .GroupBy(p =>
            {
                var propertyScope = interfaceStableId != null
                    ? ScopeFactory.ViewSurface(type, interfaceStableId, p.IsStatic)
                    : ScopeFactory.ClassSurface(type, p.IsStatic);
                var finalName = ctx.Renamer.GetFinalMemberName(p.StableId, propertyScope);
                return (finalName, p.IsStatic);
            })
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in propertyGroups)
        {
            var (name, isStatic) = group.Key;
            var members = group.ToList();
            var scopeName = isStatic ? "static" : "instance";

            // Build member list with StableIds
            var memberDetails = string.Join("\n", members.Select(p =>
                $"    - stable: {p.StableId}\n" +
                $"      clr:    {type.ClrFullName}::{p.ClrName}\n" +
                $"      type:   {EraseTypeToString(p.PropertyType)}"));

            validationCtx.RecordDiagnostic(
                DiagnosticCodes.PG_OV_001,
                "ERROR",
                $"Duplicate property name in {surfaceName}\n" +
                $"  type:      {type.ClrFullName}\n" +
                $"  scope:     {scopeName}\n" +
                $"  property:  {name}\n" +
                $"  members:\n{memberDetails}");

            collisionCount++;
        }

        return collisionCount;
    }

    /// <summary>
    /// Erase a TypeReference to a simple string representation for signature comparison.
    /// Simplified version that doesn't require TsEmitName on types.
    /// </summary>
    private static string EraseTypeToString(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named when named.TypeArguments.Count > 0 =>
                $"{SimplifyTypeName(named.FullName)}<{string.Join(", ", named.TypeArguments.Select(EraseTypeToString))}>",

            NamedTypeReference named => SimplifyTypeName(named.FullName),

            NestedTypeReference nested => SimplifyTypeName(nested.FullReference.FullName),

            GenericParameterReference gp => gp.Name,

            ArrayTypeReference arr => $"ReadonlyArray<{EraseTypeToString(arr.ElementType)}>",

            PointerTypeReference ptr => EraseTypeToString(ptr.PointeeType),
            ByRefTypeReference byref => EraseTypeToString(byref.ReferencedType),

            _ => "unknown"
        };
    }

    /// <summary>
    /// Simplify type name to TypeScript-level representation.
    /// Maps common BCL types to their TS equivalents.
    /// </summary>
    private static string SimplifyTypeName(string fullName)
    {
        return fullName switch
        {
            "System.Void" => "void",
            "System.Object" => "any",
            "System.String" => "string",
            "System.Boolean" => "boolean",
            "System.Int32" => "number",
            "System.Int64" => "number",
            "System.Double" => "number",
            "System.Single" => "number",
            "System.Byte" => "number",
            "System.SByte" => "number",
            "System.Int16" => "number",
            "System.UInt16" => "number",
            "System.UInt32" => "number",
            "System.UInt64" => "number",
            "System.Decimal" => "number",
            _ => fullName.Replace("`", "_") // Replace generic arity marker
        };
    }

    /// <summary>
    /// Apply camelCase transformation to a name (simplified).
    /// </summary>
    private static string ApplyCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}
