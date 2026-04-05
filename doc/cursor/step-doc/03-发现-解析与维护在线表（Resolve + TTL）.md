# 03-发现-解析与维护在线表（Resolve + TTL）

## 目标与场景

把 mDNS 浏览到的“实例名”解析成可连接的 Peer 信息（IP/端口/TXT），并以 TTL/心跳策略维护在线表：及时发现上线、合理判断离线。

---

## 接口定义（概念接口）

### IPeerResolver

- **输入**：`instanceName` + `serviceType`
- **输出**：`ResolvedPeer` 或解析失败原因

### IPeerDirectory

- **职责**：维护 peer 表（`id -> ResolvedPeer`），提供查询、分组（会场/片区）与离线剔除。

---

## 解析规则

### 1) 解析顺序

1. SRV：获取目标 host + port（port 也可来自 SRV）
2. A/AAAA：获取 IP 列表
3. TXT：读取 `id/nickname/tags/ver/cap/...`

### 2) 字段校验（进入“可用 peer”前必须通过）

- `id`：非空，长度 8–64
- `nickname`：1–32
- `tags`：至少包含 0–2 个核心标签（会场/片区可为空，但 UI 显示应降级）
- `controlPort`：1–65535

### 3) 地址排序（连接优先级）

- 同子网 IPv4 > 其它 IPv4 > IPv6
- 可选：对 `addresses` 做一次 TCP quick connect 预热（< 50ms），仅在需要投递时执行（避免发现阶段过重）

---

## 在线/离线判定（TTL + 软硬阈值）

### 推荐参数

- mDNS 记录 TTL：120s（由发布方决定）
- 目录剔除阈值：  
  - **Soft stale**：`lastSeenAt + 6s`（UI 显示“可能离线/变灰”）  
  - **Hard expire**：`lastSeenAt + 12s`（从列表移除）

> 解释：会议场景希望离线反应快（拔网线/合盖），但也要容忍短暂抖动。6/12 秒是一组偏激进但体验好的默认值，可配置。

### 关键策略

- **只用 mDNS TTL 不足**：很多实现 TTL 较长，离线感知会太慢；因此使用“最近看到/心跳”作为主依据。
- **Goodbye 优先**：若收到 TTL=0 的 goodbye，立即标记离线。

---

## 性能与效率要求

- 解析应为按需触发：browse 到新实例才 resolve；更新时增量解析。
- 目录维护定时器间隔建议 1s（轻量），每次仅扫描过期窗口内的 peer。
- `ResolvedPeer` 结构应避免大对象频繁分配（字符串缓存、tags 数组复用）。

---

## 错误与边界

- 解析失败（临时）：记录失败次数与退避重试（500ms, 1s, 2s，上限 10s）。
- `id` 冲突（极少）：以最新 `lastSeenAt` 覆盖，但保留旧记录用于诊断日志。

---

## 测试用例（xUnit）

### 用例 1：SRV/TXT/A 组合解析成功
- **Given**：模拟 SRV + TXT + A 记录
- **Expect**：输出 `ResolvedPeer` 字段齐全，addresses 有序

### 用例 2：离线剔除（软/硬阈值）
- **Given**：peer lastSeenAt=now-7s
- **Expect**：状态为 stale（UI 灰）
- **Given**：peer lastSeenAt=now-13s
- **Expect**：从目录移除

### 用例 3：Goodbye 立即离线
- **Given**：收到 TTL=0 事件
- **Expect**：立即触发 PeerExpired（或 PeerRemoved）

### 用例 4：解析失败退避
- **Given**：连续失败 3 次
- **Expect**：重试间隔递增，且不超过上限

