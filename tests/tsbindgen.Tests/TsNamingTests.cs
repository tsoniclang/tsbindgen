using tsbindgen.Config;
using tsbindgen.Snapshot;
using Xunit;

namespace tsbindgen.Tests;

public class TsNamingTests
{
    [Fact]
    public void Phase3Alias_SimpleType_ReturnsIdentifier()
    {
        var path = new ClrPath(
            "System",
            new[] { new ClrSegment("Console", 0) });

        var result = TsNaming.Phase3Alias(path);

        Assert.Equal("Console", result);
    }

    [Fact]
    public void Phase3Alias_GenericType_AppendsArityWithUnderscore()
    {
        var path = new ClrPath(
            "System.Collections.Generic",
            new[] { new ClrSegment("List", 1) });

        var result = TsNaming.Phase3Alias(path);

        Assert.Equal("List_1", result);
    }

    [Fact]
    public void Phase3Alias_NestedType_JoinsWithUnderscore()
    {
        var path = new ClrPath(
            "System",
            new[]
            {
                new ClrSegment("Console", 0),
                new ClrSegment("Error", 0)
            });

        var result = TsNaming.Phase3Alias(path);

        Assert.Equal("Console_Error", result);
    }

    [Fact]
    public void Phase3Alias_NestedGenericType_CombinesUnderscores()
    {
        var path = new ClrPath(
            "System",
            new[]
            {
                new ClrSegment("Console", 0),
                new ClrSegment("Error", 1)
            });

        var result = TsNaming.Phase3Alias(path);

        Assert.Equal("Console_Error_1", result);
    }

    [Fact]
    public void Phase3Alias_TypeWithUnderscores_PreservesUnderscores()
    {
        var path = new ClrPath(
            "System.Runtime.InteropServices",
            new[] { new ClrSegment("BIND_OPTS", 0) });

        var result = TsNaming.Phase3Alias(path);

        Assert.Equal("BIND_OPTS", result);
    }

    [Fact]
    public void Phase3Alias_MultipleNestingLevels_JoinsWithUnderscores()
    {
        var path = new ClrPath(
            "System",
            new[]
            {
                new ClrSegment("Outer", 1),
                new ClrSegment("Inner", 2)
            });

        var result = TsNaming.Phase3Alias(path);

        Assert.Equal("Outer_1_Inner_2", result);
    }

    [Fact]
    public void Phase4EmitName_SimpleType_ReturnsIdentifier()
    {
        var path = new ClrPath(
            "System",
            new[] { new ClrSegment("Console", 0) });

        var result = TsNaming.Phase4EmitName(path);

        Assert.Equal("Console", result);
    }

    [Fact]
    public void Phase4EmitName_GenericType_AppendsArityWithUnderscore()
    {
        var path = new ClrPath(
            "System.Collections.Generic",
            new[] { new ClrSegment("List", 1) });

        var result = TsNaming.Phase4EmitName(path);

        Assert.Equal("List_1", result);
    }

    [Fact]
    public void Phase4EmitName_NestedType_JoinsWithDollar()
    {
        var path = new ClrPath(
            "System",
            new[]
            {
                new ClrSegment("Console", 0),
                new ClrSegment("Error", 0)
            });

        var result = TsNaming.Phase4EmitName(path);

        Assert.Equal("Console$Error", result);
    }

    [Fact]
    public void Phase4EmitName_NestedGenericType_CombinesDollarAndUnderscore()
    {
        var path = new ClrPath(
            "System",
            new[]
            {
                new ClrSegment("Console", 0),
                new ClrSegment("Error", 1)
            });

        var result = TsNaming.Phase4EmitName(path);

        Assert.Equal("Console$Error_1", result);
    }

    [Fact]
    public void Phase4EmitName_TypeWithUnderscores_PreservesUnderscores()
    {
        var path = new ClrPath(
            "System.Runtime.InteropServices",
            new[] { new ClrSegment("BIND_OPTS", 0) });

        var result = TsNaming.Phase4EmitName(path);

        Assert.Equal("BIND_OPTS", result);
    }

    [Fact]
    public void Phase4EmitName_MultipleNestingLevels_JoinsWithDollars()
    {
        var path = new ClrPath(
            "System",
            new[]
            {
                new ClrSegment("Outer", 1),
                new ClrSegment("Inner", 2)
            });

        var result = TsNaming.Phase4EmitName(path);

        Assert.Equal("Outer_1$Inner_2", result);
    }

    [Fact]
    public void Phase4EmitName_DeepNesting_HandlesCorrectly()
    {
        var path = new ClrPath(
            "System.Runtime.Intrinsics.X86",
            new[]
            {
                new ClrSegment("Avx10v1", 0),
                new ClrSegment("V512", 0),
                new ClrSegment("X64", 0)
            });

        var result = TsNaming.Phase4EmitName(path);

        Assert.Equal("Avx10v1$V512$X64", result);
    }

    [Fact]
    public void Phase3AndPhase4_DifferOnlyInNestingSeparator()
    {
        var path = new ClrPath(
            "System",
            new[]
            {
                new ClrSegment("Console", 0),
                new ClrSegment("Error", 1)
            });

        var phase3 = TsNaming.Phase3Alias(path);
        var phase4 = TsNaming.Phase4EmitName(path);

        Assert.Equal("Console_Error_1", phase3);
        Assert.Equal("Console$Error_1", phase4);
    }
}
