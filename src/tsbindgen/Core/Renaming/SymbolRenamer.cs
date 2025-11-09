namespace tsbindgen.Core.Renaming;

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
    private readonly Dictionary<StableId, RenameDecision> _decisions = new();
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
    /// Get the final TypeScript name for a type.
    /// </summary>
    public string GetFinalTypeName(StableId stableId, RenameScope scope)
    {
        if (_decisions.TryGetValue(stableId, out var decision))
            return decision.Final;

        throw new InvalidOperationException(
            $"No rename decision found for {stableId} in scope {scope.ScopeKey}. " +
            "Did you forget to reserve this name?");
    }

    /// <summary>
    /// Get the final TypeScript name for a member.
    /// </summary>
    public string GetFinalMemberName(StableId stableId, RenameScope scope, bool isStatic)
    {
        if (_decisions.TryGetValue(stableId, out var decision))
            return decision.Final;

        throw new InvalidOperationException(
            $"No rename decision found for {stableId} ({(isStatic ? "static" : "instance")}) " +
            $"in scope {scope.ScopeKey}. Did you forget to reserve this name?");
    }

    /// <summary>
    /// Try to get the rename decision for a StableId (for emitters/bindings).
    /// </summary>
    public bool TryGetDecision(StableId stableId, out RenameDecision? decision) =>
        _decisions.TryGetValue(stableId, out decision);

    /// <summary>
    /// Get all rename decisions (for metadata/bindings emission).
    /// </summary>
    public IReadOnlyCollection<RenameDecision> GetAllDecisions() => _decisions.Values;

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

        // 3. Try to reserve the styled name
        if (table.TryReserve(styled, stableId))
            return styled;

        // 4. Conflict detected - check if this is an explicit interface implementation
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
                var interfaceSuffixed = $"{styled}_{interfaceShortName}";
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

        // 5. Not an explicit interface impl - apply standard numeric suffix strategy
        var defaultBaseName = styled;
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
        _decisions[decision.Id] = decision;
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
}
