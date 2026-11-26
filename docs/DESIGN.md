# SnapOCR 设计说明

这份文档记录实现时的取舍和踩过的坑，给以后改代码的人（包括我自己）看。

## 1. 环境探测

版本号不要凭记忆写。每次换机器先跑：

```powershell
dotnet --info
dotnet workload list
Get-AppxPackage -Name "Microsoft.WindowsAppRuntime*" | Select-Object Name, Version, Architecture
Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\Include"

$t = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
$t::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag + " | " + $_.DisplayName }
```

开发机实测（2026-11）：

```
.NET SDK: 10.0.403
Windows SDK Include: 10.0.26100.0
Microsoft.WindowsAppRuntime.2  2.7.0.0  X64 / X86
OCR: zh-Hans-CN | 简体中文(中国大陆)
```

`dotnet workload list` 显示没有安装任何 workload —— 构建这个项目不需要额外 workload。

## 2. 为什么覆盖层是「冻结帧」而不是透明窗口

Windows App SDK **2.7.0 移除了 `TransparentBackdrop`**。`Microsoft.UI.Xaml.Media` 下现在只有 `MicaBackdrop` 和 `DesktopAcrylicBackdrop`，没有真正让窗口背景全透明的类型。

用反射确认过（`Microsoft.WinUI.dll` 里所有 `*Backdrop` 类型）：

```
Microsoft.UI.Xaml.Media.MicaBackdrop
Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop
Microsoft.UI.Xaml.Media.SystemBackdrop
Microsoft.UI.Composition.CompositionBackdropBrush
```

所以覆盖层的做法是：热键按下瞬间把所有屏幕 BitBlt 成 BGRA，作为背景图画满窗口，再用四块 `#A6000000` 矩形把选区外压暗。

这个绕路反而带来三个好处：

- 视觉与系统截图工具一致（画面冻结），框选时不会因为桌面在动而错位；
- OCR 用的就是用户看到的那张图，所见即所得；
- 不需要逐帧重抓。

代价是每屏要占一份整屏位图内存（2560×1600×4 ≈ 16 MB），所以做了缓冲复用（见第 7 节）。

## 3. 抓屏必须赶在覆盖层显示之前

BitBlt 读的是屏幕 DC，覆盖层一旦显示就会被拍进去（连遮罩一起），识别结果会变成灰底。

所以顺序被严格固定：

```
BitBlt 全屏 → 构造 SoftwareBitmap → 显示覆盖层
```

冷启动第一次抓屏要建 DC 和 DIB，会多花几十毫秒，所以 App 启动后立刻在后台跑一次 `ScreenCapture.Warmup()`，把那部分成本挪到用户按热键之前。

实测：预热前首次抓屏 ~59 ms，预热后稳定 26–30 ms。

## 4. 坐标换算：窗口 DPI ≠ 显示器 DPI

`DpiHelper.DipToPhysical(dip, dpi)` 是唯一换算入口，禁止在别处再写一遍 `* dpi / 96`。单测覆盖 96/120/144/168/192/216/240 七档 DPI、负坐标、往返一致性。

踩过的坑：一开始用 `GetDpiForMonitor` 拿到显示器 DPI 来做换算，结果框选整体偏了一个缩放倍率（拖 450 收到 672，正好 ×1.5）。

原因：XAML 的 DIP 基准由**窗口** DPI 决定，而窗口 DPI 不一定等于显示器 DPI（混合 DPI、远程会话、虚拟显示器都可能不一致）。正确做法是先把窗口 `MoveAndResize` 到目标屏，再读 `GetDpiForWindow`：

```csharp
AppWindow.MoveAndResize(new RectInt32(monitor.Left, monitor.Top, monitor.Width, monitor.Height));

var windowDpi = DpiHelper.GetDpiForWindowSafe(_hwnd);   // 不是 monitor.Dpi
_scale = DpiHelper.ScaleFactor(windowDpi);
_dipWidth = monitor.Width / _scale;
```

还有一个容易误判的点：**探针进程必须自己声明 Per-Monitor V2**，否则 `GetMonitorInfo` 返回的是被系统缩放过的逻辑坐标（2560×1600 会读成 1707×1067），和主程序收到的物理像素对不上，看起来像主程序算错了。

## 5. 中文 OCR 会逐字吐空格

`Windows.Media.Ocr` 对中文的实际输出：

```
公 民 身 份 证 号 码
```

直接用就是废的。`TextComposer.CollapseCjkSpaces` 把「CJK + 空格 + CJK」中间的空格删掉，同时保留中英文之间的空格（`DeepSeek 开放平台` 仍然可读）。连续多个空格一起处理。

这条规则有单测，覆盖了「中   文」这种多空格情况。

## 6. 文本拼接规则

`TextComposer.Compose`：

1. 行间只用 `\n`，绝不插入空格 —— 中文行间加空格是最常见的错误；
2. 每行首尾 `Trim`；
3. 空行丢弃；
4. 每行再做一次 CJK 空格折叠。

英文段落会保留硬换行（符合「保留换行」的要求），不做自动重排。

## 7. 内存：16 MB 缓冲必须复用

每次抓屏是 2560×1600×4 ≈ 16 MB。第一版每次都 `new byte[]`，连续触发 20 次后工作集从 261 MB 涨到 412 MB（LOH 分配压力）。

改成每屏复用一个 `Surface`（DC + DIB + byte[]），`CaptureAll` 只做 `BitBlt` + `Marshal.Copy` 到已有缓冲：

```csharp
if (surface.Buffer is null || surface.Buffer.Length != length)
{
    surface.Buffer = new byte[length];
}
Marshal.Copy(surface.Bits, surface.Buffer, 0, length);
```

改完 20 次压测内存稳定在 228–231 MB，托管堆恒定 16 MB。

注意：这个缓冲是跨帧复用的，**不能跨帧持有 `CapturedFrame.Pixels`**。当前流程有 `_busy` 闸门保证串行，所以安全。

## 8. 剪贴板：两条写入路径 + 两条读取路径

写入（按顺序尝试，任一成功即返回，**成功后才允许弹提示**）：

1. `Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(DataPackage)`，失败重试 3 次（剪贴板被占用会抛 `CLIPBRD_E_CANT_OPEN`）；
2. Win32 `OpenClipboard` + `EmptyClipboard` + `SetClipboardData(CF_UNICODETEXT)` 兜底。

读取（P6「跟随系统截图」）：

1. WinRT `DataPackageView.GetBitmapAsync()` —— 系统截图工具、主流截图软件走 OLE，能直接读；
2. 裸 `CF_DIB` 兜底 —— 有些老程序只写这个格式，此时 `AvailableFormats` 是空的，必须用 Win32 读。

第 2 条是实测发现的：用 Win32 写 CF_DIB 后，`Clipboard.GetContent().AvailableFormats` 返回空集合。真实场景（`Win+Shift+S`）不受影响，但补上兜底更稳。

自己写剪贴板会触发自己监听的 `WM_CLIPBOARDUPDATE`，用 `GetClipboardSequenceNumber()` 记录写入后的序号，下次收到消息比对跳过。

## 9. 热键

- `RegisterHotKey` + `SetWindowSubclass` 拦 `WM_HOTKEY`（WinUI 3 的窗口不暴露 WndProc）；
- 子类化回调的委托必须由**静态字段保活**，否则 GC 回收后崩溃；
- 同时有多个功能（热键、托盘、剪贴板监听）在同一 HWND 上子类化，各自用不同的 `uIdSubclass` 和独立的静态字典；
- 注册失败（`ERROR_HOTKEY_ALREADY_REGISTERED = 1409`）时自动降级到 `Ctrl+Shift+T`，并把实际生效的热键写回配置，否则重启又会撞一次。

开发机实测 `Win+Shift+T` 返回 1409 —— 这个组合在不少机器上被别的软件占了，所以降级逻辑不是摆设。

## 10. 打包相关的坑

**`startupTask` 必须用 `desktop:` 命名空间。**

写成 `uap:Extension` 时 `dotnet build` 完全正常，`Add-AppxPackage -Register` 才报：

```
错误 0x80080204: Appx 包的清单无效。
'windows.startupTask' 违反了 ... 的 enumeration 约束
```

正确写法：

```xml
<desktop:Extension Category="windows.startupTask" Executable="SnapOCR.exe"
                   EntryPoint="Windows.FullTrustApplication">
  <desktop:StartupTask TaskId="SnapOCRAutoStart" Enabled="false" DisplayName="SnapOCR" />
</desktop:Extension>
```

**`Directory.Build.props` 的 XML 注释里不能出现连续两个减号。**

写 `dotnet --info` 这种命令进注释会让 MSBuild 判定文件无效并**静默忽略整个文件**，报错只表现为「TargetFramework 为空」，非常难查。

**csproj 里不要重复声明 `<Manifest Include="$(ApplicationManifest)" />`。**

只留 `<ApplicationManifest>app.manifest</ApplicationManifest>` 即可，重复声明没有收益。可以用 `mt.exe` 验证嵌入结果：

```powershell
& "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\mt.exe" `
  -nologo "-inputresource:SnapOCR.exe;#1" "-out:embedded.manifest"
```

## 11. 调试

- **Debug 构建**会把流程写进 `%TEMP%\snapocr-trace.log`（UTF-8）：热键→可见耗时、抓屏耗时、选区矩形、裁剪尺寸、OCR 行数/字数、剪贴板写入结果、托管堆/工作集、`ApplyGeometry` 的 DPI 对照。
- **Release 构建**默认不写任何文件；要临时开，设环境变量 `SNAPOCR_TRACE=1` 再启动。

两个诊断工具（都直接链接生产代码，跑的是同一条链路）：

- `tools/OcrProbe`：不开 UI，输出识别文本、耗时、整屏截图（BMP + PNG + 1:1 裁切）。
- `tools/UiProbe`：端到端验收。读取应用真实配置的热键 → 模拟按键 → 用亮度判断遮罩是否生效 → 在 OCR 找到的文字行上模拟拖拽 → 读剪贴板比对。

```powershell
dotnet run --project tools/UiProbe -- --stress 20
dotnet run --project tools/UiProbe -- --snapshot .\shots
dotnet run --project tools/UiProbe -- --test-hotkey
dotnet run --project tools/UiProbe -- --clipboard-p6        # OLE 图片路径
dotnet run --project tools/UiProbe -- --clipboard-p6-dib    # 裸 CF_DIB 路径
```

## 12. 没有验证到的部分

- **多显示器 / 跨屏框选**：开发机只有一块屏。代码按「每屏一个窗口 + 全局物理坐标」实现，负坐标有单测，但没有在真实双屏上跑过。
- **英文识别**：只装了 `zh-Hans-CN` 语言包，无法验证英文。代码路径完全相同。
- **托盘菜单点击链路**：`Shell_NotifyIcon` 返回 `True`（图标确实加进去了），但 Win11 折叠进溢出区，自动化点击容易误触系统面板，四项菜单是人工验收。
- **开机自启的真实重启验证**：`RequestEnableAsync()` 返回成功，没有重启系统确认。
