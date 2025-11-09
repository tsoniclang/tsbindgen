using System.Text;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Renaming;

namespace tsbindgen.SinglePhase.Emit.Printers;

/// <summary>
/// Prints TypeScript class declarations from TypeSymbol.
/// Handles classes, structs, static classes, enums, and delegates.
/// </summary>
public static class ClassPrinter
{
    /// <summary>
    /// Print a complete class declaration.
    /// </summary>
    public static string Print(TypeSymbol type, BuildContext ctx)
    {
        return type.Kind switch
        {
            TypeKind.Class => PrintClass(type, ctx),
            TypeKind.Struct => PrintStruct(type, ctx),
            TypeKind.StaticNamespace => PrintStaticClass(type, ctx),
            TypeKind.Enum => PrintEnum(type, ctx),
            TypeKind.Delegate => PrintDelegate(type, ctx),
            TypeKind.Interface => PrintInterface(type, ctx),
            _ => $"// Unknown type kind: {type.Kind}"
        };
    }

    /// <summary>
    /// Print class/struct with $instance suffix (for companion views pattern).
    /// Used when type has explicit interface views that will be in separate companion interface.
    /// </summary>
    public static string PrintInstance(TypeSymbol type, BuildContext ctx)
    {
        return type.Kind switch
        {
            TypeKind.Class => PrintClass(type, ctx, instanceSuffix: true),
            TypeKind.Struct => PrintStruct(type, ctx, instanceSuffix: true),
            _ => Print(type, ctx) // Fallback
        };
    }

    private static string PrintClass(TypeSymbol type, BuildContext ctx, bool instanceSuffix = false)
    {
        var sb = new StringBuilder();

        // Get final TypeScript name from Renamer
        var nsScope = ScopeFactory.NamespaceInternal(type.Namespace);
        var finalName = ctx.Renamer.GetFinalTypeName(type.StableId, nsScope);

        // Add $instance suffix if requested (for companion views pattern)
        if (instanceSuffix)
            finalName += "$instance";

        // Class modifiers and declaration
        if (type.IsAbstract)
            sb.Append("abstract ");

        sb.Append("class ");
        sb.Append(finalName);

        // Generic parameters: class Foo<T, U>
        if (type.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp => PrintGenericParameter(gp, ctx))));
            sb.Append('>');
        }

        // Base class: extends BaseClass
        if (type.BaseType != null)
        {
            var baseTypeName = TypeRefPrinter.Print(type.BaseType, ctx);
            // Skip System.Object and System.ValueType
            if (baseTypeName != "Object" && baseTypeName != "ValueType")
            {
                sb.Append(" extends ");
                sb.Append(baseTypeName);
            }
        }

        // Interfaces: implements IFoo, IBar
        if (type.Interfaces.Length > 0)
        {
            sb.Append(" implements ");
            sb.Append(string.Join(", ", type.Interfaces.Select(i => TypeRefPrinter.Print(i, ctx))));
        }

        sb.AppendLine(" {");

        // Emit members
        EmitMembers(sb, type, ctx);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string PrintStruct(TypeSymbol type, BuildContext ctx, bool instanceSuffix = false)
    {
        // Structs emit as classes in TypeScript (with metadata noting value semantics)
        var sb = new StringBuilder();

        var nsScope = ScopeFactory.NamespaceInternal(type.Namespace);
        var finalName = ctx.Renamer.GetFinalTypeName(type.StableId, nsScope);

        // Add $instance suffix if requested (for companion views pattern)
        if (instanceSuffix)
            finalName += "$instance";

        sb.Append("class ");
        sb.Append(finalName);

        // Generic parameters
        if (type.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp => PrintGenericParameter(gp, ctx))));
            sb.Append('>');
        }

        // Interfaces
        if (type.Interfaces.Length > 0)
        {
            sb.Append(" implements ");
            sb.Append(string.Join(", ", type.Interfaces.Select(i => TypeRefPrinter.Print(i, ctx))));
        }

        sb.AppendLine(" {");

        // Emit members
        EmitMembers(sb, type, ctx);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string PrintStaticClass(TypeSymbol type, BuildContext ctx)
    {
        // Static classes emit as abstract classes with static members in TypeScript
        var sb = new StringBuilder();

        var nsScope = ScopeFactory.NamespaceInternal(type.Namespace);
        var finalName = ctx.Renamer.GetFinalTypeName(type.StableId, nsScope);

        sb.Append("abstract class ");
        sb.Append(finalName);
        sb.AppendLine(" {");

        // Emit static members only
        EmitStaticMembers(sb, type, ctx);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string PrintEnum(TypeSymbol type, BuildContext ctx)
    {
        var sb = new StringBuilder();

        var nsScope = ScopeFactory.NamespaceInternal(type.Namespace);
        var finalName = ctx.Renamer.GetFinalTypeName(type.StableId, nsScope);

        sb.Append("enum ");
        sb.Append(finalName);
        sb.AppendLine(" {");

        // Create type scope for enum member name resolution
        var typeScope = ScopeFactory.ClassStatic(type); // Enum members are like static fields

        // Emit enum fields
        var fields = type.Members.Fields.Where(f => f.IsConst).ToList();
        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var memberFinalName = ctx.Renamer.GetFinalMemberName(field.StableId, typeScope, isStatic: true);
            sb.Append("    ");
            sb.Append(memberFinalName);

            if (field.ConstValue != null)
            {
                sb.Append(" = ");
                sb.Append(field.ConstValue);
            }

            if (i < fields.Count - 1)
                sb.Append(',');

            sb.AppendLine();
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string PrintDelegate(TypeSymbol type, BuildContext ctx)
    {
        // Delegates emit as type aliases to function signatures
        var sb = new StringBuilder();

        var nsScope = ScopeFactory.NamespaceInternal(type.Namespace);
        var finalName = ctx.Renamer.GetFinalTypeName(type.StableId, nsScope);

        sb.Append("type ");
        sb.Append(finalName);

        // Generic parameters
        if (type.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp => PrintGenericParameter(gp, ctx))));
            sb.Append('>');
        }

        sb.Append(" = ");

        // Find Invoke method
        var invokeMethod = type.Members.Methods.FirstOrDefault(m => m.ClrName == "Invoke");
        if (invokeMethod != null)
        {
            // Emit function signature: (a: int, b: string) => void
            sb.Append('(');
            sb.Append(string.Join(", ", invokeMethod.Parameters.Select(p => $"{p.Name}: {TypeRefPrinter.Print(p.Type, ctx)}")));
            sb.Append(") => ");
            sb.Append(TypeRefPrinter.Print(invokeMethod.ReturnType, ctx));
        }
        else
        {
            sb.Append("Function"); // Fallback
        }

        sb.AppendLine(";");

        return sb.ToString();
    }

    private static string PrintInterface(TypeSymbol type, BuildContext ctx)
    {
        var sb = new StringBuilder();

        var nsScope = ScopeFactory.NamespaceInternal(type.Namespace);
        var finalName = ctx.Renamer.GetFinalTypeName(type.StableId, nsScope);

        sb.Append("interface ");
        sb.Append(finalName);

        // Generic parameters
        if (type.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp => PrintGenericParameter(gp, ctx))));
            sb.Append('>');
        }

        // Base interfaces: extends IFoo, IBar
        if (type.Interfaces.Length > 0)
        {
            sb.Append(" extends ");
            sb.Append(string.Join(", ", type.Interfaces.Select(i => TypeRefPrinter.Print(i, ctx))));
        }

        sb.AppendLine(" {");

        // Emit members (interfaces only have instance members)
        EmitInterfaceMembers(sb, type, ctx);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void EmitMembers(StringBuilder sb, TypeSymbol type, BuildContext ctx)
    {
        var members = type.Members;

        // Create type scope for member name resolution
        var typeScope = ScopeFactory.ClassInstance(type); // Instance members

        // Constructors
        foreach (var ctor in members.Constructors.Where(c => !c.IsStatic))
        {
            sb.Append("    constructor(");
            sb.Append(string.Join(", ", ctor.Parameters.Select(p => $"{p.Name}: {TypeRefPrinter.Print(p.Type, ctx)}")));
            sb.AppendLine(");");
        }

        // Fields
        foreach (var field in members.Fields.Where(f => !f.IsStatic))
        {
            var finalName = ctx.Renamer.GetFinalMemberName(field.StableId, typeScope, isStatic: false);
            sb.Append("    ");
            if (field.IsReadOnly)
                sb.Append("readonly ");
            sb.Append(finalName);
            sb.Append(": ");
            sb.Append(TypeRefPrinter.Print(field.FieldType, ctx));
            sb.AppendLine(";");
        }

        // Properties
        foreach (var prop in members.Properties.Where(p => !p.IsStatic))
        {
            var finalName = ctx.Renamer.GetFinalMemberName(prop.StableId, typeScope, isStatic: false);
            sb.Append("    ");
            if (!prop.HasSetter)
                sb.Append("readonly ");
            sb.Append(finalName);
            sb.Append(": ");
            sb.Append(TypeRefPrinter.Print(prop.PropertyType, ctx));
            sb.AppendLine(";");
        }

        // Methods
        foreach (var method in members.Methods.Where(m => !m.IsStatic))
        {
            sb.Append("    ");
            sb.Append(MethodPrinter.Print(method, type, ctx));
            sb.AppendLine(";");
        }

        // Static members
        EmitStaticMembers(sb, type, ctx);
    }

    private static void EmitStaticMembers(StringBuilder sb, TypeSymbol type, BuildContext ctx)
    {
        var members = type.Members;

        // Create type scope for static member name resolution
        var staticTypeScope = ScopeFactory.ClassStatic(type); // Static members

        // Static fields
        foreach (var field in members.Fields.Where(f => f.IsStatic && !f.IsConst))
        {
            var finalName = ctx.Renamer.GetFinalMemberName(field.StableId, staticTypeScope, isStatic: true);
            sb.Append("    static ");
            if (field.IsReadOnly)
                sb.Append("readonly ");
            sb.Append(finalName);
            sb.Append(": ");
            sb.Append(TypeRefPrinter.Print(field.FieldType, ctx));
            sb.AppendLine(";");
        }

        // Const fields (as static readonly)
        foreach (var field in members.Fields.Where(f => f.IsConst))
        {
            var finalName = ctx.Renamer.GetFinalMemberName(field.StableId, staticTypeScope, isStatic: true);
            sb.Append("    static readonly ");
            sb.Append(finalName);
            sb.Append(": ");
            sb.Append(TypeRefPrinter.Print(field.FieldType, ctx));
            sb.AppendLine(";");
        }

        // Static properties
        foreach (var prop in members.Properties.Where(p => p.IsStatic))
        {
            var finalName = ctx.Renamer.GetFinalMemberName(prop.StableId, staticTypeScope, isStatic: true);
            sb.Append("    static ");
            if (!prop.HasSetter)
                sb.Append("readonly ");
            sb.Append(finalName);
            sb.Append(": ");
            sb.Append(TypeRefPrinter.Print(prop.PropertyType, ctx));
            sb.AppendLine(";");
        }

        // Static methods
        foreach (var method in members.Methods.Where(m => m.IsStatic))
        {
            sb.Append("    ");
            sb.Append(MethodPrinter.Print(method, type, ctx));
            sb.AppendLine(";");
        }
    }

    private static void EmitInterfaceMembers(StringBuilder sb, TypeSymbol type, BuildContext ctx)
    {
        var members = type.Members;

        // Create type scope for member name resolution (interfaces don't have static members)
        var typeScope = ScopeFactory.ClassInstance(type);

        // Properties
        foreach (var prop in members.Properties)
        {
            var finalName = ctx.Renamer.GetFinalMemberName(prop.StableId, typeScope, isStatic: false);
            sb.Append("    ");
            if (!prop.HasSetter)
                sb.Append("readonly ");
            sb.Append(finalName);
            sb.Append(": ");
            sb.Append(TypeRefPrinter.Print(prop.PropertyType, ctx));
            sb.AppendLine(";");
        }

        // Methods
        foreach (var method in members.Methods)
        {
            sb.Append("    ");
            sb.Append(MethodPrinter.Print(method, type, ctx));
            sb.AppendLine(";");
        }
    }

    private static string PrintGenericParameter(GenericParameterSymbol gp, BuildContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append(gp.Name);

        // Constraints from IReadOnlyList<TypeReference>
        if (gp.Constraints.Length > 0)
        {
            sb.Append(" extends ");

            if (gp.Constraints.Length == 1)
            {
                sb.Append(TypeRefPrinter.Print(gp.Constraints[0], ctx));
            }
            else
            {
                // Multiple constraints: T extends IFoo & IBar
                var constraints = gp.Constraints.Select(c => TypeRefPrinter.Print(c, ctx));
                sb.Append(string.Join(" & ", constraints));
            }
        }

        return sb.ToString();
    }
}
