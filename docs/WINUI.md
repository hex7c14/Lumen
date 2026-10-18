# WinUI 3 构建 / 运行 / 截图验证

> 本文是本机（无 Visual Studio、无 Windows SDK 独立安装、无管理员、无开发者模式）上
> **WinUI 3 + Windows App SDK 2.5.1** 从零到"窗口显示 + Mica + 自定义标题栏 + 截图"的
> 实测配方。所有命令和输出均来自 2026-10-17 在本机的真实执行。
>
> 相关产物：
> - 冒烟应用：`spike/winui-smoke/`（unpackaged + self-contained，可运行）
> - 一键脚本：`scripts/winui-spike.ps1`
> - 截图证据：`spike/winui-smoke/screenshot.png`、`spike/winui-smoke/screenshot-printwindow.png`

---

## 0. 结论（TL;DR）

1. **唯一可靠的运行方式**：`WindowsPackageType=None`（unpackaged）+ `WindowsAppSDKSelfContained=true`
   + `SelfContained=true`（.NET 也自包含）+ `Platform/PlatformTarget=x64|ARM64`
   + `RuntimeIdentifier=win-x64|win-arm64`，`dotnet build` 之后**直接运行 exe**。
   不需要 Dev Mode、不需要管理员、不需要 MSIX 部署、不需要 VS/Windows SDK。
2. **Packaged（MSIX）开发部署在本机不可用**：`winapp run` 在 packaged 项目上构建成功后失败，错误为
   `Developer Mode is not enabled on this machine. Please enable Developer Mode and try again.`。
   MSIX 生产打包本身不需要 Dev Mode，但本机不能安装/运行 packaged 应用。
3. **Windows App SDK 2.5.1 与 .NET 10（`net10.0-windows10.0.26100.0`）完全兼容**，一次构建成功、0 警告 0 错误。
4. **Mica、DesktopAcrylic、`TitleBar` 控件、`ExtendsContentIntoTitleBar`/`SetTitleBar` 在本机全部可用**
   （Win11 build 26200）。Mica 在深色壁纸下是 #202020 左右的深灰，"看起来像纯黑"不是失败。
5. **.NET 必须自包含**：本机 .NET 10 是免安装目录版（`C:\Users\0x7c14\dotnet10`，不在 PATH、
   没有 `DOTNET_ROOT`、没有注册表安装位置、没有 `C:\Program Files\dotnet`），框架依赖的 WinUI exe 启动会弹
   "You must install .NET" 对话框。
6. **截图可行且已自检**：`CopyFromScreen`（主）+ `PrintWindow(PW_RENDERFULLCONTENT)`（备）都拿到了
   真实窗口内容（886×613，非黑屏）；`winapp ui screenshot` / `winapp find-ui` / `winapp find-api` 在无 Dev Mode 下也可用。

---

## 1. 环境事实（实测）

| 项 | 值 |
|---|---|
| OS | Windows 11 专业版 10.0.26200.0（build 26200） |
| .NET SDK | **10.0.402**，安装在 `C:\Users\0x7c14\dotnet10\`（`sdk\10.0.402`）；**不在任何 PATH 里** |
| .NET Runtime | 仅 `C:\Users\0x7c14\dotnet10\shared\Microsoft.NETCore.App\10.0.13`（目录版，无注册、无 DOTNET_ROOT） |
| WinApp CLI | **0.7.1**，`C:\Users\0x7c14\AppData\Local\Microsoft\WindowsApps\winapp.exe`（PATH 刷新后可用） |
| 开发者模式 | **未启用**：`HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock\AllowDevelopmentWithoutDevLicense` 不存在 |
| 管理员权限 | 无（不尝试提权） |
| Visual Studio | 未安装 |
| Windows SDK | 未独立安装（`C:\Program Files (x86)\Windows Kits\10` 不存在，无 `mt.exe`/`makeappx.exe` 系统安装版） |
| PowerShell | 只有 **Windows PowerShell 5.1**（`powershell.exe`），没有 PowerShell 7（无 `pwsh`） |
| 桌面会话 | Session 1，Active/交互式，1920×1080，可启动 GUI 并截屏 |
| GPU | RTX 5060 Laptop + AMD iGPU（与本验证无关） |
| 网络 | nuget.org / github.com 可访问；首次 restore 正常下载 |

**每个 shell 都要先补 PATH**（dotnet 与 winapp 不在系统 PATH 的默认值里）：

```powershell
$env:Path = "C:\Users\0x7c14\dotnet10;" +
    [Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
    [Environment]::GetEnvironmentVariable('Path','User')
```

环境自检命令（复现用）：

```powershell
dotnet --version                                  # 10.0.402
dotnet --list-sdks                                # 10.0.402 [C:\Users\0x7c14\dotnet10\sdk]
winapp --version                                  # 0.7.1
(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
    -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense  # 空 = 未启用
[Environment]::OSVersion.Version                  # 10.0.26200.0
Test-Path 'C:\Program Files (x86)\Windows Kits\10' # False
```

> 为什么没有 Windows SDK 也能构建 WinUI 3：`net10.0-windows10.0.26100.0` 的 WinRT 投影由
> .NET SDK 通过 NuGet 自动解析（`Microsoft.Windows.SDK.NET.Ref 10.0.26100.57`）；
> `Microsoft.Windows.SDK.BuildTools` NuGet 包自带 `makepri.exe` 等工具。二者都不依赖 VS/系统 Windows SDK。

---

## 2. 最终可用配方（`spike/winui-smoke/`）

### 2.1 技术选型（版本全部锁定，保证可复现）

| 项 | 值 |
|---|---|
| TFM | `net10.0-windows10.0.26100.0`（`TargetPlatformMinVersion` 10.0.17763.0） |
| Platform / PlatformTarget | `x64` / `x64` 或 `ARM64` / `ARM64`（**禁止 AnyCPU**，否则 0x8007000B bad image；默认跟随本机架构） |
| RuntimeIdentifier | `win-x64` / `win-arm64`，`SelfContained=true` |
| 部署形态 | `WindowsPackageType=None`（unpackaged），`WindowsAppSDKSelfContained=true` |
| Microsoft.WindowsAppSDK | **2.5.1**（2.x 起为元包，自动带以下子包） |
| 　├ Microsoft.WindowsAppSDK.WinUI | 2.3.9（WinUI 控件 + `MicaBackdrop` 等） |
| 　├ Microsoft.WindowsAppSDK.Foundation | 2.3.12 |
| 　├ Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.9（`MicaController` 等） |
| 　├ Microsoft.WindowsAppSDK.Base / Runtime | 2.0.4 / 2.5.1 |
| 　└ 其他 | DWrite 2.1.0、Widgets 2.0.5、AI 2.5.5、ML 2.1.94、Search 2.5.5 |
| Microsoft.Windows.SDK.BuildTools | **10.0.26100.9169** |
| Microsoft.Windows.SDK.BuildTools.WinApp | **0.7.1**（`dotnet run` 的 debug identity 钩子；本方案直接跑 exe，可留可删） |
| Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer | **0.7.1**（`PrivateAssets="all"`，官方推荐的 WinUI 分析器） |
| 传递依赖 | Microsoft.Web.WebView2 1.0.3719.77；Microsoft.Windows.SDK.NET.Ref 10.0.26100.57（SDK 自动） |
| .NET 运行时 | 10.0.13，随 exe 一起输出（.NET 自包含） |
| 构建输出 | `bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\`（ARM64：`bin\ARM64\...\win-arm64\`），约 **541 个文件 / 231 MB**（.NET 运行时 + WinAppSDK 全量组件自包含，含 AI/ML/Widgets 子包；体积偏大但可直接拷贝运行、无需任何机器级安装） |

### 2.2 csproj 关键片段（可直接抄进 Lumen.App）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RootNamespace>Lumen.App</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>

    <!-- ===== 无 VS / 无 Dev Mode 的唯一可靠组合 ===== -->
    <WindowsPackageType>None</WindowsPackageType>          <!-- unpackaged：不生成/部署 MSIX -->
    <EnableMsixTooling>false</EnableMsixTooling>           <!-- 不让 Package.appxmanifest 参与构建 -->
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>  <!-- WinAppSDK 运行时随包输出 -->
    <Platforms>x64;ARM64</Platforms>
    <PlatformTarget Condition="'$(Platform)' == 'ARM64'">ARM64</PlatformTarget>
    <PlatformTarget Condition="'$(PlatformTarget)' == ''">x64</PlatformTarget>  <!-- 绝不 AnyCPU -->
    <RuntimeIdentifier Condition="'$(Platform)' == 'ARM64'">win-arm64</RuntimeIdentifier>
    <RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == ''">win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>                    <!-- .NET 也自包含（本机 .NET 是目录版，必须） -->

    <!-- 注意：不要保留模板里的 <PublishProfile ...> 属性！
         它会隐式导入 Properties\PublishProfiles\win-x64.pubxml 中的
         <SelfContained>true</SelfContained>，覆盖你显式写的值（见 §7 坑 3）。 -->

    <UseWinUI>true</UseWinUI>
    <WinUISDKReferences>false</WinUISDKReferences>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <!-- 版本钉死，避免模板默认 Version="*" 浮动升级破坏可复现性 -->
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.9169" />
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.5.1" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools.WinApp" Version="0.7.1" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer" Version="0.7.1" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <Manifest Include="$(ApplicationManifest)" />
  </ItemGroup>
</Project>
```

### 2.3 `app.manifest`（unpackaged 必需，含 Win10 兼容声明 + PerMonitorV2 DPI）

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="Lumen.App"/>

  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- unpackaged 应用启用系统特性（含自定义标题栏）所必需 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>

  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

### 2.4 构建

```powershell
$env:Path = "C:\Users\0x7c14\dotnet10;" +
    [Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
    [Environment]::GetEnvironmentVariable('Path','User')

Set-Location C:\Users\0x7c14\Documents\deepseek-harness\default-workspace\spike\winui-smoke
dotnet build WinUISmoke.csproj -c Debug -p:Platform=x64 -v minimal
```

实测：首次 restore（含下载 Windows App SDK 各子包）13.57s，首次构建总耗时约 36s；
**删掉 `bin/` `obj/` 后重建**：restore 0.4s（包已缓存）+ build 13.8s，一键脚本端到端 19.7s；
之后增量构建约 12–15s。全程 **0 警告 0 错误**。输出目录（注意是 `Platform` + `RID` 两层）：

```
spike\winui-smoke\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WinUISmoke.exe
```

目录里能看到 `Microsoft.WindowsAppRuntime.dll`、`Microsoft.ui.xaml.dll`、`Microsoft.UI.Xaml.Controls.dll`、
`Microsoft.WindowsAppRuntime.Bootstrap.dll`、`coreclr.dll`、`hostfxr.dll` 等 —— 即 WinAppSDK 与 .NET 都已自包含，
换台同架构机器拷贝整个目录即可运行（不需要安装 WinAppSDK Runtime，也不需要 .NET）。

### 2.5 运行（唯一可靠方式）

```powershell
$exe = 'C:\Users\0x7c14\Documents\deepseek-harness\default-workspace\spike\winui-smoke\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WinUISmoke.exe'
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)   # 工作目录必须是 exe 目录
```

要点：
- **工作目录 = exe 目录**：`Assets/AppIcon.ico`、`*.pri` 都按应用目录解析；图标路径建议在代码里用
  `Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")` 拼绝对路径。
- 不要用 `winapp run` 的 packaged 路径（本机报 Dev Mode 错误）；`winapp run` 在 unpackaged 项目上虽然也能直接启动
  exe（实测 exit 0，返回 `{"ProcessId": ...}`），但脚本化首选 `dotnet build` + 直接启动 exe，最少依赖、行为最确定。
- 应用会把 Mica/DPI/标题栏状态写到 `bin\...\win-x64\smoke-status.txt`，便于脚本断言。

### 2.6 一键验证脚本（build + 启动 + 等待 + 截图 + 关闭）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\winui-spike.ps1
```

脚本（`scripts/winui-spike.ps1`，纯 ASCII，兼容 Windows PowerShell 5.1）做的事：

1. 自动定位 dotnet（默认 `-DotnetRoot C:\Users\0x7c14\dotnet10`，也可先在 PATH 里）；
2. 杀掉残留 `WinUISmoke` 进程（避免 exe 被占用导致 MSB3027/MSB3021）；
3. `dotnet build ... -c Debug -p:Platform=x64`；
4. 直接启动 exe（`UseShellExecute=false`，工作目录=exe 目录），轮询进程的顶层可见窗口；
5. 断言：窗口标题含 `Lumen`；`smoke-status.txt` 中 `micaSupported=True`、`backdrop=Mica`、
   `extendsContentIntoTitleBar=True`；客户区顶边相对窗口顶边偏移 ≤2px（= 自定义标题栏已生效）；
6. 截图：`CopyFromScreen` 主图 → `<project>/screenshot.png`，`PrintWindow(PW_RENDERFULLCONTENT)`
   备图 → `<project>/screenshot-printwindow.png`；像素采样检查非纯色/非黑屏（≥8 种颜色），
   主图不合格时自动用 PrintWindow 结果顶替；
7. `finally` 中关闭应用（`-KeepAlive` 可保留进程），输出 JSON 摘要，成功退出码 0。

实测输出（节选）：

```json
{
  "ok": true,
  "windowTitle": "Lumen · WinUI 3 冒烟测试",
  "windowClass": "WinUIDesktopWin32WindowClass",
  "customTitleBar": true,
  "captionBandPx": 0,
  "micaSupported": "True",
  "acrylicSupported": "True",
  "backdrop": "Mica",
  "windowSizePx": "900x620",
  "screenshotSize": "886x613",
  "screenshotColors": 114,
  "printWindowOk": true,
  "elapsedSeconds": 19.7
}
```

常用参数：`-NoBuild`（复用现有输出）、`-KeepAlive`（截完不关，便于手工看）、
`-Configuration Release`、`-ScreenshotPath <绝对路径>`、`-LaunchTimeoutSeconds 60`。

---

## 3. Mica / DesktopAcrylic 背景（Windows App SDK 2.5.1，实测可用）

### 3.1 推荐写法：程序化设置 + 能力检测（本应用采用）

```csharp
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

// Window 子类中；0=Mica, 1=DesktopAcrylic, 2=None
try
{
    SystemBackdrop = index switch
    {
        0 when MicaController.IsSupported() => new MicaBackdrop(),
        1 when DesktopAcrylicController.IsSupported() => new DesktopAcrylicBackdrop(),
        _ => null,
    };
}
catch
{
    SystemBackdrop = null;   // 不支持时回退
}
```

XAML 一行写法在 2.5.1 同样有效（模板默认就是这种）：

```xml
<Window ...>
    <Window.SystemBackdrop>
        <MicaBackdrop />
    </Window.SystemBackdrop>
    ...
</Window>
```

实测结果（本机）：
- `MicaController.IsSupported()` → `True`，`DesktopAcrylicController.IsSupported()` → `True`
- 设置 `MicaBackdrop()` 不抛异常，窗口背景呈受壁纸影响的深灰（本机深色壁纸下像素均值约 R32/G32/B32）
- `DesktopAcrylicBackdrop()` 生效：窗口后内容被模糊（像素均值随背景变化，约 R23/G24/B26）
- `SystemBackdrop = null` 时窗口整体透明（XAML 内容也不可见，桌面内容直接透出）——这是 2.5.1 的实测现象；
  实际应用固定用 Mica（或给根元素加不透明 `Background`）即可，不要拿 `None` 当"无背景但内容可见"用。

### 3.2 如何验证 Mica 真的生效（避免"看起来黑=没生效"误判）

- `MicaController.IsSupported()` / `DesktopAcrylicController.IsSupported()` 返回 `True`；
- 应用内 `SystemBackdrop` 设置成功（未回退）；
- **不要**用 `DwmGetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE=38)` 判断 —— WinUI 3 的 XAML `MicaBackdrop`
  走的是应用合成目标（MicaController + DesktopWindowTarget），实测该 DWM 属性仍是 `0 (Auto)`，但 Mica 已生效；
- 像素对比法（冒烟应用点"切换背景"按钮）：Mica ≈ R32/G32/B32，Acrylic ≈ R23/G24/B26（背景模糊），
  两者与 None 差异明显 ⇒ backdrop 确实在切换。深色壁纸下 Mica 是深灰不是纯黑，属正常。

---

## 4. 自定义标题栏（Windows App SDK 2.5.1，实测可用）

### 4.1 推荐写法：WinUI `TitleBar` 控件 + `SetTitleBar`

XAML（`MainWindow.xaml`）：

```xml
<Grid>
  <Grid.RowDefinitions>
    <RowDefinition Height="Auto" />
    <RowDefinition Height="*" />
  </Grid.RowDefinitions>

  <TitleBar x:Name="AppTitleBar" Title="Lumen">
    <TitleBar.IconSource>
      <ImageIconSource ImageSource="Assets/AppIcon.ico" />
    </TitleBar.IconSource>
  </TitleBar>

  <Grid Grid.Row="1"><!-- 页面内容 --></Grid>
</Grid>
```

C#（`MainWindow` 构造函数）：

```csharp
public MainWindow()
{
    InitializeComponent();

    // 1) 内容扩展到标题栏区域
    ExtendsContentIntoTitleBar = true;
    // 2) 把 XAML 里的 TitleBar 注册为拖动区
    SetTitleBar(AppTitleBar);

    // 3) 设置窗口图标（unpackaged 场景务必用绝对路径）
    var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
    if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
}
```

说明（`winapp find-ui --id gallery-titlebar-2 --json` 的官方样例原文）：
> Starting with WindowsAppSDK 2.1, TitleBar walks TitleBar.Content, auto-excludes interactive controls
> from the drag region, and lets non-interactive visuals (and empty space) remain draggable.
> Use `TitleBar.IsDragRegion` to override the framework decision.

即：2.1+ 的 `TitleBar` 控件会自动把内容里的可交互控件（按钮、搜索框等）排除出拖动区，无需手动
`InputNonClientPointerSource`；需要强制时用 `TitleBar.SetIsDragRegion(element, true/false)`，
动态增删内容后调 `titleBar.RecomputeDragRegions()`。

### 4.2 验证是否真的扩展成功（脚本里的判据）

```powershell
# WindowRect / GetClientRect / ClientToScreen 对比：
# 客户区顶边相对窗口顶边的偏移 == 0（没有原生 caption 条） ⇒ 自定义标题栏已生效
$captionBandPx = $clientOrigin.Y - $windowRect.Top   # 实测 0
```

### 4.3 窗口尺寸（WinUI 3 没有 `SizeToContent`）

`AppWindow.Resize` 单位是**物理像素**，要乘 DPI 缩放；`XamlRoot.RasterizationScale` 在构造函数里是 null：

```csharp
[DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

var hwnd  = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
var scale = GetDpiForWindow(hwnd) / 96.0;
AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(900 * scale), (int)(620 * scale)));  // 900x620 DIP
```

不要用根 Grid 的 `Width/Height` 代替窗口尺寸（那只会裁剪内容）。

---

## 5. 截图方案（实测可复用）

### 5.1 主方案：`CopyFromScreen`（快、所见即所得）

```powershell
Add-Type -AssemblyName System.Drawing
# bounds 用 DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS=9)，比 GetWindowRect 少一圈不可见缩放边框
$bitmap = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bitmap)
$g.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
$bitmap.Save($absolutePngPath, [System.Drawing.Imaging.ImageFormat]::Png)   # 路径必须绝对！
$bitmap.Dispose()
```

要点：
- 截图前 `ShowWindow(SW_RESTORE)` + `SetWindowPos(HWND_TOPMOST→NOTOPMOST)` + `SetForegroundWindow`
  把窗口置顶，否则会截到压在它上面的窗口；
- 窗口未渲染完会截到空白，启动后等 2–3 秒；
- 需要交互式、未锁定的桌面会话（本机 Session 1 Active）。锁屏/RDP 最小化时 `CopyFromScreen` 会得到黑图。

### 5.2 备选：`PrintWindow` + `PW_RENDERFULLCONTENT`（0x2）

```powershell
$hdc = $g.GetHdc()
$ok  = [Win32]::PrintWindow($hwnd, $hdc, 2)   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
```

本机实测：WinUI 3 窗口可以正常抓取（886×613、30 种颜色），且**不需要窗口在最前**；
但对透明/Acrylic 区域，抓到的像素可能是 alpha=0（看图显示为白），不如 CopyFromScreen 直观。
脚本两个都抓、主图不合格时自动用 PrintWindow 顶替。

### 5.3 截图自检（脚本内置，避免"截到黑屏还以为成功"）

- 尺寸 > 200×150；
- 网格采样后不同颜色数 ≥ 8（纯黑/纯色判失败）；
- 用 `read_image` 工具人工确认确实是窗口内容（标题栏文字、彩色卡片、按钮都在）。

### 5.4 也可用 `winapp ui`（无 Dev Mode 本机实测可用，非必需）

```powershell
winapp ui list-windows --json                       # 找 HWND
winapp ui screenshot -w <hwnd> -o C:\abs\out.png --json
winapp ui invoke BtnCounter -w <hwnd>               # 按 AutomationId 点击
winapp ui get-value TxtCounter -w <hwnd> --json     # 读值
```

`winapp ui` 基于 UI Automation，**与 Dev Mode 无关**；本机实测 `list-windows` / `screenshot` / `invoke`
均工作。注意 `winapp ui screenshot` 对透明/acrylic 区域会输出 alpha=0 的像素，做视觉证据时优先用
本节的 CopyFromScreen 方案。

---

## 6. `winapp find-ui` / `winapp find-api`（0.7.1，本机实测可用）

### 6.1 find-ui：查 WinUI 官方样例（离线 corpus，首次联网会刷新缓存）

```powershell
winapp find-ui "title bar" --max 3
# TitleBar [gallery] — An example showing how to use the default TitleBar control.
#   gallery-titlebar-1  TitleBar configuration
#   gallery-titlebar-2  TitleBar drag regions
#   gallery-titlebar-3  End to end TitleBar sample

winapp find-ui --id gallery-titlebar-2 --json        # 全量 XAML + C# + 说明（可直接抄）
winapp find-ui --list                                # 浏览全部样例 id
winapp find-ui "Grid" --source core --max 3          # 只搜 curated core 模式；--source reactor 用于 Reactor 项目
winapp find-ui "<关键词>" --refresh                  # 强制从 GitHub 刷新 corpus
```

本机实测：首次调用会自动从 GitHub 拉取并缓存（"Fetching WinUI controls from GitHub..."），之后走缓存；
`--help`、搜索、`--id` 全量代码均正常。

### 6.2 find-api：按**已 restore 的项目**真实 API 表面查询（写代码前先查，避免凭记忆写错 API）

```powershell
# 必须在已 restore 的 app 项目目录下（--project-dir 指向 .csproj 所在目录）
Set-Location ...\spike\winui-smoke

winapp find-api MicaBackdrop MicaController --json --project-dir .
#   Microsoft.UI.Xaml.Media.MicaBackdrop                    (Microsoft.WindowsAppSDK.WinUI 2.3.9)
#   Microsoft.UI.Composition.SystemBackdrops.MicaController (InteractiveExperiences 2.1.9)

winapp find-api members MicaController --project-dir .
#   static Boolean IsSupported(); Kind/TintColor/LuminosityOpacity; SetTarget(...)

winapp find-api check-property Window ExtendsContentIntoTitleBar SystemBackdrop --project-dir .
#   ✅ Microsoft.UI.Xaml.Window.ExtendsContentIntoTitleBar
#   ✅ Microsoft.UI.Xaml.Window.SystemBackdrop

winapp find-api packages --project-dir .    # 该项目的 API 索引覆盖了哪些包/类型数
winapp find-api stats --project-dir .       # 索引统计
winapp find-api refresh --project-dir .     # 强制重建索引（restore 后一般自动刷新）
```

注意：
- 裸跑（不带 `--project-dir`）且当前目录不是已 restore 项目时查不到正确结果；`--project sdk` 查机器 SDK（本机无系统 Windows SDK，不要用）；
- `find-api` 的索引来自项目 restore 后的 NuGet/SDK 元数据，**先 `dotnet restore` 再查**；
- `check-property` 失败会以非 0 退出，可直接当断言用。

---

## 7. 踩坑清单（含失败过的方案与原因）

| # | 现象 | 原因 | 解决 |
|---|---|---|---|
| 1 | `winapp run .` 在默认（packaged）模板配置下失败：`{"Error":"Developer Mode is not enabled on this machine. Please enable Developer Mode and try again."}`，exit 1 | packaged 应用运行需要注册开发包（`create-debug-identity`/MSIX 部署），必须开启 Dev Mode（需管理员） | 改 **unpackaged**（`WindowsPackageType=None`）+ 直接运行 exe；本机无 Dev Mode 下的唯一可靠路径 |
| 2 | 框架依赖（`SelfContained=false`）的 exe 启动弹 "You must install .NET to run this application."（.NET location: Not found） | 本机 .NET 10 是免安装目录版：不在 PATH、无 `DOTNET_ROOT`、无注册表 `InstallLocation`、无 `C:\Program Files\dotnet`；apphost 找不到运行时 | `SelfContained=true`（.NET 也自包含）；验证：输出目录出现 `coreclr.dll`、`hostfxr.dll`，`runtimeconfig.json` 有 `includedFrameworks` 而不是 `framework` |
| 3 | 明明写了 `<SelfContained>false</SelfContained>`，`dotnet msbuild -getProperty:SelfContained` 仍是 `true` | 模板自带 `<PublishProfile ...>win-$(Platform).pubxml</PublishProfile>`；.NET SDK 的 `ImportPublishProfile` 在**求值阶段**导入 `Properties\PublishProfiles\win-x64.pubxml`，其 `<SelfContained>true</SelfContained>` 覆盖了项目里的显式值 | 删掉 `<PublishProfile>` 属性；显式写 `SelfContained=true` 并用 `dotnet msbuild ... -getProperty:SelfContained` 复核 |
| 4 | `0x8007000B` bad image / Windows App SDK targets 报平台错误 | `AnyCPU`（WinUI 3 不支持） | `Platforms`/`PlatformTarget` 设 `x64`，`RuntimeIdentifier=win-x64`；`dotnet build -p:Platform=x64` |
| 5 | 构建报 `MSB3027`/`MSB3021`：无法复制 `apphost.exe`，文件被 `WinUISmoke (PID)` 锁定 | 上一次运行的应用进程还活着，锁住了 `bin\...\WinUISmoke.exe` | 构建前 `Get-Process WinUISmoke | Stop-Process -Force`（脚本已内置）；运行时用 try/finally 保证 kill |
| 6 | PowerShell 5.1 里脚本中文变乱码（如 `计数器` 变成 `璁℃暟鍣`），UIA 文本断言失败 | 本机只有 Windows PowerShell 5.1；UTF-8 **无 BOM** 的 .ps1 会按系统 ANSI（GBK）解析 | 脚本内容用纯 ASCII，或保存为 UTF-8 **with BOM**；UIA 断言只针对 ASCII 文本（应用里计数文本用 `Counter: N`） |
| 7 | 截图/日志文件出现在错误目录（如仓库根目录而不是脚本目录） | `[System.Drawing.Bitmap]::Save`、`[IO.File]::WriteAllText` 等 **.NET API 的相对路径按进程工作目录**（不是 PowerShell 的 `Get-Location`） | 所有文件路径用 `$PSScriptRoot`/`$MyInvocation` 拼**绝对路径**；`Set-Location` 对 .NET API 不可靠 |
| 8 | 运行 exe 时图标/资源丢失或行为异常 | 未把工作目录设为 exe 目录 | `Start-Process -WorkingDirectory (Split-Path $exe)`；代码里用 `AppContext.BaseDirectory` 拼图标路径 |
| 9 | Mica 设置成功但"看起来是纯黑"，以为没生效 | 深色壁纸下 Mica 就是 #202020 深灰；且 WinUI 3 的 Mica 不走 DWM `SYSTEMBACKDROP_TYPE` 属性 | 用 `MicaController.IsSupported()` + `SystemBackdrop` 实例 + 像素均值对比（§3.2）验证，不要看 DWM 属性 |
| 10 | 设置 `SystemBackdrop = null` 后窗口连同 XAML 内容整体透明 | WinUI 3 2.5.1 实测行为；无 backdrop 时合成目标透明 | 固定用 Mica/DesktopAcrylic；需要"纯色背景"时给根元素设 `Background`，不要靠 null |
| 11 | 窗口打开是 1024×768 左右的大窗 | WinUI 3 没有 `SizeToContent`，默认尺寸与内容无关 | 构造函数里 `AppWindow.Resize`，单位物理像素，乘 `GetDpiForWindow()/96`；不要设根 Grid 的 Width/Height |
| 12 | 首次 `dotnet build` 很慢/卡在 restore | 首次要下载 Windows App SDK 2.5.1 各子包 + WebView2 + BuildTools（数百 MB 级） | 正常，等待即可（首次实测 13.57s 还原 + 总共约 36s；网络慢时几分钟） |
| 13 | packaged 项目构建成功但就是起不来 | 构建 ≠ 部署：MSIX 生成不需要 Dev Mode，但"开发部署 + 运行"需要 | 生产 MSIX 打包可用 `winapp package`；本机要在本地**跑**就用 unpackaged |
| 14 | `dotnet run` 行为与预期不符（打包路径相关） | 模板的 `Microsoft.Windows.SDK.BuildTools.WinApp` 会劫持 `dotnet run` 的 Run target，做 debug identity 注册 | 本方案直接运行 exe，不依赖 `dotnet run`；不想要该钩子可设 `<EnableWinAppRunSupport>false</EnableWinAppRunSupport>` 或删包引用 |
| 15 | `winapp find-api` 结果为空/指向奇怪包 | 未在已 restore 的项目目录执行，或 `--project-dir` 指错 | 在 app 项目目录（含 `.csproj`）执行 `winapp find-api ... --project-dir .`，先 `dotnet restore` |
| 16 | 构建输出路径找不到 exe | 路径是 `bin\<Platform>\<Configuration>\<TFM>\<RID>\`（平台 + RID 两层） | 用 `bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WinUISmoke.exe` |

---

## 8. 给 Lumen 的落地建议

1. **Lumen.App 直接采用 §2.2 的 csproj 组合**（unpackaged + WinAppSDK/.NET 双自包含 + x64 + RID win-x64 + 版本钉死）。
   这是本机无 VS/无 Dev Mode/无系统 Windows SDK 下唯一实测可靠、可脚本化、可截图验证的方式。
2. **日常开发**：改完代码 → `dotnet build ... -p:Platform=x64` → 直接跑 exe；把 `scripts/winui-spike.ps1`
   作为"构建 + 冒烟 + 截图"的回归检查（改 UI/标题栏/背景后可复用）。
3. **Mica + 自定义标题栏**：按 §3.1 + §4.1 写法即可，2.5.1 已验证；优先 `TitleBar` 控件（2.1+ 自动处理拖动区），
   不要自己手搓 caption 按钮。
4. **UI 自动化/截图**：普通 PowerShell 截图（§5）即可满足本地证据；需要交互（点击、断言文本）时用
   `winapp ui`（无 Dev Mode 可用），元素要尽早设 `AutomationProperties.AutomationId`，断言文本尽量用 ASCII。
5. **发布**：MSIX 打包路径本机没有 Dev Mode 不能本地安装验证，但 `winapp package` 生成产物可用；
   本地验证始终走 unpackaged。
6. **体积**：当前 231MB 主要来自 .NET 自包含运行时 + WinAppSDK 2.5.x 元包的全量组件（AI/ML/Widgets 等）。
   如果后续需要瘦身，可研究只引用 `Microsoft.WindowsAppSDK.WinUI` / `.Foundation` 等子包并重新验证
   （本次未做，不要把"更小"当成已验证结论）。
