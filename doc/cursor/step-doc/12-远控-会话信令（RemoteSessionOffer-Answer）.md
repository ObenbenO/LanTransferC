# 12-远控-会话信令（RemoteSessionOffer/Answer）

## 目标与场景

建立跨 Windows/macOS 的远程控制会话。推荐采用 WebRTC 的 Offer/Answer 模型：控制信令走控制通道（gRPC），媒体（视频）走 WebRTC。

---

## 接口定义（gRPC）

### 服务：`RemoteControlService`

#### 1) `CreateSession(CreateRemoteSessionRequest) -> CreateRemoteSessionResponse`

用于发起远控请求（接收端可弹窗确认/权限控制）。

**CreateRemoteSessionRequest**

| field | type | required | 说明 |
|---|---|---:|---|
| `requestId` | string | yes | 幂等键 |
| `fromId` | string | yes | 发起方 id |
| `toPeerId` | string | yes | 被控方 id |
| `mode` | string | yes | viewOnly/control |
| `preferred` | RemotePreference | no | 偏好（清晰/流畅、分辨率） |

**CreateRemoteSessionResponse**

| field | type | required | 说明 |
|---|---|---:|---|
| `accepted` | bool | yes | 是否接受 |
| `sessionId` | string | yes | 会话 id |
| `reason` | string | no | 拒绝原因 |

#### 2) `Offer(RemoteOfferRequest) -> RemoteOfferResponse`

发起方提交 WebRTC SDP offer（或等价描述）。

**RemoteOfferRequest**

| field | type | required | 说明 |
|---|---|---:|---|
| `sessionId` | string | yes | 会话 |
| `fromId` | string | yes | 发起方 |
| `sdpOffer` | string | yes | SDP offer |
| `iceUfrag` | string | no | 可选 |

**RemoteOfferResponse**

| field | type | required | 说明 |
|---|---|---:|---|
| `accepted` | bool | yes | 是否继续 |
| `sdpAnswer` | string | no | SDP answer |
| `reason` | string | no | 失败原因 |

#### 3) `TrickleIce(stream IceCandidate) -> TrickleIceResponse`

ICE 候选增量交换（可双向，简化可做两个方向的 streaming）。

**IceCandidate**

| field | type | required | 说明 |
|---|---|---:|---|
| `sessionId` | string | yes | 会话 |
| `fromId` | string | yes | 来源 |
| `candidate` | string | yes | candidate 行 |
| `sdpMid` | string | no | mid |
| `sdpMLineIndex` | int32 | no | mline |

---

## 权限与安全（最小集）

- 被控端必须显式允许：
  - macOS：录屏权限/辅助功能权限（系统提示）
  - Windows：必要时 UAC/安全策略提示
- `mode=viewOnly` 默认不允许输入注入；`control` 需要二次确认

---

## 性能与效率要求

- 信令往返（Create + Offer/Answer）目标 < 500ms（局域网）
- ICE 候选消息应做合并（每 50–100ms flush 一批），避免消息风暴

---

## 测试用例（xUnit）

### 用例 1：CreateSession 幂等
- **Given**：同 requestId 调用两次
- **Expect**：sessionId 相同

### 用例 2：拒绝策略
- **Given**：被控端设置不允许 control
- **Expect**：accepted=false，reason=policy

### 用例 3：Offer/Answer 基本流程（集成）
- **Steps**：A CreateSession→Offer，B 返回 Answer；双方交换候选
- **Expect**：会话状态变为 Connected（通过事件流或状态接口）

