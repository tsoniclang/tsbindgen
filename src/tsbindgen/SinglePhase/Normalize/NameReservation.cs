using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Normalize.Naming;

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
            var nsScope = ScopeFactory.Namespace(ns.Name, NamespaceArea.Internal);

            foreach (var type in ns.Types.OrderBy(t => t.ClrFullName))
            {
                if (Shared.IsCompilerGenerated(type.ClrName))
                {
                    ctx.Log("NameReservation", $"Skipping compiler-generated type {type.ClrFullName}");
                    skippedCompilerGenerated++;
                    continue;
                }

                // Reserve in Renamer only (don't mutate)
                var requested = Shared.ComputeTypeRequestedBase(type.ClrName);
                ctx.Renamer.ReserveTypeName(type.StableId, requested, nsScope, "TypeDeclaration", "NameReservation");
                typesReserved++;

                // Reserve class surface member names
                var (reserved, skipped) = Reservation.ReserveMemberNamesOnly(ctx, type);
                membersReserved += reserved;
                membersSkippedAlreadyRenamed += skipped;

                // Collect actual class-surface final names from Renamer tables (after reservation)
                // Use the exact same scope construction as ReserveMemberNamesOnly
                var classScope = ScopeFactory.ClassBase(type);

                // CRITICAL FIX: Rebuild class-surface name sets by checking ALL ClassSurface members
                // This catches members that had pre-existing decisions from other passes
                var classInstanceNames = new HashSet<string>(StringComparer.Ordinal);
                var classStaticNames = new HashSet<string>(StringComparer.Ordinal);

                // Track allowed methods for logging
                var allowedForLog = new HashSet<string> { "ToByte", "ToSByte", "ToInt16", "Clear", "TryFormat", "IndexOf" };

                foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface))
                {
                    var shouldLog = allowedForLog.Contains(method.ClrName);
                    var methodScope = ScopeFactory.ClassSurface(type, method.IsStatic);

                    if (ctx.Renamer.TryGetDecision(method.StableId, methodScope, out var decision))
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
                    var propertyScope = ScopeFactory.ClassSurface(type, property.IsStatic);
                    if (ctx.Renamer.TryGetDecision(property.StableId, propertyScope, out var decision))
                    {
                        if (property.IsStatic)
                            classStaticNames.Add(decision.Final);
                        else
                            classInstanceNames.Add(decision.Final);
                    }
                }

                foreach (var field in type.Members.Fields.Where(f => f.EmitScope == EmitScope.ClassSurface))
                {
                    var fieldScope = ScopeFactory.ClassSurface(type, field.IsStatic);
                    if (ctx.Renamer.TryGetDecision(field.StableId, fieldScope, out var decision))
                    {
                        if (field.IsStatic)
                            classStaticNames.Add(decision.Final);
                        else
                            classInstanceNames.Add(decision.Final);
                    }
                }

                foreach (var ev in type.Members.Events.Where(e => e.EmitScope == EmitScope.ClassSurface))
                {
                    var eventScope = ScopeFactory.ClassSurface(type, ev.IsStatic);
                    if (ctx.Renamer.TryGetDecision(ev.StableId, eventScope, out var decision))
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
                        var traceMethodScope = ScopeFactory.ClassSurface(type, method.IsStatic);
                        var hasDec = ctx.Renamer.TryGetDecision(method.StableId, traceMethodScope, out var decision);
                        var final = hasDec ? decision.Final : "NA";
                        ctx.Log("trace:resv:class",
                            $"[trace:resv:class] {type.StableId}::{Plan.Validation.Scopes.FormatMemberStableId(method.StableId)} isStatic={method.IsStatic} TryGetDecision={hasDec} final={final} EmitScope={method.EmitScope}");
                    }

                    foreach (var prop in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface && canaryNames.Contains(p.ClrName)))
                    {
                        var tracePropScope = ScopeFactory.ClassSurface(type, prop.IsStatic);
                        var hasDec = ctx.Renamer.TryGetDecision(prop.StableId, tracePropScope, out var decision);
                        var final = hasDec ? decision.Final : "NA";
                        ctx.Log("trace:resv:class",
                            $"[trace:resv:class] {type.StableId}::{Plan.Validation.Scopes.FormatMemberStableId(prop.StableId)} isStatic={prop.IsStatic} TryGetDecision={hasDec} final={final} EmitScope={prop.EmitScope}");
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
                var (viewReserved, viewSkipped) = Reservation.ReserveViewMemberNamesOnly(ctx, graph, type, classAllNames);
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

        // Step 5: Post-reservation audit (fail fast)
        // Verify every emitted member has a rename decision in its correct scope
        Audit.AuditReservationCompleteness(ctx, graph);

        // Phase 2: Apply names to graph (pure transformation)
        var updatedGraph = Application.ApplyNamesToGraph(ctx, graph);
        return updatedGraph;
    }
}
