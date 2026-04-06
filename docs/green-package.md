## 目标

生成“绿色包”目录，满足：

- 单个可执行文件（.exe / macOS 可执行）包含 .NET 依赖
- ffmpeg 等工具随应用一起打包（不依赖用户安装）
- 配置、数据、日志全部在可执行文件同级目录（可整体拷贝到任意位置运行）

## 目录结构（运行后会自动生成）

```
XTransferTool/
  XTransferTool.exe
  config/
    default/
      appsettings.json
  data/
    default/
      recv/
  log/
    default/
      xtransfer-YYYYMMDD.log
  native/
    windows/
      ffmpeg/
        ffmpeg.exe
    macos/
      ffmpeg/
        ffmpeg
      sck_capture/
        sck_capture
```

说明：

- `config/default/appsettings.json` 会在首次启动时生成
- `log/default` 会在首次启动时生成
- `native/...` 下的工具文件如果没有提前放入工程，会尝试从内嵌资源解压；如果资源也不存在则保持缺失
- 绿色包模式的“同级目录”指的是可执行文件所在目录（单文件发布时 AppContext.BaseDirectory 可能指向临时解压目录，代码已改为使用可执行文件路径作为根目录）

## 1) 准备内嵌工具（必须）

### Windows

把 `ffmpeg.exe` 放到：

```
XTransferTool/native/windows/ffmpeg/ffmpeg.exe
```

要求：能运行 `ffmpeg -version`。

### macOS

把 ffmpeg 放到：

```
XTransferTool/native/macos/ffmpeg/ffmpeg
```

把 ScreenCaptureKit helper 放到：

```
XTransferTool/native/macos/sck_capture/sck_capture
```

并确保有执行权限：

```bash
chmod +x native/macos/ffmpeg/ffmpeg
chmod +x native/macos/sck_capture/sck_capture
```

## 2) 打包（Windows 绿色包）

在仓库根目录执行：

```powershell
cd d:\workspace\rust\workspace\LanTransferC
dotnet publish .\XTransferTool\XTransferTool.csproj -c Release -r win-x64 `
  -p:SelfContained=true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

输出目录：

```
XTransferTool\bin\Release\net10.0\win-x64\publish\
```

把该目录整体复制出来，就是绿色包目录。

首次运行时会在同级目录自动生成 `config/`、`data/`、`log/`、`native/` 等。

## 3) 打包（macOS 绿色包）

在 macOS 上执行：

```bash
dotnet publish ./XTransferTool/XTransferTool.csproj -c Release -r osx-arm64 \
  -p:SelfContained=true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

输出目录：

```
XTransferTool/bin/Release/net10.0/osx-arm64/publish/
```

把该目录整体复制出来，就是绿色包目录。

首次运行时会在同级目录自动生成 `config/`、`data/`、`log/`、`native/` 等。

## 4) 验证

- 启动后检查同级目录是否出现：
  - `config/default/appsettings.json`
  - `log/default/xtransfer-*.log`
- 日志里应包含：
  - `logger initialized ... folder=...`
- 远程桌面编码/解码工具：
  - Windows：`native/windows/ffmpeg/ffmpeg.exe`
  - macOS：`native/macos/ffmpeg/ffmpeg`
