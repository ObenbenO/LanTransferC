# 04-发现-fallback（UDP 广播）

## 目标与场景

当 mDNS 不可用（网络禁用 5353、组播异常、系统限制）时，使用 UDP 广播/组播在同网段实现“自动发现在线设备”的降级方案。

---

## 协议概览

- **传输**：UDP
- **端口**：`37020`（建议可配置）
- **广播地址**：每个 IPv4 网卡的子网广播地址（例如 `192.168.1.255`），发送到该地址
- **消息**：小 JSON 或 Protobuf（建议 Protobuf；调试期可 JSON）
- **频率**：
  - announce：每 2s 发送一次（可抖动 0–300ms 防同步）
  - offline 判定：6s stale / 12s expire（与 mDNS 目录保持一致）

---

## 接口定义（概念接口）

### IDiscoveryUdpAnnouncer
- 周期性广播 `Announce`。

### IDiscoveryUdpListener
- 监听端口，接收 `Announce` 并更新目录。

---

## 消息结构（JSON 示例）

### Announce

```json
{
  "type": "announce",
  "id": "b4d1f7a8-...",
  "nickname": "赵小明",
  "tags": ["会场1", "A片区"],
  "os": "macos",
  "ver": "1.0.0",
  "controlPort": 50051,
  "cap": ["file", "remote"],
  "ts": 1710000000
}
```

字段约束同 mDNS TXT 最小集，且总包长建议 < 1200 bytes（避免分片）。

---

## 去重与安全边界

- **去重主键**：`id`
- **忽略自我广播**：若 `id` 等于本机 id，丢弃
- **简单防护（建议）**：
  - `ts` 必须在合理窗口内（±30s），防重放
  - 可增加 `sig`：使用共享会议信任码（可选，后续增强）

---

## 性能与效率要求

- 发送包小、频率稳定，避免广播风暴：
  - announce 间隔建议 2s，不要 < 500ms
  - 在线设备 200 台以内仍可用
- 接收端解析必须 O(1) per packet，避免 JSON 大量分配（推荐 Protobuf）。

---

## 错误与边界

- 部分网络禁用广播：此时 UDP fallback 仍可能失败，应提示用户“发现受限，可手动添加 IP”（后续接口）。
- 多网卡：每个活动网卡单独发送与监听；目录聚合以 `id` 为准。

---

## 测试用例（xUnit）

### 用例 1：Announce 序列化包长控制
- **Given**：昵称 32、tags 8 项
- **Expect**：序列化后长度 < 1200 bytes（否则触发截断策略）

### 用例 2：自我广播过滤
- **Given**：listener 收到 id==selfId
- **Expect**：不进入 peer 目录

### 用例 3：离线剔除
- **Given**：停止接收某 peer announce
- **Expect**：6s 标 stale，12s 移除

### 用例 4（集成）：同机两进程互相发现 < 3s
- **Steps**：
  - A 开启 announcer（2s）
  - B 开启 listener
- **Expect**：B 在 3s 内发现 A

