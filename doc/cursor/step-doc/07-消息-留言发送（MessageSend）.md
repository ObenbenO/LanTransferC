# 07-消息-留言发送（MessageSend）

## 目标与场景

支持“拖拽投递文件后弹出留言框”的留言发送能力；接收端需要显示“文件名 + 留言”。

留言必须：
- 延迟低（<100ms 局域网）
- 幂等（重复发送不会产生多条）
- 可关联到一次文件传输（transferId）

---

## 接口定义（gRPC）

### 服务：`MessagingService`

#### `SendMessage(SendMessageRequest) -> SendMessageResponse`

**SendMessageRequest**

| field | type | required | 说明 |
|---|---|---:|---|
| `requestId` | string | yes | 幂等键（UUID） |
| `fromId` | string | yes | 发送者 peer id |
| `to` | oneof | yes | 目标：单用户或标签群组 |
| `text` | string | no | 留言正文（0–500 字符） |
| `transferId` | string | no | 若与文件绑定则传 |
| `fileNames[]` | string | no | 仅用于通知展示（可选） |
| `tsMs` | int64 | yes | 客户端时间戳 |

`to` 建议：
- `toPeerId: string`
- `toTagPath: TagPath`（如 会场1/A片区）

**SendMessageResponse**

| field | type | required | 说明 |
|---|---|---:|---|
| `accepted` | bool | yes | 是否接收 |
| `serverMessageId` | string | yes | 服务端消息 id |
| `deduped` | bool | yes | 是否命中幂等（重复请求） |

---

## 群发语义（TagPath）

### TagPath

| field | type | required | 说明 |
|---|---|---:|---|
| `segments[]` | string | yes | 例如 ["会场1","A片区"] |

群发建议采用“服务器侧 fan-out”，并对每个接收方生成收件记录（见 `11`）。

---

## 性能与效率要求

- **P99**：`SendMessage` < 100ms（不落盘也可；但至少入内存队列）
- **幂等**：requestId 作为 dedupe key，保留窗口建议 10–30 分钟
- **文本存储**：UTF-8，限制长度，避免异常大消息

---

## 错误与边界

- 目标为空：`INVALID_ARGUMENT`
- 对方离线：可 `accepted=true` 但标记为“离线队列”（若做离线投递）；V1 可直接失败 `UNAVAILABLE`
- 文本过长：`INVALID_ARGUMENT`

---

## 测试用例（xUnit）

### 用例 1：幂等去重
- **Given**：同 requestId 重复调用 3 次
- **Expect**：只产生 1 条消息记录，Response.deduped 在后两次为 true

### 用例 2：关联 transferId
- **Given**：发送消息携带 transferId + fileNames
- **Expect**：接收端通知事件包含 transferId，可在 UI 里关联到该文件

### 用例 3：群发 fan-out 数量正确
- **Given**：TagPath 命中 10 个在线 peer
- **Expect**：产生 10 条收件记录/通知（或 10 个投递结果）

