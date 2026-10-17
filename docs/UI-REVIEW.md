# Lumen.App UI 代码评审（独立 QA）

> 评审人：winui-arch（task-5，只读评审，未修改 `src/**`）
> 评审对象：`src/Lumen.App`（WinUI 3 / Windows App SDK 2.5.1，unpackaged + self-contained）
> 评审快照：2026-10-17 18:50–19:05（期间 ui-dev 正在持续迭代，行号以快照时文件为准）
> 参考：`docs/UI-DESIGN.md`、`docs/ARCHITECTURE.md`、`refs/winappCli/plugins/winui/agent-plugin/skills/*`、`docs/WINUI.md`
> 验证手段：源码静态评审 + 实机运行（`scripts/ui-smoke.ps1` 冒烟：空状态/载入/真实 4x 超分/退出），
> **未修改任何 `src/**` 文件**。

## 0. 结论摘要

| 级别 | 数量 | 说明 |
|---|---|---|
| **P0（阻断/数据丢失）** | **0** | 未发现阻断性问题；空状态、载入、4x 超分、对比、退出主链路实测可用 |
| **P1（应在发布前修复）** | **6（已全部修复，19:07 构建）** | 键盘可达性 2 处、崩溃/异常兜底 2 处、内存上限 1 处、取消语义 1 处；ui-dev 已在 19:07 构建修复，见 §1.1 修复跟踪 |
| **P2（建议修复/打磨）** | **12（2 条已修，其余待排期）** | 资源泄漏、主题/高对比度细节、布局边界、自动化可用性等 |
| **存疑（需实测确认）** | **4** | 已标注"存疑"，不当作确定缺陷 |

### 1.1 P1 修复跟踪（ui-dev 19:07 构建，已按新源码抽查确认）

| 编号 | 状态 | 修复后位置/证据 |
|---|---|---|
| P1-1 Space 全局劫持 | ✅ 已修 | `EditorView.xaml.cs:242`（`FocusManager.GetFocusedElement` 限定画布焦点）、`56/105`（Viewer LostFocus 复位）、`264`；`MainWindow.xaml.cs:253-255`（窗口 Deactivated 时 `OnWindowActivated` 复位） |
| P1-2 关闭确认重入/异常 | ✅ 已修 | `MainWindow.xaml.cs:23`（`_closeDialogOpen`）、`282-313`（防重入 + try/catch/finally） |
| P1-3 全局异常被吞 | ✅ 已修 | `App.xaml.cs:36` 改为 `e.Handled = false`（保留 WER dump） |
| P1-4 画布可访问性 | ✅ 已修 | `EditorView.xaml:150`（`AutomationProperties.Name`）、`152`（`UseSystemFocusVisuals=True`）；`CompareViewer.xaml.cs:39/387-411`（KeyDown：左右调对比、+/- 缩放、方向键平移） |
| P1-5 输出像素上限 | ✅ 已修 | `MainViewModel.cs:31`（`MaxOutputPixels = 150_000_000`）、`434/649`（catch 覆盖 `OutOfMemoryException`） |
| P1-6 Dispose 不 Cancel | ✅ 已修 | `MainViewModel.cs:945`（`_cancellation?.Cancel()` 后 Dispose） |

> 说明：上面的行号是 19:07 构建后的新代码；本评审其余 P2/存疑条目的行号仍按 18:50–19:05 快照，ui-dev 后续改动可能轻微偏移。

实测冒烟结果（19:07/19:15 新构建，AutomationId 全部生效）：
- 空状态 Dark：`ok=True`，4.2–4.7s，19 项断言（1 窗口标题 + 18 控件）0 必失败；
  15/18 控件 `via=automationId`，其余 3 个 `BtnZoomIn/BtnZoomOut/BtnCancel` 仅载入/处理中可见（正常缺席），
  `missingAutomationIds=[]`、`fallbackControls=[]`；
- 载入状态（`LUMEN_OPEN`）：`ok=True`，4.5s，18 控件中 17 个命中（`BtnCancel` 未出现属正常），全部 `via=automationId`；
- 载入 + 点击"开始超分"全链路：旧构建 14.4s 首次跑通；新构建首次复跑因**与 ui-dev 的并发真实导出测试抢 GPU**
  触发 `OnnxRuntimeException: bad allocation`（引擎侧显存竞争，非 UI 缺陷）；19:12:56 GPU 空闲后串行复跑
  **ok=True，12.0s，`phases.upscale=completed`，3 张截图，WM_CLOSE exit 0**（`artifacts/ui-smoke/final-upscale-check/`）；
- 产物：`artifacts/ui-smoke/phase1-check/`、`artifacts/ui-smoke/loaded-upscale-check/`、
  `artifacts/ui-smoke/automation-id-check/`、`artifacts/ui-smoke/automation-id-check-loaded/`、
  `artifacts/ui-smoke/final-empty-check/`、`artifacts/ui-smoke/final-upscale-check/`；
- **源码冻结版（19:17:24 Debug / 19:17:37 Release）复验**：空状态 + 载入态各 `ok=True`（4.3s），
  `missingAutomationIds=[]`、`fallbackControls=[]`（`artifacts/ui-smoke/final-build-empty/`、`final-build-loaded/`），
  本轮评审结论对最终版成立。

> 备注：ui-dev 已在 19:03 的 XAML 中补全 31 个 AutomationId（含 `BtnStart` 动态 Name），
> 19:07 新构建已实测验证（见 `docs/UI-SMOKE.md` §0），原"自动化可观测性"P1 已关闭。

### 1.2 迭代 2（v2）评审结果（源码冻结版，2026-10-17 20:00–20:20）

- 评审对象：v2 冻结版 `src/Lumen.App`（Release exe 构建 20:01:08；只读评审，未修改 `src/**`）。
- 已评审文件（行号以冻结版为准）：`ViewModels/MainViewModel.cs`（1901 行，队列/批处理/模式/缩略图/设置）、
  `ViewModels/QueueItem.cs`、`Views/EditorView.xaml`（1053 行）+ `.xaml.cs`（483 行）、
  `MainWindow.xaml(.cs)`、`Services/AppServices.cs`、`Services/FileDialogService.cs`、`Helpers/Ui.cs`；
  以及 `src/Lumen.Core/Batch/*` 契约与 `docs/ITERATION-2.md`。
- 验证手段：14 场景 UIA 冒烟（见 `docs/UI-SMOKE.md` §12）+ 源码静态评审。

**结论（最终版 20:20:15，2026-10-17 20:25 复验）：P0 = 0；P1 = 1（已修复、已独立复验关闭）；P2 = 8（2 条已修，6 条 Lead 裁决本轮不修、记录风险）；存疑 = 2。**

最终 QA 结果（详见 `docs/UI-SMOKE.md` §12）：
- 14 场景矩阵 **14/14 通过**（13 场景直接通过 + `compat-missing-input` 修复版复跑通过）；
- P1 修复复验：9 张 batch-run `ok=True`，`成功 9 / 失败 0 · 用时 54.6s`，9/9 输出尺寸正确，
  InfoBar + "打开输出文件夹" 正常，日志无 `Collection was modified`；
- 修复版受影响场景（batch-load / batch-skip / system-info / compat-missing-input）全绿；
- "调试变量不写回 settings.json"保护经 9 张用例验证有效（关闭后 settings 仍为干净默认）。

#### P1-v2-1（阻塞验收建议修复）：批量 >8 张时 `TrimResultPreviewCache` 枚举期修改集合 → UI 误报"处理失败"

- 位置：`src/Lumen.App/ViewModels/MainViewModel.cs:974-997`（`TrimResultPreviewCache`），
  违规行 **982** `cached.Remove(item)` 位于 `foreach (QueueItem item in cached)` 内；
  调用点 `ApplyBatchResults` **1272**、`EnsureResultPreviewAsync` **963-964**；
  异常被 `StartBatchAsync` 的 catch 捕获后走 `HandleBatchFailure`（**1089** 附近）。
- 触发条件：队列结果预览缓存超过 `MaxCachedResultPreviews = 8`（`MainViewModel.cs:36`），
  即**任何 >8 张且 `KeepPreview` 的批次**（`BuildBatchOptions` 固定 `KeepPreview = true`）。
- 影响：引擎实际全部成功、输出文件已写出，但 UI 显示"处理失败"/`TxtStatusDetail` 为英文异常文本，
  完成汇总 InfoBar 不出现；用户可能重试导致重复处理/重复文件。懒加载路径（选中第 9 个缓存项）
  还会在 `async void RefreshSelectedItem` 中抛出未被 catch 过滤列表覆盖的 `InvalidOperationException`
  （结合 v1 修复后的 `App.UnhandledException` Handled=false，存在崩溃风险）。
- 实测证据（9 张动漫 PNG，GPU 串行）：
  - 输出目录 9 个文件全部写出（`img-01..09_smoke.png`，9.2–13.8MB）；
  - App log：`批处理结束：共 9 张，成功 9，失败 0`，随后
    `System.InvalidOperationException: Collection was modified` at `TrimResultPreviewCache` line 982
    → `ApplyBatchResults` line 1272 → `StartBatchAsync` line 1089；
  - UI：`TxtQueueSummary='9 张 · 已完成 9 / 失败 0'`、`TxtStatus='处理失败'`、
    `TxtStatusDetail='Collection was modified; enumeration operation may not execute.'`；
  - 证据：`artifacts/ui-smoke/v2-run-20261017-2008/15-repro-preview-cache-9/`
    （`result.json`、`15-repro-failure.png`、`ui-dump.json`、`app-log-excerpt.txt`）；
  - 复现命令：`scripts\ui-smoke.ps1 -Scenario batch-run -OpenMany "<9 张图;分隔>" -SaveDir <out>`。
- 最小修复（低风险，建议解冻修）：
  ```csharp
  int excess = cached.Count - MaxCachedResultPreviews;
  foreach (QueueItem item in cached)
  {
      if (excess <= 0) break;
      if (ReferenceEquals(item, preferred) || ReferenceEquals(item, SelectedQueueItem)) continue;
      item.ResultPreview = null;
      excess--;
  }
  // cached 是局部快照，无需 Remove
  ```
- 状态：**✅ 已修复（最终 Release 20:20:15）+ 源码复核 + 独立复验关闭**（`MainViewModel.cs:979-1003` 改为先算 `excess`、
  遍历快照计数清理，不再修改枚举集合）；修复版四个受影响场景全绿
  （`artifacts/ui-smoke/v2-final-fixed-20261017-2025/`）；9 张批量用例独立复验通过
  （`artifacts/ui-smoke/v2-fixed-9img-20261017-2023/`）：`成功 9 / 失败 0 · 用时 54.6s`、输出 9/9 尺寸正确、
  InfoBar+打开输出文件夹正常、日志无 `Collection was modified`、UI 无"处理失败"。
- 同时修复：P2-v2-1（有失败项时汇总 InfoBar 为 Warning 仍保留"打开输出文件夹"，`MainViewModel.cs:1302-1303`）；
  P2-v2-3（输入路径不存在时状态条改为"路径不存在或不可用 / 有 N 个路径不存在或不是有效文件"，`744-756`）；
  另外新增"调试变量不写回 settings.json"保护（冒烟注入的 `LUMEN_*` 不再污染用户默认配置），已实测验证；
  其余 P2 本轮按 Lead 裁决不修（记录在下方 P2 表）。

#### P2（v2 建议修复，不阻塞验收）

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| P2-v2-1 | `MainViewModel.cs:1285-1293` + `1863-1866` | 有失败项的批量完成 InfoBar（severity=Warning）会把 `CanOpenOutputFolder` 置 false → "打开输出文件夹"按钮消失；与规格 §5.2.4 不符 | **✅ 已修（20:18:46）**：`ShowInfoBar` 之后重设 `CanOpenOutputFolder = _lastOutputDirectory is not null`（`1302-1303`） |
| P2-v2-2 | `MainViewModel.cs:1206-1300` | 单张失败只在批次结束后汇总，无规格 §5.2.3 要求的"单项失败 InfoBar"（当前靠队列项"失败"+HelpText） | `OnBatchProgress` 收到 `progress.Message`/最终结果时对首个失败项弹一次 InfoBar（可合并计数） |
| P2-v2-3 | `MainViewModel.cs:735-760`（`AddPathsAsync` 尾部） | 输入路径全部不存在时 `StatusDetail` 仍是"文件重复或格式不受支持"，文案误导（InfoBar 内容正确） | **✅ 已修（20:18:46）**：`files.Count==0 && missingPaths>0` → `路径不存在或不可用 / 有 N 个路径不存在或不是有效文件`（`744-756`），冒烟新增断言通过 |
| P2-v2-4 | `MainViewModel.cs:1115-1163`（`BuildBatchOptions`） | 输出目录模式选"自定义"但路径为空时静默回退到源目录 | 开始前校验并提示"请选择输出目录"，或自动切回"源文件目录"模式 |
| P2-v2-5 | `MainViewModel.cs:162-164` | `QueueSummary` 只统计完成/失败，不含跳过（skip 批次显示"已完成 0 / 失败 0"） | 追加 `/ 跳过 N`（规格 §5.2.7 要求汇总含跳过） |
| P2-v2-6 | `MainViewModel.cs:803-833`（`LoadThumbnailAsync`） | 每张缩略图都全尺寸解码（`_codec.Load` 后 CreatePreview 96），大批量 IO/CPU/内存浪费 | 增加 `CreatePreviewFromFile(path, 96)` / 解码时下采样 |
| P2-v2-7 | `MainViewModel.cs:1899` + `732` | `_thumbnailGate.Dispose()` 时仍有 fire-and-forget 缩略图任务排队 → `Release` 抛 `ObjectDisposedException`（进程退出期，低风险） | **本轮不修（Lead 裁决，已记录）**；建议后续将 gate 置空/不 Dispose，或 Release 包 try/catch |
| P2-v2-8 | `MainViewModel.cs:861-941`（`RefreshSelectedItem`） | 选中项预览加载中把选中项切空时，旧任务 finally 因 `_selectionCancellation` 已换新而不复位 `IsLoadingPreview`，ProgressRing 可能卡住 | finally 无条件比较并复位，或每次切换先复位 `IsLoadingPreview=false` |

#### 存疑（未自动化验证，不算缺陷）

1. 原生 `FileOpenPicker` / `FolderPicker` / `FileSavePicker` 对话框：代码层 `InitializeWithWindow` 正确
   （`FileDialogService.cs:141-145`），但 UIA 无人值守未点击（`docs/UI-NOTES.md` §9.7 一致）；建议发布前人工点一次。
2. 关闭窗口时的处理中确认对话框（v2 加了 `RequestedTheme` 跟随，`MainWindow.xaml.cs:290-310`）：
   无人值守未覆盖（会需要真实点击对话框）；代码层已加重入保护与 try/catch，静态评审通过。

#### v2 检查清单逐项结果（对应 §1.2 准备阶段的 13 项）

| # | 检查项 | 结果 |
|---|---|---|
| 1 | 队列并发/取消 | ✅ 通过（`_batchCancellation` 生命周期、`CancelBatch`、处理中锁定参数与添加/移除；批量 14 场景实测） |
| 2 | 队列内存/预览缓存 | ❌ **P1-v2-1**（Trim 枚举期修改集合）；另建议 P2-v2-6 |
| 3 | 缩略图线程 | ✅ 实现正确（后台解码 + UI 线程 WriteableBitmap，SemaphoreSlim(2)）；P2-v2-6/P2-v2-7 为优化项 |
| 4 | 批量设置绑定 | ✅ `SelectedIndex/IsOn/Text` 均 TwoWay，文件名模板 `UpdateSourceTrigger=PropertyChanged`；处理中 `IsPanelEnabled` 锁定；持久化字段齐全 |
| 5 | 模式映射 | ✅ `EffectiveBackend/EffectivePreferFp16/EffectiveCpuCompatibilityMode` 映射正确；`LUMEN_FORCE_NO_DML=1` 自动切 CPU + InfoBar 实测通过；GPU 失败不回退（`StartBatchAsync` 提前报错） |
| 6 | InfoBar 重入 | ✅ 单条 InfoBar + 计时器重入安全；P2-v2-1/P2-v2-2 为语义问题 |
| 7 | 文件/文件夹选择器 | ✅ 代码层 `InitializeWithWindow` + 多选/文件夹递归/去重；原生对话框未自动化（存疑 1） |
| 8 | 输出目录/命名/冲突策略 | ✅ 命名模板/token、冲突策略（skip 实测 SHA256 不变）、目录创建均通过；P2-v2-4/P2-v2-5 为文案/校验问题 |
| 9 | 系统信息对话框 | ✅ `DlgSystemInfo` + 7 字段 + 4 按钮全部 AutomationId 生效；`RequestedTheme` 跟随；剪贴板/打开文件夹有 try/catch |
| 10 | 动漫专精 | ✅ 默认只显示 anime 模型（CUGAN 保真为默认）、`ShowGeneralModels` 开关、`isDefault` 生效；空状态/副标题文案符合规格 §2 |
| 11 | 键盘/焦点 | ✅ Space 仅画布聚焦拦截 + LostFocus/窗口失活复位；Esc 仅在有状态时 Handled；Ctrl+O/±/0 保留；队列 ListView 选择与 Space 不冲突（静态评审） |
| 12 | 布局/DPI | ✅ 280 队列面板 + 画布 + 300 参数面板在 1320×860 正常；最小 1040×680 逻辑沿用 v1 修复；深/浅色截图通过 |
| 13 | 自动化/UIA | ✅ v2 新增 31+ AutomationId 实测全部 `automationId` 命中（`missingAutomationIds=[]`）、文本类 Name=显示文本、队列项 `文件名 · 状态`、失败原因 HelpText 均按约定实现 |

#### Batch 契约层面发现（准备阶段结论，已闭环）

| # | 级别 | 位置 | 问题 | 结论 |
|---|---|---|---|---|
| C-1 | P1（契约/规格冲突）→ ✅ 已裁决 | `src/Lumen.Core/Batch/BatchItemResult.cs` vs `docs/ITERATION-2.md` §4.5 | `OutputPath` 语义冲突。**Lead 裁决（19:41）：Completed=实际路径；Skipped=拟输出路径（不写入）；Failed/Canceled=null**。契约注释与规格已同步；冒烟用 `batch-skip` + sentinel SHA256 断言验证 | 已关闭 |
| C-2 | P2 → Lead 确认 | `BatchOptions.KeepPreview` + `BatchItemResult.Preview` | 预览内存由 UI 控制（≤8 张缓存）；**但注意 P1-v2-1 的 trim 实现有 bug** | 已确认，跟踪 P1 |
| C-3 | P2 | `BatchOptions.Validate()` | 未校验输出目录可写/非法字符 | 建议后续补 |
| C-4 | P2 → 已确认 | `UpscaleOptions.MaxOutputPixels` | UI `BuildBatchOptions` 已显式传 `MaxOutputPixels = 150_000_000`（`MainViewModel.cs:1143` 附近） | 已闭环 |
| C-5 | P2 | `BatchProgress` 无单张错误文本 | UI 逐项失败依赖最终 `BatchItemResult.ErrorMessage`；排期可加 `progress.Message` 失败回调 | 建议后续 |

> 待办：等 Lead 通知后重读 `src/Lumen.App` v2 全部改动（预计新增 QueueView/队列 VM/BatchSettings/SystemInfo 等），
> 按上述清单输出 P0/P1/P2 + 修复跟踪；同时确认 v1 的 6 条 P1 修复在 v2 中未回归。

### 1.3 迭代 3（本体优化）QA/评审准备（2026-10-17 21:15，准备阶段）

- **状态**：v3 优化进行中、尚未冻结（21:12 engine-dev 仍在改 `src/Lumen.Core/Imaging/SkiaImageCodec.cs`；
  新增 `src/Lumen.Core/Imaging/IImageThumbnailLoader.cs`（Lead 定义的缩略图契约）；`scripts/build.ps1` 已加入
  发布清理）。本阶段只准备测量脚本与检查清单：**不启动应用、不占 GPU、不改 `src/**`**。
- **准备阶段已确认的基线**（详见 `docs/UI-SMOKE.md` §13）：
  - 发布包已清理调试文件（`scripts/build.ps1` 移除 `*.pdb`/`*.lib`/`DirectML.Debug.dll`）：
    当前 `artifacts/Lumen` = **532 files / 250.8 MB / pdb=0 / debugDll=0**（`createdump.exe` 存在，advisory 记录）；**注：§18 根目录布局调整后为 263.2 MB（含启动器），zip 107.1 MB，文件数变化，属预期。**
    模型仅 `realesr-cugan-4x-conservative`（fp32 5.65MB + fp16 2.85MB）。
  - **文档数字不一致**：`README.md`/`docs/ACCEPTANCE.md`（迭代 2 章节）仍写发布产物 ≈356.4 MB，
    与当前 250.8 MB 不符（清理 pdb/lib 后体积下降）→ 交叉核对项，待 Lead 更新文档后同步 `perf-v3-expectations.json`。
  - 14 场景冒烟脚本在新仓库（`D:\Project\Lumen`）`-DryRun` **13/13 通过**（脚本按 `$PSScriptRoot` 定位仓库，迁移无影响）。
  - 新增独立性能脚本 `artifacts/ui-smoke/perf-v3.ps1`（纯 ASCII/PS5.1，DryRun 自检通过；
    startup/single 1024²→4096²/batch9 墙钟+WorkingSet/2x/3x 尺寸/package 检查；GPU 串行守卫；交叉核对 expectations）。
- **迭代 3 评审检查清单（冻结后逐项核对，写文件:行号 + P0/P1/P2）**：
  1. `IImageThumbnailLoader` 落地：实现方是否后台解码 + 目标尺寸下采样、线程上限/取消/异常占位；
     UI 不再对每张图全尺寸解码（收口 v2 P2-v2-6），缩略图/队列显示无回归。
  2. 启动路径：启动到主窗口不阻塞（不得同步解码模型/大图/同步探测 GPU）；与 `startup` ≥5 次中位对照。
  3. 批量性能/内存：9 张批量墙钟、峰值/完成后 WorkingSet；`KeepPreview` 策略与 ≤8 张预览缓存；
     >8 张 trim 回归（v2 P1-v2-1 修复不得回退）。
  4. 发布包：`*.pdb`/`*.lib`/`DirectML.Debug*` 清理、`Lumen.pri`/`*.xbf` 完整、发布后启动自检、
     体积/文件数与 README/ACCEPTANCE 一致。
  5. 倍率：2x/3x 输出尺寸（2048²/3072²）与命名 `{scale}` token；4x 回归。
  6. 14 场景矩阵全绿（GPU 串行），重点 9 张批量与 CPU/兼容性用例无回归。
  7. 代码评审（只读）：v3 改动范围（SkiaImageCodec/IImageThumbnailLoader/启动路径/发布脚本）的
     async void、CancellationToken、Dispose、线程/内存、异常兜底；不重复评 v2 已关闭项。
- **冻结后执行（task-13）**：发布 → 14 场景矩阵 → `perf-v3.ps1 -Mode all` → 更新 expectations 并交叉核对
  `docs/PERFORMANCE.md §8`/`docs/UI-NOTES` → 实测与结论回填本节 + `docs/UI-SMOKE.md` §13.4；
  P0/P1 直报 Lead+ui-dev（复现命令/result.json/截图/日志）。

---

## P1 — 应在发布前修复（6 条）

### P1-1. Space 按键被根级 PreviewKeyDown 全局劫持：破坏按钮/输入框的 Space 激活，且失焦后状态卡住

- 位置：`src/Lumen.App/Views/EditorView.xaml.cs:201-227`（`OnRootPreviewKeyDown` / `OnRootKeyUp`），
  事件挂在 `src/Lumen.App/Views/EditorView.xaml:21`（`PreviewKeyDown`），`_spaceHeld` 定义在 `EditorView.xaml.cs:26`。
- 问题：
  1. 只要 `Vm.HasImage`，**任何焦点位置**（工具栏按钮、ToggleButton、ComboBox、NumberBox 等）按 Space 都会被
     `e.Handled = true` 吞掉，导致键盘用户无法用 Space 激活当前聚焦的按钮/开关（Windows 无障碍惯例是 Space 激活）。
     代码注释写"避免焦点在按钮上时按 Space 触发按钮"，但代价是把所有控件都禁用了 Space，属于可访问性回退。
  2. `_spaceHeld` 只在 `KeyUp` 复位。若按住 Space 时焦点被移走（Alt+Tab、点击任务栏、弹出 ContentDialog 等），
     `KeyUp` 不会到达本控件 → `_spaceHeld` 永久为 true，画布一直停留在"只看原图"状态，直到下次 Space。
- 影响：键盘/无障碍用户操作异常；隐藏状态卡死。
- 最小修复：
  ```csharp
  // 只在画布自身拥有焦点时拦截 Space（其余控件保持系统默认行为）
  var focused = FocusManager.GetFocusedElement(XamlRoot);
  if (!ReferenceEquals(focused, Viewer) && !Viewer.IsFocused) { return; }
  ```
  并在 `MainWindow` 的 `AppWindow` 失活/窗口 Deactivated 时复位：
  ```csharp
  if (_spaceHeld) { _spaceHeld = false; Viewer.ShowOriginalTemporarily(false); }
  ```
  （或在 `EditorView` 的 `LostFocus`/`Deactivated` 兜底。）

### P1-2. 关闭窗口时"正在处理"确认对话框：async void 无重入保护、无异常兜底，可能抛"只能有一个 ContentDialog"

- 位置：`src/Lumen.App/MainWindow.xaml.cs:223-258`（`OnAppWindowClosing`，`async void`；`args.Cancel = true;`
  后 `await dialog.ShowAsync()`；`249-257`）。
- 问题：`AppWindow.Closing` 是同步事件，处理函数却是 `async void`：
  1. 对话框打开期间用户再次 Alt+F4/点关闭 → 第二次进入 handler，再次 `ShowAsync()` → WinUI 抛
     `InvalidOperationException: Only a single ContentDialog can be open at any time.`，
     该异常发生在 `async void` 中，可能直接崩溃或进入全局异常处理器；
  2. `await dialog.ShowAsync()` 没有任何 try/catch，`XamlRoot` 失效等异常同样未兜底；
  3. 对话框选择"取消处理并退出"后 `CancelProcessing()` 后立即 `Close()`，没有等待处理真正结束（见 P1-6）。
- 最小修复：
  ```csharp
  private bool _closeDialogOpen;
  ...
  if (_closeDialogOpen) { args.Cancel = true; return; }   // 已有对话框，忽略重复关闭
  ...
  args.Cancel = true;
  _closeDialogOpen = true;
  try { result = await dialog.ShowAsync(); }
  catch (Exception ex) { AppLog.Warn(...); return; }
  finally { _closeDialogOpen = false; }
  ```
  并把 `ShowAsync` 放进 try/catch。

### P1-3. 全局异常处理器一律 `e.Handled = true`，会吞掉致命异常并让应用停留在不一致状态

- 位置：`src/Lumen.App/App.xaml.cs:29-33`。
- 问题：`OnUnhandledException` 无条件 `e.Handled = true`。XAML 线程上的任何异常（包括 XAML 解析失败、
  绑定源生成代码异常、对话框异常等）都被标记为"已处理"，应用继续运行但状态可能已损坏（例如窗口半初始化、
  `App.Services` 未装配完成），且用户看不到任何提示（只写日志）。
- 影响：掩盖真实崩溃、线上问题难以定位；严重时会静默功能异常。
- 最小修复：只对**已知可恢复**异常（例如已捕获记录过的业务异常）`Handled = true`；未知异常至少弹一次性
  InfoBar/ContentDialog 提示并记录，或直接 `e.Handled = false` 让其走标准崩溃路径（保留 WER dump 便于定位）：
  ```csharp
  AppLog.LogUnhandled("Xaml", e.Exception);
  e.Handled = false;   // 或：仅在已显式标记 recoverable 时为 true
  ```

### P1-4. 画布可聚焦但无 AccessibleName、无焦点视觉；对比分隔线只支持指针拖动

- 位置：`src/Lumen.App/Views/EditorView.xaml:147-151`（`CompareViewer` `IsTabStop="True"`、
  `UseSystemFocusVisuals="False"`，仅有 `AutomationProperties.AutomationId="CanvasViewer"`，
  **没有 `AutomationProperties.Name`**）；
  `src/Lumen.App/Controls/CompareViewer.xaml.cs:286-344`（`OnImageHostPointerPressed/Moved/Released`
  处理平移与对比拖动）；`Controls/CompareViewer.xaml:36-73`（分隔线/手柄为纯视觉元素）。
- 问题：
  1. Tab 能聚焦画布，但聚焦后没有任何视觉指示（`UseSystemFocusVisuals=False` 且无自绘焦点），
     屏幕阅读器读到的是一个没有名字的自定义控件；
  2. 对比分隔线（`ComparePosition`）只能鼠标拖动，键盘用户无法调整；平移也只有指针/滚轮。
     叠加 P1-1 的 Space 全局拦截后，键盘用户几乎无法使用对比功能。
- 最小修复（局部即可）：
  ```xml
  <controls:CompareViewer x:Name="Viewer"
      AutomationProperties.Name="图片画布（Ctrl+滚轮缩放，空格临时查看原图）"
      IsTabStop="True" UseSystemFocusVisuals="True" ... />
  ```
  并给 `CompareViewer` 加键盘支持：`Left/Right` 调整 `ComparePosition`（步进 2%），`+/-` 缩放、
  方向键平移（在 `KeyDown` 中处理并 `e.Handled = true`）。

### P1-5. 输出像素上限 4 亿 ≈ 1.6GB RGBA：UI 只在超过 4 亿像素时才拦截，存在 OOM/显存耗尽风险

- 位置：`src/Lumen.App/ViewModels/MainViewModel.cs:28`（`MaxOutputPixels = 400_000_000`）、
  `829-846`（`UpdatePanelEstimates` 用像素总数与上限比较）、`src/Lumen.App/Services/ImageDisplayService.cs:20-26`
  （每个可见图再复制一份 BGRA 缓冲）。
- 问题：4 亿像素 × 4 bytes = **约 1.6 GB** 仅输出 RGBA 缓冲；引擎侧还有分块缓冲、源图、预览、
  `WriteableBitmap` 副本（预览图 ≤2560 长边约 26MB，可接受）。在 32GB 机器上叠加引擎中间缓冲后，
  大图很容易 OOM（`OutOfMemoryException` 也**不在** `LoadImageAsync`/`SaveResultToPathAsync` 的 catch 过滤列表里，
  见 `MainViewModel.cs:420-424` 与 `635`）。
- 影响：用户点"开始超分"后可能直接失败/崩溃，且错误提示是通用异常（`Ui.UserMessage` 兜底为"未预期错误"）。
- 最小修复：
  1. 把上限按"字节"表达并留出余量，例如 `MaxOutputPixels` 降到 1.0 亿–1.5 亿（≈400–600MB），
     或用 `GC.GetGCMemoryInfo()`/可用内存动态计算；
  2. 在 `Ui.UserMessage`/catch 过滤中加入 `OutOfMemoryException`（已映射为友好文案，需让它真正被捕获，
     即在 `LoadImageAsync`/`StartUpscaleAsync` 的 catch 里覆盖 OOM）；
  3. 状态条/面板在"靠近上限"时给 Warning（现在只有超过上限才提示）。

### P1-6. `MainViewModel.Dispose()` 只 Dispose CTS 不 Cancel：窗口卸载后后台处理仍在跑，且可能对已释放的 CTS 注册

- 位置：`src/Lumen.App/ViewModels/MainViewModel.cs:908-913`；调用点 `Views/EditorView.xaml.cs`（`OnUnloaded`）。
- 问题：`Dispose()` 里 `_cancellation?.Dispose()` 之前没有 `Cancel()`：
  - 窗口关闭/视图卸载时，正在进行的 `UpscaleAsync` 不会被取消，后台继续占用 GPU/CPU，进程退出前可能继续写文件；
  - `CancellationTokenSource.Dispose()` 之后，引擎若再调用 `token.Register(...)` 会抛 `ObjectDisposedException`；
  - 关闭确认对话框路径（P1-2）里 `CancelProcessing()` 后立即 `Close()`，也没有 await 引擎真正停下。
- 最小修复：
  ```csharp
  public void Dispose()
  {
      _infoBarTimer.Stop();
      _decodeDelayTimer.Stop();
      try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }
      _cancellation?.Dispose();
      _cancellation = null;
  }
  ```
  关闭流程中在 `CancelProcessing()` 后给引擎一个短的收敛窗口（或让 `Close()` 等 `IsProcessing == false` 最多 N 秒）。

---

## P2 — 建议修复 / 打磨（12 条）

> 修复跟踪：ui-dev 在 19:17 冻结版中报告 **P2-1…P2-12 全部处理**（详见 `docs/UI-NOTES.md` §7）。
> 本评审抽查确认了其中 P1 全部 6 条 + P2-1（ArrayPool）与 P2-5（HighContrast 标签底）的代码改动；
> 其余 P2 按 ui-dev 记录保留原文供验收参考，未逐条复验。
> 另：原生 FileOpenPicker/FileSavePicker 的自动化点击双方均未验证（见文末存疑 4 与 `docs/UI-NOTES.md` §8），
> 建议发布前人工点一次"打开图片/导出"。

| # | 位置 | 问题 | 最小修复建议 |
|---|---|---|---|
| P2-1 ✅已修 | `Services/ImageDisplayService.cs:16-30` | `CreateBitmap` 每次调用都额外分配整幅 `byte[]`（2560×1440≈15MB），大结果可见图时瞬时双份内存 | 用 `ArrayPool<byte>.Shared.Rent` 复用缓冲（**已按此修复**） |
| P2-2 | `Views/EditorView.xaml.cs:68-76`（`OnLoaded` async void）、`253-270`（`OnRootDrop` async void） | `async void` 事件内 `await` 抛异常（如拖放 `GetStorageItemsAsync` COM 异常、启动自动化异常）没有本地 try/catch，依赖全局兜底（见 P1-3） | 给这两个 handler 加 try/catch + `AppLog.Error`；`RunStartupAutomationAsync` 内部再兜底 |
| P2-3 | `Views/EditorView.xaml:669-708`（状态条中间 StackPanel） | 最小窗口 1040 DIP 时：160px 进度条 + 状态文本（无 TextTrimming）+ 明细 + 取消按钮 + 左右信息可能挤压/溢出；`TextBlock` 未设 `MaxWidth`/`TextTrimming` | 给 `TxtStatus`/`TxtStatusDetail` 设 `TextTrimming="CharacterEllipsis"`、`MaxWidth`；或中间区改 Grid 列分配 |
| P2-4 | `MainWindow.xaml.cs:121`、`79-93` | Mica/Acrylic 都不可用时的回退背景硬编码 `#202020`（深色）；`LUMEN_THEME=Light` 时与浅色 UI 不一致；标题按钮颜色只在 LUMEN_THEME 下设置（系统主题切换不刷新） | 回退背景改用主题资源（如 `{ThemeResource ApplicationPageBackgroundThemeBrush}`）；标题按钮颜色放到 `RootGrid.ActualThemeChanged` 时刷新，或干脆不覆盖（让系统按主题自绘） |
| P2-5 ✅已修 | `App.xaml:30-36`（HighContrast 字典） | `CompareLabelBackgroundBrush` 与 `EditorCanvasBrush` 在 HighContrast 下都是 `SystemColorWindowColor` → "原图/处理后"标签底与画布同色，标签框不可见（文字仍可读） | HC 下标签底改 `SystemColorButtonFaceColor`/`SystemColorHighlightColor`（**已按此修复**） |
| P2-6 | `App.xaml:18,21,25,28,32,35` | `CaptionHoverBrush`、`EditorCanvasBorderBrush` 定义了三套主题但**从未被引用**（死资源） | 删除或真正用于标题栏悬停/画布描边；避免主题字典膨胀 |
| P2-7 | `Views/EditorView.xaml:74` 与 `168`（都叫"打开图片"）；`104` 与 `308`（都叫"适应窗口"） | 同名按钮重复，UIA/读屏按名字定位有歧义（现已加 AutomationId，但 Name 仍重复） | 给两个场景不同的 `AutomationProperties.Name`（如"打开图片（空状态）"），或空状态按钮仅保留 `BtnEmptyOpen` 且 Name="打开图片"、工具栏为"打开图片" |
| P2-8 | `Controls/CompareViewer.xaml.cs:33,338-349` | Ctrl+滚轮缩放：`AddHandler(handledEventsToo:true)` 时 ScrollViewer 已先处理滚轮（可能同时滚动），且缩放以 (0,0) 为基准而非指针位置，交互不跟手 | 在无 Ctrl 时不处理；有 Ctrl 时记录指针相对位置并调整滚动偏移以"以指针为中心"缩放 |
| P2-9 | `Views/EditorView.xaml.cs:181-196`（Esc 加速器） | Esc 加速器无条件 `args.Handled=true`：当下拉框/Expander 打开时，可能吞掉控件本该处理的 Esc（是否发生取决于事件路由，**存疑**） | 只在 `Vm.IsProcessing/IsCompareMode/IsInfoBarOpen` 之一为真时 `Handled=true` 并处理；否则不标记 Handled |
| P2-10 | `ViewModels/MainViewModel.cs:242`、`404` | `IsEngineAvailable` 定义了但 UI 未使用（引擎不可用时"开始超分"按钮仍可点，点击才弹 InfoBar）；`_settings.LastOpenDirectory` 赋值但打开/保存对话框从未使用（始终 PicturesLibrary，保存靠 `SuggestedSaveFile`） | 把 `CanStartOrCancel` 与 `IsEngineAvailable` 联动；或在 XAML 显示"引擎不可用"禁用态；`LastOpenDirectory` 用于 `SuggestedStartLocation`/下次打开目录 |
| P2-11 | `Controls/CompareViewer.xaml.cs:149` 与 `ViewModels/MainViewModel.cs:857` 附近 `SetZoom` | "适应窗口"把 Zoom 钳到 `[0.02,4.0]`，而 `SetZoom`/Ctrl+滚轮钳到 `[0.05,8.0]`；超小图适应后 2%，再滚轮立刻跳 5% | 统一钳制区间（建议 `[0.02,8.0]`），或 Fit 后按同一函数收口 |
| P2-12 | `MainWindow.xaml.cs:143-167`（`ApplyDefaultSize`） | 尺寸 clamp 到工作区（`Math.Min(width, work.Width)`）但没有对最小尺寸做二次判断：工作区小于 1040×680 的屏幕会被 `OnAppWindowChanged` 立刻拉回最小尺寸，窗口大于屏幕；另 `GetDpiScale` 在构造期取的是初始监视器 DPI，多屏居中后可能不是目标屏 DPI（**存疑**） | clamp 时用 `Math.Clamp(desired, min(work), work)`；`Move` 后用 `GetDpiForWindow` 复核再 Resize 一次 |

---

## 存疑（需实测确认，不算确定缺陷）

1. **LUMEN_THEME 强制浅色时 ContentDialog/Flyout/InfoBar 的主题**：`MainWindow.xaml.cs:54-67` 只设置
   `RootGrid.RequestedTheme`。WinUI 的 `ContentDialog` 放在窗口根 XamlRoot 的弹层中，可能按系统主题（而非
   RootGrid 主题）渲染；关闭确认对话框在 LUMEN_THEME=Light 下可能是深色。建议实测深浅色截图。
2. **`AppWindow.Changed` 里同步 `AppWindow.Resize` 在最小化/最大化/跨屏时的行为**：`MainWindow.xaml.cs:173-205`
   仅在 `args.DidSizeChange` 且 `_resizingToMinimum` 下重入保护，最小化触发 `DidSizeChange` 的时序未确认；
   建议手动拖到最小/最大化/跨屏各测一次。
3. **Esc 与 ComboBox/Expander 的冲突**：见 P2-9，取决于 WinUI 事件路由（KeyboardAccelerator 在控件处理之后），
   未做运行时验证。
4. **FileOpenPicker/FileSavePicker 在 unpackaged 下实际弹出**：`Services/FileDialogService.cs:108-112`
   已正确 `InitializeWithWindow`（代码层面无问题），但本次 QA 未对原生对话框做自动化点击验证；
   建议 ui-dev 手工验证一次打开/保存（或后续用 winapp ui 处理 PickerHost 窗口）。

---

## 已验证良好（避免误报，供参考）

- **x:Bind 默认模式**：`EditorView.xaml:22` 已设 `x:DefaultBindMode="OneWay"`，实测载入图片后状态文本、尺寸、
  缩放百分比、ImageSource、面板估算均正确刷新（空状态 → 载入 → 处理中 → 完成 4 个状态的截图均正确），
  未发现 OneTime 绑定不更新问题；`ItemTemplate` 内显式 `Mode=OneTime` 用于不变数据，正确。
- **主题资源**：除 P2-5/P2-6 外，控件颜色均使用 `{ThemeResource ...}`；自定义刷子 Light/Dark/HighContrast
  三套齐全（符合 WinUI 技能要求）；`StaticResource` 只用于 `AccentButtonStyle` 等样式，使用正确。
- **文件选择器**：`FileDialogService` 正确使用 `WinRT.Interop.WindowNative.GetWindowHandle` +
  `InitializeWithWindow.Initialize`（unpackaged 必需），并避免 Jpeg 时保留 alpha 的语义。
- **UI 线程**：解码/预览/保存均在 `Task.Run`，`WriteableBitmap` 在 UI 线程创建（`MainViewModel.cs:394-398, 502-504, 630`），
  未发现 `.Result`/`.Wait()`/`Thread.Sleep` 等阻塞 UI 线程的写法。
- **async 命令**：`[RelayCommand]` 生成的 `AsyncRelayCommand` 默认禁止并发执行，打开/导出/超分不会重复进入。
- **关闭主链路**：无处理时 WM_CLOSE 正常退出（实测 exit code 0）；处理中关闭有确认对话框（设计符合 UI-DESIGN §4）。
- **画布实现**：缩放（像素尺寸 × Zoom）、ScrollViewer 平移、对比 Clip + 分隔线、Space 临时看原图、DPI 处理
  的实现思路正确；实测 4x 超分后自动进入对比模式、分隔线/标签渲染正常。

---

## 附录：AutomationId 状态（ui-dev 19:03 添加，19:07 新构建已实测验证 ✅）

本次评审开始时全项目只有 `AdvancedExpander` 一个显式 AutomationId（`CompareToggle` 等来自 `x:Name`）。
ui-dev 已在 `EditorView.xaml`（19:03:39）补全，评审建议的清单全部覆盖：

`BtnOpenImage, BtnExport, BtnFit, BtnZoom100, BtnCompare, BtnStart, CanvasViewer, BtnEmptyOpen,
BtnZoomOut, BtnZoomIn, BtnFitCanvas, BtnCollapsePanel, BtnExpandPanel, CmbModel, RadioScale, CmbFormat,
SliderQuality, TogglePreserveAlpha, ExpanderAdvanced, CmbBackend, ToggleFp16, NumTileSize, NumTilePadding,
InfoBarStatus, ProgressBarOverlay, TxtBackend, ProgressBarStatus, TxtStatus, TxtStatusDetail, BtnCancel,
TxtDimensions`；`BtnStart` 的 `AutomationProperties.Name` 已改为 `{x:Bind Vm.StartButtonText}`。

复验结果（19:07 新构建 + `scripts/ui-smoke.ps1`）：
- 空状态：19 项断言全部 `via=automationId`，`missingAutomationIds=[]`（`artifacts/ui-smoke/automation-id-check/result.json`）；
- 载入状态：含 `BtnZoomIn/BtnZoomOut` 在内全部 `via=automationId`（`artifacts/ui-smoke/automation-id-check-loaded/result.json`）；
- `fallbackControls` 与 `missingAutomationIds` 均为空 → AutomationId 契约已闭环。
