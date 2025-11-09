using System.Collections.Generic;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Builds cross-assembly interface index.
/// Creates a global index of all public interfaces across assemblies for:
/// - Resolving type-forwarded interfaces
/// - Structural conformance checking
/// - Interface member resolution
/// </summary>
public static class GlobalInterfaceIndex
{
    /// <summary>
    /// Global interface index keyed by full CLR name.
    /// </summary>
    private static Dictionary<string, InterfaceInfo> _globalIndex = new();

    public static void Build(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("GlobalInterfaceIndex", "Building global interface index...");

        // Clear any previous index
        _globalIndex.Clear();

        var allInterfaces = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Interface)
            .ToList();

        ctx.Log("GlobalInterfaceIndex", $"Indexing {allInterfaces.Count} interfaces");

        foreach (var iface in allInterfaces)
        {
            var info = new InterfaceInfo(
                Symbol: iface,
                FullName: iface.ClrFullName,
                AssemblyName: iface.StableId.AssemblyName,
                MethodSignatures: ComputeMethodSignatures(ctx, iface),
                PropertySignatures: ComputePropertySignatures(ctx, iface));

            _globalIndex[iface.ClrFullName] = info;
        }

        ctx.Log("GlobalInterfaceIndex", $"Indexed {_globalIndex.Count} interfaces");
    }

    /// <summary>
    /// Get interface information by full CLR name.
    /// </summary>
    public static InterfaceInfo? GetInterface(string fullName)
    {
        _globalIndex.TryGetValue(fullName, out var info);
        return info;
    }

    /// <summary>
    /// Check if an interface exists in the index.
    /// </summary>
    public static bool ContainsInterface(string fullName)
    {
        return _globalIndex.ContainsKey(fullName);
    }

    /// <summary>
    /// Get all interfaces in the index.
    /// </summary>
    public static IEnumerable<InterfaceInfo> GetAllInterfaces()
    {
        return _globalIndex.Values;
    }

    private static HashSet<string> ComputeMethodSignatures(BuildContext ctx, TypeSymbol iface)
    {
        var signatures = new HashSet<string>();

        foreach (var method in iface.Members.Methods)
        {
            var sig = ctx.CanonicalizeMethod(
                method.ClrName,
                method.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                GetTypeFullName(method.ReturnType));

            signatures.Add(sig);
        }

        return signatures;
    }

    private static HashSet<string> ComputePropertySignatures(BuildContext ctx, TypeSymbol iface)
    {
        var signatures = new HashSet<string>();

        foreach (var property in iface.Members.Properties)
        {
            var indexParams = property.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();

            var sig = ctx.CanonicalizeProperty(
                property.ClrName,
                indexParams,
                GetTypeFullName(property.PropertyType));

            signatures.Add(sig);
        }

        return signatures;
    }

    private static string GetTypeFullName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            Model.Types.GenericParameterReference gp => gp.Name,
            Model.Types.ArrayTypeReference arr => $"{GetTypeFullName(arr.ElementType)}[]",
            Model.Types.PointerTypeReference ptr => $"{GetTypeFullName(ptr.PointeeType)}*",
            Model.Types.ByRefTypeReference byref => $"{GetTypeFullName(byref.ReferencedType)}&",
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    /// <summary>
    /// Information about an indexed interface.
    /// </summary>
    public record InterfaceInfo(
        TypeSymbol Symbol,
        string FullName,
        string AssemblyName,
        HashSet<string> MethodSignatures,
        HashSet<string> PropertySignatures);
}

/// <summary>
/// Index of interface members that are DECLARED (not inherited).
/// Used to resolve which interface actually declares a member when walking inheritance chains.
/// </summary>
public static class InterfaceDeclIndex
{
    /// <summary>
    /// Map of interface CLR full name -> set of canonical member signatures declared ONLY on that interface.
    /// Excludes inherited members.
    /// </summary>
    private static Dictionary<string, DeclaredMembers> _declIndex = new();

    public static void Build(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("InterfaceDeclIndex", "Building declares-only interface index...");

        // Clear any previous index
        _declIndex.Clear();

        var allInterfaces = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Interface)
            .ToList();

        ctx.Log("InterfaceDeclIndex", $"Indexing {allInterfaces.Count} interfaces");

        foreach (var iface in allInterfaces)
        {
            // Collect base interface signatures to exclude inherited members
            var inheritedSignatures = CollectInheritedSignatures(iface);

            // Compute declared-only signatures
            var declaredMethods = new HashSet<string>();
            foreach (var method in iface.Members.Methods)
            {
                var sig = ctx.CanonicalizeMethod(
                    method.ClrName,
                    method.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                    GetTypeFullName(method.ReturnType));

                // Only include if not inherited
                if (!inheritedSignatures.Contains(sig))
                {
                    declaredMethods.Add(sig);
                }
            }

            var declaredProperties = new HashSet<string>();
            foreach (var property in iface.Members.Properties)
            {
                var indexParams = property.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();
                var sig = ctx.CanonicalizeProperty(
                    property.ClrName,
                    indexParams,
                    GetTypeFullName(property.PropertyType));

                // Only include if not inherited
                if (!inheritedSignatures.Contains(sig))
                {
                    declaredProperties.Add(sig);
                }
            }

            var declMembers = new DeclaredMembers(
                InterfaceFullName: iface.ClrFullName,
                MethodSignatures: declaredMethods,
                PropertySignatures: declaredProperties);

            _declIndex[iface.ClrFullName] = declMembers;
        }

        ctx.Log("InterfaceDeclIndex", $"Indexed {_declIndex.Count} interfaces with declared-only members");
    }

    /// <summary>
    /// Get declared-only members for an interface.
    /// </summary>
    public static DeclaredMembers? GetDeclaredMembers(string ifaceFullName)
    {
        _declIndex.TryGetValue(ifaceFullName, out var decl);
        return decl;
    }

    /// <summary>
    /// Check if an interface declares a specific method signature.
    /// </summary>
    public static bool DeclaresMethod(string ifaceFullName, string canonicalSig)
    {
        if (_declIndex.TryGetValue(ifaceFullName, out var decl))
        {
            return decl.MethodSignatures.Contains(canonicalSig);
        }
        return false;
    }

    /// <summary>
    /// Check if an interface declares a specific property signature.
    /// </summary>
    public static bool DeclaresProperty(string ifaceFullName, string canonicalSig)
    {
        if (_declIndex.TryGetValue(ifaceFullName, out var decl))
        {
            return decl.PropertySignatures.Contains(canonicalSig);
        }
        return false;
    }

    private static HashSet<string> CollectInheritedSignatures(TypeSymbol iface)
    {
        var inherited = new HashSet<string>();

        // Walk all base interfaces and collect their members
        foreach (var baseIfaceRef in iface.Interfaces)
        {
            var baseIfaceName = GetTypeRefFullName(baseIfaceRef);
            var baseInfo = GlobalInterfaceIndex.GetInterface(baseIfaceName);

            if (baseInfo != null)
            {
                // Add all base interface signatures (including what it inherited)
                foreach (var sig in baseInfo.MethodSignatures)
                {
                    inherited.Add(sig);
                }
                foreach (var sig in baseInfo.PropertySignatures)
                {
                    inherited.Add(sig);
                }
            }
        }

        return inherited;
    }

    private static string GetTypeRefFullName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            Model.Types.GenericParameterReference gp => gp.Name,
            Model.Types.ArrayTypeReference arr => $"{GetTypeRefFullName(arr.ElementType)}[]",
            Model.Types.PointerTypeReference ptr => $"{GetTypeRefFullName(ptr.PointeeType)}*",
            Model.Types.ByRefTypeReference byref => $"{GetTypeRefFullName(byref.ReferencedType)}&",
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static string GetTypeFullName(Model.Types.TypeReference typeRef)
    {
        return GetTypeRefFullName(typeRef);
    }

    /// <summary>
    /// Declared members for an interface (excludes inherited).
    /// </summary>
    public record DeclaredMembers(
        string InterfaceFullName,
        HashSet<string> MethodSignatures,
        HashSet<string> PropertySignatures);
}
