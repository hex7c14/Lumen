# Lumen WinUI 3 界面实现说明（ui-dev）

> 实现依据：`docs/UI-DESIGN.md`（逐条落实）、`docs/ARCHITECTURE.md` §3/§9。
> 本文只记录**实现约定、调试入口与验证方法**，产品设计规格仍在 UI-DESIGN.md。

## 1. 代码结构（`src/Lumen.App`）

```
App.xaml / App.xaml.cs        主题字典（EditorCanvasBrush/CompareDividerBrush/CompareLabelBackgroundBrush 三套）、服务装配
MainWindow.xaml / .cs         窗口：Mica、自定义标题栏、默认/最小尺寸、LUMEN_THEME、关闭处理中确认、设置持久化
Views/EditorView.xaml / .cs   主视图：工具栏（56）+ 画布 + 右侧参数面板（300/48，可折叠）+ 状态条（32）、快捷键、拖放
Controls/CompareViewer.xaml/.cs  画布控件：ScrollViewer 缩放平移、原图/结果叠加对比、分隔线拖拽、Space 临时看原图
ViewModels/MainViewModel.cs   CommunityToolkit.Mvvm 源生成器（partial property + [RelayCommand]）状态机与命令
ViewModels/ModelOption.cs     模型下拉项
Services/AppServices.cs       手工装配：SkiaImageCodec / ModelCatalog / JsonSettingsStore / ComputeEnvironment / OnnxUpscalerService
Services/FileDialogService.cs FileOpenPicker / FileSavePicker + InitializeWithWindow（unpackaged 必需）
Services/ImageDisplayService.cs ImagePixels -> WriteableBitmap（Core 的 CopyToBgraPremultiplied，UI 线程写入 PixelBuffer）
Helpers/Ui.cs                 x:Bind 静态函数（Visible/Collapsed、文件大小、耗时、中文错误文案）与面板宽度
tools/capture.ps1             无人值守截图/导出验证脚本（ASCII，PowerShell 5.1 可跑）
```

- 视图只做视图工作；状态、命令、进度、InfoBar、设置全部在 `MainViewModel`。
- 超分通过 Core 契约 `Lumen.Core.Upscaling.IUpscalerService` 编程，默认注入 engine-dev 的
  `OnnxUpscalerService`（DirectML/CPU）；引擎初始化失败时 `AppServices.Upscaler == null`，界面给出
  InfoBar 提示而不崩溃。
- 预览只用 Core 生成的 `Preview`（长边 ≤ 2560）建 `WriteableBitmap`；全尺寸 `Output` 只在保存时使用。

## 2. 调试环境变量（仅用于无人值守截图/验证，UI 不暴露）

| 变量 | 取值 | 作用 |
|---|---|---|
| `LUMEN_OPEN` | 图片绝对路径 | 启动后自动打开该图片 |
| `LUMEN_AUTOSTART` | `1` | 打开后自动开始一次超分 |
| `LUMEN_THEME` | `Dark` / `Light` | 强制根元素 `RequestedTheme`（同时设置标题栏按钮颜色） |
| `LUMEN_SCALE` | `2` / `3` / `4` | 启动时预设目标倍率（用于验证 2×/3× 输出尺寸） |
| `LUMEN_SAVE_TO` | 输出文件路径 | 自动超分完成后自动保存（真实编码链路验证；兼容旧名 `LUMEN_SAVE`） |

示例：

```powershell
$env:LUMEN_OPEN = (Resolve-Path assets\samples\photo-01.jpg).Path
$env:LUMEN_AUTOSTART = "1"
$env:LUMEN_SAVE_TO = (Join-Path (Get-Location) "artifacts\ui-export\photo-01_4x.png")
Start-Process -FilePath <Lumen.exe 路径> -WorkingDirectory (Split-Path <Lumen.exe 路径>)
```

## 3. 无人值守截图脚本

```powershell
powershell -ExecutionPolicy Bypass -File src\Lumen.App\tools\capture.ps1 -Scenario loaded-dark
```

场景：`empty-dark` / `loaded-dark` / `processing-dark` / `compare-dark` / `empty-light` /
`loaded-light` / `panel-expanded`（用 UIA 展开高级 Expander）/ `export`（4× photo-01）/
`export-2x`、`export-3x`（photo-02）。脚本**只按自己启动的 PID 截图和结束进程**，
不会清理机器上其他 Lumen 实例。

## 4. 键盘快捷键

| 快捷键 | 行为 |
|---|---|
| Ctrl+O | 打开图片 |
| Ctrl+S | 导出结果 |
| Ctrl+= / Ctrl+- | 放大 / 缩小（主键盘 OEM 键与小键盘 +/- 都映射） |
| Ctrl+0 | 适应窗口 |
| Space（按住） | 临时只显示原图，松开恢复对比 |
| Esc | 取消处理；无处理时退出对比模式；再按关闭 InfoBar |

实现要点：Space 用根 Grid 的 `PreviewKeyDown` 在按钮之前截获，避免焦点在按钮上时按空格误触按钮；
Ctrl+O/S/0/Esc 用 `KeyboardAccelerator`；Ctrl+=/Ctrl+- 额外用数值 `VirtualKey`（0xBB/0xBD）注册加速器。

## 5. UI 自动化（UIA）标识

`AutomationProperties.AutomationId` 清单：

- 工具栏：`BtnOpenImage`、`BtnExport`、`BtnFit`、`BtnZoom100`、`BtnCompare`、`BtnStart`
- 画布：`CanvasViewer`、`BtnEmptyOpen`、`BtnZoomOut`、`BtnZoomIn`、`BtnFitCanvas`、`ProgressBarOverlay`、`InfoBarStatus`
- 参数面板：`BtnCollapsePanel`、`BtnExpandPanel`、`CmbModel`、`RadioScale`、`CmbFormat`、`SliderQuality`、
  `TogglePreserveAlpha`、`CmbBackend`、`ToggleFp16`、`NumTileSize`、`NumTilePadding`、`ExpanderAdvanced`
- 状态条：`TxtBackend`、`TxtStatus`、`TxtStatusDetail`、`BtnCancel`、`ProgressBarStatus`、`TxtDimensions`

`BtnStart` 的**可访问名称是动态的**：`{x:Bind Vm.StartButtonText}`，UIA 读到“开始超分”或“取消”。
窗口标题固定为 `Lumen`。

## 6. 已知限制

- `FileOpenPicker` / `FileSavePicker` 的标准对话框本身无法在无人值守环境点击，UIA 冒烟只验证按钮可用性；
  真实保存链路用 `LUMEN_SAVE_TO` 走与导出按钮完全相同的 `SaveResultToPathAsync` → `SkiaImageCodec.Save`。
- 最小窗口尺寸是轮询 `AppWindow.Changed` 回拉（WinUI 3 `OverlappedPresenter` 没有 MinSize API），
  拖动时可能有一帧小于下限。
- 100% 显示使用 Core 预览图（长边 ≤ 2560）；超大图在 100% 下是预览放大，属 UI-DESIGN §2 的既定行为。

## 7. 独立 QA（docs/UI-REVIEW.md）修复记录

P1 全修（6/6）：

| 编号 | 修复 |
|---|---|
| P1-1 | Space 只在画布 `FocusManager.GetFocusedElement == Viewer` 时拦截；画布 `LostFocus` 与窗口 `Activated(Deactivated)` 时复位临时“看原图”状态 |
| P1-2 | 关闭确认对话框加 `_closeDialogOpen` 重入保护 + `try/catch/finally`；重复 Alt+F4 只 Cancel 不再弹第二个 |
| P1-3 | 未处理异常记录后 `e.Handled = false`（不再吞致命异常；页面/命令层已有本地 try/catch） |
| P1-4 | 画布补 `AutomationProperties.Name`、`UseSystemFocusVisuals=True`；新增键盘：左右键调对比位（2%/次）、+/- 缩放、方向键平移 |
| P1-5 | 输出上限 4 亿 → **1.5 亿像素**（约 600MB），1 亿像素起面板黄色警告 + InfoBar 提示；`OutOfMemoryException` 加入加载/保存 catch 并映射中文 |
| P1-6 | `Dispose()` 先 `Cancel()` 再释放 CTS；关闭确认后最多等 3 秒让处理收敛再 `Close()` |

P2 修复：P2-1 `ArrayPool` 复用 BGRA 缓冲；P2-2 async void 事件本地 try/catch；P2-3 状态文本 MaxWidth + 省略号；
P2-4 Mica/亚克力回退改用主题资源 `WindowFallbackBackgroundBrush`，`ActualThemeChanged` 刷新标题栏按钮色；
P2-5 高对比度标签底改 `SystemColorButtonFaceColor`；P2-6 删除未引用资源；P2-7 空状态/画布按钮可访问名去重；
P2-8 Ctrl+滚轮改为以指针为中心的缩放；P2-9 Esc 仅在确有可处理状态时标记 Handled；P2-10 “开始超分”联动引擎可用性；
P2-11 缩放统一钳制 `[0.02, 8.0]`；P2-12 最小尺寸与工作区取交集。

## 8. 最终验收状态（19:17）

- Debug / Release：**0 警告 0 错误**（`dotnet build src/Lumen.App/Lumen.App.csproj -c Debug|Release`）。
- 截图：`01-empty-dark`、`02-loaded-dark`、`03-processing-dark`、`04-compare-dark`、`05-empty-light`、
  `06-loaded-light`、`07-panel-expanded`（7 张验收截图），另附 `08-export`、`09-export-2x`、`10-export-3x` 导出证据。
- 真实导出（同一 `SaveResultToPathAsync` → `SkiaImageCodec.Save` 链路，`LUMEN_SAVE_TO` 触发）：
  `artifacts/ui-export/photo-01_4x.png`（6400×4268，27,322,249 B）、
  `photo-02_2x.png`（2400×2400，6,783,518 B）、`photo-02_3x.png`（3600×3600，13,950,494 B）。
- 原生选择器：FileOpenPicker 在无人值守环境两次尝试（UIA 调用 `BtnOpenImage`），按钮可调用、无异常日志，
  但未在 UIA 顶层窗口里捕获到对话框窗口，因此不作为“已验证”；FileSavePicker 同理未自动化点击。
  保存编码链路已由 `LUMEN_SAVE_TO` 的真实文件验证覆盖。建议发布前人工点一次“打开图片/导出”。
- 已知限制：最小窗口 1040×680 时右侧参数面板内容需要滚动（正常行为）；高级展开在默认 1320×860 下可以一屏显示。
## 9. 迭代 2（v2）实现与验收

规格：`docs/ITERATION-2.md` §5。v2 在迭代 1 单窗口编辑器上**增量**实现：

### 9.1 布局与功能

- 工具栏：添加图片（多选）/ 添加文件夹（递归）/ 清空队列 / 分隔线 / 开始超分·开始处理全部·取消（主按钮，Accent，Name 动态）/ 右侧处理模式胶囊。
- 左侧队列面板（宽 280，可折叠为 48 图标栏）：48×48 缩略图、单行文件名、状态行（等待 / 处理中 N% / 完成 · 12.4s / 失败）、当前项进度条、选中高亮、右键菜单（重新处理 / 移除 / 打开输出文件 / 在文件夹中显示）、底部统计 `N 张 · 已完成 X / 失败 Y`。
- 中央画布：显示选中项；完成后自动进入对比模式（原图 / 结果预览叠加、拖拽分隔线、Space 临时原图）；空队列显示动漫文案空状态；处理中显示整体进度覆盖层（`第 N / M 张 · 整体 X% · 当前块 a/b`）。
- 右侧参数面板：处理模式（自动 / GPU / CPU 兼容）+ 说明与当前设备；模型（默认只显示 anime 模型，`高级 → 显示通用模型` 显示全部，默认模型取 model.json `isDefault`）；倍率；批量输出（输出目录模式 + 选择/恢复按钮 + 文件名规则 + token 提示 + 冲突策略 + 完成后打开文件夹）；输出格式/质量/透明通道；高级（显示通用模型 / CPU 线程数 / 分块大小 / 分块边距）。
- 状态条：左实际后端 + 设备（CPU 模式显示 CPU 型号）、中整体进度 + 状态 + 明细 + 取消、右尺寸。
- 系统信息（标题栏右侧图标，`BtnSystemInfo`）：版本 / 系统 / 运行时 / CPU / GPU / DirectML / 模型目录 / 批处理服务 / 日志路径，按钮：复制信息、打开日志文件夹、打开模型目录、打开输出文件夹。

### 9.2 语义要点

- 处理模式映射：自动 → `Backend=Auto` + fp16；GPU → `DirectML` + fp16；CPU 兼容 → `Cpu` + fp32 + `CpuCompatibilityMode=true` + 可选 `CpuThreads`。启动时无 DirectML 且模式为自动 → 自动切 CPU 兼容并弹一次 InfoBar；GPU 模式失败明确报错不回退。
- 批量：队列去重（绝对路径）、顺序处理、单张失败隔离、取消、逐项进度、冲突策略（自动重命名/覆盖/跳过）、命名模板 `{name}{ext}{scale}{model}{index}{date}`；完成后汇总 InfoBar 固定包含 `成功 N / 失败 M`，可打开输出文件夹。
- 跳过（skip）：目标已存在时不写文件，队列项显示“已跳过”，汇总含 `跳过 N`。
- 内存：队列缩略图 96px；结果预览长边 ≤1600 且最多缓存 8 张，其余从输出文件懒加载；画布预览长边 ≤2560；全尺寸像素只在引擎内部使用。
- 调试用兜底：若 `LUMEN_SIMULATE_BATCH=1` 且批处理服务不可用，用计时器模拟批次（仅截图验证，不写文件）。

### 9.3 调试环境变量（v2，仅调试用）

| 变量 | 说明 |
|---|---|
| `LUMEN_OPEN` / `LUMEN_OPEN_MANY` | 单张 / 多张（`;` 分隔）加入队列 |
| `LUMEN_SAVE_DIR` | 批量输出目录 |
| `LUMEN_SAVE_TO` | 兼容 v1：单文件输出路径（自动转换为目录 + 文件名模板） |
| `LUMEN_AUTOSTART=1` | 启动后自动开始批处理 |
| `LUMEN_BACKEND=auto|gpu|cpu` | 覆盖处理模式 |
| `LUMEN_CONFLICT=rename|overwrite|skip` | 覆盖冲突策略 |
| `LUMEN_PATTERN=<模板>` | 覆盖文件名规则 |
| `LUMEN_FORCE_NO_DML=1` | 让 UI 认为 DirectML 不可用（验证自动降级 CPU + InfoBar） |
| `LUMEN_SCALE` / `LUMEN_THEME` | 沿用：目标倍率 / 强制主题 |
| `LUMEN_MODELS_ROOT` | Core 支持：指向自定义模型目录（空目录 → 中文提示 + 开始按钮禁用） |
| `LUMEN_SIMULATE_BATCH=1` | 批处理服务不可用时的模拟进度（仅截图/联调） |

### 9.4 UIA 标识（v2 新增）

- 队列：`QueueList`、`TxtQueueSummary`、`BtnAddImages`、`BtnAddFolder`、`BtnClearQueue`、`BtnCollapseQueue`、`BtnExpandQueue`
- 处理模式：`CmbComputeMode`、`TxtComputeModeHint`、`TxtComputeModeChip`
- 批量输出：`TxtOutputDirectory`、`BtnPickOutputDir`、`BtnClearOutputDir`、`TxtFileNamePattern`、`CmbConflictPolicy`、`ToggleOpenOutputFolder`、`RadioOutputDirectory`
- 模型/参数：`CmbModel`、`ToggleShowGeneralModels`、`RadioScale`、`CmbFormat`、`SliderQuality`、`TogglePreserveAlpha`、`NumCpuThreads`、`NumTileSize`、`NumTilePad`、`ExpanderAdvanced`
- 状态/反馈：`TxtBatchProgress`、`TxtStatus`、`TxtStatusDetail`、`TxtBackend`、`TxtDimensions`、`InfoBarStatus`、`ProgressBarStatus`、`ProgressBarOverlay`、`BtnCancel`、`BtnStartAll`（Name 动态）、`BtnSystemInfo`
- 系统信息：`DlgSystemInfo`、`TxtSysOs`、`TxtSysCpu`、`TxtSysGpu`、`TxtSysDirectMl`、`TxtSysModels`、`TxtSysLogPath`、`BtnCopySystemInfo`、`BtnOpenLogFolder`、`BtnOpenModelsFolder`
- 文本类元素的 UIA `Name` 就是其显示文本（未再覆盖 `AutomationProperties.Name`）；队列项 UIA Name = `文件名 · 状态`，失败原因写在 `AutomationProperties.HelpText` 与 ToolTip。

### 9.5 v2 截图（`docs/screenshots/`）

`20-empty-queue-dark`、`21-queue-waiting-dark`、`22-batch-processing-dark`、`23-batch-done-dark`、
`24-cpu-mode-dark`（+ `24-cpu-mode-processing-dark` 额外证据）、`25-system-info-dark`、`26-force-no-dml-dark`、
`27-empty-queue-light`、`28-queue-waiting-light`、`29-batch-done-light`。

### 9.6 真实验收证据

- GPU 自动模式（默认 `realesr-cugan-4x-conservative`，3 张动漫 + 1 张损坏）：
  成功 3 / 失败 1，用时 54.2s；输出 `artifacts/ui-batch/gpu/`：
  `Anime_girl_with_red_eyes_4x.png` 4096×4096（11,126,397 B）、`Red_suit_anime_girl_4x.png` 7680×5304（9,263,614 B）、
  `Moe_anime_girl_in_sailor_fuku_4x.png` 7680×9308（13,801,837 B）；损坏文件 `anime-corrupt.png` 失败且不影响其它项。
- 冲突策略 skip：预置同名 sentinel 文件，处理完成后 SHA256 不变（未覆盖），队列项“已跳过”，输出目录只保留 sentinel。
- CPU 兼容模式（`anime-small.png` 384×384、2×）：成功 1，用时 5.5s，输出 `artifacts/ui-batch/cpu/anime-small_2x.png`（768×768，807,247 B）；状态条显示 `CPU · AMD Ryzen 9 8945HX`。
- 自动模式强制无 GPU（`LUMEN_FORCE_NO_DML=1`）：自动切换 CPU 兼容并显示 InfoBar，状态条为 CPU。

### 9.7 已知限制

- CPU 兼容模式大图较慢（CUGAN 1080p 级约数秒到十几秒），符合“较慢”提示；批量时整体进度可取消。
- 原生 FileOpenPicker / FolderPicker / FileSavePicker 对话框未做无人值守点击验证（unpackaged + UIA 环境限制）；文件添加/输出选择的代码路径已通过 LUMEN_* 调试变量与真实批量保存验证。
- 队列结果预览缓存 8 张，更早的从输出文件懒加载；输出文件被移动/删除时该张只显示原图。
- 批处理过程中参数/输出设置锁定（`IsPanelEnabled=false`），符合规格“统一锁定更简单”。
## 10. v2 QA 回归修复（冻结后解冻）

独立 QA（winui-arch，Release 20:01 版）报 1 个 P1 + 若干 P2；本轮按 Lead 裁决修 P1 与两项 P2：

- **P1（已修）批量 >8 张误报“处理失败”**：`TrimResultPreviewCache` 在 `foreach (cached)` 中 `cached.Remove(item)` 抛
  `InvalidOperationException: Collection was modified`，异常被批处理 catch 后整批标记失败（实际引擎全部成功、文件已写出）。
  修复：先算 `excess`，遍历快照计数清理，不再修改正在枚举的集合。
- **P2-3（已修）有失败项时“打开输出文件夹”按钮消失**：汇总 InfoBar 为 Warning 时 `ShowInfoBar` 会把
  `CanOpenOutputFolder` 置 false。修复：汇总 InfoBar 之后再按本批是否有输出目录设置该标志（规格 §5.2.4）。
- **P2-7（已修）不存在输入路径的状态文案**：`没有新增图片 / 文件重复或格式不受支持` → `路径不存在或不可用 / 有 N 个路径不存在或不是有效文件`。

本轮**记录但不改动**（Lead 裁决，留待后续迭代）：缩略图仍按全尺寸解码再降采样、批处理中单项失败无即时 InfoBar、
跳过数未进入 completion summary、`_thumbnailGate.Dispose()` 在进程退出瞬间的理论竞态、自定义输出目录为空时的静默回退。

回归验证：9 张动漫图（>8，触发预览缓存淘汰）GPU 自动模式 → 期望 `9 张 · 已完成 9 / 失败 0`、
`TxtStatus` 含“批处理完成”、InfoBar 含“成功 9 / 失败 0”、日志无 `Collection was modified`。
### 10.1 回归自测与设置污染修复

- 9 张动漫图（>8，触发结果预览缓存淘汰）：`9 张 · 已完成 9 / 失败 0`、`TxtStatus=批量完成`、
  `TxtStatusDetail=成功 9 / 失败 0 · 用时 31.8s`、InfoBar 含“成功 9 / 失败 0”且“打开输出文件夹”可见；
  9 个输出文件命名/尺寸正确；日志 `批处理结束：共 9 张，成功 9，失败 0`，无新的 `Collection was modified`。
  截图：`docs/screenshots/30-batch-9-processing-dark.png`、`30-batch-9-done-dark.png`。
- 自测中发现并修复**设置污染**：QA/截图自动化使用的 `LUMEN_PATTERN / LUMEN_SAVE_DIR / LUMEN_BACKEND / LUMEN_CONFLICT /
  LUMEN_SCALE / LUMEN_FORCE_NO_DML` 关闭窗口时会被 `PersistSettings` 写回 `%LOCALAPPDATA%\Lumen\settings.json`，
  导致后续正常启动继承“smoke”命名模板、CPU 模式、artifacts 输出目录等。修复：`PersistSettings` 检测到对应调试变量
  时跳过该字段写回；已重置本机 settings.json。
- 本轮仍按 Lead 裁决保留：缩略图全尺寸解码、批处理中单项失败即时 InfoBar、跳过数入 summary、`_thumbnailGate.Dispose` 竞态、
  自定义输出目录为空静默回退（记录待后续迭代）。
---

## 11. 迭代 3：UI 性能与内存优化（2026-10-17）

> 基线/对比数据来自 `artifacts/perf/before-*.txt`、`after-*.txt`（Debug 构建 + `LUMEN_PERF_LOG=1`，图集为 9/10 张 1920 宽 JPEG）。Debug 构建只用于前后相对比较。

### 11.1 改动

1. **缩略图快速解码**：`IImageThumbnailLoader.LoadThumbnail`（`SkiaImageCodec` 实现）优先用 `SKCodec.GetScaledDimensions` 采样解码（JPEG 1/2、1/4、1/8），不支持缩放解码的格式回退全解码；缩略图解码并发 2→1，降低批量添加峰值。
2. **结果预览缓存 8→4 张**（1600 长边，约 4–6 MB/张），其余按需从输出文件懒加载；画布/对比的 RGBA→BGRA 预乘转换移到**后台线程**，UI 线程只写 `PixelBuffer`。
3. **批量进度节流**：按“阶段变化 / ≥1% / ≥100 ms”合并后再 Post 到 UI 线程。
4. **文件夹递归扫描移到后台线程**，添加文件夹不再阻塞 UI。

### 11.2 实测（before → after）

| 场景 | 指标 | before | after | 变化 |
|---|---|---|---|---|
| 添加 10×1920 图 | 缩略图就绪 | 263 ms | **178 ms** | -32% |
| 添加 10×1920 图 | 峰值 WorkingSet | 374.8 MB | **229.7 MB** | -39% |
| 批量 9×1920 图 | 墙钟 | 102.05 s | **78.15 s** | -23% |
| 批量 9×1920 图 | 结束后 WorkingSet | 1662 MB | 1956 MB | +18%（流水线多保留 1 张结果/输出） |
| 批量 9×1920 图 | 峰值 WorkingSet | 2207.9 MB | 2563.5 MB | +16% |

### 11.3 已知代价与待复测项

- 批量墙钟提升主要来自引擎批量流水线；UI 侧“保存与下一张重叠”会让峰值内存增加约 350 MB——在 8 GB 内存机器上建议用 CPU 兼容模式或减少单次批量。
- 第一轮 after 数据的 UIA 延迟（26.9 → 198.7 ms）明显变差，判断与“结果预览 BGRA 转换在 UI 线程”有关；已把该转换移到后台线程（`ImageDisplayService.PrepareBgraPremultiplied`），但**最终 after2 数字因测试机两次崩溃未重测**，列为待复测项。
- 缩略图队列仍保留“添加后若源文件变化不刷新”的旧限制。