using System.Text;

namespace GenerateDts.Emit.Writers;

/// <summary>
/// Generates the _intrinsics.d.ts file containing branded numeric types and helper types.
/// </summary>
public static class IntrinsicsWriter
{
    /// <summary>
    /// Generates the content for the _intrinsics.d.ts file containing branded numeric types.
    /// This file should be created once in the output directory and referenced by all other declarations.
    /// </summary>
    public static string RenderIntrinsics()
    {
        var sb = new StringBuilder();

        sb.AppendLine("// Intrinsic type definitions for .NET numeric types");
        sb.AppendLine("// This file provides branded numeric type aliases used across all BCL declarations.");
        sb.AppendLine("// ESM module exports for full module support.");
        sb.AppendLine();
        sb.AppendLine("// Branded numeric types");
        sb.AppendLine("export type int = number & { __brand: \"int\" };");
        sb.AppendLine("export type uint = number & { __brand: \"uint\" };");
        sb.AppendLine("export type byte = number & { __brand: \"byte\" };");
        sb.AppendLine("export type sbyte = number & { __brand: \"sbyte\" };");
        sb.AppendLine("export type short = number & { __brand: \"short\" };");
        sb.AppendLine("export type ushort = number & { __brand: \"ushort\" };");
        sb.AppendLine("export type long = number & { __brand: \"long\" };");
        sb.AppendLine("export type ulong = number & { __brand: \"ulong\" };");
        sb.AppendLine("export type float = number & { __brand: \"float\" };");
        sb.AppendLine("export type double = number & { __brand: \"double\" };");
        sb.AppendLine("export type decimal = number & { __brand: \"decimal\" };");
        sb.AppendLine();
        sb.AppendLine("// Phase 8B: Covariance helper for property type variance");
        sb.AppendLine("// Allows derived types to return more specific types than base/interface contracts");
        sb.AppendLine("export type Covariant<TSpecific, TContract> = TSpecific & { readonly __contract?: TContract };");

        return sb.ToString();
    }
}
