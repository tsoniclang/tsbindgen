using System.Diagnostics;
using tsbindgen.SinglePhase.Model.Symbols;

namespace tsbindgen.SinglePhase.Renaming;

/// <summary>
/// Central naming authority for the entire generation pipeline.
/// All TypeScript identifiers flow through this component.
/// Responsibilities:
/// - Materialize final TS identifiers for types and members
/// - Record every rename with full provenance (RenameDecision)
/// - Provide deterministic suffix allocation
/// - Separate static and instance member scopes
/// </summary>
public sealed class SymbolRenamer
{
    private readonly Dictionary<string, NameReservationTable> _tablesByScope = new();
    // M5 FIX: Key by (StableId, ScopeKey) to support dual-scope reservations (class + view)
    private readonly Dictionary<(StableId Id, string ScopeKey), RenameDecision> _decisions = new();
    private readonly Dictionary<StableId, string> _explicitOverrides = new();
    private Func<string, string>? _styleTransform;

    /// <summary>
    /// Apply explicit CLI/user overrides. Called first, before any other reservations.
    /// </summary>
    public void ApplyExplicitOverrides(IReadOnlyDictionary<string, string> explicitMap)
    {
        foreach (var (clrPath, targetName) in explicitMap)
        {
            // Parse clrPath to StableId (simplified - real impl would be more robust)
            // For now, store by string key
            _explicitOverrides[new TypeStableId
            {
                AssemblyName = "unknown",
                ClrFullName = clrPath
            }] = targetName;
        }
    }

    /// <summary>
    /// Adopt a style transform (e.g., camelCase) that applies to all identifiers.
    /// Called once during context setup, before any reservations.
    /// </summary>
    public void AdoptStyleTransform(Func<string, string> transform)
    {
        _styleTransform = transform;
    }

    /// <summary>
    /// Apply the style transform to a name without reserving it.
    /// Used for collision detection when checking if a name is already taken.
    /// </summary>
    public string ApplyStyleTransform(string name)
    {
        return _styleTransform?.Invoke(name) ?? name;
    }

    /// <summary>
    /// Reserve a type name in a namespace scope.
    /// </summary>
    public void ReserveTypeName(
        StableId stableId,
        string requested,
        RenameScope scope,
        string reason,
        string decisionSource = "Unknown")
    {
        var table = GetOrCreateTable(scope);
        var final = ResolveNameWithConflicts(
            stableId,
            requested,
            table,
            scope,
            reason,
            decisionSource,
            isStatic: null);

        // Record decision
        RecordDecision(new RenameDecision
        {
            Id = stableId,
            Requested = requested,
            Final = final,
            From = ExtractOriginalName(requested),
            Reason = reason,
            DecisionSource = decisionSource,
            Strategy = final == requested ? "None" : "NumericSuffix",
            ScopeKey = scope.ScopeKey,
            IsStatic = null
        });
    }

    /// <summary>
    /// Reserve a member name in a type scope.
    /// Static and instance members are tracked separately.
    /// </summary>
    public void ReserveMemberName(
        StableId stableId,
        string requested,
        RenameScope scope,
        string reason,
        bool isStatic,
        string decisionSource = "Unknown")
    {
        // Create a sub-scope for static vs instance
        var effectiveScope = scope is TypeScope ts
            ? ts with { IsStatic = isStatic, ScopeKey = $"{ts.ScopeKey}#{(isStatic ? "static" : "instance")}" }
            : scope;

        var table = GetOrCreateTable(effectiveScope);
        var final = ResolveNameWithConflicts(
            stableId,
            requested,
            table,
            effectiveScope,
            reason,
            decisionSource,
            isStatic);

        // Record decision
        RecordDecision(new RenameDecision
        {
            Id = stableId,
            Requested = requested,
            Final = final,
            From = ExtractOriginalName(requested),
            Reason = reason,
            DecisionSource = decisionSource,
            Strategy = final == requested ? "None" : "NumericSuffix",
            ScopeKey = effectiveScope.ScopeKey,
            IsStatic = isStatic
        });
    }

    /// <summary>
    /// Get the final TypeScript name for a type (SAFE API - use this).
    /// Automatically derives the correct namespace scope from the type.
    /// </summary>
    public string GetFinalTypeName(TypeSymbol type, NamespaceArea area = NamespaceArea.Internal)
    {
        var nsScope = ScopeFactory.Namespace(type.Namespace, area);
        return GetFinalTypeNameCore(type.StableId, nsScope);
    }

    /// <summary>
    /// Get the final TypeScript name for a type (INTERNAL CORE - do not call directly).
    /// Callers should use GetFinalTypeName(TypeSymbol, NamespaceArea) instead.
    /// </summary>
    internal string GetFinalTypeNameCore(StableId stableId, NamespaceScope scope)
    {
        AssertNamespaceScope(scope);

        // M5 FIX: Look up by (StableId, ScopeKey) tuple
        if (_decisions.TryGetValue((stableId, scope.ScopeKey), out var decision))
            return decision.Final;

        throw new InvalidOperationException(
            $"No rename decision found for {stableId} in scope {scope.ScopeKey}. " +
            "Did you forget to reserve this name?");
    }

    /// <summary>
    /// Get the final TypeScript name for a member.
    /// M5 FIX: Now scope-aware - different scopes (class vs view) return different names.
    /// CRITICAL: Scope must be a SURFACE scope (with #static or #instance suffix).
    /// Use ScopeFactory.ClassSurface/ViewSurface for lookups.
    /// </summary>
    public string GetFinalMemberName(StableId stableId, RenameScope scope)
    {
        // Step 6: Validate scope format - must be a surface scope with #static or #instance
        if (scope is TypeScope typeScope)
        {
            AssertMemberScope(typeScope);

#if DEBUG
            // Step 6: Additional DEBUG validation - ensure view scopes match view members
            // For view scopes, the caller MUST be looking up a ViewOnly member
            if (typeScope.ScopeKey.StartsWith("view:", StringComparison.Ordinal))
            {
                // This is a view scope lookup - verify it's intentional
                // Note: We can't check EmitScope here since we only have StableId
                // The PhaseGate validation (PG_SCOPE_004) will catch scope/EmitScope mismatches
            }
#endif
        }

        // M5 FIX: Members may be reserved in multiple scopes (class + view)
        if (_decisions.TryGetValue((stableId, scope.ScopeKey), out var decision))
            return decision.Final;

        // Debug: Find all scopes where this StableId was reserved
        var availableScopes = _decisions.Keys
            .Where(k => k.Id.Equals(stableId))
            .Select(k => k.ScopeKey)
            .ToList();

        var scopeInfo = availableScopes.Count > 0
            ? $"Available scopes for this StableId: [{string.Join(", ", availableScopes)}]"
            : "This StableId was not reserved in any scope.";

        throw new InvalidOperationException(
            $"No rename decision found for {stableId} in scope {scope.ScopeKey}. " +
            $"Did you forget to reserve this name? Check that you're using the correct scope (class vs view). " +
            $"{scopeInfo}");
    }

    /// <summary>
    /// Try to get the rename decision for a StableId in a specific scope.
    /// M5 FIX: Now requires scope parameter since members can be reserved in multiple scopes.
    /// CRITICAL: Scope must be a SURFACE scope (with #static or #instance suffix).
    /// </summary>
    public bool TryGetDecision(StableId stableId, RenameScope scope, out RenameDecision? decision)
    {
        // Validate scope format - must be a surface scope
        if (scope is TypeScope typeScope)
        {
            AssertMemberScope(typeScope);
        }

        return _decisions.TryGetValue((stableId, scope.ScopeKey), out decision);
    }

    /// <summary>
    /// Get all rename decisions (for metadata/bindings emission).
    /// </summary>
    public IReadOnlyCollection<RenameDecision> GetAllDecisions() => _decisions.Values;

    // ============================================================================
    // QUERY HELPERS (for PhaseGate and emitters - prevent guessing)
    // ============================================================================

    /// <summary>
    /// Check if a type name has been reserved in the specified namespace scope.
    /// Returns true if a rename decision exists for this type.
    /// </summary>
    public bool HasFinalTypeName(StableId stableId, NamespaceScope scope)
    {
        AssertNamespaceScope(scope);
        return _decisions.ContainsKey((stableId, scope.ScopeKey));
    }

    /// <summary>
    /// Check if a member name has been reserved in the CLASS surface scope.
    /// CRITICAL: Scope must be a SURFACE scope (with #static or #instance suffix).
    /// Use ScopeFactory.ClassSurface(type, isStatic) to create the scope.
    /// </summary>
    public bool HasFinalMemberName(StableId stableId, TypeScope scope)
    {
        AssertMemberScope(scope);

        // Ensure this is a class scope, not a view scope
        if (scope.ScopeKey.StartsWith("view:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"HasFinalMemberName called with view scope '{scope.ScopeKey}'. " +
                "Use HasFinalViewMemberName for view members.");
        }

        return _decisions.ContainsKey((stableId, scope.ScopeKey));
    }

    /// <summary>
    /// Check if a member name has been reserved in the VIEW surface scope.
    /// CRITICAL: Scope must be a SURFACE scope (with #static or #instance suffix).
    /// Use ScopeFactory.ViewSurface(type, interfaceStableId, isStatic) to create the scope.
    /// </summary>
    public bool HasFinalViewMemberName(StableId stableId, TypeScope scope)
    {
        AssertMemberScope(scope);

        // Ensure this is a view scope, not a class scope
        if (scope.ScopeKey.StartsWith("type:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"HasFinalViewMemberName called with class scope '{scope.ScopeKey}'. " +
                "Use HasFinalMemberName for class members.");
        }

        return _decisions.ContainsKey((stableId, scope.ScopeKey));
    }

    /// <summary>
    /// Check if a name is already reserved in a specific scope.
    /// Used for collision detection when reserving view members.
    /// </summary>
    public bool IsNameTaken(RenameScope scope, string name, bool isStatic)
    {
        // For member scopes, adjust for static/instance sub-scope
        var effectiveScope = scope is TypeScope ts
            ? ts with { IsStatic = isStatic, ScopeKey = $"{ts.ScopeKey}#{(isStatic ? "static" : "instance")}" }
            : scope;

        if (!_tablesByScope.TryGetValue(effectiveScope.ScopeKey, out var table))
            return false; // Scope doesn't exist yet - name not taken

        return table.IsReserved(name);
    }

    /// <summary>
    /// List all reserved names in a scope.
    /// Returns the actual final names from the reservation table (after suffix resolution).
    /// </summary>
    public HashSet<string> ListReservedNames(RenameScope scope, bool isStatic)
    {
        // For member scopes, adjust for static/instance sub-scope
        var effectiveScope = scope is TypeScope ts
            ? ts with { IsStatic = isStatic, ScopeKey = $"{ts.ScopeKey}#{(isStatic ? "static" : "instance")}" }
            : scope;

        if (!_tablesByScope.TryGetValue(effectiveScope.ScopeKey, out var table))
            return new HashSet<string>(StringComparer.Ordinal); // Empty set if scope doesn't exist

        return table.GetAllReservedNames();
    }

    /// <summary>
    /// Peek at what final name would be assigned in a scope without committing.
    /// Used for collision detection before reservation.
    /// Applies style transform and sanitization, then finds next available suffix if needed.
    /// </summary>
    public string PeekFinalMemberName(RenameScope scope, string requestedBase, bool isStatic)
    {
        if (_styleTransform == null)
        {
            throw new InvalidOperationException(
                "PeekFinalMemberName called before style transform was set. " +
                "Ensure AdoptStyleTransform is called during context initialization.");
        }

        // Create effective scope for static/instance
        var effectiveScope = scope is TypeScope ts
            ? ts with { IsStatic = isStatic, ScopeKey = $"{ts.ScopeKey}#{(isStatic ? "static" : "instance")}" }
            : scope;

        // Apply transforms like in ResolveNameWithConflicts
        var styled = _styleTransform.Invoke(requestedBase);
        var sanitized = TypeScriptReservedWords.Sanitize(styled).Sanitized;

        if (!_tablesByScope.TryGetValue(effectiveScope.ScopeKey, out var table))
        {
            // Scope doesn't exist yet - would be first reservation
            return sanitized;
        }

        // If base name is available, return it
        if (!table.IsReserved(sanitized))
            return sanitized;

        // Otherwise, find next available suffix (without mutating)
        int suffix = 2;
        while (table.IsReserved($"{sanitized}{suffix}"))
        {
            suffix++;
            if (suffix > 1000) // Safety limit
                throw new InvalidOperationException($"Could not find available suffix for {sanitized} after 1000 attempts");
        }

        return $"{sanitized}{suffix}";
    }

    private NameReservationTable GetOrCreateTable(RenameScope scope)
    {
        var key = scope.ScopeKey;
        if (!_tablesByScope.TryGetValue(key, out var table))
        {
            table = new NameReservationTable();
            _tablesByScope[key] = table;
        }
        return table;
    }

    private string ResolveNameWithConflicts(
        StableId stableId,
        string requested,
        NameReservationTable table,
        RenameScope scope,
        string reason,
        string decisionSource,
        bool? isStatic)
    {
        // 1. Check for explicit override
        if (_explicitOverrides.TryGetValue(stableId, out var explicitName))
        {
            if (table.TryReserve(explicitName, stableId))
                return explicitName;
            // Explicit override conflicts - fall through to suffix strategy
        }

        // 2. Apply style transform if set
        var styled = _styleTransform?.Invoke(requested) ?? requested;

        // 3. Sanitize TypeScript reserved words (add trailing underscore if needed)
        var sanitized = TypeScriptReservedWords.Sanitize(styled).Sanitized;

        // 4. Try to reserve the sanitized name
        if (table.TryReserve(sanitized, stableId))
            return sanitized;

        // 5. Conflict detected - check if this is an explicit interface implementation
        if (stableId is MemberStableId memberStableId && memberStableId.MemberName.Contains('.'))
        {
            // Explicit interface implementation: extract interface short name
            // Example: "System.Collections.ICollection.SyncRoot" -> "ICollection"
            var qualifiedName = memberStableId.MemberName;
            var lastDot = qualifiedName.LastIndexOf('.');
            if (lastDot > 0)
            {
                var beforeLastDot = qualifiedName[..lastDot];
                var interfaceShortName = beforeLastDot.Split('.').Last();

                // Try: <base>_<ifaceShortName>
                var interfaceSuffixed = $"{sanitized}_{interfaceShortName}";
                if (table.TryReserve(interfaceSuffixed, stableId))
                    return interfaceSuffixed;

                // Still conflicts - fall through to numeric suffix on the interface-suffixed name
                var baseName = interfaceSuffixed;
                var suffix = table.AllocateNextSuffix(baseName);
                var candidate = $"{baseName}{suffix}";

                while (!table.TryReserve(candidate, stableId))
                {
                    suffix = table.AllocateNextSuffix(baseName);
                    candidate = $"{baseName}{suffix}";
                }

                return candidate;
            }
        }

        // 6. Not an explicit interface impl - apply standard numeric suffix strategy
        var defaultBaseName = sanitized;
        var defaultSuffix = table.AllocateNextSuffix(defaultBaseName);
        var defaultCandidate = $"{defaultBaseName}{defaultSuffix}";

        // Keep trying until we find an available name
        while (!table.TryReserve(defaultCandidate, stableId))
        {
            defaultSuffix = table.AllocateNextSuffix(defaultBaseName);
            defaultCandidate = $"{defaultBaseName}{defaultSuffix}";
        }

        return defaultCandidate;
    }

    private void RecordDecision(RenameDecision decision)
    {
        // M5 FIX: Key by (StableId, ScopeKey) to support dual-scope reservations
        _decisions[(decision.Id, decision.ScopeKey)] = decision;
    }

    private string ExtractOriginalName(string requested)
    {
        // Remove common suffixes to get original name
        if (requested.EndsWith("_new"))
            return requested[..^4];

        // Remove numeric suffixes
        var lastNonDigit = requested.Length - 1;
        while (lastNonDigit >= 0 && char.IsDigit(requested[lastNonDigit]))
            lastNonDigit--;

        if (lastNonDigit < requested.Length - 1)
            return requested[..(lastNonDigit + 1)];

        return requested;
    }

    // ============================================================================
    // SCOPE VALIDATION (catch scope misuse immediately - always enabled)
    // ============================================================================

    private static void AssertNamespaceScope(NamespaceScope scope)
    {
        if (string.IsNullOrWhiteSpace(scope.ScopeKey) || !scope.ScopeKey.StartsWith("ns:", StringComparison.Ordinal))
            throw new InvalidOperationException($"Invalid NamespaceScope '{scope.ScopeKey}' - must start with 'ns:'");
    }

    private static void AssertMemberScope(TypeScope scope)
    {
        var s = scope.ScopeKey;
        var ok = s.StartsWith("type:", StringComparison.Ordinal) || s.StartsWith("view:", StringComparison.Ordinal);
        var hasSide = s.EndsWith("#instance", StringComparison.Ordinal) || s.EndsWith("#static", StringComparison.Ordinal);

        if (!ok || !hasSide)
            throw new InvalidOperationException(
                $"Invalid member scope '{s}' - must be 'type:...' or 'view:...' and end with '#instance' or '#static'");
    }
}
