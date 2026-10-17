# Lumen 项目大纲（初始版本，随实现更新）

> 2026-10-17 立项。用户要求：.NET 10 + C# + WinUI 3 的**端侧**图片超分辨率软件，**质量优先**，性能配置往后放；界面尽量减少“AI 味”；WinUI 部分参照 microsoft/win-dev-skills。

## 1. 定位

本地离线、GPU 加速、质量优先的图片超分桌面应用。无账号、无云端、无网络也能完整使用；界面像 Windows 自带工具，而不是“AI 网页套壳”。

## 2. 技术选型

| 层 | 方案 | 取舍 |
|---|---|---|
| 运行时 | .NET 10（10.0.402） | 用户指定 |
| UI | WinUI 3 / Windows App SDK 2.5.1 + CommunityToolkit.Mvvm | 用户指定；参考 microsoft/winui 技能 |
| 推理 | ONNX Runtime 1.24.4 + DirectML EP（GPU）/ CPU EP 回退；fp16/fp32 自动 | 本机无管理员/无开发者模式，DirectML 免 CUDA 工具链，纯端侧 |
| 图像 I/O | SkiaSharp 4 | PNG/JPEG/WebP/BMP/GIF；EXIF 方向归一化；Mitchell 三次重采样 |
| 模型 | Real-ESRGAN x4plus（写实旗舰）/ 4x-UltraSharp（锐化）/ anime_6B（插画）/ x4v3（极速） | 质量优先，四档覆盖照片、二次元与速度需求 |
| 交付 | unpackaged + self-contained（.NET 运行时也内置） | 本机无法部署 MSIX（无 Dev Mode、无提权）；双击 exe 即可运行 |

## 3. 功能范围

**MVP（本次交付）**

1. 打开 / 拖放 / 快捷键打开图片（PNG/JPEG/WebP/BMP）
2. 四档模型选择、2×/3×/4× 目标倍率
3. DirectML GPU 推理，CPU 自动回退
4. 分块推理 + 上下文边距拼接（无接缝）、进度上报、可取消
5. 原图/结果对比滑杆、缩放/平移、适应窗口/100%
6. 导出 PNG/JPEG/WebP（质量可调、保留透明通道）
7. 设置持久化（模型、倍率、格式、后端、分块参数、窗口尺寸）
8. 键盘快捷键、错误 InfoBar、日志文件

**进阶（后续版本）**：人脸修复（GFPGAN）、批量队列、8×（两次 4×）、去噪/锐化强度、中英双语、高对比度主题、拖拽多图。

## 4. 架构

```
Lumen.App (WinUI) ──► Lumen.Core ◄── Lumen.Cli
                          ▲
                  Lumen.Core.Tests
```

- `Lumen.Core`：编解码、模型目录、ONNX 推理、分块拼接、设置、日志。不依赖 WinUI，可在 CLI 中测试。
- `Lumen.App`：单窗口编辑器 UI、文件对话框、WriteableBitmap 预览、任务调度。
- `Lumen.Cli`：`list / info / upscale / bench / quality`，质量与性能回归工具。

详见 `docs/ARCHITECTURE.md`。

## 5. 质量策略（第一优先级）

- 模型决定上限：RRDB 系 4 倍模型，fp16 与 fp32 输出差异须 ≤ 0.2 dB 才默认 fp16。
- 分块边距 ≥ 24 px（RRDB），实测接缝处梯度能量与邻域差异 < 15%，无明显拼接痕迹。
- 不做过度锐化/磨皮；保留自然纹理与噪点；2×/3× 由 4× 输出高质量降采样得到。
- 评估口径：真实照片降采样 → 超分 → PSNR/SSIM，并与 Lanczos 基线目视对比（见 `docs/MODELS.md`）。

## 6. UI / 去 AI 味原则

- 单窗口媒体编辑器（Photos/Clipchamp 形态），不使用 NavigationView 全家桶。
- Mica 材质、Segoe UI Variable、系统强调色、Segoe Fluent Icons 图标。
- 克制色彩与留白；禁止紫蓝渐变、玻璃卡片堆叠、✨/🤖 emoji、营销文案、假数据。
- 顶栏 + 画布 + 参数面板 + 状态条；空状态为朴素拖放区。
- 深色/浅色/高对比度均可读；键盘可达、控件有 AutomationProperties。

详见 `docs/UI-DESIGN.md`。

## 7. 里程碑

| 阶段 | 内容 | 状态 |
|---|---|---|
| M0 | 环境勘察（.NET 10 SDK 用户级安装、WinApp CLI、GPU 识别）；WinUI 冒烟；ORT DirectML 冒烟 | ✅ 完成 |
| M1 | 解决方案骨架、Core 契约、CLI 壳、App 壳、图标、构建脚本；四模型入库 | ✅ 完成 |
| M2 | Core 引擎（DirectML/CPU、fp16、分块、alpha、进度、取消、预览） | ✅ 完成（86/86 测试） |
| M3 | WinUI 主界面（画布/对比/参数/状态/导出） | ✅ 完成（P1 评审全修） |
| M4 | 集成联调、真实图片质量验证、发布产物、截图与文档 | ✅ 完成（见 docs/ACCEPTANCE.md） |

## 7.1 最终结果（2026-10-17）

- 构建：`dotnet build Lumen.slnx -c Release` **0 警告 0 错误**；`dotnet test` **86/86 通过**。
- 引擎：1600×1067 → 6400×4268 在 RTX 5060 + DirectML 上约 **16–21 s**；分块/整图 PSNR 50.62 dB；fp16 与 fp32 输出 PSNR 56.96 dB。
- 应用：发布版 `artifacts\Lumen\Lumen.exe`（573 MB，含运行时与模型），端到端真实超分 + 导出成功；UIA 冒烟 19 项断言通过。
- 文档：ARCHITECTURE / UI-DESIGN / UI-REVIEW / UI-SMOKE / WINUI / MODELS / PERFORMANCE / ACCEPTANCE 全部落地。


## 8. 风险与对策

| 风险 | 对策 |
|---|---|
| 本机无 VS / 无管理员 / 无开发者模式 | dotnet CLI + WindowsAppSDK 2.5.1 + unpackaged + self-contained；不依赖 MSIX 部署 |
| 机器级 .NET 运行时缺失导致双击 exe 报错 | 应用自包含 .NET 运行时（已验证） |
| DirectML 在 Blackwell 上的兼容性 | 已实测 RTX 5060 fp16 推理成功；失败自动回退 CPU，并在状态条显示实际后端 |
| 大图内存峰值 | 输出像素上限保护（默认 1.5 亿像素）+ 单块张量，不分配全图 float 缓冲 |
| 模型许可 | 4x-UltraSharp 为 CC-BY-NC-SA-4.0（非商业），在模型说明与文档中标注；默认模型使用动漫专用模型 |
| 接缝/色带质量 | 分块边距 24px + 梯度能量统计 + 目视 1:1 检查 |

## 9. 验收标准

1. `dotnet build Lumen.slnx -c Release` 零错误；`dotnet test` 通过。
2. CLI 在 RTX 5060 + DirectML 上完成真实图片 4× 超分（输出无接缝），`--backend cpu` 也可用。
3. WinUI 应用双击可启动，完成 打开 → 超分 → 对比 → 导出 全流程；截图留档于 `docs/screenshots/`。
4. `artifacts/Lumen/Lumen.exe` 双击可直接运行（含模型与运行时）。
5. `docs/MODELS.md` 含真实基准与质量结论；`docs/WINUI.md` 含可复现构建配方。

## 10. 迭代 2（2026-10-17，已完成）

主题：**二次元插画专精 + 多图批处理 + CPU 兼容模式**。规格见 `docs/ITERATION-2.md`，验收见 `docs/ACCEPTANCE.md` 迭代 2 章节。

| 目标 | 结果 |
|---|---|
| 二次元专精 | ✅ 默认模型 `realesr-cugan-4x-conservative`；模型列表默认只显示动漫模型（高级可显示全部）；文案/空状态/副标题已改 |
| 模型升级 | ✅ Real-CUGAN 4× 保真 + 去噪（MIT）+ anime-6B；动漫评估 PSNR 26.05/SSIM 0.9405 > anime-6B 23.50 > Lanczos 22.24 |
| 多图批处理 | ✅ 队列面板（缩略图/状态/进度/失败原因）、多图/文件夹/拖放、批量输出（目录/命名规则/冲突策略/打开文件夹）、失败隔离、可取消 |
| CPU 兼容 | ✅ 处理模式自动/GPU/CPU 兼容；无 DirectML 自动切 CPU + 提示；CPU 模式 fp32 + tile 256 + 8 线程，640²→2560² 8.6 s；发布版实测通过 |
| 兼容性 | ✅ 系统信息（OS/CPU/GPU/DirectML/模型目录/日志）、缺模型/坏图/路径不存在中文提示不崩溃；QA 14/14 场景通过 |
| 稳定性 | ✅ P1（>8 张批量误报失败）已修复复验；构建 0 警告 0 错误；测试 126/126 |

发布产物：`artifacts\Lumen\Lumen.exe`（589.6 MB，6 个模型，自包含运行时）。
