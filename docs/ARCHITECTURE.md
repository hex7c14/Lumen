# Lumen 架构与实现契约

> 状态：M1（骨架与契约）。本文件是团队共享的实现契约，改动接口需同步通知 Lead。
> 产品名：Lumen（流明）。目标框架 `.NET 10` + WinUI 3，端侧 ONNX Runtime 超分。

## 1. 仓库结构

```
Lumen.slnx                    解决方案（.NET 10 新格式）
Directory.Build.props         公共编译属性（x64、nullable、implicit usings）
global.json                   固定 SDK 10.0.402
src/
  Lumen.Core/                 纯 .NET 核心：编解码、模型目录、推理引擎、分块、设置、日志
  Lumen.App/                  WinUI 3 应用（unpackaged + self-contained）
  Lumen.Cli/                  命令行：基准、质量评估、引擎冒烟（不依赖 WinUI）
  Lumen.Core.Tests/           xunit 单元测试
models/<id>/                  模型权重 + model.json（见 §4）
tools/model-pipeline/         模型下载/转换/评估脚本（Python 或 C#，model-ops 维护）
docs/                         ARCHITECTURE / MODELS / WINUI / PLAN / README
scripts/                      build.ps1 / run.ps1 / 截图等工具脚本
assets/                       图标、示例图
artifacts/                    发布产物（gitignore）
spike/                        一次性验证工程（gitignore）
```

## 2. 分层与依赖

```
Lumen.App ──► Lumen.Core ◄── Lumen.Cli
                 ▲
         Lumen.Core.Tests
```

- `Lumen.Core` 不引用任何 WinUI/WindowsAppSDK 类型，仅使用 P/Invoke 与注册表读取环境信息。可在控制台完整测试。
- `Lumen.App` 只做 UI、文件对话框、预览渲染、调度；所有像素处理调用 Core。
- `Lumen.Cli` 是质量与性能回归入口，也是脱离 UI 的引擎验证手段。

## 3. Core 公共契约（已实现于 src/Lumen.Core）

| 类型 | 文件 | 说明 |
|---|---|---|
| `ImagePixels` | Imaging/ImagePixels.cs | RGBA8 行优先像素缓冲；`CopyToBgraPremultiplied` 给 WriteableBitmap 用 |
| `LoadedImage` | Imaging/LoadedImage.cs | 解码结果；EXIF 方向已归一化 |
| `IImageCodec` / `SkiaImageCodec` | Imaging/*.cs | 解码、EXIF 转正、Mitchell 三次重采样、预览、PNG/JPEG/WebP/BMP 编码 |
| `SaveOptions` / `OutputFormat` | Imaging/IImageCodec.cs | 输出格式与质量 |
| `ModelDescriptor` / `ModelPrecision` | Models/ModelDescriptor.cs | model.json 映射；fp16/fp32 权重解析 |
| `IModelCatalog` / `ModelCatalog` | Models/ModelCatalog.cs | 扫描 models/*/model.json；`Default` 选择推荐给照片的质量档模型 |
| `UpscaleOptions` / `UpscaleProgress` / `UpscaleResult` | Upscaling/*.cs | 选项、进度、结果 |
| `IUpscalerService` | Upscaling/IUpscalerService.cs | **由引擎实现**：`UpscaleAsync` + `ClearSessionCache` |
| `ISettingsStore` / `JsonSettingsStore` | Settings/AppSettings.cs | %LOCALAPPDATA%\Lumen\settings.json |
| `IComputeEnvironment` / `ComputeEnvironment` | Diagnostics/ComputeEnvironment.cs | GPU/CPU 名称、DirectML 可用性 |
| `AppPaths` / `AppLog` | AppPaths.cs / Diagnostics/AppLog.cs | 路径约定、文件日志（`%LOCALAPPDATA%\Lumen\logs\lumen.log`） |

### 3.1 UI 与引擎的边界

- UI 线程只做：加载（后台线程）、重绘、进度刷新（DispatcherQueue）。
- `IUpscalerService.UpscaleAsync` 内部自行切换到线程池，UI 直接 `await`。
- 大图预览：Core 在结果中生成 `Preview`（默认长边 ≤ 2560），UI 用 `WriteableBitmap` 渲染；保存时用 `Output` 原始像素。
- 所有失败以异常表达，UI 捕获后显示 `InfoBar`，并写 `AppLog`。

## 4. 模型目录契约（model-ops 维护）

目录：`models/<id>/`，`<id>` 与 `model.json` 的 `id` 一致。

```json
{
  "id": "realesrgan-x4plus",
  "displayName": "写实照片",
  "description": "通用 4 倍超分，适合真实照片",
  "scale": 4,
  "arch": "rrdb",
  "qualityTier": "quality",
  "recommendedFor": ["photo"],
  "inputLayout": "NCHW",
  "outputLayout": "NCHW",
  "dynamicAxes": true,
  "files": { "fp32": "model.onnx", "fp16": "model.fp16.onnx" },
  "sha256": { "model.onnx": "…", "model.fp16.onnx": "…" },
  "sizeBytes": 67000000,
  "sourceUrl": "https://…",
  "license": "BSD-3-Clause",
  "author": "Xinntao Wang / community"
}
```

- 输入 `NCHW`，3 通道 RGB，值域 `[0,1]`；输出同布局，`scale` 倍。
- `dynamicAxes` 必须为 `true`（引擎按块推理需要动态 H/W）。
- `qualityTier`：`fast` | `balanced` | `quality`；`recommendedFor`：`photo` | `anime` | `general`。
- 缺 fp16 文件时只提供 fp32；`fp32` 必须存在，否则整个目录被模型目录忽略。

## 5. 引擎实现规格（engine-dev）

实现类：`Lumen.Core.Upscaling.OnnxUpscalerService : IUpscalerService`。

### 5.1 会话管理

- 会话按 `(模型绝对路径, 精度, 后端, deviceId)` 缓存，复用；`ClearSessionCache()` 释放全部。
- 精度：`options.PreferFp16 && model.HasFp16` → fp16；CPU 后端强制 fp32（避免 Half 转换开销）。
- 后端选择：
  - `Auto`：先尝试 DirectML（`SessionOptions.AppendExecutionProvider_DML(deviceId)`），会话创建失败则回退 CPU 并记录日志；`Backend` 结果字符串说明实际后端。
  - `DirectML`：失败直接抛错（不静默回退）。
  - `Cpu`：`AppendExecutionProvider_CPU(0)`。
- ORT SessionOptions：
  - `GraphOptimizationLevel = ORT_ENABLE_ALL`
  - `EnableMemoryPattern = false`（DirectML EP 必需）
  - DirectML 时 `ExecutionMode = ORT_SEQUENTIAL`
  - CPU 时 `InterOpNumThreads = 0`（默认按核数）、`ExecutionMode = ORT_SEQUENTIAL`
- 读取 `session.InputMetadata` 判断输入元素类型（float / Float16）、输入名、维数；输出同理。支持任意可变 H/W（dynamic axes）。
- 推理输入：`DenseTensor<float>` 或 `DenseTensor<Half>`，`[1, 3, H, W]`，RGB 顺序，`/255f`。
- 输出处理：`[1, 3, H*scale, W*scale]` → 转回 RGBA8（alpha 单独处理），clamp `[0,1]`，四舍五入。

### 5.2 分块算法（质量第一）

- 参数：`tile`（输入块边长）、`pad`（重叠边距）。
  - `options.TileSize > 0` 时采用用户值；否则自动（`TilePlanner.ResolveAutoTileSize(backend, arch, width, height)`）：
    - DirectML：长边 ≤ 512 → 单块直推（tile = 长边，避免每块 0.3–0.5 s 的固定开销）；长边 > 512 → **384**（8GB 显存下的实测最优值，512 块开始非线性变慢，768 会 thrash）。
    - CPU → 128。
  - `options.TilePadding > 0` 时采用用户值；否则自动 `pad = max(8, min(32, tile/8))`，rrdb 默认 24。
  - **形状一致性（默认开启）**：每个块的输入张量先做反射（reflect-101）填充到统一尺寸 `(tile+2*pad)²`，让 DirectML 在一次运行中只看到一种输入形状；实测平均提速约 24%，质量无回退（见 docs/PERFORMANCE.md §2）。`OnnxUpscalerService.PadTilesToUniformShape` 可关闭。
  - **会话缓存**：按 `(路径, 精度, 后端, deviceId)` 缓存；新建某精度会话时释放同一模型的其他精度会话，避免 fp32+fp16 双 session 争显存。
- 先生成块矩形列表（输入坐标）：
  - `stride = tile - 2*pad`（必须 > 0）。
  - `nx = ceil((W - 2*pad) / stride)`，`ny` 同理；第 i 块的 `x = min(i*stride, W - tile)`，保证最后一块贴边、尺寸完整。
  - 若图片小于 tile，则块尺寸 = 图片尺寸，`pad` 取 `min(pad, 边长/4)`，允许只有 1 块。
- 每块流程：
  1. 扩展矩形 `srcRect = (x-pad, y-pad, tileW+2pad, tileH+2pad)` 裁剪到图片内；
  2. 从输入 RGBA 提取 RGB → float 张量（NCHW）；
  3. 推理；
  4. 把输出张量裁剪 `crop = (x-srcRect.X)*scale, (y-srcRect.Y)*scale, tileW*scale, tileH*scale`；
  5. 写入输出缓冲 `(x*scale, y*scale)`。
- 输出缓冲一次性分配：`outW = W*scale`、`outH = H*scale`，RGBA8。`outW*outH > options.MaxOutputPixels` 时抛 `InvalidOperationException`（中文提示）。
- 最终目标倍率 `TargetScale < model.Scale`：在输出缓冲上用 `IImageCodec.Resize` 做 Mitchell 降采样到 `W*TargetScale`。
- alpha：输入含透明像素且 `PreserveAlpha` 时，RGB 走模型，alpha 用 `IImageCodec.Resize` 直接重采样到最终尺寸后合并；否则 JPEG 式白底合成交给编码层。
- 进度：`Preparing` 0–5%，`Inference` 每完成一块上报（`Fraction = 0.05 + 0.9*done/total`，含 `TilesCompleted/TilesTotal/Elapsed`），`PostProcessing` 97%，`Completed` 1.0。每块之间检查 `cancellationToken.ThrowIfCancellationRequested()`。
- 会话创建/模型加载属于 `Preparing` 阶段，可能耗时数秒（首次）。
- 复用：整个流程只建一个 session，单线程串行推理。不要多 session 并发抢 GPU。

### 5.3 结果

`UpscaleResult` 必填：`Output`、`Preview`（`options.PreviewMaxDimension > 0` 时用 codec 生成）、`Duration`、`Backend`（`"DirectML"` / `"CPU"`，可带设备名）、`ModelId`、`Precision`、`TileSize`、`TileCount`。

## 6. CLI 规格（engine-dev）

`src/Lumen.Cli`（net10.0-windows，x64，输出到 console）：

```
Lumen.Cli list                                      列出可用模型与默认模型
Lumen.Cli info --input <img>                        打印图像尺寸/格式/透明通道
Lumen.Cli upscale --input <img> --output <out> --model <id> --scale 4
        [--backend auto|gpu|cpu] [--fp16 on|off] [--tile N] [--pad N] [--save-format png|jpeg|webp]
Lumen.Cli bench --model <id> --size 512 [--iters 3] [--backend ...]   单块耗时与首块耗时
Lumen.Cli quality --model <id> --reference <png>   4 倍降采样再超分，算 PSNR/SSIM 与 Lanczos 基线对比
```

要求：真实数字、失败退出码非 0、日志到控制台。`upscale` 成功后打印输出尺寸、耗时、后端与 tile 数。

## 7. UI 规格（ui-dev，待 WinUI 冒烟结论后细化）

见 `docs/WINUI.md`（winui-arch 产出）与 §8 设计原则。

## 8. 界面与“去 AI 味”设计原则

- 形态：单窗口媒体编辑器（Photos / Clipchamp 形态），**不**使用 NavigationView 全量导航。
- 材质：Mica（desktop acrylic 作备选）+ 自定义标题栏；主题跟随系统。
- 文字：Segoe UI Variable；中文优先，切换英文；字号 12/14/20/28 四档。
- 颜色：一个系统强调色，背景两级；禁止紫蓝渐变、玻璃卡片堆叠、发光描边。
- 图标：Segoe Fluent Icons 字形；禁止 emoji 当图标。
- 文案：陈述事实（“4× · 12,288 × 8,192”），不写“AI 魔法”“一键变清晰”之类营销语；不使用“✨”。
- 布局：顶栏（应用名 + 打开/保存/处理）＋ 中央画布（棋盘格底、缩放/平移、对比滑杆）＋ 底部状态条（尺寸、后端、进度、取消）＋ 右侧可折叠参数区（模型/倍率/输出）。
- 空状态：朴素拖放区域 + 一行说明 + 快捷键提示，无插画/emoji。
- 动效：只用系统自带过渡（进度环、内容淡入），不自造弹跳/视差。
- 无障碍：所有按钮有 `AutomationProperties.Name`，键盘可达，焦点可视。
- 深色/浅色/高对比度都要能看；资源用语义命名（`CardBackgroundBrush` 而非 `PurpleBrush`）。

## 9. 构建与运行

```powershell
$env:Path = "$env:USERPROFILE\dotnet10;" + $env:Path
dotnet build Lumen.slnx -c Debug           # 编译全部
dotnet run --project src/Lumen.Cli -- list # CLI 冒烟
dotnet run --project src/Lumen.App         # 运行 WinUI（unpackaged + self-contained）
```

本机无管理员、无 VS、无 Developer Mode：WinUI 应用必须以 **unpackaged + WindowsAppSDKSelfContained** 方式构建运行；不执行 MSIX 部署。

## 10. 验收标准

1. `dotnet build Lumen.slnx -c Release` 零错误。
2. CLI `upscale` 在 RTX 5060 + DirectML 上跑通真实照片，输出无接缝（4x），`--backend cpu` 也能跑。
3. WinUI 应用可双击 exe 启动，完成“打开 → 处理 → 对比 → 保存”全流程，截图留档。
4. `docs/MODELS.md` 含真实基准与质量结论。
5. 内存/尺寸保护生效：超过上限的图片给出明确错误而不是崩溃。

## 11. 批处理（迭代 2 新增）

契约位于 `src/Lumen.Core/Batch/`：

| 类型 | 作用 |
|---|---|
| `BatchOptions` | 批处理选项：`UpscaleOptions` + `SaveOptions` + 输出目录 + 文件名模板（`{name}{ext}{scale}{model}{index}{date}`）+ 冲突策略 + 预览设置 |
| `IBatchUpscalerService` | `ProcessAsync(inputPaths, options, progress, ct)`：逐张顺序处理，单张失败不中断，返回与输入一一对应的 `BatchItemResult` |
| `BatchProgress` | 逐项阶段（Loading/Upscaling/Saving）+ 整体/当前项进度 + 块计数 |
| `BatchItemResult` | 输入/输出路径、状态（Completed/Failed/Skipped/Canceled）、尺寸、耗时、失败原因、结果预览、实际后端 |
| `OutputConflictPolicy` | AutoRename（追加序号）/ Overwrite / Skip |

实现要求与 CLI `batch` 规格见 `docs/ITERATION-2.md` §4。

## 12. CPU 兼容模式（迭代 2 新增）

- UI 的“处理模式”映射：`自动` → `ExecutionBackend.Auto` + fp16；`GPU` → `ExecutionBackend.DirectML`（失败明确报错）；`CPU 兼容` → `ExecutionBackend.Cpu` + fp32 + `UpscaleOptions.CpuCompatibilityMode = true`。
- `UpscaleOptions.CpuThreads` 映射到 ORT `SessionOptions.IntraOpNumThreads`（0 = 自动）。
- CPU 模式使用 CPU 友好的自动分块（见 `TilePlanner` 与 `docs/PERFORMANCE.md` 的 CPU 调优结论），优先保证低配机器稳定完成。
- 启动探测：`IComputeEnvironment.DirectMlAvailable == false` 且模式为 `自动` 时，UI 自动切 CPU 并提示；未知后端失败不回退，避免掩盖显卡/驱动问题。

