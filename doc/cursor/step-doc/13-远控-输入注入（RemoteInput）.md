# 13-远控-输入注入（RemoteInput）

## 目标与场景

让控制端把鼠标/键盘事件发送到被控端并快速响应，满足“极低延迟、操作响应快”。同时要保证安全（会话绑定、权限控制、速率限制）。

---

## 接口定义（gRPC Bidirectional Streaming）

### 服务：`RemoteControlService`

#### `InputStream(stream RemoteInputEvent) -> stream RemoteInputAck`

> 双向流允许：客户端持续发送输入事件；服务端可回传 ack/丢弃原因/节流提示。

**RemoteInputEvent**

| field | type | required | 说明 |
|---|---|---:|---|
| `sessionId` | string | yes | 远控会话 id |
| `seq` | int64 | yes | 单调递增序号 |
| `tsMs` | int64 | yes | 客户端时间 |
| `type` | string | yes | mouseMove/mouseDown/mouseUp/wheel/keyDown/keyUp/text |
| `payload` | bytes | yes | 事件内容（protobuf oneof 亦可） |

常用 payload（建议）：
- MouseMove：x,y（相对/绝对）+ 屏幕尺寸基准
- MouseButton：button + x,y
- Wheel：deltaX/deltaY
- Key：keyCode + modifiers
- Text：utf8 string（IME 简化路径，V2 再完善）

**RemoteInputAck**

| field | type | required | 说明 |
|---|---|---:|---|
| `seq` | int64 | yes | ack 的输入 seq |
| `accepted` | bool | yes | 是否注入成功 |
| `reason` | string | no | 丢弃原因（not-authorized/throttled/out-of-range） |

---

## 关键语义（低延迟）

- 鼠标移动属于高频事件：
  - 控制端**合并**：最多 60–120Hz（可配置）
  - 被控端**最后写 wins**：若堆积，只保留最新 move
- 鼠标点击/键盘必须按序可靠处理（不丢）

---

## 安全与限制

- 会话绑定：所有输入必须携带 `sessionId`，且服务端校验该会话为 `control` 模式
- 权限：被控端用户授权后才允许注入
- 速率限制：
  - move：上限 120Hz
  - wheel：上限 60Hz
  - key：上限 30Hz（防误发风暴）

---

## 平台实现建议（被控端）

- **Windows**：Win32 `SendInput`
- **macOS**：`CGEventPost`（需辅助功能权限）
- 坐标系统：建议使用“归一化坐标”（0..1），被控端按当前分辨率映射，减少分辨率变化带来的偏差。

---

## 测试用例（xUnit）

### 用例 1：move 合并策略
- **Given**：1 秒内发送 1000 个 mouseMove
- **Expect**：被控端实际注入次数被限制（<= 120），且最终位置与最后一个事件一致

### 用例 2：权限阻止
- **Given**：session mode=viewOnly
- **Expect**：所有输入 accepted=false，reason=not-authorized

### 用例 3：按序处理 keyDown/keyUp
- **Given**：发送 A keyDown，再 keyUp
- **Expect**：被控端按序注入，ack seq 单调递增

