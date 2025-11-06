using System.Text;
using tsbindgen.Snapshot;

namespace tsbindgen.Config;

/// <summary>
/// Single source of truth for TypeScript naming.
/// All name transformations happen here - no heuristics in emitters.
/// </summary>
public static class TsNaming
{
    /// <summary>
    /// Generates TypeScript alias for Phase 3 analysis.
    /// Uses underscore for both nesting and arity: "Console_Error_1"
    /// </summary>
    public static string Phase3Alias(in ClrPath path)
    {
        if (path.Segments.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        for (int i = 0; i < path.Segments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('_');
            }

            var segment = path.Segments[i];
            sb.Append(segment.Identifier);

            if (segment.GenericArity > 0)
            {
                sb.Append('_');
                sb.Append(segment.GenericArity);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates TypeScript emit name for Phase 4 rendering.
    /// Uses dollar sign for nesting, underscore for arity: "Console$Error_1"
    /// </summary>
    public static string Phase4EmitName(in ClrPath path)
    {
        if (path.Segments.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        for (int i = 0; i < path.Segments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('$');
            }

            var segment = path.Segments[i];
            sb.Append(segment.Identifier);

            if (segment.GenericArity > 0)
            {
                sb.Append('_');
                sb.Append(segment.GenericArity);
            }
        }

        return sb.ToString();
    }
}
