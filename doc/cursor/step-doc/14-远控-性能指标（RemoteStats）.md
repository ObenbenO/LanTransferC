# 14-远控-性能指标（RemoteStats）

## 目标与场景

UI 需要显示远控关键指标（延迟 ms、帧率 fps、码率 Mbps、丢包等），并支持“流畅/平衡/清晰”档位切换的效果验证与问题定位。

---

## 接口定义（gRPC Server Streaming）

### 服务：`RemoteControlService`

#### `SubscribeStats(SubscribeRemoteStatsRequest) -> stream RemoteStatsEnvelope`

**SubscribeRemoteStatsRequest**

| field | type | required | 说明 |
|---|---|---:|---|
| `sessionId` | string | yes | 会话 |
| `fromId` | string | yes | 订阅方 |
| `intervalMs` | int32 | no | 默认 500ms（2Hz），上限 10Hz |

**RemoteStatsEnvelope**

| field | type | required | 说明 |
|---|---|---:|---|
| `tsMs` | int64 | yes | 时间 |
| `rttMs` | int32 | yes | 往返时延（若可得） |
| `fps` | float | yes | 实际渲染 fps |
| `bitrateMbps` | float | yes | 当前码率 |
| `packetLoss` | float | no | 丢包率（0..1） |
| `resolution` | string | no | 例如 1920x1080 |

---

## 性能与效率要求

- 指标采样不应影响媒体管线：
  - 默认 2Hz（500ms）
  - HUD 显示足够平滑
- 计算尽量复用 WebRTC/编码器内部统计，不额外扫描帧

---

## 错误与边界

- 未连接：返回空流或立即结束，并在 UI 显示“未连接”
- intervalMs 过小：服务端 clamp 到最小值（例如 100ms）

---

## 测试用例（xUnit）

### 用例 1：interval clamp
- **Given**：intervalMs=10
- **Expect**：实际推送频率 <= 10Hz

### 用例 2：断线结束
- **Given**：会话断开
- **Expect**：stats stream 在 1s 内结束或发送终止事件

### 用例 3（集成）：连接后 stats 2Hz 推送
- **Expect**：1 秒内收到约 2 条（允许抖动）

