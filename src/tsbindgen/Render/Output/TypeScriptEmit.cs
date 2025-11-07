using System.Text;
using tsbindgen.Config;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Output;

/// <summary>
/// Emits TypeScript declaration files (.d.ts) from NamespaceModel.
/// </summary>
public static class TypeScriptEmit
{
    // Track bindings during emit for later generation
    private static Dictionary<string, TypeBindings> _bindingsMap = new();

    // Store all namespace models for cross-namespace lookups
    private static IReadOnlyDictionary<string, NamespaceModel> _allModels = new Dictionary<string, NamespaceModel>();

    // Track all type names defined in current namespace (for filtering undefined implements/extends)
    private static HashSet<string> _definedTypes = new();

    // Analysis context for name computation
    private static AnalysisContext _ctx = null!;

    public static string Emit(NamespaceModel model, IReadOnlyDictionary<string, NamespaceModel> allModels, AnalysisContext ctx)
    {
        // Reset bindings for this namespace
        _bindingsMap = new Dictionary<string, TypeBindings>();

        // Reset type tracking
        _definedTypes = new HashSet<string>();

        // Store all models for cross-namespace interface lookups
        _allModels = allModels;

        // Store context for name computation
        _ctx = ctx;

        var builder = new StringBuilder();

        // Header comment
        builder.AppendLine($"// Module for {model.ClrName}");
        builder.AppendLine($"// Generated from {model.SourceAssemblies.Count} assembly(ies)");
        builder.AppendLine();

        // Kind branding types for Tsonic compatibility
        EmitKindBrandingTypes(builder);

        // Imports - collect all unique namespaces from all assemblies
        if (model.Imports.Count > 0)
        {
            var allNamespaces = model.Imports
                .SelectMany(kvp => kvp.Value)
                .Where(ns => ns != model.ClrName) // Skip self-references
                .Distinct()
                .OrderBy(ns => ns);

            foreach (var ns in allNamespaces)
            {
                var nsAlias = ns.Replace(".", "$");
                builder.AppendLine($"import type * as {nsAlias} from \"../../{ns}/internal/index.js\";");
            }
            builder.AppendLine();
        }

        // Helper declarations first - export them directly
        foreach (var type in model.Types)
        {
            foreach (var helper in type.Helpers)
            {
                builder.AppendLine($"export {helper.TsDefinition}");
                builder.AppendLine();
            }
        }

        // Track all defined types for filtering implements/extends
        foreach (var type in model.Types)
        {
            var tsTypeName = ToTypeScriptType(type.Binding.Type, model.ClrName, includeNamespacePrefix: false, includeGenericArgs: false);
            _definedTypes.Add(tsTypeName);
        }

        // Types - export each type directly
        foreach (var type in model.Types)
        {
            EmitType(builder, type, "", model.ClrName, model);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Emits kind branding type definitions for Tsonic compatibility.
    /// These types enable structural typing for .NET value types.
    /// </summary>
    private static void EmitKindBrandingTypes(StringBuilder builder)
    {
        builder.AppendLine("// .NET kind markers for Tsonic compatibility");
        builder.AppendLine("export interface struct { readonly __brand: \"struct\"; }");
        builder.AppendLine();
    }

    private static void EmitType(StringBuilder builder, TypeModel type, string indent, string currentNamespace, NamespaceModel namespaceModel)
    {
        switch (type.Kind)
        {
            case TypeKind.Enum:
                EmitEnum(builder, type, indent, currentNamespace);
                break;
            case TypeKind.Interface:
                EmitInterface(builder, type, indent, currentNamespace);
                break;
            case TypeKind.Class:
            case TypeKind.Struct:
                EmitClass(builder, type, indent, currentNamespace, namespaceModel);
                break;
            case TypeKind.Delegate:
                EmitDelegate(builder, type, indent, currentNamespace);
                break;
            case TypeKind.StaticNamespace:
                EmitStaticNamespace(builder, type, indent, currentNamespace);
                break;
        }

        builder.AppendLine();
    }

    private static void EmitEnum(StringBuilder builder, TypeModel type, string indent, string currentNamespace)
    {
        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);

        // Emit as branded number type for Tsonic compatibility
        // Format: type EnumName = number & System.ValueType & struct & { readonly __enum: "EnumName" }
        // Use fully qualified System.ValueType to satisfy generic constraints
        var valueTypeRef = currentNamespace == "System"
            ? "ValueType"
            : "System.ValueType";
        builder.AppendLine($"{indent}export type {typeName} = number & {valueTypeRef} & struct & {{ readonly __enum: \"{typeName}\" }};");

        // Emit namespace with enum members as const declarations (no initializers in .d.ts)
        builder.AppendLine($"{indent}export namespace {typeName} {{");

        if (type.EnumMembers != null)
        {
            foreach (var member in type.EnumMembers)
            {
                builder.AppendLine($"{indent}    export const {member.Name}: {typeName};");
            }
        }

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitInterface(StringBuilder builder, TypeModel type, string indent, string currentNamespace)
    {
        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        var genericParams = FormatGenericParameters(type.GenericParameters, currentNamespace);

        // Filter out implements that reference undefined types (internal types)
        var validImplements = type.Implements
            .Where(i => IsTypeDefinedInCurrentNamespace(i, currentNamespace))
            .ToList();

        var extends = validImplements.Count > 0
            ? " extends " + string.Join(", ", validImplements.Select(i => ToTypeScriptType(i, currentNamespace)))
            : "";

        builder.AppendLine($"{indent}export interface {typeName}{genericParams}{extends} {{");

        // Members - skip static members (TypeScript doesn't support static interface members)
        // For interfaces, emit properties as getter/setter methods
        EmitMembers(builder, type.Members, typeBindings: null, indent + "    ", skipStatic: true, currentNamespace: currentNamespace, isInterface: true, typeModel: type);

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitClass(StringBuilder builder, TypeModel type, string indent, string currentNamespace, NamespaceModel namespaceModel)
    {
        // For structs with static members, use instance/static split to avoid TS2417
        if (type.Kind == TypeKind.Struct && HasStaticMembers(type))
        {
            EmitStructWithSplit(builder, type, indent, currentNamespace, namespaceModel);
            return;
        }

        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        var genericParams = FormatGenericParameters(type.GenericParameters, currentNamespace);

        // Structs should extend ValueType for proper constraint checking
        // Unless they already have an explicit base type
        string extends;
        if (type.BaseType != null)
        {
            extends = $" extends {ToTypeScriptType(type.BaseType, currentNamespace)}";
        }
        else if (type.Kind == TypeKind.Struct)
        {
            // Use qualified name only if not in System namespace
            var valueTypeName = currentNamespace == "System" ? "ValueType" : "System.ValueType";
            extends = $" extends {valueTypeName}";
        }
        else
        {
            extends = "";
        }

        // Filter out implements that reference undefined types (internal types)
        var validImplements = type.Implements
            .Where(i => IsTypeDefinedInCurrentNamespace(i, currentNamespace))
            .ToList();

        var implements = validImplements.Count > 0
            ? " implements " + string.Join(", ", validImplements.Select(i => ToTypeScriptType(i, currentNamespace)))
            : "";

        var modifiers = type.IsAbstract ? "abstract " : "";
        builder.AppendLine($"{indent}export {modifiers}class {typeName}{genericParams}{extends}{implements} {{");

        // Initialize bindings for this type
        var typeBindings = new TypeBindings(
            type.Binding.Type.TypeName,
            typeName,
            new Dictionary<string, MemberBindingInfo>());
        _bindingsMap[typeName] = typeBindings;

        // Members - emit methods (properties become methods too)
        EmitMembers(builder, type.Members, typeBindings, indent + "    ", currentNamespace: currentNamespace, typeModel: type);

        // Emit getter/setter methods for properties to satisfy interface contracts
        // Pass the namespace model so we can look up interface types
        EmitPropertyMethods(builder, type.Members, type.Implements, namespaceModel, typeBindings, indent + "    ", currentNamespace, type);

        // Note: Missing interface members are now added in Phase 3 by ExplicitInterfaceImplementation analysis pass

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitDelegate(StringBuilder builder, TypeModel type, string indent, string currentNamespace)
    {
        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        var genericParams = FormatGenericParameters(type.GenericParameters, currentNamespace);
        var parameters = type.DelegateParameters != null
            ? string.Join(", ", type.DelegateParameters.Select(p => $"{EscapeIdentifier(p.Name)}: {ToTypeScriptType(p.Type, currentNamespace)}"))
            : "";
        var returnType = type.DelegateReturnType != null ? ToTypeScriptType(type.DelegateReturnType, currentNamespace) : "void";

        builder.AppendLine($"{indent}export type {typeName}{genericParams} = ({parameters}) => {returnType};");
    }

    private static void EmitStaticNamespace(StringBuilder builder, TypeModel type, string indent, string currentNamespace)
    {
        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        var genericParams = FormatGenericParameters(type.GenericParameters, currentNamespace);
        builder.AppendLine($"{indent}export class {typeName}{genericParams} {{");

        // Only static members
        EmitMembers(builder, type.Members, typeBindings: null, indent + "    ", staticOnly: true, currentNamespace: currentNamespace, typeModel: type);

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitMembers(StringBuilder builder, MemberCollectionModel members, TypeBindings? typeBindings, string indent, bool staticOnly = false, bool skipStatic = false, string currentNamespace = "", bool isInterface = false, TypeModel? typeModel = null)
    {
        // Constructors (if not staticOnly and not skipStatic)
        if (!staticOnly && !skipStatic)
        {
            foreach (var ctor in members.Constructors)
            {
                var parameters = string.Join(", ", ctor.Parameters.Select(p => $"{EscapeIdentifier(p.Name)}: {ToTypeScriptType(p.Type, currentNamespace)}"));
                builder.AppendLine($"{indent}constructor({parameters});");
            }
        }

        // Methods
        foreach (var method in members.Methods)
        {
            if (staticOnly && !method.IsStatic) continue;
            if (skipStatic && method.IsStatic) continue;

            // Don't emit 'static' modifier in interfaces - it's implied by context
            var modifiers = (method.IsStatic && !isInterface) ? "static " : "";

            // For static methods, check if they reference class-level type parameters
            // If so, add those type parameters to the method level
            var methodGenericParams = method.GenericParameters.ToList();
            if (method.IsStatic && typeModel != null)
            {
                // Collect all type parameters referenced in parameters and return type
                var referencedTypeParams = new HashSet<string>();
                foreach (var param in method.Parameters)
                {
                    referencedTypeParams.UnionWith(CollectTypeParameters(param.Type));
                }
                referencedTypeParams.UnionWith(CollectTypeParameters(method.ReturnType));

                // If this class is non-generic but the method references type parameters,
                // skip the method (it's from a generic base and can't be represented)
                if (typeModel.GenericParameters.Count == 0 && referencedTypeParams.Count > 0)
                {
                    continue;  // Skip methods with orphaned type parameters
                }

                // If the class is generic, check which class-level type parameters are referenced
                if (typeModel.GenericParameters.Count > 0)
                {
                    var classTypeParamNames = new HashSet<string>(typeModel.GenericParameters.Select(p => _ctx.GetGenericParameterIdentifier(p)));
                    var referencedClassTypeParams = referencedTypeParams.Where(tp => classTypeParamNames.Contains(tp)).ToList();

                    // Add referenced class type parameters to method's generic parameters (at the beginning)
                    foreach (var classTypeParam in typeModel.GenericParameters)
                    {
                        if (referencedClassTypeParams.Contains(_ctx.GetGenericParameterIdentifier(classTypeParam)))
                        {
                            // Only add if not already in method's generic parameters
                            if (!methodGenericParams.Any(mp => _ctx.GetGenericParameterIdentifier(mp) == _ctx.GetGenericParameterIdentifier(classTypeParam)))
                            {
                                methodGenericParams.Insert(0, classTypeParam);
                            }
                        }
                    }
                }
            }

            var genericParams = FormatGenericParameters(methodGenericParams, currentNamespace);
            var parameters = string.Join(", ", method.Parameters.Select(p => $"{EscapeIdentifier(p.Name)}: {ToTypeScriptType(p.Type, currentNamespace)}"));

            // Note: Base class method overloads are now added in Phase 3 by BaseClassOverloadFix analysis pass
            var methodName = _ctx.GetMethodIdentifier(method);
            builder.AppendLine($"{indent}{modifiers}{methodName}{genericParams}({parameters}): {ToTypeScriptType(method.ReturnType, currentNamespace)};");

            // Track binding
            if (typeBindings != null)
            {
                typeBindings.Members[methodName] = new MemberBindingInfo(
                    Kind: "method",
                    ClrName: method.ClrName,
                    ClrMemberType: "method");
            }
        }

        // Properties
        // For interfaces: emit as getter/setter methods
        // For classes: skip (will be emitted as methods by EmitPropertyMethods)
        if (isInterface)
        {
            foreach (var prop in members.Properties)
            {
                if (staticOnly && !prop.IsStatic) continue;
                if (skipStatic && prop.IsStatic) continue;

                // Don't emit 'static' modifier in interfaces - it's implied by context
                var modifiers = (prop.IsStatic && !isInterface) ? "static " : "";
                var propertyType = ToTypeScriptType(prop.Type, currentNamespace);

                // Use property name directly - no "get"/"set" prefix
                var methodName = _ctx.GetPropertyIdentifier(prop);

                // Emit getter
                builder.AppendLine($"{indent}{modifiers}{methodName}(): {propertyType};");

                // If not readonly, emit setter as overload of same method
                if (!prop.IsReadonly)
                {
                    var voidType = currentNamespace == "System" ? "Void" : "System.Void";
                    builder.AppendLine($"{indent}{modifiers}{methodName}(value: {propertyType}): {voidType};");
                }
            }
        }

        // Fields - convert to methods (same as properties)
        foreach (var field in members.Fields)
        {
            if (staticOnly && !field.IsStatic) continue;
            if (skipStatic && field.IsStatic) continue;

            // Don't emit 'static' modifier in interfaces - it's implied by context
            var modifiers = (field.IsStatic && !isInterface) ? "static " : "";
            var fieldType = ToTypeScriptType(field.Type, currentNamespace);
            var methodName = _ctx.GetFieldIdentifier(field);

            // For static fields that reference class type parameters, add them as method-level generics
            var methodGenericParams = new List<GenericParameterModel>();
            if (field.IsStatic && typeModel != null)
            {
                var referencedTypeParams = CollectTypeParameters(field.Type);

                // If this class is non-generic but the field references type parameters,
                // skip the field (it's from a generic base and can't be represented)
                if (typeModel.GenericParameters.Count == 0 && referencedTypeParams.Count > 0)
                {
                    continue;  // Skip fields with orphaned type parameters
                }

                // If the class is generic, add referenced type parameters to method-level generics
                if (typeModel.GenericParameters.Count > 0)
                {
                    var classTypeParamNames = new HashSet<string>(typeModel.GenericParameters.Select(p => _ctx.GetGenericParameterIdentifier(p)));

                    foreach (var classTypeParam in typeModel.GenericParameters)
                    {
                        if (referencedTypeParams.Contains(_ctx.GetGenericParameterIdentifier(classTypeParam)))
                        {
                            methodGenericParams.Add(classTypeParam);
                        }
                    }
                }
            }

            var genericParams = FormatGenericParameters(methodGenericParams, currentNamespace);

            // Emit getter
            builder.AppendLine($"{indent}{modifiers}{methodName}{genericParams}(): {fieldType};");

            // If not readonly, emit setter as overload
            if (!field.IsReadonly)
            {
                var voidType = currentNamespace == "System" ? "Void" : "System.Void";
                builder.AppendLine($"{indent}{modifiers}{methodName}{genericParams}(value: {fieldType}): {voidType};");
            }

            // Track binding
            if (typeBindings != null)
            {
                typeBindings.Members[methodName] = new MemberBindingInfo(
                    Kind: "method",
                    ClrName: field.ClrName,
                    ClrMemberType: "field",
                    Access: field.IsReadonly ? "get" : "get+set");
            }
        }

        // Events
        foreach (var evt in members.Events)
        {
            if (staticOnly && !evt.IsStatic) continue;
            if (skipStatic && evt.IsStatic) continue;

            // Don't emit 'static' modifier in interfaces - it's implied by context
            var modifiers = (evt.IsStatic && !isInterface) ? "static " : "";

            builder.AppendLine($"{indent}{modifiers}readonly {_ctx.GetEventIdentifier(evt)}: {ToTypeScriptType(evt.Type, currentNamespace)};");
        }
    }

    /// <summary>
    /// Emits getter/setter methods for properties to satisfy interface contracts.
    /// Classes need these methods to implement interfaces (which have methods, not properties).
    /// Generates overloads based on implemented interfaces.
    /// Handles name conflicts with existing methods by adding numeric suffixes.
    /// </summary>
    private static void EmitPropertyMethods(StringBuilder builder, MemberCollectionModel members, IReadOnlyList<TypeReference> implements, NamespaceModel namespaceModel, TypeBindings typeBindings, string indent, string currentNamespace, TypeModel typeModel)
    {
        // Collect existing method names to detect conflicts
        var existingMethodNames = new HashSet<string>(
            members.Methods.Select(m => _ctx.GetMethodIdentifier(m)),
            StringComparer.Ordinal);

        foreach (var prop in members.Properties)
        {
            // Skip static properties for now (interfaces don't have static members)
            if (prop.IsStatic) continue;

            var propertyType = ToTypeScriptType(prop.Type, currentNamespace);

            // Use property name directly - no "get"/"set" prefix
            // C# doesn't allow property and method with same name, so no conflicts
            var baseMethodName = _ctx.GetPropertyIdentifier(prop);

            // Resolve name conflicts (shouldn't happen, but defensive)
            var methodName = ResolveConflict(baseMethodName, existingMethodNames);

            // Collect all return types for this property from implemented interfaces (deduplicated)
            var interfaceTypes = new HashSet<string>();

            foreach (var interfaceRef in implements)
            {
                // Look up the interface type in the namespace model
                var interfaceType = FindInterfaceType(interfaceRef, namespaceModel, currentNamespace);
                if (interfaceType != null)
                {
                    // Find matching property in interface
                    var interfaceProp = interfaceType.Members.Properties
                        .FirstOrDefault(p => _ctx.SameIdentifier(p, prop));

                    if (interfaceProp != null)
                    {
                        // Check if this interface property type references type parameters not in current class scope
                        // Example: IEnumerator<T>.Current returns T, but implementing class uses IEnumerator<KeyValuePair<K,V>>
                        // We can't emit "current(): T" because T is not in scope
                        var referencedTypeParams = CollectTypeParameters(interfaceProp.Type);
                        var classTypeParams = new HashSet<string>(typeModel.GenericParameters.Select(p => _ctx.GetGenericParameterIdentifier(p)));

                        // Skip this overload if it references type parameters not defined on this class
                        var hasOutOfScopeTypeParams = referencedTypeParams.Any(tp => !classTypeParams.Contains(tp));
                        if (hasOutOfScopeTypeParams)
                            continue;

                        // Always use current namespace for type resolution so cross-namespace types get prefixed
                        var interfacePropertyType = ToTypeScriptType(interfaceProp.Type, currentNamespace);
                        // Only add if different from the property's own type
                        if (interfacePropertyType != propertyType)
                        {
                            interfaceTypes.Add(interfacePropertyType);
                        }
                    }
                }
            }

            // Check if this property overrides/hides a base class property
            // If base has setter but this doesn't, we need to emit base setter to satisfy TypeScript
            // Note: Check even if not marked as override, as property hiding also causes TS2416
            PropertyModel? baseProperty = null;
            if (typeModel.BaseType != null)
            {
                baseProperty = FindBaseClassProperty(typeModel.BaseType, prop.ClrName, currentNamespace);
            }

            // Emit getter overloads for each unique interface type
            foreach (var interfaceType in interfaceTypes.OrderBy(t => t))
            {
                builder.AppendLine($"{indent}{methodName}(): {interfaceType};");
            }

            // Emit the getter implementation signature (most specific type)
            builder.AppendLine($"{indent}{methodName}(): {propertyType};");

            // Determine if we need to emit setter
            // 1. If this property is not readonly, emit setter
            // 2. If this property IS readonly but base property has setter, emit base setter to satisfy LSP
            var shouldEmitSetter = !prop.IsReadonly || (baseProperty != null && !baseProperty.IsReadonly);

            if (shouldEmitSetter)
            {
                var voidType = currentNamespace == "System" ? "Void" : "System.Void";

                // If this is readonly but base has setter, emit base property type setter first
                if (prop.IsReadonly && baseProperty != null && !baseProperty.IsReadonly)
                {
                    var basePropertyType = ToTypeScriptType(baseProperty.Type, currentNamespace);
                    builder.AppendLine($"{indent}{methodName}(value: {basePropertyType}): {voidType};");
                }
                // Otherwise emit setter overloads as usual
                else
                {
                    // Emit setter overloads
                    foreach (var interfaceType in interfaceTypes.OrderBy(t => t))
                    {
                        builder.AppendLine($"{indent}{methodName}(value: {interfaceType}): {voidType};");
                    }

                    // Emit setter implementation signature
                    builder.AppendLine($"{indent}{methodName}(value: {propertyType}): {voidType};");
                }
            }

            // Track binding (single entry for both getter and setter)
            typeBindings.Members[methodName] = new MemberBindingInfo(
                Kind: "method",
                ClrName: prop.ClrName,
                ClrMemberType: "property",
                Access: prop.IsReadonly ? "get" : "get+set",
                Overloads: interfaceTypes.Count > 0 ? interfaceTypes.Concat(new[] { propertyType }).ToList() : null);
        }
    }

    /// <summary>
    /// Emits interface members that are missing from the class (explicitly implemented in C#).
    /// TypeScript doesn't support explicit interface implementation, so all interface members
    /// must be present on the class.
    /// </summary>
    private static void EmitMissingInterfaceMembers(StringBuilder builder, TypeModel type, NamespaceModel namespaceModel, TypeBindings typeBindings, string indent, string currentNamespace)
    {
        // Collect all member names already on the class
        var existingMembers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in type.Members.Methods)
            existingMembers.Add(_ctx.GetMethodIdentifier(method));

        foreach (var prop in type.Members.Properties)
            existingMembers.Add(_ctx.GetPropertyIdentifier(prop));

        foreach (var field in type.Members.Fields)
            existingMembers.Add(_ctx.GetFieldIdentifier(field));

        foreach (var evt in type.Members.Events)
            existingMembers.Add(_ctx.GetEventIdentifier(evt));

        // Check each implemented interface
        foreach (var interfaceRef in type.Implements)
        {
            var interfaceType = FindInterfaceType(interfaceRef, namespaceModel, currentNamespace);
            if (interfaceType == null) continue;

            // Check for missing properties
            foreach (var interfaceProp in interfaceType.Members.Properties)
            {
                var memberName = _ctx.GetPropertyIdentifier(interfaceProp);

                if (!existingMembers.Contains(memberName))
                {
                    // Check if property type references type parameters not defined on the class
                    var referencedTypeParams = CollectTypeParameters(interfaceProp.Type);
                    var classTypeParams = new HashSet<string>(type.GenericParameters.Select(p => _ctx.GetGenericParameterIdentifier(p)));

                    // Skip if property uses type parameters not available on the class
                    if (referencedTypeParams.Any(tp => !classTypeParams.Contains(tp)))
                        continue;

                    // Emit missing property as getter/setter methods
                    var propertyType = ToTypeScriptType(interfaceProp.Type, currentNamespace);
                    var modifiers = interfaceProp.IsStatic ? "static " : "";

                    // Emit getter
                    builder.AppendLine($"{indent}{modifiers}{memberName}(): {propertyType};");

                    // Emit setter if not readonly
                    if (!interfaceProp.IsReadonly)
                    {
                        var voidType = currentNamespace == "System" ? "Void" : "System.Void";
                        builder.AppendLine($"{indent}{modifiers}{memberName}(value: {propertyType}): {voidType};");
                    }

                    // Track in bindings
                    typeBindings.Members[memberName] = new MemberBindingInfo(
                        Kind: "method",
                        ClrName: interfaceProp.ClrName,
                        ClrMemberType: "property",
                        Access: interfaceProp.IsReadonly ? "get" : "get+set");

                    existingMembers.Add(memberName);
                }
            }

            // Check for missing methods (less common, but possible)
            foreach (var interfaceMethod in interfaceType.Members.Methods)
            {
                var memberName = _ctx.GetMethodIdentifier(interfaceMethod);

                // Skip if already exists (checking just by name, not full signature)
                if (!existingMembers.Contains(memberName))
                {
                    // Check if method uses type parameters not defined on the class
                    var referencedTypeParams = new HashSet<string>();
                    foreach (var param in interfaceMethod.Parameters)
                        referencedTypeParams.UnionWith(CollectTypeParameters(param.Type));
                    referencedTypeParams.UnionWith(CollectTypeParameters(interfaceMethod.ReturnType));

                    var classTypeParams = new HashSet<string>(type.GenericParameters.Select(p => _ctx.GetGenericParameterIdentifier(p)));

                    // Skip if method uses type parameters not available on the class
                    if (referencedTypeParams.Any(tp => !classTypeParams.Contains(tp)))
                        continue;

                    var genericParams = FormatGenericParameters(interfaceMethod.GenericParameters, currentNamespace);
                    var parameters = string.Join(", ", interfaceMethod.Parameters.Select(p => $"{EscapeIdentifier(p.Name)}: {ToTypeScriptType(p.Type, currentNamespace)}"));
                    var returnType = ToTypeScriptType(interfaceMethod.ReturnType, currentNamespace);
                    var modifiers = interfaceMethod.IsStatic ? "static " : "";

                    builder.AppendLine($"{indent}{modifiers}{memberName}{genericParams}({parameters}): {returnType};");

                    // Track in bindings
                    typeBindings.Members[memberName] = new MemberBindingInfo(
                        Kind: "method",
                        ClrName: interfaceMethod.ClrName,
                        ClrMemberType: "method");

                    existingMembers.Add(memberName);
                }
            }

            // Check for missing events
            foreach (var interfaceEvent in interfaceType.Members.Events)
            {
                var memberName = _ctx.GetEventIdentifier(interfaceEvent);

                if (!existingMembers.Contains(memberName))
                {
                    // Check if event type references type parameters not defined on the class
                    var referencedTypeParams = CollectTypeParameters(interfaceEvent.Type);
                    var classTypeParams = new HashSet<string>(type.GenericParameters.Select(p => _ctx.GetGenericParameterIdentifier(p)));

                    // Skip if event uses type parameters not available on the class
                    if (referencedTypeParams.Any(tp => !classTypeParams.Contains(tp)))
                        continue;

                    // Emit event as readonly property (events are readonly in TypeScript declarations)
                    var eventType = ToTypeScriptType(interfaceEvent.Type, currentNamespace);
                    var modifiers = interfaceEvent.IsStatic ? "static " : "";

                    builder.AppendLine($"{indent}{modifiers}readonly {memberName}: {eventType};");

                    // Track in bindings
                    typeBindings.Members[memberName] = new MemberBindingInfo(
                        Kind: "event",
                        ClrName: interfaceEvent.ClrName,
                        ClrMemberType: "event");

                    existingMembers.Add(memberName);
                }
            }
        }
    }

    /// <summary>
    /// Resolves name conflicts by appending numeric suffix.
    /// Also adds the resolved name to the existing names set.
    /// </summary>
    private static string ResolveConflict(string baseName, HashSet<string> existingNames)
    {
        var name = baseName;
        var counter = 1;

        while (existingNames.Contains(name))
        {
            name = baseName + counter;
            counter++;
        }

        existingNames.Add(name);
        return name;
    }

    /// <summary>
    /// Finds an interface type by TypeReference, searching current namespace first, then all other namespaces.
    /// Returns null if not found.
    /// </summary>
    private static TypeModel? FindInterfaceType(TypeReference typeRef, NamespaceModel namespaceModel, string currentNamespace)
    {
        var targetNamespace = typeRef.Namespace ?? currentNamespace;
        var typeName = typeRef.TypeName;

        // Try current namespace first
        if (targetNamespace == currentNamespace)
        {
            var localType = namespaceModel.Types.FirstOrDefault(t =>
                t.Kind == TypeKind.Interface &&
                t.Binding.Type.TypeName == typeName);
            if (localType != null)
                return localType;
        }

        // Search in target namespace from all models
        if (_allModels.TryGetValue(targetNamespace, out var targetModel))
        {
            return targetModel.Types.FirstOrDefault(t =>
                t.Kind == TypeKind.Interface &&
                t.Binding.Type.TypeName == typeName);
        }

        return null;
    }

    private static string FormatGenericParameters(IReadOnlyList<GenericParameterModel> parameters, string currentNamespace)
    {
        if (parameters.Count == 0)
            return "";

        var formatted = parameters.Select(p =>
        {
            var constraints = p.Constraints.Count > 0
                ? " extends " + string.Join(" & ", p.Constraints.Select(c => ToTypeScriptType(c, currentNamespace)))
                : "";
            return $"{_ctx.GetGenericParameterIdentifier(p)}{constraints}";
        });

        return $"<{string.Join(", ", formatted)}>";
    }

    /// <summary>
    /// Formats generic parameter names only (no constraints), for use in type references.
    /// Example: "<T, U, TKey>" instead of "<T extends Foo, U extends Bar>"
    /// </summary>
    private static string FormatGenericParameterNames(IReadOnlyList<GenericParameterModel> parameters)
    {
        if (parameters.Count == 0)
            return "";

        var names = parameters.Select(p => _ctx.GetGenericParameterIdentifier(p));
        return $"<{string.Join(", ", names)}>";
    }


    /// <summary>
    /// Checks if a TypeReference represents a generic type parameter (T, TKey, etc.)
    /// rather than an actual type from a namespace.
    /// Type parameters should never get namespace prefixes.
    /// </summary>
    private static bool IsTypeParameter(TypeReference typeRef)
    {
        // Type parameters have no declaring type and no generic args
        // Note: ArrayRank and PointerDepth can be > 0 (e.g., T[], T*)
        // We check the base TypeName to see if it's a type parameter pattern
        if (typeRef.DeclaringType != null) return false;
        if (typeRef.GenericArgs.Count > 0) return false;

        // Type parameters typically start with 'T' followed by uppercase or end immediately
        // Examples: T, TKey, TValue, TResult, TSource, TElement, etc.
        var name = typeRef.TypeName;
        if (name.Length == 1 && char.IsUpper(name[0]))
            return true; // Single letter: T, U, V, etc.

        if (name.Length > 1 && name[0] == 'T' && char.IsUpper(name[1]))
            return true; // TKey, TValue, TResult, etc.

        return false;
    }

    /// <summary>
    /// Recursively collects all type parameter names referenced in a type.
    /// Example: List_1<T> returns ["T"], Dictionary_2<TKey, TValue> returns ["TKey", "TValue"]
    /// Also handles arrays: T[] returns ["T"]
    /// </summary>
    private static HashSet<string> CollectTypeParameters(TypeReference typeRef)
    {
        var result = new HashSet<string>();

        // Check if base type (ignoring arrays/pointers) is a type parameter
        // For T[], we want to detect T
        if (typeRef.DeclaringType == null && typeRef.GenericArgs.Count == 0)
        {
            var name = typeRef.TypeName;
            // Single letter: T, U, V
            if (name.Length == 1 && char.IsUpper(name[0]))
            {
                result.Add(name);
            }
            // TKey, TValue, TResult, etc.
            else if (name.Length > 1 && name[0] == 'T' && char.IsUpper(name[1]))
            {
                result.Add(name);
            }
        }

        // Recursively collect from generic arguments
        foreach (var arg in typeRef.GenericArgs)
        {
            result.UnionWith(CollectTypeParameters(arg));
        }

        // Recursively collect from declaring type (for nested types)
        if (typeRef.DeclaringType != null)
        {
            result.UnionWith(CollectTypeParameters(typeRef.DeclaringType));
        }

        return result;
    }

    /// <summary>
    /// Checks if a type is defined in the current namespace or is cross-namespace.
    /// Returns true for cross-namespace types (always available via imports).
    /// Returns true for types defined in current namespace.
    /// Returns false for internal/private types that aren't generated.
    /// </summary>
    private static bool IsTypeDefinedInCurrentNamespace(TypeReference typeRef, string currentNamespace)
    {
        // Cross-namespace types are fine (they're imported)
        if (typeRef.Namespace != null && typeRef.Namespace != currentNamespace)
            return true;

        // Check if the type is defined in our set of generated types
        var typeName = ToTypeScriptType(typeRef, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        return _definedTypes.Contains(typeName);
    }

    /// <summary>
    /// Checks if a type has any static members (methods, properties, fields).
    /// </summary>
    private static bool HasStaticMembers(TypeModel type)
    {
        return type.Members.Methods.Any(m => m.IsStatic)
            || type.Members.Properties.Any(p => p.IsStatic)
            || type.Members.Fields.Any(f => f.IsStatic);
    }

    /// <summary>
    /// Emits a struct with instance/static split to avoid TS2417 static-side inheritance conflicts.
    /// Format: Type$instance interface, Type$static interface, then type alias and const declaration.
    /// </summary>
    private static void EmitStructWithSplit(StringBuilder builder, TypeModel type, string indent, string currentNamespace, NamespaceModel namespaceModel)
    {
        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        var genericParams = FormatGenericParameters(type.GenericParameters, currentNamespace);

        // Use qualified name only if not in System namespace
        var valueTypeName = currentNamespace == "System" ? "ValueType" : "System.ValueType";

        // Filter out implements that reference undefined types
        var validImplements = type.Implements
            .Where(i => IsTypeDefinedInCurrentNamespace(i, currentNamespace))
            .ToList();

        var implements = validImplements.Count > 0
            ? ", " + string.Join(", ", validImplements.Select(i => ToTypeScriptType(i, currentNamespace)))
            : "";

        // Emit instance interface (extends ValueType & struct, implements interfaces)
        builder.AppendLine($"{indent}export interface {typeName}$instance{genericParams} extends {valueTypeName}, struct{implements} {{");

        // Instance members only
        EmitMembers(builder, type.Members, typeBindings: null, indent + "    ", skipStatic: true, currentNamespace: currentNamespace, isInterface: true, typeModel: type);

        builder.AppendLine($"{indent}}}");
        builder.AppendLine();

        // Emit static interface (no extends/implements)
        builder.AppendLine($"{indent}export interface {typeName}$static{genericParams} {{");

        // Static members only
        EmitMembers(builder, type.Members, typeBindings: null, indent + "    ", staticOnly: true, currentNamespace: currentNamespace, isInterface: true, typeModel: type);

        builder.AppendLine($"{indent}}}");
        builder.AppendLine();

        // Public type alias (use names-only for references)
        var genericNames = FormatGenericParameterNames(type.GenericParameters);
        builder.AppendLine($"{indent}export type {typeName}{genericParams} = {typeName}$instance{genericNames};");

        // Only emit const declaration for non-generic types
        // Generic types can't have ambient const declarations with type parameters
        if (type.GenericParameters.Count == 0)
        {
            builder.AppendLine($"{indent}export const {typeName}: {typeName}$static;");
        }
    }

    /// <summary>
    /// Escapes TypeScript/JavaScript reserved keywords using $$name$$ format.
    /// This is the standard Tsonic escaping format for reserved identifiers.
    /// </summary>
    private static string EscapeIdentifier(string name)
    {
        // List of TypeScript/JavaScript reserved keywords
        var reservedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "break", "case", "catch", "class", "const", "continue", "debugger", "default",
            "delete", "do", "else", "enum", "export", "extends", "false", "finally",
            "for", "function", "if", "import", "in", "instanceof", "new", "null",
            "return", "super", "switch", "this", "throw", "true", "try", "typeof",
            "var", "void", "while", "with", "yield",
            "let", "static", "implements", "interface", "package", "private", "protected",
            "public", "as", "async", "await", "constructor", "get", "set",
            "from", "of", "namespace", "module", "declare", "abstract", "any", "boolean",
            "never", "number", "object", "string", "symbol", "unknown", "type", "readonly"
        };

        return reservedKeywords.Contains(name) ? $"$${name}$$" : name;
    }

    /// <summary>
    /// Converts a TypeReference to TypeScript type syntax.
    /// Handles nested types, generics, arrays, pointers, and namespace prefixes.
    /// </summary>
    /// <param name="typeRef">The type reference to convert</param>
    /// <param name="currentNamespace">Current namespace context (for determining if cross-namespace prefix needed)</param>
    /// <param name="includeNamespacePrefix">If true, includes namespace prefix for cross-namespace types. If false, only includes type name.</param>
    /// <param name="includeGenericArgs">If true, includes generic type arguments. If false, omits them (for type declarations).</param>
    private static string ToTypeScriptType(TypeReference typeRef, string currentNamespace, bool includeNamespacePrefix = true, bool includeGenericArgs = true)
    {
        // Function pointers are mapped to 'any'
        if (typeRef.TypeName == "__FunctionPointer")
            return "any";

        var sb = new StringBuilder();

        // Type parameters (generic parameters) should never get namespace prefixes
        // They're resolved in the current generic context, not as types from a namespace
        // Examples: T, TKey, TValue, TResult, etc.
        var isTypeParameter = IsTypeParameter(typeRef);

        // Build the base type name with declaring type hierarchy
        if (typeRef.DeclaringType != null)
        {
            // Nested type: build qualified name with $ separator
            var parts = new List<string>();
            var current = typeRef;
            while (current != null)
            {
                parts.Insert(0, current.TypeName);
                current = current.DeclaringType;
            }

            // Get namespace from root declaring type
            current = typeRef;
            while (current.DeclaringType != null)
                current = current.DeclaringType;

            var ns = current.Namespace;

            // Namespace prefix if cross-namespace and requested (but NOT for type parameters)
            if (!isTypeParameter && includeNamespacePrefix && ns != null && ns != currentNamespace)
            {
                sb.Append(ns.Replace(".", "$"));
                sb.Append(".");
            }

            // Join nested type names with $ separator
            sb.Append(string.Join("$", parts));
        }
        else
        {
            // Top-level type
            if (!isTypeParameter && includeNamespacePrefix && typeRef.Namespace != null && typeRef.Namespace != currentNamespace)
            {
                sb.Append(typeRef.Namespace.Replace(".", "$"));
                sb.Append(".");
            }
            sb.Append(typeRef.TypeName);
        }

        // Generic arguments
        if (includeGenericArgs && typeRef.GenericArgs.Count > 0)
        {
            sb.Append('<');
            for (int i = 0; i < typeRef.GenericArgs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ToTypeScriptType(typeRef.GenericArgs[i], currentNamespace, includeNamespacePrefix, includeGenericArgs));
            }
            sb.Append('>');
        }

        // Pointers become 'any'
        if (typeRef.PointerDepth > 0)
        {
            return "any";
        }

        // Arrays
        for (int i = 0; i < typeRef.ArrayRank; i++)
        {
            sb.Append("[]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get the bindings map generated during emit.
    /// </summary>
    public static IReadOnlyDictionary<string, TypeBindings> GetBindings() => _bindingsMap;

    /// <summary>
    /// Finds a property with the given CLR name in the base class hierarchy.
    /// Returns null if base type is not found or has no matching property.
    /// Note: Base class methods are now handled in Phase 3 by BaseClassOverloadFix analysis pass.
    /// </summary>
    private static PropertyModel? FindBaseClassProperty(TypeReference baseTypeRef, string clrPropertyName, string currentNamespace)
    {
        // Look up the base type in all namespace models
        var baseNamespace = baseTypeRef.Namespace ?? currentNamespace;
        if (!_allModels.TryGetValue(baseNamespace, out var namespaceModel))
            return null;

        // Find the base type by name (without generic args)
        var baseTypeName = baseTypeRef.TypeName;
        var baseType = namespaceModel.Types.FirstOrDefault(t => t.Binding.Type.TypeName == baseTypeName);
        if (baseType == null)
            return null;

        // Find property with matching CLR name
        var property = baseType.Members.Properties.FirstOrDefault(p => p.ClrName == clrPropertyName);
        if (property != null)
            return property;

        // Recursively check base class's base class
        if (baseType.BaseType != null)
        {
            return FindBaseClassProperty(baseType.BaseType, clrPropertyName, baseNamespace);
        }

        return null;
    }
}

/// <summary>
/// Bindings for a single type.
/// </summary>
public sealed record TypeBindings(
    string ClrName,
    string TsAlias,
    Dictionary<string, MemberBindingInfo> Members);

/// <summary>
/// Binding information for a member (maps TS method name to CLR member).
/// </summary>
public sealed record MemberBindingInfo(
    string Kind,           // "method" in TS (all are methods now)
    string ClrName,        // CLR member name
    string ClrMemberType,  // "method", "property", "field", "event"
    string? Access = null, // "get" or "set" for properties
    List<string>? Overloads = null); // Return types for overloaded methods
