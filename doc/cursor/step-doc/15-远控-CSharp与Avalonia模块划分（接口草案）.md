# 15-远控-C# 与 Avalonia 模块划分（接口草案）

> 目的：在现有 `XTransferTool`（Avalonia UI）与 `step-doc` 中 **12/13/14**（信令、输入、指标）之间，落一层**可替换实现**的 C# 抽象，便于分阶段实现（先 UI 状态机 + Mock，再接 WebRTC / 原生采集与注入）。  
> 约定：**gRPC 契约以 12/13/14 为准**；本文只定义 **客户端侧** 的领域接口与 UI 绑定方式。

---

## 1. 与 step-doc 的对应关系

| step-doc | 领域职责 | 客户端抽象（本文） |
|---|---|---|
| `12-远控-会话信令` | CreateSession、Offer/Answer、Trickle ICE | `IRemoteSignalingClient` |
| `13-远控-输入注入` | 双向流 RemoteInputEvent/Ack | `IRemoteInputChannel` |
| `14-远控-性能指标` | SubscribeStats 流 | `IRemoteStatsSource` |
| `05/06` 控制通道 | Handshake、事件订阅 | `IControlChannel`（远控只消费其中子能力） |

媒体（视频）**不**走 gRPC 大块流（见 `00-总览` 中 Data Channel 假设），由 **WebRTC** 承载；gRPC 只负责信令与输入/统计等控制面。

---

## 2. 建议解决方案结构（单仓库内分层）

在 `XTransferTool/` 旁或子目录中按需拆分（先可放在一个项目里用 `partial`/文件夹隔离，体量变大再拆类库）：

```
XTransferTool/                    # Avalonia 宿主 + Views/ViewModels
XTransferTool.Core/               # 可选：领域模型、接口、状态机（无 UI 依赖）
XTransferTool.Control.Grpc/       # 可选：gRPC 生成的客户端 + IControlChannel 实现
XTransferTool.Remote.WebRtc/      # 可选：WebRTC + 视频渲染桥接（重依赖隔离）
XTransferTool.Platform.Windows/   # 可选：WGC 采集、SendInput
XTransferTool.Platform.macOS/     # 可选：ScreenCaptureKit、CGEventPost
```

**原则**：`Core` 不引用 Avalonia；`Remote.WebRtc` 不引用 Views；平台项目仅被宿主根据 `RuntimeInformation` 或 `TargetFramework` 条件引用。

---

## 3. 核心领域模型（DTO，UI 与实现共用）

```csharp
// 命名空间示例：XTransferTool.Core.Remote

public enum RemoteSessionState
{
    Idle,
    AwaitingAcceptance,   // CreateSession 已发，等对端/用户确认
    Negotiating,          // Offer/Answer + ICE
    Connected,
    Disconnecting,
    Failed
}

public enum RemoteControlMode
{
    ViewOnly,
    Control
}

public sealed record RemotePreference(
    string QualityPreset,   // "smooth" | "balanced" | "clear"
    string? MaxResolution   // null | "1920x1080" 等
);

public sealed record RemoteSessionInfo(
    string SessionId,
    string PeerId,
    RemoteControlMode Mode,
    RemotePreference? Preference
);

public sealed record RemoteStatsSnapshot(
    long TsMs,
    int RttMs,
    double Fps,
    double BitrateMbps,
    double? PacketLoss,
    string? Resolution
);
```

事件类型（供 UI 订阅）建议统一：

```csharp
public abstract record RemoteUiEvent;

public sealed record RemoteStateChanged(RemoteSessionState State, string? Reason) : RemoteUiEvent;
public sealed record RemoteStatsUpdated(RemoteStatsSnapshot Stats) : RemoteUiEvent;
public sealed record RemoteVideoFrameAvailable(/* 平台相关句柄或像素缓冲，见第 6 节 */) : RemoteUiEvent;
```

---

## 4. 客户端接口草案（与 gRPC 12/13/14 对齐）

### 4.1 信令：`IRemoteSignalingClient`

封装 `RemoteControlService` 的 CreateSession、Offer、TrickleIce；对外用 **async/Task** + **回调/Channel** 推送 Answer 与对端 ICE。

```csharp
public interface IRemoteSignalingClient
{
    /// <summary>建立会话请求；对端接受后进入 Negotiating。</summary>
    Task<RemoteSessionInfo> CreateSessionAsync(
        string toPeerId,
        RemoteControlMode mode,
        RemotePreference? preference,
        CancellationToken ct = default);

    /// <summary>提交本地 SDP Offer，返回 Answer（或抛异常/失败原因）。</summary>
    Task<string> ExchangeOfferAnswerAsync(
        string sessionId,
        string sdpOffer,
        CancellationToken ct = default);

    /// <summary>发送 ICE 候选；实现内应做 50–100ms 批量 flush（见 12 文档）。</summary>
    void EnqueueLocalIceCandidate(string sessionId, string candidateJsonOrLine);

    /// <summary>订阅对端 ICE（直到会话结束或取消）。</summary>
    IAsyncEnumerable<string> SubscribeRemoteIceCandidatesAsync(
        string sessionId,
        CancellationToken ct = default);
}
```

实现类：`GrpcRemoteSignalingClient`，内部调用生成的 gRPC stub。

### 4.2 媒体管线（WebRTC 侧）：`IRemoteMediaSession`

不暴露 SDP 细节给 UI；UI 只关心「开始协商 / 已连接 / 断开」与「视频表面」。

```csharp
public interface IRemoteMediaSession : IAsyncDisposable
{
    RemoteSessionInfo Session { get; }

    /// <summary>在已有 sessionId 下，绑定信令交换与 ICE，完成 PeerConnection。</summary>
    Task StartAsync(IRemoteSignalingClient signaling, CancellationToken ct = default);

    Task StopAsync();

    /// <summary>本地是否为控制端（发送 Offer 的一方）。由会话角色决定。</summary>
    bool IsController { get; }
}
```

**被控端**在 `StartAsync` 内：创建 PeerConnection、设置 Answer 流程、附加 **本地 VideoTrack**（来自 `IScreenVideoSource`）。  
**控制端**：订阅 **远端 VideoTrack**，产出帧到 UI（见第 6 节）。

### 4.3 屏幕采集（平台实现）：`IScreenVideoSource`

```csharp
public interface IScreenVideoSource : IAsyncDisposable
{
    /// <summary>绑定到 WebRTC 或编码器；具体类型与所选 WebRTC 库相关。</summary>
    object /* IVideoSource or similar */ AttachToMediaSession(IRemoteMediaSession session);
}
```

- Windows：`WindowsScreenVideoSource`（WGC）
- macOS：`MacScreenVideoSource`（ScreenCaptureKit）

### 4.4 输入注入（控制端发、被控端收）：`IRemoteInputChannel`

对齐 `13`：双向流；**UI 不直接调 gRPC**，只通过 `IRemoteInputSession`。

```csharp
public interface IRemoteInputSession : IAsyncDisposable
{
    /// <summary>控制端：把画布坐标系下的指针/按键发到被控端。</summary>
    ValueTask SendMoveAsync(double nx, double ny, CancellationToken ct = default);
    ValueTask SendButtonAsync(/* ... */, CancellationToken ct = default);
    ValueTask SendKeyAsync(/* ... */, CancellationToken ct = default);

    /// <summary>被控端：启动消费循环（内部连接 InputStream）。</summary>
    Task RunHostAsync(CancellationToken ct);
}
```

实现：`GrpcRemoteInputSession`（封装 client/server 双向流，按 13 的合并/限速策略在 **被控端** 执行）。

### 4.5 性能指标：`IRemoteStatsSource`

对齐 `14`：订阅 gRPC `SubscribeStats`，转换为 `RemoteStatsSnapshot`。

```csharp
public interface IRemoteStatsSource
{
    IAsyncEnumerable<RemoteStatsSnapshot> SubscribeAsync(
        string sessionId,
        int intervalMs = 500,
        CancellationToken ct = default);
}
```

---

## 5. UI 编排：`RemoteDesktopViewModel` 状态机（Avalonia）

建议单一 ViewModel 持有用户可见状态，内部组合上述接口（构造函数注入或工厂）。

### 5.1 用户操作流程（与当前 UI 对齐）

1. **选择设备**（列表选中 `PeerId`）→ `SelectedPeerId`
2. **连接** → `StartRemoteAsync`
   - 调 `CreateSessionAsync` → 若需本机确认则弹窗（后续）
   - 创建 `IRemoteMediaSession`，`StartAsync(signaling)`
   - 控制端：`SubscribeStats` 启动，`RemoteStatsUpdated` 驱动 HUD
3. **控制模式** → 仅当 `Mode==Control` 且权限 OK 时，启用 `IRemoteInputSession` 与画布指针捕获
4. **断开** → `StopAsync` + 释放媒体

### 5.2 ViewModel 伪代码结构

```csharp
public sealed partial class RemoteDesktopViewModel : ViewModelBase
{
    private readonly IRemoteSignalingClient _signaling;
    private readonly IRemoteMediaSessionFactory _mediaFactory;
    private readonly IRemoteStatsSource _stats;
    private IRemoteMediaSession? _media;
    private CancellationTokenSource? _statsCts;

    [ObservableProperty] private RemoteSessionState _sessionState = RemoteSessionState.Idle;
    [ObservableProperty] private RemoteStatsSnapshot? _lastStats;
    [ObservableProperty] private string? _selectedPeerId;
    // VideoSurface 绑定：见第 6 节

    [RelayCommand]
    private async Task ConnectAsync() { /* ... */ }

    [RelayCommand]
    private async Task DisconnectAsync() { /* ... */ }
}
```

---

## 6. 视频渲染与 Avalonia 集成（实现选型说明）

**目标**：控制端把 WebRTC 解码后的帧显示在 `RemoteDesktopView.axaml` 的画布区域。

可选路径（按推荐顺序）：

1. **纹理/共享句柄**（最佳性能，工程量最大）  
   - 若 WebRTC 库提供 GPU 纹理或平台原生视图，考虑 `NativeControlHost` 嵌入原生视频视图（平台分支）。

2. **I420/RGBA → WriteableBitmap**（原型友好）  
   - 在 `IRemoteMediaSession` 实现中订阅视频帧，转换为 `Bitmap`/`WriteableBitmap`，在 UI 线程 `InvalidateVisual` 或绑定 Image。  
   - 注意：需限制分辨率与拷贝次数，避免 CPU 顶满（与 `99-通用-性能与测试基线` 一致）。

3. **独立窗口渲染**（临时方案）  
   - 仅用于调试 WebRTC，不作为最终产品 UI。

具体 API 依赖所选 WebRTC 包（如 `SIPSorcery` + 自定义编码链，或 libwebrtc 封装），**接口草案保持 `IRemoteMediaSession` 稳定，内部可替换**。

---

## 7. 权限与系统差异（UI 提示文案）

| 平台 | 采集 | 输入注入 |
|---|---|---|
| macOS | 录屏权限（ScreenCaptureKit） | 辅助功能权限（CGEventPost） |
| Windows | 无额外权限或按版本提示 | 通常可直接 SendInput（注意 UAC 提升场景） |

建议在 `ConnectAsync` 失败时，将 gRPC `reason` 映射为本地化短文案，并在设置页提供「权限说明」链接。

---

## 8. Mock 与测试（与 12/13/14 的 xUnit 思路一致）

- **MockSignalingClient**：内存中模拟 Offer/Answer、ICE 队列，用于 UI 状态机测试。  
- **MockMediaSession**：不产生真实视频，仅切换 `Connected` 并定时伪造 `RemoteStatsSnapshot`。  
- **集成测试**：两进程本机回环，验证 Handshake + CreateSession + Offer/Answer（不强制首版带视频）。

---

## 9. 实施顺序建议（在 Phase 5 内再拆）

1. 定义 `Core` 中接口 + `RemoteSessionState` + Mock。  
2. Avalonia：`RemoteDesktopViewModel` 绑定状态与 HUD（无真实画面也可演示）。  
3. 实现 `GrpcRemoteSignalingClient`（仅 12）。  
4. 接入 WebRTC：`IRemoteMediaSession` 首版「仅接收远端黑屏/测试图案」验证延迟路径。  
5. 平台采集 + 编码接入；最后接 `IRemoteInputChannel`（13）与 Stats（14）。

---

## 10. 文档维护

- 若 gRPC 字段在 12/13/14 中变更，优先改 proto 与对应 step-doc，再同步本文中的**方法语义**（不必逐字复制字段）。  
- 本文编号 **15**，已在 `00-总览与执行顺序.md` 索引中登记。
