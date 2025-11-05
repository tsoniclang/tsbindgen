# Assembly Snapshot Schema

This document defines the JSON schema for assembly snapshot files generated in Phase 1 of the tsbindgen pipeline.

## Overview

An assembly snapshot (`assemblies/<Assembly>.snapshot.json`) contains a complete, self-contained representation of everything discovered through reflection for that assembly, including:

- All exported types (after filtering and transforms)
- All members (methods, properties, constructors, fields, events)
- Type forwarding information
- Cross-assembly dependencies
- Bindings (CLR name → TypeScript alias)
- Diagnostics (warnings, errors)

**Key principles:**
- Snapshots are the **canonical IR** (Intermediate Representation)
- Phase 2 consumes only snapshots—no reflection occurs
- All naming transforms are already applied
- Type forwarding targets are resolved and included

## File Location

```
out/
└── assemblies/
    ├── System.Linq.snapshot.json
    ├── System.Linq.Expressions.snapshot.json
    └── assemblies-manifest.json
```

## Root Schema

```json
{
  "assemblyName": "System.Linq",
  "assemblyPath": "/path/to/System.Linq.dll",
  "timestamp": "2025-11-05T12:34:56Z",
  "typeForwardingTargets": ["System.Linq.Expressions", "System.Private.CoreLib"],
  "namespaces": [ /* NamespaceSnapshot[] */ ]
}
```

| Field | Type | Description |
| --- | --- | --- |
| `assemblyName` | string | Simple name without extension |
| `assemblyPath` | string | Absolute path to source .dll |
| `timestamp` | string | ISO 8601 timestamp of snapshot generation |
| `typeForwardingTargets` | string[] | Assemblies this assembly forwards types to |
| `namespaces` | NamespaceSnapshot[] | Array of namespaces exported by this assembly |

## NamespaceSnapshot

```json
{
  "clrName": "System.Linq",
  "tsAlias": "systemLinq",
  "types": [ /* TypeSnapshot[] */ ],
  "imports": [
    {
      "namespace": "System.Collections.Generic",
      "assembly": "System.Private.CoreLib"
    }
  ],
  "diagnostics": [
    {
      "code": "TSB1001",
      "severity": "warning",
      "message": "Skipping generic static member Enumerable.DefaultValue<T>"
    }
  ]
}
```

| Field | Type | Description |
| --- | --- | --- |
| `clrName` | string | CLR namespace name (e.g., "System.Linq") |
| `tsAlias` | string | TypeScript identifier after transforms (e.g., "systemLinq" if camelCase) |
| `types` | TypeSnapshot[] | All types in this namespace |
| `imports` | DependencyRef[] | Cross-namespace dependencies |
| `diagnostics` | Diagnostic[] | Warnings/errors for this namespace |

## TypeSnapshot

```json
{
  "clrName": "Enumerable",
  "tsAlias": "enumerable",
  "fullName": "System.Linq.Enumerable",
  "kind": "class",
  "isStatic": true,
  "isSealed": false,
  "isAbstract": false,
  "visibility": "public",
  "genericParameters": [
    {
      "name": "TSource",
      "constraints": [],
      "variance": "none"
    }
  ],
  "baseType": null,
  "implements": [],
  "members": {
    "constructors": [ /* ConstructorSnapshot[] */ ],
    "methods": [ /* MethodSnapshot[] */ ],
    "properties": [ /* PropertySnapshot[] */ ],
    "fields": [ /* FieldSnapshot[] */ ],
    "events": [ /* EventSnapshot[] */ ]
  },
  "binding": {
    "assembly": "System.Linq",
    "type": "System.Linq.Enumerable"
  }
}
```

| Field | Type | Description |
| --- | --- | --- |
| `clrName` | string | CLR type name (e.g., "Enumerable") |
| `tsAlias` | string | TypeScript identifier after transforms |
| `fullName` | string | Fully-qualified CLR name |
| `kind` | string | "class", "struct", "interface", "enum", "delegate", "staticNamespace" |
| `isStatic` | bool | True for static classes |
| `isSealed` | bool | True for sealed types |
| `isAbstract` | bool | True for abstract types |
| `visibility` | string | "public", "internal", etc. |
| `genericParameters` | GenericParameter[] | Generic type parameters |
| `baseType` | TypeReference? | Base class (null for interfaces/objects) |
| `implements` | TypeReference[] | Implemented interfaces |
| `members` | MemberCollection | All members grouped by kind |
| `binding` | BindingInfo | Assembly and type location |

### For Enums

```json
{
  "kind": "enum",
  "clrName": "DayOfWeek",
  "tsAlias": "dayOfWeek",
  "fullName": "System.DayOfWeek",
  "underlyingType": "int",
  "members": [
    { "name": "Sunday", "value": 0 },
    { "name": "Monday", "value": 1 }
  ]
}
```

### For Delegates

```json
{
  "kind": "delegate",
  "clrName": "Action",
  "tsAlias": "action",
  "fullName": "System.Action`1",
  "genericParameters": [ { "name": "T", "constraints": [] } ],
  "parameters": [
    { "name": "obj", "clrType": "T", "tsType": "T" }
  ],
  "returnType": {
    "clrType": "System.Void",
    "tsType": "void"
  }
}
```

## MethodSnapshot

```json
{
  "clrName": "SelectMany",
  "tsAlias": "selectMany",
  "isStatic": true,
  "isVirtual": false,
  "isOverride": false,
  "isAbstract": false,
  "visibility": "public",
  "genericParameters": [
    { "name": "TResult", "constraints": [] }
  ],
  "parameters": [
    {
      "name": "source",
      "clrType": "System.Collections.Generic.IEnumerable`1",
      "tsType": "System_Private_CoreLib.System.Collections.Generic.IEnumerable_1<TSource>",
      "kind": "in",
      "isOptional": false,
      "defaultValue": null
    }
  ],
  "returnType": {
    "clrType": "System.Collections.Generic.IEnumerable`1",
    "tsType": "System_Private_CoreLib.System.Collections.Generic.IEnumerable_1<TResult>"
  },
  "binding": {
    "assembly": "System.Linq",
    "type": "System.Linq.Enumerable",
    "member": "SelectMany"
  }
}
```

| Field | Type | Description |
| --- | --- | --- |
| `clrName` | string | CLR method name |
| `tsAlias` | string | TypeScript identifier after transforms |
| `isStatic` | bool | Static method flag |
| `isVirtual` | bool | Virtual method flag |
| `isOverride` | bool | Override method flag |
| `isAbstract` | bool | Abstract method flag |
| `visibility` | string | "public", "protected", etc. |
| `genericParameters` | GenericParameter[] | Method-level generic parameters |
| `parameters` | ParameterSnapshot[] | Method parameters |
| `returnType` | TypeReference | Return type |
| `binding` | MemberBinding | Location information |

## PropertySnapshot

```json
{
  "clrName": "Length",
  "tsAlias": "length",
  "clrType": "System.Int32",
  "tsType": "int",
  "isReadOnly": true,
  "isStatic": false,
  "isVirtual": false,
  "isOverride": false,
  "visibility": "public",
  "binding": {
    "assembly": "System.Private.CoreLib",
    "type": "System.String",
    "member": "Length"
  }
}
```

## ConstructorSnapshot

```json
{
  "visibility": "public",
  "parameters": [
    {
      "name": "capacity",
      "clrType": "System.Int32",
      "tsType": "int",
      "kind": "in",
      "isOptional": false
    }
  ]
}
```

## Supporting Types

### TypeReference

```json
{
  "clrType": "System.Collections.Generic.List`1",
  "tsType": "System_Private_CoreLib.System.Collections.Generic.List_1<T>",
  "assembly": "System.Private.CoreLib"
}
```

### GenericParameter

```json
{
  "name": "TSource",
  "constraints": ["System.IComparable"],
  "variance": "in"
}
```

| Field | Values |
| --- | --- |
| `variance` | "none", "in" (contravariant), "out" (covariant) |

### ParameterSnapshot

```json
{
  "name": "source",
  "clrType": "System.Collections.Generic.IEnumerable`1",
  "tsType": "IEnumerable<T>",
  "kind": "in",
  "isOptional": false,
  "defaultValue": null,
  "isParams": false
}
```

| Field | Values |
| --- | --- |
| `kind` | "in", "ref", "out", "params" |

### DependencyRef

```json
{
  "namespace": "System.Collections.Generic",
  "assembly": "System.Private.CoreLib"
}
```

### Diagnostic

```json
{
  "code": "TSB1001",
  "severity": "warning",
  "message": "Skipping generic static member"
}
```

| Field | Values |
| --- | --- |
| `severity` | "info", "warning", "error" |

### BindingInfo

```json
{
  "assembly": "System.Linq",
  "type": "System.Linq.Enumerable",
  "member": "SelectMany"
}
```

## Assemblies Manifest

`assemblies/assemblies-manifest.json` lists all processed assemblies:

```json
{
  "assemblies": [
    {
      "name": "System.Linq",
      "snapshot": "System.Linq.snapshot.json",
      "typeCount": 218,
      "namespaceCount": 3
    },
    {
      "name": "System.Linq.Expressions",
      "snapshot": "System.Linq.Expressions.snapshot.json",
      "typeCount": 145,
      "namespaceCount": 2
    }
  ]
}
```

## Usage in Phase 2

Phase 2 components load snapshots via:

```csharp
var manifest = AssemblyManifest.Load("out/assemblies/assemblies-manifest.json");
var snapshots = manifest.Assemblies.Select(a =>
    AssemblySnapshot.Load($"out/assemblies/{a.Snapshot}"));

var aggregator = new NamespaceAggregator(snapshots);
var namespaceBundles = aggregator.AggregateByNamespace();
```

No reflection APIs are used in Phase 2—all data comes from snapshot JSON.

## Design Notes

- **Self-contained**: Each snapshot includes everything needed to understand that assembly
- **Post-transform**: All naming transforms already applied (tsAlias fields populated)
- **Type forwarding resolved**: Snapshots include forwarded types in their logical namespaces
- **Serializable**: Standard JSON for easy debugging and tooling integration
- **Versioned**: Can add fields without breaking consumers (ignore unknown properties)
