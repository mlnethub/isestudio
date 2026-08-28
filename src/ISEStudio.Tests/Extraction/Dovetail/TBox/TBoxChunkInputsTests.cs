using ISEStudio.Extraction.Dovetail.TBox;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox;

public class TBoxChunkInputsTests
{
    [Fact]
    public void TBoxChunkInput_RecordExists() => Assert.NotNull(typeof(TBoxChunkInput));

    [Fact]
    public void CriticOutput_RecordExists() => Assert.NotNull(typeof(CriticOutput));

    [Fact]
    public void AdjudicatorOutput_RecordExists() => Assert.NotNull(typeof(AdjudicatorOutput));

    [Fact]
    public void DenotationOutput_RecordExists() => Assert.NotNull(typeof(DenotationOutput));

    [Fact]
    public void TBoxJobInput_RecordExists() => Assert.NotNull(typeof(TBoxJobInput));

    [Fact]
    public void TBoxJobResult_RecordExists() => Assert.NotNull(typeof(TBoxJobResult));
}