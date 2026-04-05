# 08-文件-发起传输（TransferCreate）

## 目标与场景

发送方在真正上传文件块前，先创建一次“传输会话”，明确：
- 发送到谁（单用户/标签群组）
- 文件元数据（名称/大小/hash/数量）
- 接收端默认目录策略（由接收端决定）
- 并发/断点续传/覆盖策略

---

## 接口定义（gRPC）

### 服务：`FileTransferService`

#### `CreateTransfer(CreateTransferRequest) -> CreateTransferResponse`

**CreateTransferRequest**

| field | type | required | 说明 |
|---|---|---:|---|
| `requestId` | string | yes | 幂等键（UUID） |
| `fromId` | string | yes | 发送方 peer id |
| `to` | oneof | yes | 目标：peer 或 TagPath |
| `message` | string | no | 留言（0–500） |
| `items[]` | TransferItemMeta | yes | 文件列表 |
| `overwritePolicy` | string | no | ask/rename/overwrite（建议默认 ask，由接收端 UI 决定） |
| `wantResume` | bool | no | 是否允许断点续传 |

**TransferItemMeta**

| field | type | required | 说明 |
|---|---|---:|---|
| `itemId` | string | yes | 文件项 id（UUID） |
| `fileName` | string | yes | 仅文件名，不含路径 |
| `sizeBytes` | int64 | yes | 文件大小 |
| `sha256` | bytes | no | 可选：整文件 hash（大文件计算成本高，可后置） |
| `mtimeMs` | int64 | no | 源文件修改时间 |

**CreateTransferResponse**

| field | type | required | 说明 |
|---|---|---:|---|
| `accepted` | bool | yes | 接收端是否接受（可弹窗确认） |
| `transferId` | string | yes | 传输会话 id |
| `uploadToken` | bytes | yes | 上传令牌（绑定 transferId） |
| `chunkSizeBytes` | int32 | yes | 建议 chunk 大小（1–4MB） |
| `resumeInfo[]` | ResumeItemInfo | no | 若 wantResume=true，返回可续传信息 |
| `reason` | string | no | 拒绝原因 |

**ResumeItemInfo**

| field | type | required | 说明 |
|---|---|---:|---|
| `itemId` | string | yes | 对应文件项 |
| `receivedBytes` | int64 | yes | 已接收字节数（按 chunk 对齐） |

---

## 性能与效率要求

- CreateTransfer 必须轻量（不做落盘大 IO）：
  - 仅创建内存/轻量存储记录
  - 返回 chunkSize、token
- 群发：可返回一个 transferId（逻辑会话）+ 每接收方子会话 id（V2）；V1 可简化为发送端逐个 peer CreateTransfer。

---

## 错误与重试

- `UNAVAILABLE`：对方不在线 → 不重试或用户触发重试
- 幂等：requestId 重复调用应返回同一个 transferId（dedupe）

---

## 测试用例（xUnit）

### 用例 1：幂等创建
- **Given**：同 requestId 调用 2 次
- **Expect**：transferId 相同，且不会创建两条记录

### 用例 2：chunkSize 协商
- **Given**：文件 50MB
- **Expect**：返回 chunkSize 在 [1MB,4MB]，且为 2 的幂（建议）

### 用例 3：续传信息对齐
- **Given**：已接收 3.5MB，chunk=1MB
- **Expect**：receivedBytes 向下对齐到 3MB（避免从中间 chunk 续传）

