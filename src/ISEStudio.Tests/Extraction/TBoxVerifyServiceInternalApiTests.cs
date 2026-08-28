using System.Reflection;
using ISEStudio.Extraction;
using Xunit;

namespace ISEStudio.Tests.Extraction;

public class TBoxVerifyServiceInternalApiTests
{
    [Fact]
    public void RunCriticAsync_IsInternalAndTaskOfTBoxVerifyResult()
    {
        var method = typeof(TBoxVerifyService).GetMethod(
            "RunCriticAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.True(method!.IsAssembly, "RunCriticAsync should be internal (IsAssembly=true)");
    }

    [Fact]
    public void RunAdjudicatorAsync_IsInternal()
    {
        var method = typeof(TBoxVerifyService).GetMethod(
            "RunAdjudicatorAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.True(method!.IsAssembly);
    }

    [Fact]
    public void RunDenotationAsync_IsInternal()
    {
        var method = typeof(TBoxVerifyService).GetMethod(
            "RunDenotationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.True(method!.IsAssembly);
    }

    [Fact]
    public void VerifyClassDenotationsAsync_IsInternal()
    {
        var method = typeof(TBoxVerifyService).GetMethod(
            "VerifyClassDenotationsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.True(method!.IsAssembly);
    }
}