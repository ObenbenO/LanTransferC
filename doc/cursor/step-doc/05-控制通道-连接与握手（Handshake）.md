# 05-控制通道-连接与握手（Handshake）

## 目标与场景

在发现到 Peer 后建立可靠的控制通道连接，并完成身份校验、能力协商、版本兼容检查，为后续“留言/文件/远控信令”提供统一承载。

---

## 协议选择（建议）

- **传输**：gRPC over HTTP/2（局域网）
- **序列化**：Protobuf
- **端口来源**：来自发现（mDNS TXT/UDP announce 的 `controlPort`）

---

## 接口定义（gRPC）

### 服务：`ControlService`

#### 1) `Handshake(HandshakeRequest) -> HandshakeResponse`

用于首次连接时的双向身份确认与能力协商。

**HandshakeRequest**

| field | type | required | 说明 |
|---|---|---:|---|
| `clientId` | string | yes | 发起方设备 ID |
| `nickname` | string | yes | 发起方昵称 |
| `tags[]` | string | yes | 发起方标签 |
| `os` | string | yes | windows/macos |
| `appVersion` | string | yes | SemVer |
| `capabilities[]` | string | no | file/remote 等 |
| `sessionNonce` | bytes | yes | 16–32 bytes 随机数（防重放/绑定会话） |

**HandshakeResponse**

| field | type | required | 说明 |
|---|---|---:|---|
| `serverId` | string | yes | 被连接方设备 ID |
| `nickname` | string | yes | 被连接方昵称 |
| `tags[]` | string | yes | 被连接方标签 |
| `os` | string | yes | windows/macos |
| `appVersion` | string | yes | SemVer |
| `capabilities[]` | string | no | file/remote |
| `serverTimeMs` | int64 | yes | 服务器时间（用于粗略校时） |
| `accepted` | bool | yes | 是否接受连接 |
| `reason` | string | no | 拒绝原因（版本不兼容/忙碌/权限） |
| `sessionId` | string | yes | 会话 ID（UUID） |

---

## 兼容性与效率要求

- **版本策略**：
  - Major 不一致：默认拒绝（`accepted=false`）
  - Minor 不一致：允许但降级能力（capabilities 取交集）
- **性能**：
  - Handshake P99 < 100ms（局域网）
  - 不做重计算/IO；只读内存中的 Identity 配置
- **连接复用**：
  - 同一 peer 的控制通道应复用（长连接），避免每次发文件都重新握手

---

## 错误码与重试

- gRPC status：
  - `UNAVAILABLE`：网络不可达 → 退避重试（1s, 2s, 5s，上限 30s）
  - `FAILED_PRECONDITION`：版本不兼容 → 不重试，提示升级
  - `PERMISSION_DENIED`：对方拒绝/策略阻止 → 不重试或用户触发重试

---

## 测试用例（xUnit）

### 用例 1：能力交集协商
- **Given**：client cap = {file,remote}，server cap = {file}
- **Expect**：最终可用 cap = {file}

### 用例 2：版本拒绝
- **Given**：client major=2，server major=1
- **Expect**：`accepted=false` 且 reason 包含 “version”

### 用例 3：会话 nonce 不重复
- **Given**：连续 1000 次握手
- **Expect**：nonce 随机性基本满足（不出现重复；可用 HashSet 断言）

### 用例 4（集成）：同机起两个控制服务端口，握手 P99 < 100ms
- **Steps**：启动 server，client 连续握手 200 次
- **Expect**：统计 P99 小于阈值（可放宽到 200ms 以适配 CI）

