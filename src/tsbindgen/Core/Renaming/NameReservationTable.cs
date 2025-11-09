namespace tsbindgen.Core.Renaming;

/// <summary>
/// Internal structure for tracking name reservations within a scope.
/// Manages collision detection and numeric suffix allocation.
/// </summary>
public sealed class NameReservationTable
{
    private readonly Dictionary<string, StableId> _finalNameToId = new();
    private readonly Dictionary<string, int> _nextSuffixByBase = new();

    /// <summary>
    /// Check if a name is already reserved in this scope.
    /// </summary>
    public bool IsReserved(string finalName) => _finalNameToId.ContainsKey(finalName);

    /// <summary>
    /// Get the StableId that owns a reserved name, or null if not reserved.
    /// </summary>
    public StableId? GetOwner(string finalName) =>
        _finalNameToId.TryGetValue(finalName, out var id) ? id : null;

    /// <summary>
    /// Reserve a name for a StableId. Returns true if successful, false if already taken.
    /// If the same StableId tries to reserve the same name again, returns true (idempotent).
    /// </summary>
    public bool TryReserve(string finalName, StableId id)
    {
        if (IsReserved(finalName))
        {
            // Allow re-reservation if it's the same StableId (idempotent)
            var currentOwner = _finalNameToId[finalName];
            if (currentOwner.Equals(id))
                return true;

            return false; // Different owner - conflict
        }

        _finalNameToId[finalName] = id;
        return true;
    }

    /// <summary>
    /// Allocate the next numeric suffix for a base name.
    /// First call for "compare" returns 2, second returns 3, etc.
    /// </summary>
    public int AllocateNextSuffix(string baseName)
    {
        if (!_nextSuffixByBase.TryGetValue(baseName, out var current))
        {
            current = 2; // Start at 2 (base name is implicitly "1")
        }

        _nextSuffixByBase[baseName] = current + 1;
        return current;
    }

    /// <summary>
    /// Get all reserved names (for debugging/diagnostics).
    /// </summary>
    public IEnumerable<string> GetReservedNames() => _finalNameToId.Keys;

    /// <summary>
    /// Get the count of reserved names.
    /// </summary>
    public int Count => _finalNameToId.Count;
}
