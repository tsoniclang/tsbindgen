using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Core;
using tsbindgen.Core.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Normalize;

/// <summary>
/// Reserves all TypeScript names through the central Renamer.
/// Runs after Shape phase, before Plan phase (Phase 3.5).
///
/// Process:
/// 1. Applies syntax transforms (`+` → `_`, `` ` `` → `_`, etc.)
/// 2. Applies reserved word sanitization (adds `_` suffix)
/// 3. Reserves names through Renamer
/// 4. Sets TsEmitName on symbols for PhaseGate validation
///
/// Skips members that already have rename decisions from earlier passes
/// (e.g., HiddenMemberPlanner, IndexerPlanner).
/// </summary>
public static class NameReservation
{
    /// <summary>
    /// Reserve all type and member names in the symbol graph.
    /// This is the ONLY place where names are reserved - all other components
    /// must use Renamer.GetFinal*() to retrieve names.
    /// PURE - reserves names in Renamer and returns new graph with TsEmitName set.
    /// </summary>
    public static SymbolGraph ReserveAllNames(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("NameReservation", "Reserving all TypeScript names...");

        int typesReserved = 0;
        int membersReserved = 0;
        int membersSkippedAlreadyRenamed = 0;
        int skippedCompilerGenerated = 0;

        // Phase 1: Reserve all names in Renamer (populates internal dictionaries)
        foreach (var ns in graph.Namespaces.OrderBy(n => n.Name))
        {
            var nsScope = new NamespaceScope
            {
                ScopeKey = $"ns:{ns.Name}",
                Namespace = ns.Name,
                IsInternal = true
            };

            foreach (var type in ns.Types.OrderBy(t => t.ClrFullName))
            {
                if (IsCompilerGenerated(type.ClrName))
                {
                    ctx.Log("NameReservation", $"Skipping compiler-generated type {type.ClrFullName}");
                    skippedCompilerGenerated++;
                    continue;
                }

                // Reserve in Renamer only (don't mutate)
                var requested = ComputeTypeRequestedBase(type.ClrName);
                ctx.Renamer.ReserveTypeName(type.StableId, requested, nsScope, "TypeDeclaration", "NameReservation");
                typesReserved++;

                // Reserve class surface member names
                var (reserved, skipped) = ReserveMemberNamesOnly(ctx, type);
                membersReserved += reserved;
                membersSkippedAlreadyRenamed += skipped;

                // Collect actual class-surface final names from Renamer tables (after reservation)
                // Use the exact same scope construction as ReserveMemberNamesOnly
                var classScope = new TypeScope
                {
                    ScopeKey = $"type:{type.ClrFullName}",
                    TypeFullName = type.ClrFullName,
                    IsStatic = false
                };

                // CRITICAL FIX: Rebuild class-surface name sets by checking ALL ClassSurface members
                // This catches members that had pre-existing decisions from other passes
                var classInstanceNames = new HashSet<string>(StringComparer.Ordinal);
                var classStaticNames = new HashSet<string>(StringComparer.Ordinal);

                // Track allowed methods for logging
                var allowedForLog = new HashSet<string> { "ToByte", "ToSByte", "ToInt16", "Clear", "TryFormat", "IndexOf" };

                foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface))
                {
                    var shouldLog = allowedForLog.Contains(method.ClrName);

                    if (ctx.Renamer.TryGetDecision(method.StableId, out var decision))
                    {
                        if (shouldLog)
                        {
                            ctx.Log("name-resv:class",
                                $"{type.StableId}::{method.ClrName} emitScope={method.EmitScope} isStatic={method.IsStatic} tryDecision=true final={decision.Final}");
                        }

                        if (method.IsStatic)
                            classStaticNames.Add(decision.Final);
                        else
                            classInstanceNames.Add(decision.Final);
                    }
                    else if (shouldLog)
                    {
                        ctx.Log("name-resv:class",
                            $"{type.StableId}::{method.ClrName} emitScope={method.EmitScope} isStatic={method.IsStatic} tryDecision=false final=<none>");
                    }
                }

                foreach (var property in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface))
                {
                    if (ctx.Renamer.TryGetDecision(property.StableId, out var decision))
                    {
                        if (property.IsStatic)
                            classStaticNames.Add(decision.Final);
                        else
                            classInstanceNames.Add(decision.Final);
                    }
                }

                foreach (var field in type.Members.Fields.Where(f => f.EmitScope == EmitScope.ClassSurface))
                {
                    if (ctx.Renamer.TryGetDecision(field.StableId, out var decision))
                    {
                        if (field.IsStatic)
                            classStaticNames.Add(decision.Final);
                        else
                            classInstanceNames.Add(decision.Final);
                    }
                }

                foreach (var ev in type.Members.Events.Where(e => e.EmitScope == EmitScope.ClassSurface))
                {
                    if (ctx.Renamer.TryGetDecision(ev.StableId, out var decision))
                    {
                        if (ev.IsStatic)
                            classStaticNames.Add(decision.Final);
                        else
                            classInstanceNames.Add(decision.Final);
                    }
                }

                // Union used for view-vs-class collision checks (PhaseGate mirrors this)
                var classAllNames = new HashSet<string>(StringComparer.Ordinal);
                classAllNames.UnionWith(classInstanceNames);
                classAllNames.UnionWith(classStaticNames);

                // B1) Trace: class-surface name sets
                var tracedTypes = new[] { "System.Decimal", "System.Array", "System.CharEnumerator", "System.Enum", "System.TypeInfo" };
                if (tracedTypes.Contains(type.ClrFullName))
                {
                    ctx.Log("trace:resv:class",
                        $"[trace:resv:class] {type.StableId} set=instance count={classInstanceNames.Count} names=[{string.Join(",", classInstanceNames.OrderBy(x => x))}]");
                    ctx.Log("trace:resv:class",
                        $"[trace:resv:class] {type.StableId} set=static count={classStaticNames.Count} names=[{string.Join(",", classStaticNames.OrderBy(x => x))}]");
                    ctx.Log("trace:resv:class",
                        $"[trace:resv:class] {type.StableId} set=all count={classAllNames.Count} names=[{string.Join(",", classAllNames.OrderBy(x => x))}]");

                    // B1) Trace: canary members
                    var canaryNames = new HashSet<string> { "ToByte", "ToSByte", "ToInt16", "Clear", "IndexOf", "Current", "TryFormat", "GetMethods", "GetFields" };
                    foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface && canaryNames.Contains(m.ClrName)))
                    {
                        var hasDec = ctx.Renamer.TryGetDecision(method.StableId, out var decision);
                        var final = hasDec ? decision.Final : "NA";
                        ctx.Log("trace:resv:class",
                            $"[trace:resv:class] {type.StableId}::{Plan.PhaseGate.FormatMemberStableId(method.StableId)} isStatic={method.IsStatic} TryGetDecision={hasDec} final={final} EmitScope={method.EmitScope}");
                    }

                    foreach (var prop in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface && canaryNames.Contains(p.ClrName)))
                    {
                        var hasDec = ctx.Renamer.TryGetDecision(prop.StableId, out var decision);
                        var final = hasDec ? decision.Final : "NA";
                        ctx.Log("trace:resv:class",
                            $"[trace:resv:class] {type.StableId}::{Plan.PhaseGate.FormatMemberStableId(prop.StableId)} isStatic={prop.IsStatic} TryGetDecision={hasDec} final={final} EmitScope={prop.EmitScope}");
                    }
                }

                // DEBUG: Write to file for System.Decimal and System.Boolean to diagnose
                if (type.ClrFullName == "System.Decimal" || type.ClrFullName == "System.Boolean")
                {
                    var debugPath = System.IO.Path.Combine(System.Environment.CurrentDirectory, ".tests", "decimal-debug.txt");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(debugPath));

                    var staticToMethods = type.Members.Methods
                        .Where(m => m.IsStatic && m.ClrName.StartsWith("To"))
                        .Select(m => $"{m.ClrName}({m.EmitScope})")
                        .ToList();

                    System.IO.File.AppendAllText(debugPath,
                        $"{type.ClrFullName} static To* methods ({staticToMethods.Count} items): {string.Join(", ", staticToMethods)}\n" +
                        $"{type.ClrFullName} classInstanceNames ({classInstanceNames.Count} items): {string.Join(", ", classInstanceNames)}\n" +
                        $"{type.ClrFullName} classStaticNames ({classStaticNames.Count} items): {string.Join(", ", classStaticNames)}\n" +
                        $"{type.ClrFullName} classAllNames ({classAllNames.Count} items): {string.Join(", ", classAllNames)}\n\n");
                }

                ctx.Log("name-resv:class", $"type={type.StableId} classAll=[{string.Join(",", classAllNames)}]");

                // M5: Reserve view-scoped member names (separate scope per interface)
                var (viewReserved, viewSkipped) = ReserveViewMemberNamesOnly(ctx, graph, type, classAllNames);
                membersReserved += viewReserved;
                membersSkippedAlreadyRenamed += viewSkipped;
            }
        }

        ctx.Log("NameReservation", $"Reserved {typesReserved} type names, {membersReserved} member names");
        if (membersSkippedAlreadyRenamed > 0)
        {
            ctx.Log("NameReservation", $"Skipped {membersSkippedAlreadyRenamed} members (already renamed by earlier passes)");
        }
        if (skippedCompilerGenerated > 0)
        {
            ctx.Log("NameReservation", $"Skipped {skippedCompilerGenerated} compiler-generated types");
        }

        // Phase 2: Apply names to graph (pure transformation)
        var updatedGraph = ApplyNamesToGraph(ctx, graph);
        return updatedGraph;
    }

    /// <summary>
    /// Compute the requested base name for a type.
    /// Applies syntax transforms (nested `+` → `_`, generic arity, etc.)
    /// then sanitizes reserved words (adds `_` suffix).
    /// Does NOT apply style/casing - Renamer handles that.
    /// </summary>
    private static string ComputeTypeRequestedBase(string clrName)
    {
        var baseName = clrName;

        // Handle nested types: Outer+Inner → Outer_Inner
        baseName = baseName.Replace('+', '_');

        // Handle generic arity: List`1 → List_1
        baseName = baseName.Replace('`', '_');

        // Remove other invalid TS identifier characters
        baseName = baseName.Replace('<', '_');
        baseName = baseName.Replace('>', '_');
        baseName = baseName.Replace('[', '_');
        baseName = baseName.Replace(']', '_');

        // Apply reserved word sanitization
        var sanitized = TypeScriptReservedWords.Sanitize(baseName);
        return sanitized.Sanitized;
    }

    private static string ComputeMethodBase(MethodSymbol method)
    {
        var name = method.ClrName;

        // Handle operators (map to policy-defined names)
        if (name.StartsWith("op_"))
        {
            var mapped = name switch
            {
                "op_Equality" => "equals",
                "op_Inequality" => "notEquals",
                "op_Addition" => "add",
                "op_Subtraction" => "subtract",
                "op_Multiply" => "multiply",
                "op_Division" => "divide",
                "op_Modulus" => "modulus",
                "op_BitwiseAnd" => "bitwiseAnd",
                "op_BitwiseOr" => "bitwiseOr",
                "op_ExclusiveOr" => "bitwiseXor",
                "op_LeftShift" => "leftShift",
                "op_RightShift" => "rightShift",
                "op_UnaryNegation" => "negate",
                "op_UnaryPlus" => "plus",
                "op_LogicalNot" => "not",
                "op_OnesComplement" => "complement",
                "op_Increment" => "increment",
                "op_Decrement" => "decrement",
                "op_True" => "isTrue",
                "op_False" => "isFalse",
                "op_GreaterThan" => "greaterThan",
                "op_LessThan" => "lessThan",
                "op_GreaterThanOrEqual" => "greaterThanOrEqual",
                "op_LessThanOrEqual" => "lessThanOrEqual",
                _ => name.Replace("op_", "operator_")
            };

            // Apply reserved word sanitization to operator names
            var sanitized = TypeScriptReservedWords.Sanitize(mapped);
            return sanitized.Sanitized;
        }

        // Accessors (get_, set_, add_, remove_) and regular methods use CLR name
        return SanitizeMemberName(name);
    }

    private static string SanitizeMemberName(string name)
    {
        // Remove invalid TS identifier characters
        var cleaned = name.Replace('<', '_');
        cleaned = cleaned.Replace('>', '_');
        cleaned = cleaned.Replace('[', '_');
        cleaned = cleaned.Replace(']', '_');
        cleaned = cleaned.Replace('+', '_');

        // Apply reserved word sanitization
        var sanitized = TypeScriptReservedWords.Sanitize(cleaned);
        return sanitized.Sanitized;
    }

    /// <summary>
    /// Centralized function to compute requested base name for any member.
    /// Both class surface and view members use this to ensure consistency.
    /// Returns the base name that will be passed to Renamer (before style transform and numeric suffixes).
    /// </summary>
    private static string RequestedBaseForMember(string clrName)
    {
        // Sanitize CLR name (remove invalid TS chars, handle reserved words)
        return SanitizeMemberName(clrName);
    }

    /// <summary>
    /// Check if a type name indicates compiler-generated code.
    /// Compiler-generated types have unspeakable names containing < or >
    /// Examples: "<Module>", "<PrivateImplementationDetails>", "<Name>e__FixedBuffer", "<>c__DisplayClass"
    /// </summary>
    private static bool IsCompilerGenerated(string clrName)
    {
        return clrName.Contains('<') || clrName.Contains('>');
    }

    /// <summary>
    /// Reserve member names without mutating symbols (Phase 1).
    /// Returns (Reserved, Skipped) counts.
    /// Skips members that already have rename decisions from earlier passes.
    /// </summary>
    private static (int Reserved, int Skipped) ReserveMemberNamesOnly(BuildContext ctx, TypeSymbol type)
    {
        var typeScope = new TypeScope
        {
            ScopeKey = $"type:{type.ClrFullName}",
            TypeFullName = type.ClrFullName,
            IsStatic = false
        };

        int reserved = 0;
        int skipped = 0;

        foreach (var method in type.Members.Methods.OrderBy(m => m.ClrName))
        {
            // DEBUG: Log all Decimal To* methods
            bool isDecimalToMethod = type.ClrFullName == "System.Decimal" && method.ClrName.StartsWith("To");

            // M5: Skip ViewOnly members - they'll be reserved in view-scoped reservation
            if (method.EmitScope == EmitScope.ViewOnly)
            {
                if (isDecimalToMethod)
                    ctx.Log("name-resv-debug", $"Skip ViewOnly: {method.ClrName} (static={method.IsStatic})");
                skipped++;
                continue;
            }

            // Check if already renamed by earlier pass (e.g., HiddenMemberPlanner, IndexerPlanner)
            if (ctx.Renamer.TryGetDecision(method.StableId, out var existingDecision))
            {
                if (isDecimalToMethod)
                    ctx.Log("name-resv-debug", $"Skip existing: {method.ClrName} (from {existingDecision.DecisionSource}, final={existingDecision.Final})");
                skipped++;
                continue;
            }

            var reason = method.Provenance switch
            {
                MemberProvenance.Original => "MethodDeclaration",
                MemberProvenance.FromInterface => "InterfaceMember",
                MemberProvenance.Synthesized => "SynthesizedMember",
                _ => "Unknown"
            };

            var requested = ComputeMethodBase(method);
            if (isDecimalToMethod)
                ctx.Log("name-resv-debug", $"Reserving: {method.ClrName} (static={method.IsStatic}, requested={requested})");
            ctx.Renamer.ReserveMemberName(method.StableId, requested, typeScope, reason, method.IsStatic, "NameReservation");
            reserved++;
        }

        foreach (var property in type.Members.Properties.OrderBy(p => p.ClrName))
        {
            // M5: Skip ViewOnly members - they'll be reserved in view-scoped reservation
            if (property.EmitScope == EmitScope.ViewOnly)
            {
                skipped++;
                continue;
            }

            // Check if already renamed (e.g., IndexerPlanner)
            if (ctx.Renamer.TryGetDecision(property.StableId, out var existingDecision))
            {
                skipped++;
                continue;
            }

            var reason = property.IsIndexer ? "IndexerProperty" : "PropertyDeclaration";
            var requested = RequestedBaseForMember(property.ClrName);
            ctx.Renamer.ReserveMemberName(property.StableId, requested, typeScope, reason, property.IsStatic, "NameReservation");
            reserved++;
        }

        foreach (var field in type.Members.Fields.OrderBy(f => f.ClrName))
        {
            // Check if already renamed
            if (ctx.Renamer.TryGetDecision(field.StableId, out var existingDecision))
            {
                skipped++;
                continue;
            }

            var reason = field.IsConst ? "ConstantField" : "FieldDeclaration";
            var requested = RequestedBaseForMember(field.ClrName);
            ctx.Renamer.ReserveMemberName(field.StableId, requested, typeScope, reason, field.IsStatic, "NameReservation");
            reserved++;
        }

        foreach (var ev in type.Members.Events.OrderBy(e => e.ClrName))
        {
            // Check if already renamed
            if (ctx.Renamer.TryGetDecision(ev.StableId, out var existingDecision))
            {
                skipped++;
                continue;
            }

            var requested = RequestedBaseForMember(ev.ClrName);
            ctx.Renamer.ReserveMemberName(ev.StableId, requested, typeScope, reason: "EventDeclaration", ev.IsStatic, "NameReservation");
            reserved++;
        }

        foreach (var ctor in type.Members.Constructors)
        {
            // Check if already renamed
            if (ctx.Renamer.TryGetDecision(ctor.StableId, out var existingDecision))
            {
                skipped++;
                continue;
            }

            ctx.Renamer.ReserveMemberName(ctor.StableId, "constructor", typeScope, "ConstructorDeclaration", ctor.IsStatic, "NameReservation");
            reserved++;
        }

        return (reserved, skipped);
    }

    /// <summary>
    /// M5: Reserve view member names in view-scoped namespace (separate from class surface).
    /// Each view gets its own scope: (TypeStableId, InterfaceStableId, isStatic).
    /// Uses PeekFinalMemberName to detect collisions with actual class-surface names.
    /// Returns (Reserved, Skipped) counts.
    /// </summary>
    private static (int Reserved, int Skipped) ReserveViewMemberNamesOnly(
        BuildContext ctx,
        SymbolGraph graph,
        TypeSymbol type,
        HashSet<string> classAllNames)
    {
        int reserved = 0;
        int skipped = 0;

        // DEBUG: Log entry for canary types
        var canaryTypes = new[] { "System.Decimal", "System.Array", "System.CharEnumerator", "System.Enum", "System.TypeInfo" };
        if (canaryTypes.Contains(type.ClrFullName))
        {
            ctx.Log("NameReservation", $"[DEBUG] ReserveViewMemberNamesOnly CALLED: type={type.StableId} ExplicitViews.Length={type.ExplicitViews.Length}");
            foreach (var view in type.ExplicitViews)
            {
                ctx.Log("NameReservation", $"  view={view.ViewPropertyName} ViewMembers.Length={view.ViewMembers.Length}");
            }
        }

        // Check if type has any explicit views
        if (type.ExplicitViews.Length == 0)
            return (0, 0);

        // For each view, create a separate scope and reserve names (deterministic order)
        // Sort views by interface StableId for consistent ordering
        var sortedViews = type.ExplicitViews.OrderBy(v =>
        {
            var stableId = ResolveInterfaceStableId(graph, v.InterfaceReference);
            return stableId ?? "zzz"; // Put unresolved at end
        });

        foreach (var view in sortedViews)
        {
            // Resolve interface StableId (assembly-qualified)
            var interfaceStableId = ResolveInterfaceStableId(graph, view.InterfaceReference);
            if (interfaceStableId == null)
            {
                ctx.Log("NameReservation", $"WARNING: Could not resolve interface StableId for view {view.ViewPropertyName} on {type.ClrFullName}");
                continue;
            }

            // Create view-specific scope using StableIds (not CLR names)
            var viewScope = new TypeScope
            {
                ScopeKey = $"view:{type.StableId}:{interfaceStableId}",
                TypeFullName = type.ClrFullName,
                IsStatic = false
            };

            // Create class surface scope for collision detection (must match scope used in ReserveMemberNamesOnly)
            var classSurfaceScope = new TypeScope
            {
                ScopeKey = $"type:{type.ClrFullName}",
                TypeFullName = type.ClrFullName,
                IsStatic = false
            };

            // Reserve names for each ViewOnly member (deterministic order)
            // ViewOnly members get separate view-scoped names even if they exist on class surface
            foreach (var viewMember in view.ViewMembers.OrderBy(vm => vm.Kind).ThenBy(vm => vm.StableId.ToString()))
            {
                // DEBUG: Log entry for canaries
                var canaryNames = new HashSet<string> { "ToByte", "ToSByte", "ToInt16", "Clear", "IndexOf", "Current", "TryFormat", "GetMethods", "GetFields" };
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view", $"[trace:resv:view] ENTER loop: member={viewMember.ClrName} stableId={viewMember.StableId}");
                }

                // M5 FIX: DO NOT skip! ViewOnly members need separate view-scoped decisions
                // even if they also exist on ClassSurface with the same StableId.
                // The collision detection below will apply $view suffix if needed.

                // This is a ViewOnly member - verify by checking EmitScope
                bool isViewOnly = false;
                switch (viewMember.Kind)
                {
                    case Shape.ViewPlanner.ViewMemberKind.Method:
                        var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                        isViewOnly = method?.EmitScope == EmitScope.ViewOnly;
                        break;
                    case Shape.ViewPlanner.ViewMemberKind.Property:
                        var prop = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                        isViewOnly = prop?.EmitScope == EmitScope.ViewOnly;
                        break;
                    case Shape.ViewPlanner.ViewMemberKind.Event:
                        var evt = type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId));
                        isViewOnly = evt?.EmitScope == EmitScope.ViewOnly;
                        break;
                }

                if (!isViewOnly)
                {
                    skipped++;
                    continue; // Not a ViewOnly member
                }

                // Find the actual member symbol to get isStatic
                var isStatic = FindMemberIsStatic(type, viewMember);

                // Compute base requested name using centralized function (same as class surface)
                var requested = RequestedBaseForMember(viewMember.ClrName);

                // Peek at what the view member would get in its scope
                var peek = ctx.Renamer.PeekFinalMemberName(viewScope, requested, isStatic);

                // DEBUG: Log peek result for canaries
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view", $"[DEBUG] TYPE={type.ClrFullName} member={viewMember.ClrName}");
                    ctx.Log("trace:resv:view", $"[DEBUG] requested={requested} peek={peek} isStatic={isStatic}");
                    ctx.Log("trace:resv:view", $"[DEBUG] classAllNames.Count={classAllNames.Count} Contains(peek)={classAllNames.Contains(peek)}");
                }

                // Collision if the view's final name equals ANY class-surface final name (static or instance)
                var collided = classAllNames.Contains(peek);

                // DEBUG: Log collision result
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view", $"[DEBUG] collided={collided}");
                }

                string finalRequested;
                string reason;
                string applySuffix;

                if (collided)
                {
                    // Collision with class surface - apply $view suffix
                    finalRequested = requested + "$view";

                    // DEBUG: Log suffix application
                    if (canaryNames.Contains(viewMember.ClrName))
                    {
                        ctx.Log("trace:resv:view", $"[DEBUG] APPLYING $view suffix: finalRequested={finalRequested}");
                    }

                    // If $view is also taken in the view scope, try $view2, $view3, etc.
                    var suffix = 1;
                    while (ctx.Renamer.IsNameTaken(viewScope, finalRequested, isStatic))
                    {
                        suffix++;
                        finalRequested = requested + "$view" + suffix;
                    }

                    reason = "ViewCollision";
                    applySuffix = finalRequested;  // e.g., "toSByte$view"
                }
                else
                {
                    finalRequested = requested;
                    reason = $"ViewMember:{view.ViewPropertyName}";
                    applySuffix = "none";
                }

                // B2) Trace: view reservation with detailed collision info
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view",
                        $"[trace:resv:view] scope=view:{type.StableId}:{interfaceStableId}:{isStatic} member={Plan.PhaseGate.FormatMemberStableId(viewMember.StableId)}");
                    ctx.Log("trace:resv:view",
                        $"  requested={requested} peek={peek} classAllHit={collided} applySuffix={applySuffix} final={finalRequested}");
                }

                // Reserve in view scope
                ctx.Renamer.ReserveMemberName(
                    viewMember.StableId,
                    finalRequested,
                    viewScope,
                    reason,
                    isStatic,
                    "NameReservation");

                reserved++;
            }
        }

        return (reserved, skipped);
    }

    /// <summary>
    /// Helper to find if a view member is static by looking up the actual member symbol.
    /// </summary>
    private static bool FindMemberIsStatic(TypeSymbol type, Shape.ViewPlanner.ViewMember viewMember)
    {
        // Try to find the member in the type's members collection
        switch (viewMember.Kind)
        {
            case Shape.ViewPlanner.ViewMemberKind.Method:
                var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                return method?.IsStatic ?? false;

            case Shape.ViewPlanner.ViewMemberKind.Property:
                var property = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                return property?.IsStatic ?? false;

            case Shape.ViewPlanner.ViewMemberKind.Event:
                var evt = type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId));
                return evt?.IsStatic ?? false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Helper to get full type name from TypeReference.
    /// </summary>
    private static string GetTypeReferenceName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    /// <summary>
    /// Resolve the StableId of an interface TypeReference by looking it up in the graph.
    /// Returns null if the interface is not found in the graph.
    /// </summary>
    private static string? ResolveInterfaceStableId(SymbolGraph graph, Model.Types.TypeReference ifaceRef)
    {
        var fullName = GetTypeReferenceName(ifaceRef);

        var iface = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);

        return iface?.StableId.ToString();
    }

    /// <summary>
    /// Apply reserved names to graph (Phase 2 - pure transformation).
    /// </summary>
    private static SymbolGraph ApplyNamesToGraph(BuildContext ctx, SymbolGraph graph)
    {
        var updatedNamespaces = graph.Namespaces.Select(ns => ApplyNamesToNamespace(ctx, ns)).ToImmutableArray();
        return graph with { Namespaces = updatedNamespaces };
    }

    private static NamespaceSymbol ApplyNamesToNamespace(BuildContext ctx, NamespaceSymbol ns)
    {
        var nsScope = new NamespaceScope
        {
            ScopeKey = $"ns:{ns.Name}",
            Namespace = ns.Name,
            IsInternal = true
        };

        var updatedTypes = ns.Types.Select(t =>
        {
            if (IsCompilerGenerated(t.ClrName))
                return t;

            return ApplyNamesToType(ctx, t, nsScope);
        }).ToImmutableArray();

        return ns with { Types = updatedTypes };
    }

    private static TypeSymbol ApplyNamesToType(BuildContext ctx, TypeSymbol type, NamespaceScope nsScope)
    {
        var typeScope = new TypeScope
        {
            ScopeKey = $"type:{type.ClrFullName}",
            TypeFullName = type.ClrFullName,
            IsStatic = false
        };

        // Get TsEmitName from Renamer
        var tsEmitName = ctx.Renamer.GetFinalTypeName(type.StableId, nsScope);

        // Update members
        var updatedMembers = ApplyNamesToMembers(ctx, type.Members, typeScope);

        // Return new type with updated names
        return type with
        {
            TsEmitName = tsEmitName,
            Members = updatedMembers
        };
    }

    private static TypeMembers ApplyNamesToMembers(BuildContext ctx, TypeMembers members, TypeScope typeScope)
    {
        var updatedMethods = members.Methods.Select(m =>
        {
            var tsEmitName = ctx.Renamer.GetFinalMemberName(m.StableId, typeScope, m.IsStatic);
            return m with { TsEmitName = tsEmitName };
        }).ToImmutableArray();

        var updatedProperties = members.Properties.Select(p =>
        {
            var tsEmitName = ctx.Renamer.GetFinalMemberName(p.StableId, typeScope, p.IsStatic);
            return p with { TsEmitName = tsEmitName };
        }).ToImmutableArray();

        var updatedFields = members.Fields.Select(f =>
        {
            var tsEmitName = ctx.Renamer.GetFinalMemberName(f.StableId, typeScope, f.IsStatic);
            return f with { TsEmitName = tsEmitName };
        }).ToImmutableArray();

        var updatedEvents = members.Events.Select(e =>
        {
            var tsEmitName = ctx.Renamer.GetFinalMemberName(e.StableId, typeScope, e.IsStatic);
            return e with { TsEmitName = tsEmitName };
        }).ToImmutableArray();

        return members with
        {
            Methods = updatedMethods.ToImmutableArray(),
            Properties = updatedProperties.ToImmutableArray(),
            Fields = updatedFields,
            Events = updatedEvents
        };
    }
}
