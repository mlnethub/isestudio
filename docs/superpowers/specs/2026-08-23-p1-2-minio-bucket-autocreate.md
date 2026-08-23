# P1-2: MinIO Bucket 自动创建

**状态**: 已完成（修复 + 单元/集成测试 + 容器验证）
**日期**: 2026-08-23
**分支**: `dotnet`
**范围**: `src/OnToPilot/Storage/MinioBlobStore.cs` + `MinioBucketInitializer.cs` (新) + `Program.cs` + 2× 测试文件

---

## 1. 背景

P0 captive-dep 修复（commit cee2ae5）跑 e2e 时发现:对一个**全新的** MinIO 实例(或删掉 bucket 后),`POST /api/knowledge/{id}/documents/upload` 会 `500 AmazonS3Exception: The specified bucket does not exist`。因为 `MinioBlobStore` 假定 bucket 已经存在(之前通过 `docker exec ontopilot-minio-1 mc mb local/ontopilot-blobs` 手动创建),没有任何启动时自检/自建逻辑。

| ID | 现象 | 根因 |
|---|---|---|
| P1-2 | upload → `AmazonS3Exception: The specified bucket does not exist` | `MinioBlobStore` 只在 PutAsync/GetAsync 等操作里用 bucket,从不 probe/create;bucket 靠手动 `mc mb` 预建 |

这个缺口在前一个 P0 ADR（`2026-08-23-p0-captive-dep-and-a11y.md §5.2`）已登记,本 ADR 是它的收尾。

---

## 2. 决策

### 2.1 启动期 `IHostedService` 一次性 probe + create（不是懒创建）

**方案**: 注册一个 `MinioBucketInitializer : IHostedService`,在 host 启动时(请求管道打开前)调 `MinioBlobStore.EnsureBucketExistsAsync()`,bucket 不存在则 `PutBucketAsync`。

**为什么是启动期而不是懒创建**:
- ✅ 失败在启动期暴露(host 拒绝启动),而不是第一个上传请求的 500 — 后者更难排查
- ✅ 只 probe 一次(head bucket),不摊到每个上传请求的热路径
- ✅ `IHostedService.StartAsync` 被 host 同步 await,时序明确
- ❌ 懒创建(每次 PutAsync 前 head + 可能的 put)会让每次上传多一次 head 往返,而且并发上传时会有 N 个 create race

### 2.2 只对 MinIO 分支注册(不碰 LocalCasBlobStore)

`MinioBucketInitializer` 构造器接收 `IBlobStore` 并 cast 到 `MinioBlobStore`;`Program.cs` 只在 `OnToPilot:Storage:Endpoint` 非空时 `AddHostedService<MinioBucketInitializer>()`。

**为什么**:
- LocalCasBlobStore 用本地文件系统目录,`PutAsync` 已经懒建目录,不需要启动 probe
- cast + `ArgumentException` 兜底:未来若有人误把 initializer 挂到 local 分支,启动即炸而非静默 no-op

### 2.3 `EnsureBucketExistsAsync` 幂等 + 容忍 create race

`HeadBucket` 成功 → return;NotFound → `PutBucket`;`BucketAlreadyOwnedByYou`(HTTP 409)在 head 或 put 阶段都当成功(peer replica race)。

---

## 3. 实施

### 3.1 `MinioBlobStore.EnsureBucketExistsAsync`（新方法）

```csharp
public virtual async Task EnsureBucketExistsAsync(CancellationToken cancellationToken)
{
    try { await _s3.HeadBucketAsync(...); return; }
    catch (AmazonS3Exception ex) when (IsNotFound(ex)) { /* create below */ }
    catch (AmazonS3Exception ex) when (IsAlreadyOwned(ex)) { return; }

    try { await _s3.PutBucketAsync(...); }
    catch (AmazonS3Exception ex) when (IsAlreadyOwned(ex)) { /* lost race */ }
}
```

`IsAlreadyOwned` 匹配 HTTP 409 + `BucketAlreadyOwnedByYou`/`BucketAlreadyExists` 错误码。

类从 `sealed` 改为非 sealed,让单测 fake 可以 override `EnsureBucketExistsAsync`。

### 3.2 `MinioBucketInitializer`（新文件）

`IHostedService.StartAsync` 调 `_store.EnsureBucketExistsAsync`,成功 log `MinIO bucket '{Bucket}' is ready`;失败 log error 并 `throw`(host 启动失败,优于半坏的 upload 路径)。

### 3.3 `Program.cs` 注册

```csharp
builder.Services.AddSingleton<MinioBlobStore>(_ =>
    MinioBlobStore.Create(endpoint, minioAccess, minioSecret, minioBucket));
builder.Services.AddSingleton<IBlobStore>(sp =>
    sp.GetRequiredService<MinioBlobStore>());
builder.Services.AddHostedService<MinioBucketInitializer>();
```

之前直接 `AddSingleton<IBlobStore>(_ => MinioBlobStore.Create(...))`,现在拆成具体类型 + 接口代理,让 `MinioBucketInitializer` 能注入 `MinioBlobStore`。

---

## 4. 验证

### 4.1 单元测试（`MinioBucketInitializerTests`,4/4 通过）

- `StartAsync_calls_EnsureBucketExistsAsync_exactly_once`
- `StartAsync_propagates_EnsureBucketExistsAsync_failure`
- `StopAsync_completes_without_error`
- `Constructor_throws_when_IBlobStore_is_not_MinioBlobStore`

### 4.2 集成测试（`MinioBlobStoreTests`,新增 2 个 Testcontainers case）

- `EnsureBucketExistsAsync_creates_bucket_when_missing` — 用随机 bucket 名验证 head 先抛、ensure 后 head 成功
- `EnsureBucketExistsAsync_is_idempotent_when_bucket_already_exists` — 连续 3 次调用不抛

### 4.3 容器验证（真实路径）

```bash
docker exec ontopilot-minio-1 mc rb --force local/ontopilot-blobs   # 删掉手动建的 bucket
docker compose -f docker-compose.yml up -d backend                   # 用新镜像重建
docker exec ontopilot-minio-1 mc ls local/                            # 确认 bucket 自动重建
# [2026-08-23 00:36:10 UTC]  0B ontopilot-blobs/   ← 自动创建
docker logs ontopilot-backend-1 | grep -i bucket
# [00:36:10 INF] MinIO bucket 'ontopilot-blobs' is ready (created or already present).
```

### 4.4 e2e（upload 路径）

```bash
pnpm exec playwright test e2e/dotnet/upload-extract-publish.spec.ts --reporter=list
```

**结果**: upload 步骤通过 — `pump.pdf` 文档成功上传并显示为 "Parsed"（bucket 缺失时文档根本不会出现）。后续 Parse 步骤失败是**测试幂等性遗留问题**(上次运行遗留的已解析文档让按钮变成 "Re-parse",正则 `/^parse$|reparse/` 匹配不到带连字符的 "Re-parse"),与 P1-2 无关。

---

## 5. 遗留 / 不在本次范围

- e2e `upload-extract-publish.spec.ts` 的 "Parse" 按钮定位正则不对 "Re-parse"(连字符)幂等 — 属于 e2e 测试自身问题,且该文件已按用户要求排除在 git 管控外,故不修。
- 不引入 S3 bucket 生命周期管理(region/versioning/policy) — 超出本缺口范围。
- `EnsureBucketExistsAsync` 的 create race 只容忍 `BucketAlreadyOwnedByYou`;其余 `AmazonS3Exception`(坏凭证/错 endpoint)照常抛出,由 host 启动失败暴露。

---

## 6. 参考

- [[2026-08-23-p0-captive-dep-and-a11y]] — §5.2 登记本缺口
- [[ontopilot-slice9-minio-conflict]] — MinIO config-gated BlobStore 的原始 slice
- [[ontopilot-dotnet-gap-2026-08-22]] — P1/P2 缺口跟踪
- `src/OnToPilot/Storage/MinioBlobStore.cs:261-330` — EnsureBucketExistsAsync
- `src/OnToPilot/Storage/MinioBucketInitializer.cs` — 新 hosted service
- `src/OnToPilot/Program.cs:439-447` — MinIO 分支注册
