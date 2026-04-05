# 11-收件箱-查询与未读（InboxQuery/MarkRead）

## 目标与场景

满足需求：
- 接收端工具上显示“文件名 + 留言”
- 支持查看历史收件、筛选未读、按文件名/发送者搜索

---

## 数据模型（建议）

### InboxItemRecord

| field | type | 说明 |
|---|---|---|
| `inboxId` | string | 主键（UUID） |
| `receivedAtMs` | int64 | 接收时间 |
| `fromId` | string | 发送者 id |
| `fromDisplay` | string | 发送者展示（昵称+标签摘要） |
| `transferId` | string | 关联传输 id |
| `fileName` | string | 文件名 |
| `savedPath` | string | 落盘路径 |
| `message` | string | 留言 |
| `status` | string | unread/read/failed |

---

## 接口定义（本地接口 + 可选远程）

> 收件箱通常是本地 UI 查询本地存储；不需要走网络。  
> 但为了“可测试/可替换存储”，建议以接口形式定义。

### IInboxRepository（本地）

#### `Query(QueryInboxRequest) -> QueryInboxResponse`

**QueryInboxRequest**

| field | type | required | 说明 |
|---|---|---:|---|
| `status` | string | no | unread/read/all |
| `search` | string | no | 文件名/发送者/留言模糊 |
| `fromId` | string | no | 指定发送者 |
| `limit` | int32 | yes | 默认 50，上限 200 |
| `offset` | int32 | yes | 分页 |

**QueryInboxResponse**

| field | type | 说明 |
|---|---|---|
| `items[]` | InboxItemRecord | 结果 |
| `total` | int32 | 总数（可选，代价较高可不返回） |

#### `MarkRead(inboxId) -> void`

#### `AddRecord(record) -> void`

---

## 存储与性能建议

- **存储**：SQLite（推荐）或 LiteDB（可选）
- **索引**：
  - `receivedAtMs`（排序）
  - `status`
  - `fromId`
  - `fileName`（like 查询可选 FTS）
- **查询性能目标**：
  - `Query(limit=50)` P99 < 30ms（本机）
- **写入策略**：
  - 接收完成时写入 1 条记录（不要每个 chunk 写）
  - 批量写入/事务（群发接收时）

---

## 测试用例（xUnit）

### 用例 1：写入后可查询
- **Given**：AddRecord 1 条
- **Expect**：Query(all,limit=50) 返回该条

### 用例 2：未读筛选与 MarkRead
- **Given**：插入 3 unread
- **When**：MarkRead 其中 1 条
- **Expect**：Query(unread) 返回 2 条，Query(read) 返回 1 条

### 用例 3：搜索（文件名/留言）
- **Given**：fileName="会议议程.pptx"，message="请投到大屏"
- **Expect**：search="大屏" 命中

### 用例 4：性能基线（本地）
- **Given**：预置 10k 条记录
- **Expect**：Query(limit=50,offset=0) 平均 < 10ms（本机基准；CI 可只断言 < 200ms）

