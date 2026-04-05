# 06-控制通道-事件订阅（EventStream）

## 目标与场景

建立一个高效的“事件流”接口，把以下信息以增量方式推给 UI：

- 传输队列变化（进度、完成、失败原因）
- 收到的文件/留言通知
- 远控会话状态（连接中/已连接/断开/延迟等）

避免轮询，保证低延迟与低开销。

---

## 接口定义（gRPC Server Streaming）

### 服务：`ControlService`

#### `SubscribeEvents(SubscribeEventsRequest) -> stream EventEnvelope`

**SubscribeEventsRequest**

| field | type | required | 说明 |
|---|---|---:|---|
| `sessionId` | string | yes | 来自握手 |
| `clientId` | string | yes | 自己的 id |
| `sinceSeq` | int64 | no | 断线重连续传（默认 0） |

**EventEnvelope**

| field | type | required | 说明 |
|---|---|---:|---|
| `seq` | int64 | yes | 单调递增序号 |
| `tsMs` | int64 | yes | 事件时间 |
| `type` | string | yes | `transfer` / `inbox` / `remote` |
| `payload` | bytes | yes | 对应事件类型的 protobuf bytes |

> payload 建议用 oneof（强类型）也可；Envelope + bytes 更利于未来扩展与兼容（代价是需要二次解析）。

---

## 事件类型（建议最小集）

### TransferEvent
- `TransferCreated`
- `TransferProgress`
- `TransferCompleted`
- `TransferFailed`

### InboxEvent
- `MessageReceived`
- `FileReceived`
- `ItemMarkedRead`

### RemoteEvent
- `RemoteSessionRequested`
- `RemoteSessionStateChanged`
- `RemoteStatsUpdated`

---

## 性能与效率要求

- **延迟**：事件产生到 UI 可见 < 200ms（局域网）
- **背压**：客户端处理慢时服务端应限流/丢弃可重建事件（如进度事件可采样）
- **采样**：进度事件最多 10Hz（每 100ms 一条），避免 UI/网络被刷爆
- **断线恢复**：支持 `sinceSeq`，服务端保留最近 N 秒事件（例如 30s）或最近 1–5k 条

---

## 错误与重连

- `UNAVAILABLE`：自动重连并携带 `sinceSeq`
- `RESOURCE_EXHAUSTED`：客户端消费过慢 → 降低采样率/暂停推送进度事件

---

## 测试用例（xUnit）

### 用例 1：进度事件采样 10Hz
- **Given**：传输模块每 10ms 报一次进度
- **Expect**：事件流输出不超过 10Hz（允许轻微抖动）

### 用例 2：断线重连续传
- **Given**：订阅后收到 seq=1..100，中途断开
- **When**：以 sinceSeq=80 重连
- **Expect**：从 seq>=81 继续

### 用例 3：背压策略
- **Given**：客户端故意延迟读取 2s
- **Expect**：服务端不无限缓存；进度事件被合并/采样，关键事件（完成/失败）不丢

