# 01-发现-发布服务（mDNS Announce）

## 目标与场景

在局域网内让其它客户端“看到我在线”，并携带最小必要元数据（昵称/标签/端口/能力集）。要求 Windows/macOS 均可用。

---

## 接口定义（概念接口）

### IDiscoveryAnnouncer

- **职责**：通过 mDNS/DNS-SD 发布服务实例，周期性刷新 TTL，并在退出时尽力撤销（Goodbye）。

#### 服务类型（Service Type）

- **ServiceType**：`_xtransfer._tcp`
- **Domain**：`local.`（mDNS 默认域）
- **InstanceName**：`{nickname}-{deviceIdShort}`（避免重名）

#### SRV/端口

- **Port**：控制通道端口（例如 gRPC）`controlPort`

#### TXT 记录（建议字段）

| key | type | required | 示例 | 约束/说明 |
|---|---|---:|---|---|
| `id` | string | yes | `b4d1...` | 稳定设备 ID（UUIDv4 或机器指纹 + salt） |
| `nickname` | string | yes | `赵小明` | 1–32 字符 |
| `tags` | string | yes | `会场1;A片区` | 分隔符 `;`，最多 8 项 |
| `os` | string | yes | `windows` / `macos` | 枚举 |
| `app` | string | yes | `xtransfer` | 固定 |
| `ver` | string | yes | `1.0.0` | SemVer |
| `cap` | string | no | `file,remote` | 能力列表 |
| `filesPort` | int | no | `50052` | 若文件通道独立端口 |
| `remotePort` | int | no | `50053` | 若远控信令独立端口 |

> 兼容性：TXT 记录大小有限（不同实现略有差异），建议总长度控制在 512–900 bytes 内；标签与昵称尽量短。

---

## 协议/实现建议（Makaretu.Dns）

- **发布**：创建 `ServiceDiscovery`，注册 `ServiceProfile`：
  - `ServiceName = "_xtransfer._tcp"`
  - `InstanceName = instanceName`
  - `Port = controlPort`
  - `Txt = {...}`
- **TTL**：
  - 建议 `TTL = 120s`（SRV/TXT）
  - 主动 refresh 间隔 `refresh = 60s`（TTL/2）
- **Goodbye**：应用退出时发送 TTL=0 的 goodbye（尽力而为）。

---

## 性能与效率要求

- **CPU**：发布与刷新应接近常量开销（idle < 0.5%）。
- **网络**：刷新包尽量小；只在字段变更时更新 TXT（昵称/标签变更）。
- **多网卡**：优先选择“有默认网关的 IPv4 网卡”，并允许用户在设置中指定网卡（后续增强）。

---

## 错误与边界

- **实例名冲突**：同昵称可能冲突；通过追加 `deviceIdShort` 规避。若仍冲突，自动 suffix `-2/-3`。
- **权限/防火墙**：mDNS 依赖 UDP 5353；被阻断时应触发 fallback（见 `04`）。
- **TXT 超长**：超限时截断 tags，保留 `id/nickname/tags/controlPort/ver` 最小集。

---

## 测试用例（xUnit）

> 说明：mDNS 属于网络集成测试，建议分两层：  
> 1) 单元测试：对“profile 构建/字段约束/截断策略”做纯逻辑测试；  
> 2) 集成测试：同机两个进程或两个实例进行 publish + browse（需要放行 UDP 5353）。

### 用例 1：TXT 字段最小集构建

- **Given**：nickname/tags/controlPort/ver
- **Expect**：生成的 TXT 包含 `id/nickname/tags/os/app/ver`，且长度不超过上限

### 用例 2：超长 tags 截断

- **Given**：tags 8 项，每项 64 字符
- **Expect**：截断后总长度在阈值内，且仍包含前 2 项（会场/片区）优先

### 用例 3：实例名冲突处理

- **Given**：同名两次构建实例名
- **Expect**：instanceName 不同（包含 deviceIdShort）

### 用例 4（集成）：发布后可被浏览到

- **Steps**
  - A 实例发布 `_xtransfer._tcp`（controlPort=50051）
  - B 实例 browse 该服务类型
- **Expect**
  - B 在 1s 内收到服务实例
  - 解析 TXT 中 `id/nickname/tags/port`

