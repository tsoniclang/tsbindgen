namespace tsbindgen.Core.Policy;

/// <summary>
/// Provides default policy values that match current tsbindgen behavior.
/// </summary>
public static class PolicyDefaults
{
    public static GenerationPolicy Create() => new()
    {
        Interfaces = new InterfacePolicy
        {
            InlineAll = true,
            DiamondResolution = DiamondResolutionStrategy.OverloadAll
        },

        Classes = new ClassPolicy
        {
            KeepExtends = true,
            HiddenMemberSuffix = "_new",
            SynthesizeExplicitImpl = ExplicitImplStrategy.EmitExplicitViews
        },

        Indexers = new IndexerPolicy
        {
            EmitPropertyWhenSingle = true,
            EmitMethodsWhenMultiple = true,
            MethodName = "Item"
        },

        Constraints = new ConstraintPolicy
        {
            StrictClosure = false,
            MergeStrategy = ConstraintMergeStrategy.Intersection
        },

        Emission = new EmissionPolicy
        {
            TypeNameTransform = NameTransformStrategy.None,
            MemberNameTransform = NameTransformStrategy.CamelCase,
            SortOrder = SortOrderStrategy.ByKindThenName,
            EmitDocComments = false
        },

        Diagnostics = new DiagnosticPolicy
        {
            FailOn = new HashSet<string>(),
            WarnOn = new HashSet<string>()
        },

        Renaming = new RenamingPolicy
        {
            StaticConflict = ConflictStrategy.NumericSuffix,
            HiddenNew = ConflictStrategy.DisambiguatingSuffix,
            ExplicitMap = new Dictionary<string, string>(),
            AllowStaticMemberRename = false
        },

        Modules = new ModulesPolicy
        {
            UseNamespaceDirectories = true,
            AlwaysAliasImports = false
        },

        StaticSide = new StaticSidePolicy
        {
            Action = StaticSideAction.Analyze
        }
    };
}
