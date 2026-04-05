# 09-文件-分块上传（TransferChunkStream）

## 目标与场景

提供高吞吐、可断点续传、可校验的文件块上传接口。必须适配大文件（视频/音频/图片/zip），并且在局域网中尽可能榨干带宽但不阻塞 UI。

---

## 接口定义（gRPC Client Streaming）

### 服务：`FileTransferService`

#### `UploadChunks(stream UploadChunkRequest) -> UploadChunksResponse`

**UploadChunkRequest**

| field | type | required | 说明 |
|---|---|---:|---|
| `transferId` | string | yes | 会话 id |
| `uploadToken` | bytes | yes | CreateTransfer 返回的 token |
| `itemId` | string | yes | 文件项 id |
| `offsetBytes` | int64 | yes | 当前块起始偏移（必须 chunkSize 对齐，最后一块可不对齐） |
| `data` | bytes | yes | chunk 数据 |
| `crc32c` | uint32 | no | 可选：块校验（快速） |
| `isLastChunk` | bool | no | 最后一块标记（可选） |

**UploadChunksResponse**

| field | type | required | 说明 |
|---|---|---:|---|
| `acceptedBytes` | int64 | yes | 服务端已接收并持久化的总字节数（按 item 汇总或最后一个 item） |
| `nextOffsetBytes` | int64 | yes | 建议下一次上传的 offset（用于续传/纠错） |
| `status` | string | yes | ok/need-resume/failed |
| `reason` | string | no | 失败原因 |

> 说明：若需要并发多文件，推荐“每个文件 itemId 单独一个 UploadChunks 流”，避免同一流里交织多 item（实现更简单、吞吐更稳定）。

---

## 服务端落盘策略（性能关键）

- 采用顺序写入（FileStream + 大缓冲）
- 避免多次拷贝：
  - gRPC bytes 到落盘尽量一次拷贝
- 预分配（可选）：根据 sizeBytes 预分配文件（Windows/mac 不同 API，V2 做）
- 块校验：
  - 推荐 CRC32C（快），整文件 SHA256 可在完成后异步计算/校验（不阻塞写入）

---

## 并发与流控

- 发送端并发：
  - 默认并发 2–4 个文件流
  - 单文件内 chunk 发送保持顺序
- 背压：
  - 使用 gRPC streaming 的自然背压（await write）
  - UI 进度更新采样到 10Hz（配合 `06`）

---

## 断点续传

- 若服务端检测到 offset 不匹配（例如重复/跳跃）：
  - 返回 `status=need-resume` + `nextOffsetBytes=expected`
- 发送端据此 seek 文件并继续

---

## 错误与重试

- 网络断开：客户端重新 `CreateTransfer(wantResume=true)` 或调用 Resume（V2）获取 `receivedBytes`
- `PERMISSION_DENIED`：token 无效/过期 → 重新 CreateTransfer
- `INVALID_ARGUMENT`：offset 不对齐、itemId 不存在

---

## 测试用例（xUnit）

### 用例 1：顺序上传完整性
- **Given**：随机生成 20MB 数据，chunk=1MB
- **When**：上传完成
- **Expect**：服务端落盘文件 hash 与原始一致（SHA256）

### 用例 2：offset 纠错（need-resume）
- **Given**：发送到 offset=2MB 后故意跳到 4MB
- **Expect**：服务端返回 need-resume + nextOffset=3MB

### 用例 3：吞吐基线（同机回环）
- **Given**：1GB 文件（可用生成器模拟）
- **Expect**：回环上传吞吐达到某阈值（例如 > 200MB/s，本地可调；CI 可只断言“无异常且时间 < 上限”）

### 用例 4：并发 4 流不崩溃
- **Given**：同时上传 4 个 200MB 文件
- **Expect**：全部完成，内存峰值不随文件大小线性增长（chunk 缓冲受控）

