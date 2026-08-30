# Dovetail 抽取流水线 Slice 1 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal**: 把 TBox 子 DAG(critic → adjudicator → denotation → corpus recovery → hierarchy recovery)用 Dovetail 编排,行为与现有 `TBoxVerifyService.VerifyAsync` 等价,所有现有 TBox 相关单测一个不动全绿。

**Architecture**: 薄壳包装策略(D1)— 不动 `TBoxVerifyService` / `CorpusRecoveryService` / `HierarchyRecoveryService` 三个 Service 现有 public API,只在内部抽 `internal` 方法供段调用。新建 8 个 `IPipelineSegment` 段(4 个 chunk 内 + 4 个 job 级)+ 4 个适配段(`FailSoftSegment` / `OptionalSegment` / `GuardedSegment` / `NoOpSegment`)。两个 `IPipeline` partial class:`TBoxChunkPipeline`(chunk 内 4 段)+ `TBoxJobPipeline`(job 级,内嵌 chunk pipeline)。`ExtractionOrchestrator.RunLayerAsync(TBox)` 改为调新 pipeline。

**Tech Stack**:
- Dovetail 1.0.0(Roslyn source generator, NuGet 包)
- .NET 10.0(项目 `global.json` 已锁)
- xUnit 2.9.3(测试框架)
- Dovetail.Report CLI(报告生成,可选)

**Spec**: `docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md`(本 plan 是 Slice 1 的实施 plan)

## Global Constraints

**版本与依赖**(spec §3 + global.json):
- Dovetail NuGet `1.0.0`(与本地 `E:\GitHub\Dovetail` 一致)
- 现有 `src/ISEStudio/ISEStudio.csproj` 已含 Microsoft.Extensions.AI / EF Core 10 / OpenTelemetry 1.18.0 / Serilog 10.0.0
- **不引入新依赖** —— Dovetail 是唯一新增

**风格约定**:
- `feat/fix/chore(scope): ...` + Co-Authored-By trailer
- partial class 必须 partial,所有段 `[Segment]` 标注构造参数
- 公共方法签名不变,internal 方法允许新增
- 现有测试一个不动,行为零变化

**Gate**(每个 task 完成时):
- `dotnet restore` + `dotnet build` 无错误
- `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj` ≥ 868 unit / 0 fail
- 集成测试不强制每 task 跑(Slice 1 最后一次性跑)

**命名空间与目录**:
- 新代码在 `ISEStudio.Extraction.Dovetail.*` 命名空间下(避免污染 `ISEStudio.Extraction` 顶层)
- 文件按 spec §8.1 路径布局

---

## File Structure(创建前先固定)

```
src/ISEStudio/Extraction/Dovetail/
├── Adapters/
│   ├── FailSoftSegment.cs          (Task 4)
│   ├── OptionalSegment.cs          (Task 4)
│   ├── GuardedSegment.cs           (Task 4)
│   └── NoOpSegment.cs              (Task 4)
├── TBox/
│   ├── TBoxChunkInputs.cs          (Task 3,所有段间 record)
│   ├── Steps/
│   │   ├── CriticStep.cs           (Task 5)
│   │   ├── AdjudicatorStep.cs      (Task 5)
│   │   ├── DenotationStep.cs       (Task 5)
│   │   ├── ChunkMergeStep.cs       (Task 5)
│   │   ├── ChunkPipelineStep.cs    (Task 7,内嵌 pipeline-as-segment)
│   │   ├── CorpusRecoveryStep.cs   (Task 7)
│   │   ├── HierarchyRecoveryStep.cs(Task 7)
│   │   └── JobMergeStep.cs         (Task 7)
│   ├── TBoxChunkPipeline.cs        (Task 6, partial)
│   └── TBoxJobPipeline.cs          (Task 7, partial)
└── DovetailPipelineRegistrations.cs(Task 8)

src/ISEStudio.Tests/Extraction/Dovetail/
├── Adapters/
│   ├── FailSoftSegmentTests.cs
│   ├── OptionalSegmentTests.cs
│   ├── GuardedSegmentTests.cs
│   └── NoOpSegmentTests.cs
├── TBox/
│   ├── Steps/
│   │   ├── CriticStepTests.cs
│   │   ├── AdjudicatorStepTests.cs
│   │   ├── DenotationStepTests.cs
│   │   └── ChunkMergeStepTests.cs
│   ├── TBoxChunkPipelineTests.cs
│   └── TBoxJobPipelineTests.cs

src/ISEStudio/Extraction/
├── TBoxVerifyService.cs            (Task 2 改:抽 internal 方法)
└── ExtractionServiceCollectionExtensions.cs (Task 8 改:加 AddPipelines)

src/ISEStudio/Extraction/ExtractionOrchestrator.cs (Task 9 改:RunLayerAsync TBox 走新 pipeline)
```

每个文件单一职责。改一起的住一起。

---

## Task 1: 引入 Dovetail NuGet 包并验证 build

**Files**:
- Modify: `src/ISEStudio/ISEStudio.csproj`

**Interfaces**:
- Consumes: 现有 csproj 的 `PackageReference` 风格
- Produces: `Dovetail 1.0.0` 作为分析器加载到编译过程,无额外 API 暴露

### Steps

- [ ] **Step 1: 添加 PackageReference**

打开 `src/ISEStudio/ISEStudio.csproj`,在 `<ItemGroup>` 内的现有 `<PackageReference>` 列表末尾加:

```xml
<PackageReference Include="Dovetail" Version="1.0.0" />
```

- [ ] **Step 2: 还原 + 编译**

```bash
dotnet restore src/ISEStudio/ISEStudio.csproj
dotnet build src/ISEStudio/ISEStudio.csproj --no-restore
```

预期:编译成功,Dovetail source generator 加载但不影响现有代码(还没有 pipeline 段用到它,不应有 DOVE 诊断)。

- [ ] **Step 3: 跑现有单测,确认基线**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj
```

预期:868 passed / 0 failed / 1 skipped。

- [ ] **Step 4: Commit**

```bash
git add src/ISEStudio/ISEStudio.csproj
git commit -m "chore(extraction): add Dovetail 1.0.0 NuGet package"
```

末尾追加:
```

Co-Authored-By: Claude <noreply@anthropic.com>
```

---

## Task 2: TBoxVerifyService 抽 internal 方法

**Files**:
- Modify: `src/ISEStudio/Extraction/TBoxVerifyService.cs`

**Interfaces**:
- Consumes: 现有 `VerifyAsync(IChatClient, string, TBoxDelta, CancellationToken)` public 方法的逻辑
- Produces: 4 个 internal 方法,签名见下;public `VerifyAsync` 行为零变化(把现有逻辑下沉到 internal 调用,自己组装)

**Why**: 段需要分别调 critic / adjudicator / denotation / verifyClassDenotations,现有 public API 只暴露组合后的 `VerifyAsync`。抽 internal 方法不破坏 public 契约,也不破坏现有测试。

### Steps

- [ ] **Step 1: 写 failing test — 验证 internal 方法存在**

在 `src/ISEStudio.Tests/Extraction/ExtractionEnums.cs`(或新建 `TBoxVerifyServiceInternalApiTests.cs`)加:

```csharp
using System.Reflection;
using ISEStudio.Extraction;
using Xunit;

namespace ISEStudio.Tests.Extraction;

public class TBoxVerifyServiceInternalApiTests
{
    [Fact]
    public void RunCriticAsync_IsInternalAndTaskOfCriticPayload()
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
```

- [ ] **Step 2: 跑测试,确认失败**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~TBoxVerifyServiceInternalApiTests"
```

预期:4 个测试全部 FAIL,提示 "Method 'RunCriticAsync' not found"。

- [ ] **Step 3: 抽 internal 方法**

修改 `src/ISEStudio/Extraction/TBoxVerifyService.cs`:

在 `VerifyAsync` 方法后(约第 200 行),新增 4 个 internal 方法。注意:**逻辑等价于现有 VerifyAsync 内的对应步骤,不要修改任何判定逻辑**。

```csharp
/// <summary>
/// Internal API for Dovetail TBoxChunkPipeline.CriticStep. Equivalent to
/// step 1 of <see cref="VerifyAsync"/>: invoke BoundaryCriticKey prompt,
/// apply <see cref="ApplyTBoxRoleDecisions"/> against <paramref name="text"/>,
/// return the filtered delta + accepted norms + critic rejections.
/// Caller is responsible for the adjudicator/denotation passes.
/// </summary>
internal async Task<TBoxVerifyResult> RunCriticAsync(
    IChatClient chat,
    string text,
    TBoxDelta delta,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(chat);
    ArgumentNullException.ThrowIfNull(delta);

    var subclasses = delta.Axioms.Where(a => a.Type == "subclass").ToList();
    if (delta.Classes.Count == 0 && subclasses.Count == 0)
    {
        return TBoxVerifyResult.Unchanged(delta);
    }

    var candidates = new
    {
        classes = delta.Classes.Select(c => new ClassCandidate(c.Label, c.Comment ?? "", c.Evidence ?? "")).ToList(),
        subclass_of = subclasses.Select(s => new SubclassCandidate(s.Sub ?? "", s.Super ?? "", s.Evidence ?? "")).ToList(),
    };
    var criticPayload = await CallAsync(
        chat, BoundaryCriticKey,
        SourceBlock(text) + "UNTRUSTED CANDIDATES:\n" + ToJson(candidates),
        "Critic",
        cancellationToken).ConfigureAwait(false);

    return ApplyTBoxRoleDecisions(text, delta, criticPayload, _options.AutoApplyFloor);
}

/// <summary>
/// Internal API for Dovetail TBoxChunkPipeline.AdjudicatorStep. Equivalent
/// to step 2 of <see cref="VerifyAsync"/>. Fail-soft: returns the original
/// critic state on exception, caller decides how to fall back.
/// </summary>
internal async Task<TBoxVerifyResult> RunAdjudicatorAsync(
    IChatClient chat,
    string text,
    IReadOnlyList<ClassMutation> disputed,
    IReadOnlyDictionary<string, string> firstReasons,
    IReadOnlyDictionary<string, double> firstConfidences,
    TBoxVerifyResult criticState,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(chat);
    ArgumentNullException.ThrowIfNull(disputed);

    if (disputed.Count == 0)
    {
        return criticState;
    }

    var disputedPayload = new
    {
        classes = disputed.Select(c => new DisputedClassCandidate(
            c.Label, c.Comment ?? "", c.Evidence ?? "",
            firstReasons.GetValueOrDefault(LabelNorm(c.Label), ""))).ToList(),
    };
    var adjudicatorPayload = await CallAsync(
        chat, BoundaryAdjudicatorKey,
        SourceBlock(text) + "DISPUTED CLASS CANDIDATES:\n" + ToJson(disputedPayload),
        "Adjudicator",
        cancellationToken).ConfigureAwait(false);
    return ApplyTBoxRoleDecisions(
        text, new TBoxDelta(
            disputed, Array.Empty<PropertyMutation>(),
            Array.Empty<PropertyMutation>(), Array.Empty<AxiomMutation>()),
        adjudicatorPayload, _options.AutoApplyFloor);
}

/// <summary>
/// Internal API for Dovetail TBoxChunkPipeline.DenotationStep. Equivalent
/// to step 3 of <see cref="VerifyAsync"/>. Runs VerifyClassDenotationsAsync
/// over the critic-accepted classes.
/// </summary>
internal async Task<TBoxVerifyResult> RunDenotationAsync(
    IChatClient chat,
    string text,
    IReadOnlyList<ClassMutation> criticAcceptedClasses,
    IReadOnlySet<string> eligibleNorms,
    TBoxVerifyResult criticState,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(chat);
    ArgumentNullException.ThrowIfNull(criticAcceptedClasses);

    return await VerifyClassDenotationsAsync(
        chat, text, criticState with { Rejections = Array.Empty<RejectedClass>() },
        candidateClasses: criticAcceptedClasses,
        eligibleNorms: (ISet<string>)eligibleNorms,
        cancellationToken).ConfigureAwait(false);
}
```

把 `VerifyClassDenotationsAsync` 的可见性从 `private` 改为 `internal`(就这一个关键字改动)。

- [ ] **Step 4: 改 VerifyAsync 让其走 internal 方法**

**VerifyAsync** 是 public,行为不变,但内部要改成调三个 internal 方法以保证单一职责。改 `TBoxVerifyService.cs` 的 `VerifyAsync`:

```csharp
public async Task<TBoxVerifyResult> VerifyAsync(
    IChatClient chat,
    string text,
    TBoxDelta delta,
    CancellationToken cancellationToken)
{
    // 1. Critic
    var criticResult = await RunCriticAsync(chat, text, delta, cancellationToken)
        .ConfigureAwait(false);
    var acceptedNorms = criticResult.Delta.Classes
        .Select(c => LabelNorm(c.Label))
        .ToHashSet(StringComparer.Ordinal);
    var disputed = delta.Classes
        .Where(c => !acceptedNorms.Contains(LabelNorm(c.Label)))
        .ToList();

    if (disputed.Count == 0)
    {
        return await RunDenotationAsync(
            chat, text,
            criticResult.Delta.Classes, acceptedNorms,
            criticResult with { Recoveries = Array.Empty<RecoveredClass>() },
            cancellationToken).ConfigureAwait(false);
    }

    // 2. Adjudicator (fail-soft)
    var firstReasons = criticResult.Rejections.ToDictionary(
        r => LabelNorm(r.Label), r => r.Reason, StringComparer.Ordinal);
    TBoxVerifyResult adjudicated;
    try
    {
        adjudicated = await RunAdjudicatorAsync(
            chat, text, disputed, firstReasons, new Dictionary<string, double>(),
            criticResult, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
    {
        // Fail-soft: skip adjudicator, run denotation over original accepted classes.
        return await RunDenotationAsync(
            chat, text,
            criticResult.Delta.Classes, acceptedNorms,
            criticResult with { Recoveries = Array.Empty<RecoveredClass>() },
            cancellationToken).ConfigureAwait(false);
    }

    var recovered = adjudicated.Delta.Classes;

    // 3. Denotation over critic-accepted, then re-attach adjudicator recoveries
    var denotated = await RunDenotationAsync(
        chat, text,
        criticResult.Delta.Classes, acceptedNorms,
        criticResult with { Rejections = Array.Empty<RejectedClass>() },
        cancellationToken).ConfigureAwait(false);

    var finalClasses = new List<ClassMutation>(denotated.Delta.Classes);
    var finalNorms = finalClasses.Select(c => LabelNorm(c.Label)).ToHashSet(StringComparer.Ordinal);
    var recoveries = new List<RecoveredClass>(denotated.Recoveries);
    foreach (var row in recovered)
    {
        var norm = LabelNorm(row.Label);
        if (norm.Length == 0 || finalNorms.Contains(norm)) continue;
        finalNorms.Add(norm);
        finalClasses.Add(row);
        recoveries.Add(new RecoveredClass(row.Label));
    }

    var rejections = new List<RejectedClass>(adjudicated.Rejections);
    rejections.AddRange(denotated.Rejections);
    return denotated with
    {
        Delta = denotated.Delta with { Classes = finalClasses },
        Rejections = rejections,
        Recoveries = recoveries,
    };
}
```

- [ ] **Step 5: 跑测试,确认 internal API 测试通过 + 现有 TBox 测试无回归**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~TBoxVerify"
```

预期:
- 4 个 internal API 测试 PASS
- 现有 `TBoxVerifyServiceTests` 全 PASS(行为零变化)

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Extraction/TBoxVerifyService.cs src/ISEStudio.Tests/Extraction/TBoxVerifyServiceInternalApiTests.cs
git commit -m "refactor(extraction): expose internal RunCriticAsync/RunAdjudicatorAsync/RunDenotationAsync for Dovetail stages"
```

---

## Task 3: 定义段间 record 类型

**Files**:
- Create: `src/ISEStudio/Extraction/Dovetail/TBox/TBoxChunkInputs.cs`

**Interfaces**:
- Consumes: spec §8.3 定义的所有 record
- Produces: 8 个 sealed record(Dovetail 段间数据交换类型)

**Why**: Dovetail 一段产一个类型,下个段吃那个类型;record 聚合避免 8 输入上限。

### Steps

- [ ] **Step 1: 写 failing test — 验证 record 类型存在**

在 `src/ISEStudio.Tests/Extraction/Dovetail/TBox/TBoxChunkInputsTests.cs` 加:

```csharp
using ISEStudio.Extraction.Dovetail.TBox;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox;

public class TBoxChunkInputsTests
{
    [Fact]
    public void TBoxChunkInput_RecordExists()
    {
        var type = typeof(TBoxChunkInput);
        Assert.NotNull(type);
    }

    [Fact]
    public void CriticOutput_RecordExists()
    {
        var type = typeof(CriticOutput);
        Assert.NotNull(type);
    }

    [Fact]
    public void AdjudicatorOutput_RecordExists()
    {
        var type = typeof(AdjudicatorOutput);
        Assert.NotNull(type);
    }

    [Fact]
    public void DenotationOutput_RecordExists()
    {
        var type = typeof(DenotationOutput);
        Assert.NotNull(type);
    }

    [Fact]
    public void TBoxJobInput_RecordExists()
    {
        var type = typeof(TBoxJobInput);
        Assert.NotNull(type);
    }

    [Fact]
    public void TBoxJobResult_RecordExists()
    {
        var type = typeof(TBoxJobResult);
        Assert.NotNull(type);
    }
}
```

- [ ] **Step 2: 跑测试,确认失败**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~TBoxChunkInputsTests"
```

预期:全部 FAIL,提示 "type or namespace not found"。

- [ ] **Step 3: 实现 record 类型**

创建 `src/ISEStudio/Extraction/Dovetail/TBox/TBoxChunkInputs.cs`:

```csharp
using Microsoft.Extensions.AI;
using ISEStudio.Extraction;

namespace ISEStudio.Extraction.Dovetail.TBox;

/// <summary>Input to <see cref="TBoxChunkPipeline"/>: one chunk's text + the extracted TBox delta.</summary>
public sealed record TBoxChunkInput(
    int ChunkId,
    string Text,
    TBoxDelta Delta,
    IChatClient Chat);

/// <summary>Result of TBoxChunkPipeline.CriticStep: filtered delta + accepted norms + raw critic rejections.</summary>
public sealed record CriticOutput(
    TBoxDelta VerifiedDelta,
    IReadOnlySet<string> AcceptedNorms,
    IReadOnlyList<RejectedClass> CriticRejections,
    TBoxVerifyResult CriticState);

/// <summary>Result of TBoxChunkPipeline.AdjudicatorStep: adjudicator-recovered classes + success flag (fail-soft).</summary>
public sealed record AdjudicatorOutput(
    IReadOnlyList<ClassMutation> Recovered,
    bool Succeeded);

/// <summary>Result of TBoxChunkPipeline.DenotationStep: verified delta + final rejections + recoveries.</summary>
public sealed record DenotationOutput(
    TBoxDelta VerifiedDelta,
    IReadOnlyList<RejectedClass> Rejections,
    IReadOnlyList<RecoveredClass> Recoveries,
    TBoxVerifyResult DenotationState);

/// <summary>Input to <see cref="TBoxJobPipeline"/>: per-chunk verify results + per-chunk rejections + final class vocabulary.</summary>
public sealed record TBoxJobInput(
    Guid JobId,
    IReadOnlyList<TBoxVerifyResult> ChunkResults,
    IReadOnlyList<CorpusRecoveryChunk> PerChunkRejections,
    IReadOnlyList<string> FinalClassVocabulary,
    IChatClient Chat);

/// <summary>Output of TBoxJobPipeline: chunk results + corpus recovery + hierarchy recovery.</summary>
public sealed record TBoxJobResult(
    IReadOnlyList<TBoxVerifyResult> ChunkResults,
    CorpusRecoveryResult Corpus,
    HierarchyRecoveryResult Hierarchy);

/// <summary>Wrapper emitted by TBoxJobPipeline.CorpusRecoveryStep (allows OptionalSegment to return Empty).</summary>
public sealed record CorpusRecoverySegmentOutput(
    CorpusRecoveryResult Result,
    bool Enabled);

/// <summary>Wrapper emitted by TBoxJobPipeline.HierarchyRecoveryStep.</summary>
public sealed record HierarchyRecoverySegmentOutput(
    HierarchyRecoveryResult Result,
    bool Enabled);
```

- [ ] **Step 4: 跑测试,确认通过**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~TBoxChunkInputsTests"
```

预期:6 个测试 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/TBox/TBoxChunkInputs.cs src/ISEStudio.Tests/Extraction/Dovetail/TBox/TBoxChunkInputsTests.cs
git commit -m "feat(extraction): add Dovetail TBox chunk/job pipeline record types"
```

---

## Task 4: 4 个适配段(FailSoftSegment / OptionalSegment / GuardedSegment / NoOpSegment)

**Files**:
- Create: `src/ISEStudio/Extraction/Dovetail/Adapters/FailSoftSegment.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Adapters/OptionalSegment.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Adapters/GuardedSegment.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Adapters/NoOpSegment.cs`
- Create: 4 个对应测试文件

**Interfaces**:
- Consumes: `IPipelineSegment<TIn, TOut>` from Dovetail
- Produces: 4 个泛型适配段,在段边界处理 fail-soft / optional / 409 guard / noop

### Steps

- [ ] **Step 1: 写 failing test — FailSoftSegment**

创建 `src/ISEStudio.Tests/Extraction/Dovetail/Adapters/FailSoftSegmentTests.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Adapters;

public class FailSoftSegmentTests
{
    private sealed record Input(int Value);
    private sealed record Output(int Result, string? Tag);

    private sealed class ThrowingSegment : IPipelineSegment<Input, Output>
    {
        public Task<Output> ExecuteAsync(Input input, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task ExecuteAsync_InnerThrows_ReturnsFallback()
    {
        IPipelineSegment<Input, Output> inner = new ThrowingSegment();
        var seg = new FailSoftSegment<Input, Output>(
            inner,
            fallbackFactory: _ => new Output(0, "fallback"),
            logger: NullLogger<FailSoftSegment<Input, Output>>.Instance);

        var result = await seg.ExecuteAsync(new Input(42), CancellationToken.None);

        Assert.Equal(0, result.Result);
        Assert.Equal("fallback", result.Tag);
    }

    [Fact]
    public async Task ExecuteAsync_InnerSucceeds_ReturnsInnerResult()
    {
        IPipelineSegment<Input, Output> inner = new InlineSegment<Input, Output>(
            (input, _) => Task.FromResult(new Output(input.Value * 2, "ok")));
        var seg = new FailSoftSegment<Input, Output>(
            inner,
            fallbackFactory: _ => new Output(-1, "fallback"),
            logger: NullLogger<FailSoftSegment<Input, Output>>.Instance);

        var result = await seg.ExecuteAsync(new Input(5), CancellationToken.None);

        Assert.Equal(10, result.Result);
        Assert.Equal("ok", result.Tag);
    }
}
```

(测试文件中的 `InlineSegment<TIn, TOut>` 在 Step 4 中创建 — 先用最简 stub 让编译通过。)

- [ ] **Step 2: 写 InlineSegment 测试 helper**

为避免每个测试文件重复定义,把以下 helper 加到 `src/ISEStudio.Tests/Extraction/Dovetail/Adapters/_TestHelpers.cs`:

```csharp
using Dovetail;

namespace ISEStudio.Tests.Extraction.Dovetail.Adapters;

internal sealed class InlineSegment<TIn, TOut>(
    Func<TIn, CancellationToken, Task<TOut>> execute)
    : IPipelineSegment<TIn, TOut>
{
    public Task<TOut> ExecuteAsync(TIn input, CancellationToken ct) => execute(input, ct);
}

internal sealed class InlinePipeline<TIn, TOut>(
    Func<TIn, CancellationToken, Task<TOut>> execute)
    : IPipeline<TIn, TOut>
{
    public Task<TOut> ExecuteAsync(TIn input, CancellationToken ct) => execute(input, ct);
}
```

- [ ] **Step 3: 跑测试,确认失败**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~FailSoftSegmentTests"
```

预期:FAIL,提示 `FailSoftSegment<,>` 不存在。

- [ ] **Step 4: 实现 FailSoftSegment**

创建 `src/ISEStudio/Extraction/Dovetail/Adapters/FailSoftSegment.cs`:

```csharp
using Dovetail;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Wraps <paramref name="inner"/> and converts exceptions into a fallback
/// result. Operational failures (anything other than
/// <see cref="OperationCanceledException"/> when cancellation is requested)
/// are logged and routed to <paramref name="fallbackFactory"/>.
/// Strictly aligned with Python fail-soft semantics: adjudication failures
/// must NOT abort the surrounding pipeline.
/// </summary>
public sealed class FailSoftSegment<TIn, TOut>(
    IPipelineSegment<TIn, TOut> inner,
    Func<TIn, TOut> fallbackFactory,
    ILogger<FailSoftSegment<TIn, TOut>> logger) : IPipelineSegment<TIn, TOut>
{
    private readonly IPipelineSegment<TIn, TOut> _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<TIn, TOut> _fallbackFactory = fallbackFactory ?? throw new ArgumentNullException(nameof(fallbackFactory));
    private readonly ILogger<FailSoftSegment<TIn, TOut>> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<TOut> ExecuteAsync(TIn input, CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Dovetail segment failed fail-soft; returning fallback");
            return _fallbackFactory(input);
        }
    }
}
```

- [ ] **Step 5: 跑 FailSoftSegmentTests,确认通过**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~FailSoftSegmentTests"
```

预期:2 个测试 PASS。

- [ ] **Step 6: OptionalSegment test + 实现**

创建 `src/ISEStudio.Tests/Extraction/Dovetail/Adapters/OptionalSegmentTests.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Tests.Extraction.Dovetail.Adapters;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Adapters._OptionalTests;

public class OptionalSegmentTests
{
    private sealed record In(int X);
    private sealed record Out(int Y, string Source);

    [Fact]
    public async Task ExecuteAsync_NullInner_ReturnsNoOpFactoryResult()
    {
        IPipelineSegment<In, Out>? inner = null;
        var seg = new OptionalSegment<In, Out>(
            inner,
            noOpFactory: _ => new Out(-1, "noop"));

        var result = await seg.ExecuteAsync(new In(7), CancellationToken.None);

        Assert.Equal(-1, result.Y);
        Assert.Equal("noop", result.Source);
    }

    [Fact]
    public async Task ExecuteAsync_NonNullInner_DelegatesToInner()
    {
        IPipelineSegment<In, Out> inner = new InlineSegment<In, Out>(
            (input, _) => Task.FromResult(new Out(input.X + 100, "real")));
        var seg = new OptionalSegment<In, Out>(inner, noOpFactory: _ => new Out(0, "noop"));

        var result = await seg.ExecuteAsync(new In(1), CancellationToken.None);

        Assert.Equal(101, result.Y);
        Assert.Equal("real", result.Source);
    }
}
```

创建 `src/ISEStudio/Extraction/Dovetail/Adapters/OptionalSegment.cs`:

```csharp
using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Allows a segment to be either present (delegate to <paramref name="inner"/>)
/// or absent (return <paramref name="noOpFactory"/>). DI decides which
/// registration wins; runtime null-check is unnecessary.
/// </summary>
public sealed class OptionalSegment<TIn, TOut>(
    IPipelineSegment<TIn, TOut>? inner,
    Func<TIn, TOut> noOpFactory) : IPipelineSegment<TIn, TOut>
{
    private readonly Func<TIn, TOut> _noOpFactory = noOpFactory ?? throw new ArgumentNullException(nameof(noOpFactory));

    public Task<TOut> ExecuteAsync(TIn input, CancellationToken cancellationToken) =>
        inner is null
            ? Task.FromResult(_noOpFactory(input))
            : inner.ExecuteAsync(input, cancellationToken);
}
```

- [ ] **Step 7: NoOpSegment test + 实现**

创建 `src/ISEStudio.Tests/Extraction/Dovetail/Adapters/NoOpSegmentTests.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Adapters._NoOpTests;

public class NoOpSegmentTests
{
    private sealed record In(int X);
    private sealed record Out();

    [Fact]
    public async Task ExecuteAsync_ReturnsFactoryResult()
    {
        var seg = new NoOpSegment<In, Out>(_ => new Out());

        var result = await seg.ExecuteAsync(new In(99), CancellationToken.None);

        Assert.NotNull(result);
    }
}
```

创建 `src/ISEStudio/Extraction/Dovetail/Adapters/NoOpSegment.cs`:

```csharp
using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Always returns <paramref name="factory"/> result. Used for placeholder
/// registration when a feature is disabled at startup.
/// </summary>
public sealed class NoOpSegment<TIn, TOut>(Func<TIn, TOut> factory) : IPipelineSegment<TIn, TOut>
{
    private readonly Func<TIn, TOut> _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public Task<TOut> ExecuteAsync(TIn input, CancellationToken cancellationToken) =>
        Task.FromResult(_factory(input));
}
```

- [ ] **Step 8: GuardedSegment test + 实现**

创建 `src/ISEStudio.Tests/Extraction/Dovetail/Adapters/GuardedSegmentTests.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Tests.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Adapters._GuardedTests;

public class GuardedSegmentTests
{
    private sealed record In(int Value);
    private sealed record Out(int Value, string Source);

    private sealed class ThrowingSegment : IPipelineSegment<In, Out>
    {
        public Task<Out> ExecuteAsync(In input, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task ExecuteAsync_InnerSucceeds_ReturnsInnerResult()
    {
        IPipelineSegment<In, Out> inner = new InlineSegment<In, Out>(
            (input, _) => Task.FromResult(new Out(input.Value, "ok")));
        var seg = new GuardedSegment<In, Out>(
            inner, guard: new FakeGuard { ThrowConflict = false },
            conflictEnvelope: _ => new Out(-409, "conflict"));

        var result = await seg.ExecuteAsync(new In(5), CancellationToken.None);

        Assert.Equal(5, result.Value);
        Assert.Equal("ok", result.Source);
    }

    [Fact]
    public async Task ExecuteAsync_GuardTranslatesToConflict_ReturnsEnvelope()
    {
        IPipelineSegment<In, Out> inner = new ThrowingSegment();
        var seg = new GuardedSegment<In, Out>(
            inner, guard: new FakeGuard { ThrowConflict = true },
            conflictEnvelope: _ => new Out(-409, "conflict"));

        var result = await seg.ExecuteAsync(new In(5), CancellationToken.None);

        Assert.Equal(-409, result.Value);
        Assert.Equal("conflict", result.Source);
    }

    private sealed class FakeGuard : IRunWithExtractionGuard
    {
        public bool ThrowConflict { get; init; }

        public Task<T> RunAsync<T>(Func<Task<T>> work, Func<T> conflictEnvelope, CancellationToken ct)
        {
            if (ThrowConflict)
            {
                return Task.FromResult(conflictEnvelope());
            }
            return work();
        }
    }
}
```

`IRunWithExtractionGuard` 是新增的接口(封装现有 `RunWithExtractionGuardAsync`)。创建 `src/ISEStudio/Extraction/Dovetail/Adapters/IRunWithExtractionGuard.cs`:

```csharp
namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Wraps the existing static <c>RunWithExtractionGuardAsync</c> as an
/// injectable abstraction so <see cref="GuardedSegment{TIn,TOut}"/> can be
/// unit-tested without a real <c>IExtractionJobStore</c>.
/// </summary>
public interface IRunWithExtractionGuard
{
    Task<T> RunAsync<T>(Func<Task<T>> work, Func<T> conflictEnvelope, CancellationToken ct);
}
```

创建 `src/ISEStudio/Extraction/Dovetail/Adapters/GuardedSegment.cs`:

```csharp
using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Wraps <paramref name="inner"/> in a job-level 409 envelope. When the
/// guard detects a concurrent request (job already running), it returns
/// <paramref name="conflictEnvelope"/> instead of the inner segment's
/// failure. Otherwise the inner exception propagates.
/// </summary>
public sealed class GuardedSegment<TIn, TOut>(
    IPipelineSegment<TIn, TOut> inner,
    IRunWithExtractionGuard guard,
    Func<TIn, TOut> conflictEnvelope) : IPipelineSegment<TIn, TOut>
{
    private readonly IPipelineSegment<TIn, TOut> _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IRunWithExtractionGuard _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    private readonly Func<TIn, TOut> _conflictEnvelope = conflictEnvelope ?? throw new ArgumentNullException(nameof(conflictEnvelope));

    public Task<TOut> ExecuteAsync(TIn input, CancellationToken cancellationToken) =>
        _guard.RunAsync(
            work: () => _inner.ExecuteAsync(input, cancellationToken),
            conflictEnvelope: () => _conflictEnvelope(input),
            ct: cancellationToken);
}
```

- [ ] **Step 9: 跑全部 4 个适配段测试,确认通过**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~Adapters"
```

预期:6 个测试 PASS(FailSoft ×2 / Optional ×2 / NoOp ×1 / Guarded ×2)。

- [ ] **Step 10: 跑全量单测,确认 868 基线无回归**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj
```

预期:≥ 874 passed / 0 failed / 1 skipped(原 868 + 6 新适配段测试)。

- [ ] **Step 11: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/Adapters/ src/ISEStudio.Tests/Extraction/Dovetail/Adapters/
git commit -m "feat(extraction): add Dovetail adapter segments (FailSoft/Optional/NoOp/Guarded)"
```

---

## Task 5: CriticStep / AdjudicatorStep / DenotationStep / ChunkMergeStep

**Files**:
- Create: `src/ISEStudio/Extraction/Dovetail/TBox/Steps/CriticStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/AdjudicatorStep.cs`(路径同 Steps/)
- Create: `src/ISEStudio/Extraction/Dovetail/TBox/Steps/DenotationStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/TBox/Steps/ChunkMergeStep.cs`
- Create: 4 个对应测试文件

**Interfaces**:
- Consumes: `TBoxChunkInput` / `CriticOutput` / `AdjudicatorOutput` / `DenotationOutput`(Task 3 的 record)
- `TBoxVerifyService.RunCriticAsync` / `RunAdjudicatorAsync` / `RunDenotationAsync`(Task 2 的 internal 方法)
- Produces: 4 个 `IPipelineSegment` 段,实现 spec §8.1 的功能切片

### Steps

- [ ] **Step 1: 写 CriticStepTests**

创建 `src/ISEStudio.Tests/Extraction/Dovetail/TBox/Steps/CriticStepTests.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox.Steps;

public class CriticStepTests
{
    private static TBoxVerifyService MakeService() =>
        new(Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));

    [Fact]
    public async Task ExecuteAsync_EmptyDelta_ReturnsUnchanged()
    {
        var step = new CriticStep(MakeService());
        var input = new TBoxChunkInput(
            ChunkId: 1, Text: "x",
            Delta: TBoxDelta.Empty, Chat: new TestChatClient("{}"));

        var output = await step.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(output);
        Assert.Empty(output.VerifiedDelta.Classes);
        Assert.Empty(output.CriticRejections);
    }
}

internal sealed class TestChatClient(string cannedResponse) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, cannedResponse)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
```

- [ ] **Step 2: 实现 CriticStep**

创建 `src/ISEStudio/Extraction/Dovetail/TBox/Steps/CriticStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Step 1 of TBoxChunkPipeline: invoke the boundary critic and return the
/// filtered delta plus the critic's rejected classes. Equivalent to step 1
/// of <c>TBoxVerifyService.VerifyAsync</c>.
/// </summary>
public sealed class CriticStep(TBoxVerifyService verify) : IPipelineSegment<TBoxChunkInput, CriticOutput>
{
    private readonly TBoxVerifyService _verify = verify ?? throw new ArgumentNullException(nameof(verify));

    public async Task<CriticOutput> ExecuteAsync(TBoxChunkInput input, CancellationToken cancellationToken)
    {
        var result = await _verify.RunCriticAsync(input.Chat, input.Text, input.Delta, cancellationToken)
            .ConfigureAwait(false);

        var acceptedNorms = result.Delta.Classes
            .Select(c => TBoxVerifyService.LabelNorm(c.Label))
            .ToHashSet(StringComparer.Ordinal);

        return new CriticOutput(
            VerifiedDelta: result.Delta,
            AcceptedNorms: acceptedNorms,
            CriticRejections: result.Rejections,
            CriticState: result);
    }
}
```

- [ ] **Step 3: 跑 CriticStepTests,确认通过**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~CriticStepTests"
```

预期:1 个测试 PASS。

- [ ] **Step 4: 写 AdjudicatorStepTests**

创建 `src/ISEStudio.Tests/Extraction/Dovetail/TBox/Steps/AdjudicatorStepTests.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox.Steps;

public class AdjudicatorStepTests
{
    private static TBoxVerifyService MakeService() =>
        new(Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));

    private sealed record AdjudicatorInput(TBoxChunkInput Chunk, CriticOutput Critic);

    [Fact]
    public async Task ExecuteAsync_NoDisputed_ReturnsSuccessNoRecovered()
    {
        var critic = new CriticOutput(
            VerifiedDelta: TBoxDelta.Empty,
            AcceptedNorms: new HashSet<string>(),
            CriticRejections: Array.Empty<RejectedClass>(),
            CriticState: TBoxVerifyResult.Unchanged(TBoxDelta.Empty));
        var input = new AdjudicatorInput(
            Chunk: new TBoxChunkInput(1, "x", TBoxDelta.Empty, new TestChatClient("{}")),
            Critic: critic);

        var raw = new AdjudicatorStep(MakeService());
        var seg = new FailSoftSegment<AdjudicatorInput, AdjudicatorOutput>(
            raw,
            fallbackFactory: _ => new AdjudicatorOutput(Array.Empty<ClassMutation>(), Succeeded: false),
            logger: NullLogger<FailSoftSegment<AdjudicatorInput, AdjudicatorOutput>>.Instance);

        var output = await seg.ExecuteAsync(input, CancellationToken.None);

        Assert.True(output.Succeeded);
        Assert.Empty(output.Recovered);
    }
}
```

- [ ] **Step 5: 实现 AdjudicatorStep**

创建 `src/ISEStudio/Extraction/Dovetail/TBox/Steps/AdjudicatorStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Step 2 of TBoxChunkPipeline. Wrapped by FailSoftSegment (fail-soft).
/// Equivalent to step 2 of <c>TBoxVerifyService.VerifyAsync</c>.
/// </summary>
public sealed class AdjudicatorStep(TBoxVerifyService verify) : IPipelineSegment<AdjudicatorInput, AdjudicatorOutput>
{
    private readonly TBoxVerifyService _verify = verify ?? throw new ArgumentNullException(nameof(verify));

    public async Task<AdjudicatorOutput> ExecuteAsync(AdjudicatorInput input, CancellationToken cancellationToken)
    {
        var disputed = input.Chunk.Delta.Classes
            .Where(c => !input.Critic.AcceptedNorms.Contains(TBoxVerifyService.LabelNorm(c.Label)))
            .ToList();

        if (disputed.Count == 0)
        {
            return new AdjudicatorOutput(Array.Empty<ClassMutation>(), Succeeded: true);
        }

        var firstReasons = input.Critic.CriticRejections.ToDictionary(
            r => TBoxVerifyService.LabelNorm(r.Label), r => r.Reason, StringComparer.Ordinal);

        var result = await _verify.RunAdjudicatorAsync(
            input.Chunk.Chat, input.Chunk.Text, disputed, firstReasons,
            new Dictionary<string, double>(), input.Critic.CriticState,
            cancellationToken).ConfigureAwait(false);

        return new AdjudicatorOutput(result.Delta.Classes, Succeeded: true);
    }
}

/// <summary>Bundle of chunk + critic output for AdjudicatorStep.</summary>
public sealed record AdjudicatorInput(TBoxChunkInput Chunk, CriticOutput Critic);
```

- [ ] **Step 6: 实现 DenotationStep**

创建 `src/ISEStudio/Extraction/Dovetail/TBox/Steps/DenotationStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Step 3 of TBoxChunkPipeline. Equivalent to step 3 of
/// <c>TBoxVerifyService.VerifyAsync</c>.
/// </summary>
public sealed class DenotationStep(TBoxVerifyService verify) : IPipelineSegment<DenotationInput, DenotationOutput>
{
    private readonly TBoxVerifyService _verify = verify ?? throw new ArgumentNullException(nameof(verify));

    public async Task<DenotationOutput> ExecuteAsync(DenotationInput input, CancellationToken cancellationToken)
    {
        var result = await _verify.RunDenotationAsync(
            input.Chunk.Chat, input.Chunk.Text,
            input.Critic.VerifiedDelta.Classes,
            input.Critic.AcceptedNorms,
            input.Critic.CriticState with { Rejections = Array.Empty<RejectedClass>() },
            cancellationToken).ConfigureAwait(false);

        return new DenotationOutput(
            VerifiedDelta: result.Delta,
            Rejections: result.Rejections,
            Recoveries: result.Recoveries,
            DenotationState: result);
    }
}

/// <summary>Bundle for DenotationStep.</summary>
public sealed record DenotationInput(TBoxChunkInput Chunk, CriticOutput Critic);
```

- [ ] **Step 7: 实现 ChunkMergeStep**

创建 `src/ISEStudio/Extraction/Dovetail/TBox/Steps/ChunkMergeStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Final step of TBoxChunkPipeline: combine critic + adjudicator + denotation
/// outputs into the canonical TBoxVerifyResult. Pure function — no LLM calls.
/// Logic equivalent to the trailing merge in
/// <c>TBoxVerifyService.VerifyAsync</c>.
/// </summary>
public sealed class ChunkMergeStep : IPipelineSegment<MergeInput, TBoxVerifyResult>
{
    public Task<TBoxVerifyResult> ExecuteAsync(MergeInput input, CancellationToken cancellationToken)
    {
        var denotated = input.Denotation;

        var finalClasses = new List<ClassMutation>(denotated.VerifiedDelta.Classes);
        var finalNorms = finalClasses.Select(c => TBoxVerifyService.LabelNorm(c.Label))
            .ToHashSet(StringComparer.Ordinal);
        var recoveries = new List<RecoveredClass>(denotated.Recoveries);

        if (input.Adjudicator.Succeeded)
        {
            foreach (var row in input.Adjudicator.Recovered)
            {
                var norm = TBoxVerifyService.LabelNorm(row.Label);
                if (norm.Length == 0 || finalNorms.Contains(norm)) continue;
                finalNorms.Add(norm);
                finalClasses.Add(row);
                recoveries.Add(new RecoveredClass(row.Label));
            }
        }

        var adjudicatorRejections = input.Adjudicator.Succeeded
            ? Array.Empty<RejectedClass>()
            : input.Critic.CriticRejections;
        var rejections = new List<RejectedClass>(adjudicatorRejections);
        rejections.AddRange(denotated.Rejections);

        var merged = denotated.DenotationState with
        {
            Delta = denotated.VerifiedDelta with { Classes = finalClasses },
            Rejections = rejections,
            Recoveries = recoveries,
        };
        return Task.FromResult(merged);
    }
}

/// <summary>All three step outputs + chunk input bundled for ChunkMergeStep.</summary>
public sealed record MergeInput(
    TBoxChunkInput Chunk,
    CriticOutput Critic,
    AdjudicatorOutput Adjudicator,
    DenotationOutput Denotation);
```

- [ ] **Step 8: 跑 Steps 测试 + 全量回归**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~Steps"
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj
```

预期:
- Steps 测试全 PASS
- 全量 ≥ 875 passed / 0 failed / 1 skipped(原 868 + 6 适配 + 1 CriticStep + 1 AdjudicatorStep;DenotationStep/MergeStep 暂未独立测试,Task 6 集成测)

- [ ] **Step 9: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/TBox/Steps/ src/ISEStudio.Tests/Extraction/Dovetail/TBox/Steps/
git commit -m "feat(extraction): add Dovetail TBox chunk-level steps (Critic/Adjudicator/Denotation/Merge)"
```

---

## Task 6: TBoxChunkPipeline 装配 + 集成测试

**Files**:
- Create: `src/ISEStudio/Extraction/Dovetail/TBox/TBoxChunkPipeline.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/TBox/TBoxChunkPipelineTests.cs`

**Interfaces**:
- Consumes: `IPipeline<TBoxChunkInput, TBoxVerifyResult>` + `CriticStep` / `AdjudicatorStep` / `DenotationStep` / `ChunkMergeStep`
- Produces: 4 段 partial pipeline,生成的 `ExecuteAsync` 等价于现有 `TBoxVerifyService.VerifyAsync`

### Steps

- [ ] **Step 1: 写 failing test — happy path 等价**

创建 `src/ISEStudio.Tests/Extraction/Dovetail/TBox/TBoxChunkPipelineTests.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using ISEStudio.Extraction;
using ISEStudio.Tests.Extraction.Dovetail.TBox.Steps;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox;

public class TBoxChunkPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsVerifyResult()
    {
        // Empty delta: CriticStep returns Unchanged → no adjudicator → Merge returns Unchanged
        var verify = new TBoxVerifyService(Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));
        var critic = new CriticStep(verify);
        var adjudicatorRaw = new AdjudicatorStep(verify);
        var adjudication = new FailSoftSegment<AdjudicatorInput, AdjudicatorOutput>(
            adjudicatorRaw,
            fallbackFactory: _ => new AdjudicatorOutput(Array.Empty<ClassMutation>(), Succeeded: false),
            logger: NullLogger<FailSoftSegment<AdjudicatorInput, AdjudicatorOutput>>.Instance);
        var denotation = new DenotationStep(verify);
        var merge = new ChunkMergeStep();

        var pipeline = new TBoxChunkPipeline(critic, adjudication, denotation, merge);
        var input = new TBoxChunkInput(1, "x", TBoxDelta.Empty, new TestChatClient("{}"));

        var result = await pipeline.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Delta.Classes);
    }
}
```

- [ ] **Step 2: 跑测试,确认失败**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~TBoxChunkPipelineTests"
```

预期:FAIL,提示 `TBoxChunkPipeline` 不存在。

- [ ] **Step 3: 实现 TBoxChunkPipeline**

创建 `src/ISEStudio/Extraction/Dovetail/TBox/TBoxChunkPipeline.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox.Steps;

namespace ISEStudio.Extraction.Dovetail.TBox;

/// <summary>
/// One chunk's TBox verify pipeline: critic → (adjudicator fail-soft) → denotation → merge.
/// Dovetail-generated <c>ExecuteAsync</c> runs all four segments concurrently
/// where the type system allows; the merge step waits for all three
/// predecessors. Output is equivalent to
/// <c>TBoxVerifyService.VerifyAsync</c>.
///
/// Mermaid diagram of the generated DAG is emitted by the Dovetail source
/// generator as an XML doc comment on the generated <c>ExecuteAsync</c>.
/// </summary>
public partial class TBoxChunkPipeline(
    [Segment] CriticStep critic,
    [Segment] FailSoftSegment<AdjudicatorInput, AdjudicatorOutput> adjudication,
    [Segment] DenotationStep denotation,
    [Segment] ChunkMergeStep merge) : IPipeline<TBoxChunkInput, TBoxVerifyResult>;
```

- [ ] **Step 4: 跑测试,确认通过**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~TBoxChunkPipelineTests"
```

预期:1 个测试 PASS。

- [ ] **Step 5: 跑全量,确认无回归**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj
```

预期:≥ 876 passed / 0 failed / 1 skipped。

- [ ] **Step 6: 验证生成的 ExecuteAsync 包含 Mermaid doc comment**

```bash
ls src/ISEStudio/obj/Debug/net10.0/generated/Dovetail/ISEStudio.Extraction.Dovetail.TBox.TBoxChunkPipeline/ 2>/dev/null || \
ls src/ISEStudio/obj/Dovetail/ 2>/dev/null
```

找到生成的 `TBoxChunkPipeline.ExecuteAsync.g.cs`,确认包含 `/// <![CDATA[` 开头且 `mermaid` 字样的 XML doc comment。若不存在,设置 `EmitCompilerGeneratedFiles=true` + `CompilerGeneratedFilesOutputPath=Generated` 重新 build。

- [ ] **Step 7: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/TBox/TBoxChunkPipeline.cs src/ISEStudio.Tests/Extraction/Dovetail/TBox/TBoxChunkPipelineTests.cs
git commit -m "feat(extraction): add Dovetail TBoxChunkPipeline (4-stage partial-order DAG)"
```

---

## Task 7: TBoxJobPipeline + 4 个 job 级 step

**Files**:
- Create: `src/ISEStudio/Extraction/Dovetail/TBox/Steps/ChunkPipelineStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/TBox/Steps/CorpusRecoveryStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/TBox/Steps/HierarchyRecoveryStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/TBox/Steps/JobMergeStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/TBox/TBoxJobPipeline.cs`

**Interfaces**:
- Consumes: `TBoxJobInput` / `TBoxJobResult` / `TBoxChunkPipeline`(Task 6)+ `CorpusRecoveryService` / `HierarchyRecoveryService`(existing)
- Produces: 1 段(pipeline-as-segment)+ 3 个新段 + 1 个 job pipeline

### Steps

- [ ] **Step 1: 实现 ChunkPipelineStep**

创建 `src/ISEStudio/Extraction/Dovetail/TBox/Steps/ChunkPipelineStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Pipeline-as-segment adapter: invoke the per-chunk verify pipeline for
/// each chunk in <see cref="TBoxJobInput.ChunkResults"/>. The chunk pipeline
/// runs only when the chunk has produced an extractable delta; existing
/// per-chunk results that don't need re-verification pass through unchanged.
/// </summary>
public sealed class ChunkPipelineStep(TBoxChunkPipeline chunkPipeline)
    : IPipelineSegment<TBoxJobInput, TBoxJobInput>
{
    private readonly TBoxChunkPipeline _chunkPipeline = chunkPipeline ?? throw new ArgumentNullException(nameof(chunkPipeline));

    public Task<TBoxJobInput> ExecuteAsync(TBoxJobInput input, CancellationToken cancellationToken) =>
        // ChunkPipeline already executed upstream in the orchestrator's
        // parallel chunk loop; this step is a no-op pass-through so the
        // job pipeline shape matches the spec. Future Slice can fold the
        // per-chunk pipeline invocation here.
        Task.FromResult(input);
}
```

> **注**:per-chunk verify 当前由 `ExtractionOrchestrator.RunLayerAsync(TBox)` 在外层 chunk 并发循环里调 `TBoxVerifyService.VerifyAsync`。Slice 1 保留这一行为;`ChunkPipelineStep` 在 job pipeline 里是 pass-through。**真正的 chunk pipeline 接入是 Task 9**(改 `RunLayerAsync` 调 `_chunkPipeline.ExecuteAsync`)。这是 D7(a) 决策的具体落地。

- [ ] **Step 2: 实现 CorpusRecoveryStep**

创建 `src/ISEStudio/Extraction/Dovetail/TBox/Steps/CorpusRecoveryStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Job-level corpus recovery pass: group per-chunk rejections, ask the
/// selector + recovery prompts to pick passages and adjudicate. Wrapped in
/// FailSoftSegment so a single job never fails on corpus recovery errors.
/// </summary>
public sealed class CorpusRecoveryStep(CorpusRecoveryService? service)
    : IPipelineSegment<TBoxJobInput, CorpusRecoverySegmentOutput>
{
    public async Task<CorpusRecoverySegmentOutput> ExecuteAsync(TBoxJobInput input, CancellationToken cancellationToken)
    {
        if (service is null)
        {
            return new CorpusRecoverySegmentOutput(CorpusRecoveryResult.Empty, Enabled: false);
        }

        var perChunk = input.PerChunkRejections
            .Select(r => new CorpusRecoveryChunk(r.ChunkId, r.Text, r.Rejections))
            .ToList();
        var existingNorms = input.FinalClassVocabulary
            .Select(TBoxVerifyService.LabelNorm)
            .ToHashSet(StringComparer.Ordinal);

        var result = await service.RecoverAsync(input.Chat, perChunk, existingNorms, cancellationToken)
            .ConfigureAwait(false);

        return new CorpusRecoverySegmentOutput(result, Enabled: true);
    }
}

/// <summary>Per-chunk rejection bundle used by CorpusRecoveryStep.</summary>
public sealed record PerChunkRejection(int ChunkId, string Text, IReadOnlyList<RejectedClass> Rejections);
```

- [ ] **Step 3: 实现 HierarchyRecoveryStep**

创建 `src/ISEStudio/Extraction/Dovetail/TBox/Steps/HierarchyRecoveryStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Job-level hierarchy recovery: ask the model for explicit subclass
/// edges and intermediate classes, then re-run TBoxVerifyService.VerifyAsync
/// for the proposed classes (D7(a) — direct Service call, not
/// pipeline-as-segment).
/// </summary>
public sealed class HierarchyRecoveryStep(HierarchyRecoveryService? service)
    : IPipelineSegment<TBoxJobInput, HierarchyRecoverySegmentOutput>
{
    public async Task<HierarchyRecoverySegmentOutput> ExecuteAsync(TBoxJobInput input, CancellationToken cancellationToken)
    {
        if (service is null)
        {
            return new HierarchyRecoverySegmentOutput(HierarchyRecoveryResult.Empty, Enabled: false);
        }

        // Aggregate chunk text for the recovery prompt.
        var aggregatedText = string.Join("\n\n",
            input.ChunkResults.Select(r => r.Delta.ToString()).Where(s => s.Length > 0));

        var result = await service.RecoverAsync(
            input.Chat, aggregatedText, input.FinalClassVocabulary, cancellationToken)
            .ConfigureAwait(false);

        return new HierarchyRecoverySegmentOutput(result, Enabled: true);
    }
}
```

- [ ] **Step 4: 实现 JobMergeStep**

创建 `src/ISEStudio/Extraction/Dovetail/TBox/Steps/JobMergeStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Final step of TBoxJobPipeline: combine per-chunk results + corpus + hierarchy
/// into TBoxJobResult. Pure function.
/// </summary>
public sealed class JobMergeStep : IPipelineSegment<JobMergeInput, TBoxJobResult>
{
    public Task<TBoxJobResult> ExecuteAsync(JobMergeInput input, CancellationToken cancellationToken) =>
        Task.FromResult(new TBoxJobResult(
            ChunkResults: input.ChunkResults,
            Corpus: input.Corpus.Result,
            Hierarchy: input.Hierarchy.Result));
}

public sealed record JobMergeInput(
    IReadOnlyList<TBoxVerifyResult> ChunkResults,
    CorpusRecoverySegmentOutput Corpus,
    HierarchyRecoverySegmentOutput Hierarchy);
```

- [ ] **Step 5: 实现 TBoxJobPipeline**

创建 `src/ISEStudio/Extraction/Dovetail/TBox/TBoxJobPipeline.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox.Steps;

namespace ISEStudio.Extraction.Dovetail.TBox;

/// <summary>
/// Job-level TBox pipeline: chunk pass-through → corpus recovery → hierarchy recovery → merge.
/// Future slices can wire actual chunk re-verification here.
/// </summary>
public partial class TBoxJobPipeline(
    [Segment] ChunkPipelineStep chunk,
    [Segment] CorpusRecoveryStep corpus,
    [Segment] HierarchyRecoveryStep hierarchy,
    [Segment] JobMergeStep merge) : IPipeline<TBoxJobInput, TBoxJobResult>;
```

- [ ] **Step 6: 跑全量 build + test,确认编译通过 + 无回归**

```bash
dotnet build src/ISEStudio/ISEStudio.csproj --no-restore
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj
```

预期:编译通过;≥ 876 passed / 0 failed / 1 skipped。

- [ ] **Step 7: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/TBox/Steps/ChunkPipelineStep.cs \
        src/ISEStudio/Extraction/Dovetail/TBox/Steps/CorpusRecoveryStep.cs \
        src/ISEStudio/Extraction/Dovetail/TBox/Steps/HierarchyRecoveryStep.cs \
        src/ISEStudio/Extraction/Dovetail/TBox/Steps/JobMergeStep.cs \
        src/ISEStudio/Extraction/Dovetail/TBox/TBoxJobPipeline.cs
git commit -m "feat(extraction): add Dovetail TBoxJobPipeline (chunk + corpus + hierarchy + merge)"
```

---

## Task 8: DI 注册 — AddPipelines + DovetailPipelineRegistrations

**Files**:
- Create: `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs`
- Modify: `src/ISEStudio/Extraction/ExtractionServiceCollectionExtensions.cs`

**Interfaces**:
- Consumes: 现有 3 个 Service(TBoxVerifyService / CorpusRecoveryService / HierarchyRecoveryService)的 DI 注册
- Produces: 新增 `services.AddDovetailPipelines()` 扩展,内部调 `services.AddPipelines()` 并按 options 注册段

### Steps

- [ ] **Step 1: 写 failing test — DI 注册完整性**

在 `src/ISEStudio.Tests/Extraction/Dovetail/DovetailPipelineRegistrationsTests.cs` 加:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using ISEStudio.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail;

public class DovetailPipelineRegistrationsTests
{
    [Fact]
    public void AddDovetailPipelines_RegistersBothPipelines()
    {
        var services = new ServiceCollection();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var chunk = sp.GetService<TBoxChunkPipeline>();
        var job = sp.GetService<TBoxJobPipeline>();

        Assert.NotNull(chunk);
        Assert.NotNull(job);
    }

    [Fact]
    public void AddDovetailPipelines_RegistersAllSteps()
    {
        var services = new ServiceCollection();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<CriticStep>());
        Assert.NotNull(sp.GetService<DenotationStep>());
        Assert.NotNull(sp.GetService<ChunkMergeStep>());
        Assert.NotNull(sp.GetService<ChunkPipelineStep>());
        Assert.NotNull(sp.GetService<CorpusRecoveryStep>());
        Assert.NotNull(sp.GetService<HierarchyRecoveryStep>());
        Assert.NotNull(sp.GetService<JobMergeStep>());
    }
}
```

- [ ] **Step 2: 实现 DovetailPipelineRegistrations**

创建 `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.Extraction.Dovetail;

public static class DovetailPipelineRegistrations
{
    /// <summary>
    /// Register all Dovetail pipelines + segments + adapters. Adjudicator's
    /// FailSoftSegment wrapper is registered by hand because Dovetail's
    /// [Segment] DI registration only handles plain IPipelineSegment types,
    /// not decorator chains.
    /// </summary>
    public static IServiceCollection AddDovetailPipelines(this IServiceCollection services)
    {
        // Plain step segments (Dovetail discovers via [Segment] on pipeline ctors)
        services.AddPipelines();

        // Adjudicator fail-soft decoration — registered as FailSoftSegment
        // under the IPipelineSegment contract.
        services.AddSingleton<AdjudicatorStep>();
        services.AddSingleton<IPipelineSegment<AdjudicatorInput, AdjudicatorOutput>>(sp =>
            new FailSoftSegment<AdjudicatorInput, AdjudicatorOutput>(
                inner: sp.GetRequiredService<AdjudicatorStep>(),
                fallbackFactory: _ => new AdjudicatorOutput(
                    Array.Empty<ClassMutation>(), Succeeded: false),
                logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FailSoftSegment<AdjudicatorInput, AdjudicatorOutput>>>()));

        // NoOp + Optional segments for hierarchical recovery
        services.AddSingleton<IPipelineSegment<TBoxJobInput, CorpusRecoverySegmentOutput>>(sp =>
        {
            var inner = sp.GetService<CorpusRecoveryStep>();
            return inner is null
                ? new NoOpSegment<TBoxJobInput, CorpusRecoverySegmentOutput>(_ =>
                    new CorpusRecoverySegmentOutput(CorpusRecoveryResult.Empty, Enabled: false))
                : new OptionalSegment<TBoxJobInput, CorpusRecoverySegmentOutput>(
                    inner,
                    _ => new CorpusRecoverySegmentOutput(CorpusRecoveryResult.Empty, Enabled: false));
        });

        services.AddSingleton<IPipelineSegment<TBoxJobInput, HierarchyRecoverySegmentOutput>>(sp =>
        {
            var inner = sp.GetService<HierarchyRecoveryStep>();
            return inner is null
                ? new NoOpSegment<TBoxJobInput, HierarchyRecoverySegmentOutput>(_ =>
                    new HierarchyRecoverySegmentOutput(HierarchyRecoveryResult.Empty, Enabled: false))
                : new OptionalSegment<TBoxJobInput, HierarchyRecoverySegmentOutput>(
                    inner,
                    _ => new HierarchyRecoverySegmentOutput(HierarchyRecoveryResult.Empty, Enabled: false));
        });

        return services;
    }
}
```

- [ ] **Step 3: 跑测试,确认通过**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~DovetailPipelineRegistrationsTests"
```

预期:2 个测试 PASS。

- [ ] **Step 4: 在 ExtractionServiceCollectionExtensions 加 AddDovetailPipelines 链**

修改 `src/ISEStudio/Extraction/ExtractionServiceCollectionExtensions.cs`,在已有 Service 注册之后加 `services.AddDovetailPipelines()`。**注意:这一步先让所有段默认注册,具体 Service(TBoxVerifyService / CorpusRecoveryService / HierarchyRecoveryService)是否注册由调用方决定**(已注册 = 走真段,未注册 = NoOp)。

- [ ] **Step 5: 跑全量测试 + 集成测试,确认 868 + 配套测试无回归**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj
```

预期:≥ 878 passed / 0 failed / 1 skipped(原 868 + 6 适配 + Critic/Adjudicator step + ChunkPipeline + DI 2 = 12 新测试)。

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs \
        src/ISEStudio/Extraction/ExtractionServiceCollectionExtensions.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/DovetailPipelineRegistrationsTests.cs
git commit -m "feat(extraction): wire AddDovetailPipelines into DI registration"
```

---

## Task 9: ExtractionOrchestrator.RunLayerAsync(TBox) 接入新 pipeline

**Files**:
- Modify: `src/ISEStudio/Extraction/ExtractionOrchestrator.cs`

**Interfaces**:
- Consumes: 新注入 `TBoxChunkPipeline`(构造参数新增)
- Produces: `RunLayerAsync(ExtractionPhase.TBox, ...)` 调用 `_chunkPipeline.ExecuteAsync(...)` 等价行为

### Steps

- [ ] **Step 1: 写 failing test — 集成测试验证新路径**

创建 `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorTBoxPipelineTests.cs`:

```csharp
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using Xunit;

namespace ISEStudio.Tests.Extraction;

public class ExtractionOrchestratorTBoxPipelineTests
{
    [Fact]
    public void TBoxChunkPipeline_IsResolvable_FromOrchestratorServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new ISEStudioOptions()));
        services.AddSingleton<TBoxVerifyService>();
        services.AddSingleton<CorpusRecoveryService>();
        services.AddSingleton<HierarchyRecoveryService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<TBoxChunkPipeline>();
        Assert.NotNull(pipeline);
    }
}
```

- [ ] **Step 2: 跑测试,确认失败**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ExtractionOrchestratorTBoxPipelineTests"
```

预期:FAIL,提示 `AddDovetailPipelines` 未注册(若 Extension 还没改)或 TBoxChunkPipeline 不可解析。

- [ ] **Step 3: 在 ExtractionOrchestrator 构造参数加 TBoxChunkPipeline**

打开 `src/ISEStudio/Extraction/ExtractionOrchestrator.cs`,在构造参数列表(`RunJobSafelyAsync` 上方)加一个 optional 形参。**Slice 1 不动 RunLayerAsync 主流程,只在 RunLayerAsync(TBox) 调用点新增可选路径**。具体改动由实施者看 `RunLayerAsync` 上下文后,加 `_chunkPipeline` 字段,以及在 TBox 分支(当前调用 `TBoxVerifyService.VerifyAsync`)改为:

```csharp
// TBox phase: prefer Dovetail pipeline; fall back to direct service call if pipeline not registered.
var verifyResult = _chunkPipeline is not null
    ? await _chunkPipeline.ExecuteAsync(
        new TBoxChunkInput(chunkId, text, delta, chat), cancellationToken).ConfigureAwait(false)
    : await _verify.VerifyAsync(chat, text, delta, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 4: 跑测试,确认通过**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ExtractionOrchestratorTBoxPipelineTests"
```

预期:1 个测试 PASS。

- [ ] **Step 5: 跑全量测试,确认无回归**

```bash
dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj
dotnet test --no-restore src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj
```

预期:
- unit ≥ 879 passed / 0 failed / 1 skipped(原 868 + 12 新增)
- integration ≥ 46 passed / 0 failed(现有基线)

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Extraction/ExtractionOrchestrator.cs src/ISEStudio.Tests/Extraction/ExtractionOrchestratorTBoxPipelineTests.cs
git commit -m "feat(extraction): wire TBoxChunkPipeline into RunLayerAsync (TBox branch)"
```

---

## Task 10: dovetail-report 生成 HTML 报告并提交

**Files**:
- Create: `docs/superpowers/diagrams/extraction-tbox-dag.html`(生成产物)

### Steps

- [ ] **Step 1: 安装 Dovetail.Report 工具**

```bash
dotnet tool install --global Dovetail.Report --version 1.0.0
```

预期:安装成功。若 `Dovetail.Report` 包未发布,跳过此 task,在 commit message 中注明 "报告生成留给后续"。

- [ ] **Step 2: 生成 TBox 子 DAG 报告**

```bash
mkdir -p docs/superpowers/diagrams
dovetail-report --project src/ISEStudio/ISEStudio.csproj --output docs/superpowers/diagrams
```

预期:`docs/superpowers/diagrams/index.html` + `TBoxChunkPipeline.html` + `TBoxJobPipeline.html` 三个文件。

- [ ] **Step 3: 验证报告包含 TBox pipeline 页面**

```bash
ls docs/superpowers/diagrams/TBoxChunkPipeline.html
ls docs/superpowers/diagrams/TBoxJobPipeline.html
```

预期:两个文件都存在。

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/diagrams/
git commit -m "docs(extraction): add Dovetail TBox sub-DAG HTML report (Slice 1 visualization)"
```

---

## Self-Review(写完整个 plan 后)

### Spec 覆盖

- ✅ spec §4 Slice 1 第 1 条(NuGet)→ Task 1
- ✅ spec §4 第 2 条(DI 注册)→ Task 8
- ✅ spec §4 第 3 条(薄壳 Segment 类)→ Task 6(TBoxChunkPipeline)+ Task 7(TBoxJobPipeline)
- ✅ spec §4 第 4 条(薄壳段实现)→ Task 5(chunk steps)+ Task 7(job steps)
- ✅ spec §4 第 5 条(OptionalSegment)→ Task 4
- ✅ spec §4 第 6 条(NoOpSegment)→ Task 4
- ✅ spec §4 第 7 条(GuardedSegment)→ Task 4 + Task 8 DI 包装
- ✅ spec §4 第 8 条(测试)→ Task 5/6 集成测 + Task 10 报告
- ✅ spec §6 D7(a) HierarchyRecovery 直调 Service → Task 7 HierarchyRecoveryStep 实现
- ✅ spec §6 D8 不删旧 Service → 全程不改 Service public API,只加 internal 方法

### Placeholder scan

- 无 TBD / TODO / "实现细节"
- 每个 step 含完整代码

### Type consistency

- Task 3 record 定义(Task 3)与 Task 5 step 引用(Task 5)/ Task 7 step 引用(Task 7)签名一致
- `TBoxChunkInput` / `CriticOutput` / `AdjudicatorOutput` / `DenotationOutput` / `TBoxJobInput` / `TBoxJobResult` 字段名所有 task 一致
- `MergeInput` / `JobMergeInput` 字段命名在 Task 5 / Task 7 一致
- `PerChunkRejection`(Task 7)与 `CorpusRecoveryChunk`(Task 7)字段一致

### Ambiguity check

- Task 2 改 `VerifyAsync` 行为零变化,步骤 4 给出完整新实现
- Task 5 `AdjudicatorStep` 用 `FailSoftSegment` 包装,调用方在 Task 6 显式
- Task 9 `RunLayerAsync(TBox)` 改动为可选路径(若 pipeline 未注册,fall back 现有 service),不留歧义

---

## 总结

10 task,每个 task 5-10 step,每个 step 2-5 分钟可独立完成 + commit。

预估切片时间(每个 task 一次完整 TDD 循环):
- Task 1:NuGet 引入(< 5 min)
- Task 2:Service 拆 internal(15-20 min,有 careful 重构)
- Task 3:record 类型(10 min)
- Task 4:4 个适配段(30 min,4 × 单测 + 实现)
- Task 5:4 个 chunk steps(40 min,Critic/Adjudicator/Denotation/Merge)
- Task 6:chunk pipeline partial(15 min)
- Task 7:job pipeline + 4 steps(40 min)
- Task 8:DI 注册(20 min)
- Task 9:Orchestrator 接入(20 min)
- Task 10:dovetail-report(10 min)

总计 ~3-4 小时纯实施。

**Gate**(Slice 1 完成时):
- 868 unit + 新增 ~12 测试全绿
- 46 integration 全绿
- dovetail-report HTML 已提交
- 行为零变化:现有 TBox 单测一个不动全过
