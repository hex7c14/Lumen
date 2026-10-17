# 迭代 2 规格：二次元专精 + 多图批处理 + CPU 兼容模式

> 状态：**已完成（2026-10-17）**。本文件是迭代 2 的规格来源；与 `docs/ARCHITECTURE.md`/`docs/UI-DESIGN.md` 冲突时以本文件为准。
> 基线：迭代 1 已验证通过（86/86 测试、发布版真实超分/导出成功，见 `docs/ACCEPTANCE.md`）。最终验收结果见 `docs/ACCEPTANCE.md` 迭代 2 章节。

## 1. 目标

| # | 目标 | 验收口径 |
|---|---|---|
| 1 | **只专注二次元插画/动漫截图超分** | 默认模型为动漫专用；模型列表默认只显示动漫模型；文案/空状态/描述围绕插画 |
| 2 | **多图批处理** | 一次添加多张图片或整个文件夹，顺序处理；队列逐项状态/进度/耗时/失败原因；批量导出到指定目录，命名规则与冲突策略可配 |
| 3 | **兼容性 / CPU 模式** | 一等公民的“处理模式：自动 / GPU / CPU 兼容”；无 DirectML 时自动切 CPU 并提示；CPU 模式实测可用；缺模型/坏图/无 GPU 有中文提示；系统信息与日志入口 |
| 4 | 不回归 | 迭代 1 的单图流程、UI 质量、构建 0 警告、UIA 冒烟继续通过 |

## 2. 产品定位与文案

- 标题栏副标题：`二次元插画超分`；空状态主文案：`将插画或动漫截图拖到这里，或点击添加`；支持说明 `PNG / JPEG / WebP / BMP · 支持多选与文件夹`。
- 主按钮：`开始超分`（队列 >1 时 `开始处理全部`）；状态条文案围绕“第 N/M 张”。
- 模型列表默认只显示 `recommendedFor` 含 `anime` 的模型；高级设置里 `显示通用模型` 开关（默认关）才显示 photo/general 模型。
- 默认模型由 `model.json` 的 `isDefault: true` 决定（应落在最佳动漫模型上）。

## 3. 模型计划（model-ops）

目标：把动漫超分质量再拉高一档。

1. **首选 Real-CUGAN Pro**（B 站超分模型，二次元线条/色块标杆）：
   - 研究并获取/转换 ONNX：优先找现成 ONNX（HuggingFace/GitHub），否则从官方 `.pth` 用 torch CPU 导出（动态 H/W、opset 17）。注意 Real-CUGAN 有 2x/3x/4x 与不同降噪等级（noise -1/0/1/2/3），本轮取 **4x** 的 2 个变体（建议 noise=-1“保真”与 noise=1“去噪”），或至少 1 个。
   - 若转换/验证不可行（>40 分钟仍无法跑通），放弃并用备选。
2. **备选**：`4x-AnimeSharp`（RRDB，社区模型）或 AnimeJaNai 系列，任一可稳定转换即可。
3. 现有 `realesrgan-anime-6b` 保留为“轻量/快速”档；`realesrgan-x4v3` 可保留为极速档（推荐场景 general，UI 默认隐藏）。
4. 评估（必须真实）：在现有动漫评估集（`tools/model-pipeline/samples/anime/*`）上比较 PSNR/SSIM + 1:1 目视裁剪；写 `docs/MODELS.md` 新增“迭代 2：动漫模型对比”章节与推荐默认；模型目录大小、GPU/CPU 单块时间、许可（CUGAN 为 MIT？以官方仓库为准，记录清楚）。
5. 新模型 `model.json`：`recommendedFor: ["anime"]`、`qualityTier`、`isDefault: true`（仅最佳动漫模型）；`description` 注明“去噪/保真”差异与许可。
6. 同时提供 **CPU 推理耗时**（512×512 动漫图，fp32）供 UI 提示使用。

写入范围：`models/**`、`tools/model-pipeline/**`、`docs/MODELS.md`。

## 4. 批处理 Core（engine-dev）

契约已由 Lead 写在 `src/Lumen.Core/Batch/`（不要改公共契约，需要调整先告知 Lead）：

- `BatchOptions`（UpscaleOptions + SaveOptions + 输出目录 + 文件名模板 + 冲突策略 + 预览）
- `BatchProgress` / `BatchItemStage` / `BatchItemStatus` / `BatchItemResult`
- `IBatchUpscalerService.ProcessAsync(...)`

实现要求（新增文件，例如 `BatchUpscalerService.cs`、`BatchNaming.cs`）：

1. **顺序处理**：逐张 `codec.Load` → `IUpscalerService.UpscaleAsync` → `codec.Save`；复用同一个 `OnnxUpscalerService`/会话，不在每张之间重建会话。
2. **单张失败不中断**：解码/尺寸超限/推理/保存失败 → `Failed` + 中文 `ErrorMessage` + 写 `AppLog`，继续下一张。取消后当前与后续标记 `Canceled`，不抛异常。
3. **命名模板**：`{name}` 原文件名（不含扩展名）、`{ext}` 源扩展名、`{scale}`、`{model}`、`{index}`（1 基）、`{date}`（yyyyMMdd）；不含扩展名，扩展名由 `SaveOptions.Format` 决定；非法字符/空模板在 `Validate` 抛中文异常。
4. **冲突策略**：`AutoRename` 追加 ` (2)`、` (3)`；`Overwrite` 覆盖；`Skip` 返回 `Skipped` 且不处理该图（状态为 Skipped，`OutputPath` 为**拟输出路径**但不写文件；Failed/Canceled 时 `OutputPath` 为 null）。
5. **输出目录**：空 = 每张源图目录；目录不存在则创建；同一批内重名（不同源目录同名）也按冲突策略处理。
6. **进度**：每张 Loading 0–5%、Upscaling 5–95%（把 `UpscaleProgress.Fraction` 映射进来）、Saving 95–100%；`OverallFraction = (index + itemFraction) / count`；`BatchProgress.ItemFraction` 为当前项进度；`TilesCompleted/TilesTotal` 透传。
7. **预览**：`KeepPreview` 时把 `UpscaleResult.Preview` 放进 `BatchItemResult.Preview`，长边按 `PreviewMaxDimension`；为省内存，`UpscaleOptions.PreviewMaxDimension` 与 `BatchOptions.PreviewMaxDimension` 取较小值。
8. **CLI `batch`**（`src/Lumen.Cli`）：
   ```
   Lumen.Cli batch --inputs <目录 | 文件1;文件2 | 通配符> --output-dir <目录>
        [--model <id>] [--scale 4] [--format png|jpeg|webp|bmp] [--quality 92]
        [--pattern "{name}_{scale}x"] [--conflict rename|overwrite|skip]
        [--backend auto|gpu|cpu] [--fp16 on|off] [--uniform-pad on|off]
   ```
   目录递归扫描常见图片扩展名；逐项打印 `[1/8] name 640x480 → 完成 12.3s -> path`；汇总“成功 N / 失败 M / 跳过 K”；退出码：全部成功 0，任一失败 1，参数错误 2。
9. **CPU 兼容模式**（引擎侧）：
   - `options.Backend == Cpu` 时强制 fp32；`SessionOptions.IntraOpNumThreads = options.CpuThreads > 0 ? options.CpuThreads : 0`（0=ORT 默认）；保持 `ORT_ENABLE_ALL`。
   - `options.CpuCompatibilityMode == true` 时采用 **CPU 稳定优先**配置：分块自动规则按实测（建议 128–256 中取稳定值）、`Conservative` 并发（单线程串行不变）。
   - 实测 CPU 后端：动漫模型 512×512 左右图片的耗时；tile 128/192/256 对比；把结论与推荐值写 `docs/PERFORMANCE.md` 新章节，并更新 `TilePlanner` 的 CPU 自动规则（如需要）。
10. 测试：新增 xunit 覆盖命名模板/冲突策略/批处理错误隔离/取消/进度粗粒度；`dotnet test` 全绿；`dotnet build Lumen.slnx -c Release` 0 警告 0 错误。
11. 真实验收：用 `assets/samples/photo-02.jpg` + `tools/model-pipeline/samples/anime/*` + 一个故意损坏的 .png 跑 CLI batch，验证产物尺寸/命名/失败隔离；CPU 模式跑通一张动漫图（记录真实耗时）。

写入范围：`src/Lumen.Core/Batch/**`（实现文件）、`src/Lumen.Core/Upscaling/**`、`src/Lumen.Core/Inference/**`、`src/Lumen.Cli/**`、`src/Lumen.Core.Tests/**`、`docs/PERFORMANCE.md`。

## 5. UI v2（ui-dev）

在迭代 1 的 `src/Lumen.App` 上增量改造，保持“去 AI 味”原则（见 `docs/UI-DESIGN.md`）。

### 5.1 布局

- 顶部工具栏：`添加图片`、`添加文件夹`、`清空队列`、分隔线、`开始全部`/`取消`（主按钮）。右侧：处理模式简短标签（`自动`/`GPU`/`CPU 兼容`）。
- 左侧新增 **队列面板**（宽 280，可折叠，折叠后仅保留图标列）：
  - 列表项：48×48 圆角缩略图、文件名（单行省略）、状态行（`等待` / `处理中 34%` / `完成 · 12.4s` / `失败`）、当前项进度条；选中高亮。
  - 底部：`N 张 · 已完成 X / 失败 Y`。
  - 右键菜单：`重新处理`、`移除`、`打开输出文件`、`在文件夹中显示`。
- 中央画布：显示**选中项**；已完成项可对比（原图 vs 结果预览），未处理项仅显示原图；空队列时显示空状态（支持多选/文件夹的文案）。
- 右侧参数面板：最上方新增 **处理模式** 分段/下拉（`自动（推荐）` / `GPU（DirectML）` / `CPU 兼容（较慢）`），下方小字说明与当前设备（`DirectML · RTX 5060` 或 `CPU · Ryzen 9 8945HX`）。
- 右侧“输出”组改造为批量输出：
  - 输出目录：`源文件所在目录` / `自定义…`（按钮打开 FolderPicker；显示路径，可清除）。
  - 文件名规则 `TextBox` 默认 `{name}_{scale}x`，下方灰字提示 token。
  - 冲突策略 `ComboBox`：自动重命名 / 覆盖 / 跳过。
  - `完成后打开输出文件夹` `ToggleSwitch`。
- 底部状态条批处理信息：`第 2 / 8 张 · 整体 34% · 当前块 12/20 · DirectML`；空闲时显示选中图尺寸与预估。
- 新增 **系统信息** 入口（标题栏右侧 `⋯` 或 `设置` 按钮 → 弹出 `ContentDialog`/`Flyout`）：OS、CPU、GPU、DirectML 是否可用、模型目录（含模型数）、日志路径、版本；按钮：`打开日志文件夹`、`复制信息`、`打开模型目录`。

### 5.2 行为

1. **添加**：FileOpenPicker（多选）+ FolderPicker（递归常见图片扩展名）+ 窗口拖放（文件/文件夹/多选）。队列去重（同绝对路径只加一次）。
2. **开始全部**：按队列顺序处理（跳过已完成/处理中），逐项更新状态/进度；可取消；处理中禁用参数修改（输出设置与模式也可锁定或在下一张生效——统一锁定更简单）。
3. 单项失败：该项标红 + 失败原因 tooltip；显示 `InfoBar`（可关闭）；继续下一张。
4. 全部完成：`InfoBar` 汇总“成功 7 / 失败 1 / 跳过 0”，可点“打开输出文件夹”；状态条显示总耗时。
5. 选中项切换：画布/对比/信息区同步刷新；结果预览只保留最近 N（建议 8）张的 `ImagePixels`，更早的从磁盘重新加载输出文件生成预览（或释放，点击时再懒加载）。
6. 处理模式映射：
   - `自动` → `ExecutionBackend.Auto` + `PreferFp16=true`；
   - `GPU` → `ExecutionBackend.DirectML` + fp16；
   - `CPU 兼容` → `ExecutionBackend.Cpu` + fp32 + `CpuCompatibilityMode=true` + `CpuThreads`（高级设置里可调，0=自动）。
   - 启动时若 `ComputeEnvironment.DirectMlAvailable == false` 且设置为 `自动`：自动使用 CPU 兼容模式并显示一次 `InfoBar`（“未检测到可用的 DirectML GPU，已切换 CPU 兼容模式，速度较慢”）；状态条显示实际后端。
   - 选择 `GPU` 但推理失败：不静默回退，显示错误 + 建议切到 CPU 兼容模式。
7. 队列与设置持久化：`AppSettings` 新增字段（已实现）：`ComputeMode`、`ShowGeneralModels`、`BatchOutputDirectory`、`BatchFileNamePattern`、`BatchConflictPolicy`、`OpenOutputFolderWhenDone`、`LastBatchInputDirectory`、`CpuThreads`。
8. **调试环境变量（用于无人值守验收）**：
   - `LUMEN_OPEN` 单张 / `LUMEN_OPEN_MANY` 多张（`;` 分隔）
   - `LUMEN_SAVE_DIR` 批量输出目录；`LUMEN_SAVE_TO` 单张输出路径（保留）
   - `LUMEN_BACKEND=auto|gpu|cpu`（覆盖处理模式，便于验证 CPU 路径）
   - `LUMEN_PATTERN` 覆盖文件名模板
   - `LUMEN_AUTOSTART=1` 自动开始全部
   - `LUMEN_THEME`、`LUMEN_SCALE` 沿用
9. 缩略图：后台线程解码 + `CreatePreview(96×96)`，UI 线程创建 `WriteableBitmap`；大图不保留全尺寸输入（处理时重新加载）。

### 5.3 验收

- Release/Debug 构建 **0 警告 0 错误**。
- 截图（`docs/screenshots/`，新增 v2 前缀或覆盖原文件并更新文档）：空队列、队列中等待多张、批量处理中、批量完成（含 1 张失败）、CPU 兼容模式、系统信息对话框、深/浅色各一组；截图内不出现 AI 味元素。
- 真实端到端：添加 ≥3 张动漫图（含 1 张损坏文件）→ 开始全部 → 完成/失败状态正确 → 输出目录文件齐全、命名与尺寸正确；CPU 模式独立跑通一张；导出/打开文件夹入口可用。

写入范围：`src/Lumen.App/**`、`docs/screenshots/**`。

## 6. CLI 与文档

- `Lumen.Cli batch` 见 §4.8。
- 文档更新：`docs/MODELS.md`（动漫模型对比）、`docs/PERFORMANCE.md`（CPU tile/线程）、`docs/UI-DESIGN.md`（v2 追加章节）、`docs/ARCHITECTURE.md`（批处理契约 + CPU 模式）、`docs/ACCEPTANCE.md`（迭代 2 验收）、`README.md`（功能/CPU/批处理说明）。

## 7. 分工与写入范围

| 角色 | 任务 | 写入范围 |
|---|---|---|
| model-ops | 动漫模型获取/转换/评估/默认值 | `models/**`、`tools/model-pipeline/**`、`docs/MODELS.md` |
| engine-dev | 批处理 Core + CLI batch + CPU 兼容调优 | `src/Lumen.Core/Batch/**`（实现）、`Upscaling/**`、`Inference/**`、`src/Lumen.Cli/**`、`src/Lumen.Core.Tests/**`、`docs/PERFORMANCE.md` |
| ui-dev | 队列 UI + 模式/兼容 UI + 系统信息 + 截图 | `src/Lumen.App/**`、`docs/screenshots/**` |
| winui-arch | QA：UIA 冒烟 v2、兼容性矩阵、代码评审 | `docs/UI-REVIEW.md`、`docs/UI-SMOKE.md`、`scripts/ui-smoke.ps1`、`artifacts/ui-smoke/**` |
| Lead | 契约/规格/集成/发布/验收 | 其余 |

## 8. 统一验收（Lead 执行）

1. `dotnet build Lumen.slnx -c Release` 0 警告 0 错误；`dotnet test` 全绿。
2. CLI：单图 4×（动漫模型）+ `batch`（≥3 图含损坏文件）+ CPU 模式，均真实跑通并记录数字。
3. UI：`scripts/ui-smoke.ps1` v2 通过（含批量与 CPU 模式）；人工/无人值守截图齐全。
4. 发布版 `artifacts/Lumen` 重新发布，双击运行通过（批量 + CPU 模式各一次真实流程）。
5. 文档全部同步，`docs/ACCEPTANCE.md` 追加迭代 2 记录。

## 9. 最终结果（2026-10-17）

| 项 | 结果 |
|---|---|
| 构建 / 测试 | Release **0 警告 0 错误**；`dotnet test` **126/126 通过** |
| 默认模型 | `realesr-cugan-4x-conservative`（MIT，5.65 MB）；动漫模型共 3 个 |
| 动漫质量 | CUGAN 保真 PSNR 26.05 / SSIM 0.9405（anime-6B 23.50/0.9262，Lanczos 22.24/0.8655） |
| GPU 性能 | 1920×1326 → 7680×5304 **4.09 s**；900² → 3600² 约 1–3.6 s/张 |
| CPU 兼容 | 640² → 2560² **8.6 s**（tile 256）；320² → 1280² **3.7 s**；峰值内存约 0.87 GB |
| CLI batch | GPU 5 张（4 有效 + 1 损坏）：成功 4 / 失败 1，7.1 s；CPU 2 张：成功 1 / 失败 1，9.1 s |
| UI 批量 | QA 9 张：成功 9 / 失败 0，54.6 s；P1 已修复复验 |
| QA 矩阵 | 14/14 场景通过（GPU 2 / CPU 3 / 兼容性 4 / 批量 3 / 回归 2） |
| 发布产物 | `artifacts\Lumen\Lumen.exe`，**589.6 MB**；发布版 GPU 批量 6.2 s / CPU 批量 13.1 s 真实跑通 |
| 未修 P2 | 记录于 `docs/UI-REVIEW.md` §1.2 与 `docs/UI-NOTES.md` §10，不阻塞发布 |
