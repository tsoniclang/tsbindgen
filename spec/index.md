# generatedts Specification Index

This directory documents every transformation performed by the `generatedts`
tool.  Each markdown file focuses on a layer of the pipeline so that new
contributors can understand *why* a transform exists, the exact modules that
implement it, and the observable output.

| Document | Purpose |
| --- | --- |
| [pipeline.md](pipeline.md) | High level flow from CLI invocation to files on disk |
| [mapping.md](mapping.md) | CLR → TypeScript mapping rules (primitives, generics, delegates, etc.) |
| [analysis.md](analysis.md) | All semantic adjustments applied after reflection (covariance, diamonds, overloads…) |
| [name-transform.md](name-transform.md) | CLI-controlled naming transforms and bindings generation |
| [emit.md](emit.md) | How declaration text is produced from the analysed model |
| [metadata.md](metadata.md) | Metadata JSON generation used by the Tsonic emitter |
| [bindings-consumer.md](bindings-consumer.md) | How the runtime should consume `<Assembly>.bindings.json` |
| [modules.md](modules.md) | One‑line responsibilities for every `.cs` file in `Src/` |
| [validation.md](validation.md) | Validation/CI expectations |

The documents are deliberately terse and cite the exact functions and files
responsible for each behaviour.  Use them as the canonical reference when
changing the pipeline or introducing new transforms.
