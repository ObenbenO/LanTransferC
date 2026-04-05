# 02-发现-浏览服务（mDNS Browse）

## 目标与场景

监听局域网内发布的 `_xtransfer._tcp` 服务实例，实时更新“在线用户列表”（会场/片区/用户树），并为后续连接控制通道提供候选端点。

---

## 接口定义（概念接口）

### IDiscoveryBrowser

- **职责**：browse 指定 service type，产出“发现事件流”。

#### 事件模型

- **PeerDiscovered**：发现新实例（仅含 instanceName/serviceType）
- **PeerUpdated**：同一 `id` 的 TXT/端口变更
- **PeerExpired**：超时剔除

---

## 数据结构（建议）

### DiscoveredPeer（发现态）

| field | type | 说明 |
|---|---|---|
| `instanceName` | string | DNS-SD 实例名 |
| `serviceType` | string | `_xtransfer._tcp` |
| `lastSeenAt` | DateTime | 最近一次看到该实例 |

### ResolvedPeer（可连接态）

| field | type | 说明 |
|---|---|---|
| `id` | string | 稳定设备 ID（来自 TXT） |
| `nickname` | string | 昵称 |
| `tags[]` | string[] | 标签数组（会场/片区/更多） |
| `addresses[]` | string[] | IPv4/IPv6 列表（按可用性排序） |
| `controlPort` | int | 控制通道端口 |
| `capabilities[]` | string[] | file/remote 等 |
| `os` | string | windows/macos |
| `ver` | string | app 版本 |
| `lastSeenAt` | DateTime | 最近收到更新的时间 |

---

## 实现建议（Makaretu.Dns）

- 使用 `ServiceDiscovery`：
  - 订阅 service instance 变化
  - 对新增实例触发 resolve（见 `03`）
- **去抖/合并**：
  - 同一实例的短时间多次变更（网络抖动）应合并（例如 200–500ms 窗口）
- **去重策略（关键）**：
  - 以 TXT 中的 `id` 为主键；不同 instanceName 但同 `id` 视为同一设备（可能重启/重名变化）

---

## 性能与效率要求

- **在线列表刷新**：同网段 1s 内可见新增设备。
- **UI 更新**：批量更新（diff）而不是全量重建树；避免频繁重排导致卡顿。
- **内存**：维护 peer 表应为 O(n)，n 为在线设备数（通常 < 200）。

---

## 错误与边界

- **地址优先级**：优先选择同子网 IPv4；IPv6 作为备用。
- **多网卡**：同一 peer 可能被多个网段看到，需按 `id` 聚合。
- **异常 TXT**：缺字段时标记为 `InvalidPeer`，不进入可投递列表，但可在 debug 面板展示原因。

---

## 测试用例（xUnit）

### 用例 1：按 `id` 去重
- **Given**：两个实例（不同 instanceName）但 TXT `id` 相同
- **Expect**：最终 peer 表只有 1 条，且 lastSeenAt 为最新

### 用例 2：事件去抖
- **Given**：同一实例 100ms 内触发 5 次 updated
- **Expect**：对外仅产生 1 次 PeerUpdated（或最多 2 次）

### 用例 3：非法 TXT 过滤
- **Given**：TXT 缺少 `controlPort` 或 `id`
- **Expect**：不进入可连接 peer 列表，并返回可诊断错误原因

### 用例 4（集成）：A 发布，B 浏览，B 收到新增事件 < 1s
- **Expect**：`PeerDiscovered` + resolve 后得到 `ResolvedPeer`

