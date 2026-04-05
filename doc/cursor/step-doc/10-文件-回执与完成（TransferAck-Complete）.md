# 10-文件-回执与完成（TransferAck/Complete）

## 目标与场景

当接收端完成落盘后，需要给发送端明确回执（成功/失败/保存路径），并把“文件名+留言”写入收件箱记录，同时通过事件流推送通知给 UI。

---

## 接口定义（gRPC）

### 服务：`FileTransferService`

#### `CompleteTransfer(CompleteTransferRequest) -> CompleteTransferResponse`

> 说明：UploadChunks 结束后由**发送端**调用 CompleteTransfer；接收端最终校验并生成收件记录。

**CompleteTransferRequest**

| field | type | required | 说明 |
|---|---|---:|---|
| `transferId` | string | yes | 会话 id |
| `uploadToken` | bytes | yes | token |
| `items[]` | CompletedItem | yes | 每个文件的完成信息（发送端视角） |

**CompletedItem**

| field | type | required | 说明 |
|---|---|---:|---|
| `itemId` | string | yes | 文件项 id |
| `sizeBytes` | int64 | yes | 总大小 |
| `sha256` | bytes | no | 若发送端已算出则带上（可选） |

**CompleteTransferResponse**

| field | type | required | 说明 |
|---|---|---:|---|
| `status` | string | yes | ok/failed/need-resume |
| `reason` | string | no | 失败原因 |
| `receipts[]` | ItemReceipt | no | 每个文件的回执 |

**ItemReceipt**

| field | type | required | 说明 |
|---|---|---:|---|
| `itemId` | string | yes | 文件项 |
| `savedPath` | string | no | 接收端实际保存路径（可给 UI 展示/打开目录） |
| `deduped` | bool | yes | 是否检测到重复文件并复用（V2） |
| `error` | string | no | 单文件失败原因 |

---

## 与收件箱/事件流的联动

接收端在 Complete 成功后应：

- 写入收件记录（用于 `11` 查询）
- 通过 `06 SubscribeEvents` 推送：
  - `FileReceived`（含 fileName、from、message、savedPath、transferId）
  - 若失败：`TransferFailed`

---

## 性能与效率要求

- CompleteTransfer 不应重新读取全文件（避免重复 IO）：
  - 优先使用上传过程中的写入计数 + 可选块校验结果
  - 若需要 SHA256 校验，建议异步计算（完成后再更新“已验证”状态）

---

## 错误与重试

- `need-resume`：表示缺块/写入不完整，返回缺失范围（V2 扩展）
- `PERMISSION_DENIED`：token 失效 → 重新 CreateTransfer

---

## 测试用例（xUnit）

### 用例 1：成功回执包含 savedPath
- **Given**：上传完成后调用 CompleteTransfer
- **Expect**：receipts[i].savedPath 非空且文件存在

### 用例 2：不重复读全文件
- **Given**：插桩统计服务端读取次数
- **Expect**：CompleteTransfer 阶段不触发全量读（只读取元数据/小量）

### 用例 3：事件流通知
- **Given**：订阅事件流
- **When**：完成一次传输
- **Expect**：收到 `FileReceived` 事件（含留言与文件名）

