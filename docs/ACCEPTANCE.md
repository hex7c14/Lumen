# 验收记录（Lead 最终验收）

> 日期：2026-10-17。环境：Windows 11 26200 · RTX 5060 Laptop 8GB · Ryzen 9 8945HX · 32GB RAM。
> 全部数字来自本次实际执行，命令与产物路径可复现；截图见 `docs/screenshots/`。

## 1. 构建与测试

```powershell
$env:Path = "$env:USERPROFILE\dotnet10;" + $env:Path
dotnet build Lumen.slnx -c Release
dotnet test src\Lumen.Core.Tests\Lumen.Core.Tests.csproj -c Release --no-build
```

| 项 | 结果 |
|---|---|
| `dotnet build Lumen.slnx -c Release` | **0 警告 0 错误**（18 s） |
| `dotnet test` | **86 / 86 通过**（343 ms） |
| 适用范围 | Lumen.Core / Lumen.Cli / Lumen.Core.Tests / Lumen.App 全部项目 |

## 2. 引擎真实跑图（CLI，Release）

```powershell
dotnet run --project src\Lumen.Cli -c Release --no-build -- upscale `
  --input assets/samples/photo-01.jpg --output artifacts/final/photo-01_4x_cli.png `
  --model realesrgan-x4plus --backend gpu
```

- 输入 1600×1067 → 输出 **6400×4268**（26.09 MB PNG）
- 后端 **DirectML**，精度 fp16，tile=384（自适应），20 块，**总耗时 20.6 s**（会话创建 1.04 s）
- 预览 2560×1707 正常生成

质量基线（同一命令换 `quality`）：

| 方法 | PSNR | SSIM |
|---|---|---|
| Real-ESRGAN x4plus | 32.65 dB | 0.9474 |
| Lanczos3 基线 | 35.79 dB | 0.9622 |

> GAN 超分在逐像素指标上低于插值属预期；目视裁剪（见 `var/out/crops/compare-lanczos-vs-model.png`、`docs/MODELS.md` §5）显示模型明显更锐、纹理更自然。

其它模型：

- 动漫模型 `realesrgan-anime-6b`：1024×683 → 4096×2732，6 块，**2.8 s**（DirectML/fp16）。
- 极速模型 `realesrgan-x4v3`：1024×1024 → 4096×4096，tile 384，约 0.76 s（见 `docs/PERFORMANCE.md`）。
- CPU 后端：400×267 → 1600×1068，90 s（可用，仅兜底）。

## 3. 接缝检查（tile=384 + 反射 padding，最终输出）

对 `artifacts/final/photo-01_4x_cli.png` 做逐列/逐行梯度能量统计：

- 4 条垂直接缝处的梯度能量比（vs 邻域均值）：**1.15 / 1.02 / 1.33 / 1.32**，均值 1.21
- 3 条水平接缝：**1.14 / 1.04 / 1.09**，均值 1.09
- 全图列比值分布：中位数 0.99，p95 = 1.13；接缝值处于普通纹理列的正常分布内（约 p97–p99），**目视 1:1 裁剪未见接缝线**
- 引擎侧独立验证：分块（9 块）vs 单块整图 PSNR **50.62 dB**；alpha 透明图 0/128/255 三段正确，alpha=0 区域 RGB 全 0（`docs/PERFORMANCE.md` §2）

## 4. 发布产物与真实运行

```powershell
powershell -File scripts\build.ps1 -Configuration Release -Publish
```

| 项 | 结果 |
|---|---|
| 产物 | `artifacts\Lumen\Lumen.exe` + Windows App SDK 运行时 + .NET 10 运行时 + 4 个模型 |
| 总大小 | **573.3 MB**（其中模型约 180 MB） |
| 打包修复 | WinUI 的 `Lumen.pri` / `*.xbf` 不在 `dotnet publish` 默认复制范围，`build.ps1` 自动从构建输出补齐（否则启动 `XamlParseException`） |

发布版真实运行（环境变量驱动 UI 流程）：

```
LUMEN_OPEN=<photo-01.jpg> LUMEN_AUTOSTART=1 LUMEN_SAVE_TO=<out.png> LUMEN_THEME=Dark
artifacts\Lumen\Lumen.exe
```

- 日志确认：模型目录指向 `artifacts\Lumen\models`（4 个模型）；DirectML/fp16；1600×1067 → 6400×4268，**16.2 s**
- 自动保存 **27,425,075 字节 PNG**（6400×4268），走 UI 的 `SaveResultToPathAsync` → `SkiaImageCodec.Save` 链路
- 截图：`docs/screenshots/11-published-result.png`（对比滑杆、状态条“已保存”、输出尺寸均正确）

## 5. UI 冒烟与交互

- 独立 QA（winui-arch）在源码冻结版上跑 UIA 冒烟：**19 项断言通过**，`missingAutomationIds=[]`、`fallbackControls=[]`；空状态 4.3 s；载入 + 真实 4x 超分 + 退出 12.0 s，exit 0。证据：`artifacts/ui-smoke/final-build-empty/`、`final-build-loaded/`、`final-upscale-check/`。
- 关键交互修复（评审 P1 全 6 条）：Space 不再全局劫持、关闭对话框防重入、未处理异常不再吞、画布可访问性 + 键盘操作、输出上限降至 1.5 亿像素 + OOM 兜底、退出前取消处理并等待收敛。
- 原生 **FileOpenPicker 已验证**：UIA 点击 `BtnOpenImage` 后弹出系统“打开”对话框（过滤 `*.png;*.jpg;*.jpeg;*.webp;*.bmp`），说明 unpackaged 下 `InitializeWithWindow` 正确。证据：`artifacts/verify/file-open-picker.png`。
- FileSavePicker 未做自动化点击（无人值守环境无法捕获对话框），保存链路已由发布版真实文件与 `LUMEN_SAVE_TO` 覆盖。

## 6. 截图清单

| 文件 | 内容 |
|---|---|
| `01-empty-dark.png` / `05-empty-light.png` | 空状态（深/浅色） |
| `02-loaded-dark.png` / `06-loaded-light.png` | 载入图片后 |
| `03-processing-dark.png` | 处理中（进度、取消、参数禁用） |
| `04-compare-dark.png` | 完成后对比模式（滑杆/角标/状态条耗时） |
| `07-panel-expanded.png` | 高级参数展开 |
| `08-export.png`、`09-export-2x.png`、`10-export-3x.png` | 导出成功（4×/2×/3×） |
| `11-published-result.png` | 发布版端到端结果 |

## 7. 结论

- 用户要求的三项硬指标全部满足：**.NET 10 + C# + WinUI 3**、**端侧（DirectML/CPU）超分**、**质量优先（4 档模型 + 无接缝 + fp16 等价）**。
- 可从 `artifacts\Lumen\Lumen.exe` 双击运行，无网络、无安装、无账号。
- 遗留（非阻断）：FileSavePicker 未自动化点击；多显示器 DPI 与高对比度主题未截图验证；4x-UltraSharp 为非商业许可。

---

# 迭代 2 验收（二次元专精 + 批处理 + CPU 兼容）

> 日期：2026-10-17 20:00–20:30。规格见 [docs/ITERATION-2.md](ITERATION-2.md)。迭代 2 只专注二次元插画/动漫截图。

## 8. 构建与测试

| 项 | 结果 |
|---|---|
| `dotnet build Lumen.slnx -c Release` | **0 警告 0 错误** |
| `dotnet test` | **126 / 126 通过**（86 旧测试零回归 + 30 批处理 + 10 tile 规则） |
| 引擎 / UI | 均**源码冻结**；UI Release exe 20:20:15（0 警告 0 错误） |

## 9. 模型升级（Real-CUGAN 4×）

默认模型切换为 **`realesr-cugan-4x-conservative`**（CUGAN 保真，MIT，5.65 MB），新增 `realesr-cugan-4x-denoise`；动漫模型共 3 个（CUGAN 保真/去噪 + anime-6B），照片/通用模型默认隐藏。

**质量（3 张 CC0 动漫评估图，4× vs HR）**

| 方法 | PSNR | SSIM |
|---|---|---|
| Real-CUGAN 4× 保真 | **26.05 dB** | **0.9405** |
| Real-ESRGAN anime-6B | 23.50 dB | 0.9262 |
| Lanczos4 基线 | 22.24 dB | 0.8655 |

1:1 目视裁剪（`var/iter2/cugan-vs-lanczos.png`）：CUGAN 线条干净锐利、锯齿明显减少；详见 `docs/MODELS.md` §12。

## 10. 批处理（CLI + UI）

- **CLI GPU batch**（CUGAN，5 张输入含 1 张损坏 PNG）：成功 4 / 失败 1（失败隔离 + 中文原因），总 **7.1 s**，输出命名 `{name}_4x.png`、尺寸正确（900²→3600²、640²→2560² 等），退出码 1（有失败）。
- **CLI CPU batch**（`--backend cpu --cpu-compat on`，2 张含 1 损坏）：成功 1 / 失败 1，总 **9.1 s**（640²→2560²），CPU fp32/tile 256。
- **UI 批量**（QA 独立执行，Release 20:20:15）：9 张混合尺寸动漫图（>8 触发缓存淘汰），`成功 9 / 失败 0 · 用时 54.6 s`，9/9 输出尺寸校验通过，InfoBar「打开输出文件夹」可用；`Collection was modified` P1 已修复并复验。
- **冲突策略**：AutoRename 产出 ` (2)`；Skip 不覆盖 sentinel、汇总含“跳过 1”。

## 11. CPU 兼容模式

- UI “处理模式：自动 / GPU / CPU 兼容”一等公民；`LUMEN_FORCE_NO_DML=1` 验证：启动自动切 CPU + InfoBar 提示，状态条显示 `CPU · AMD Ryzen 9 8945HX`。
- 真实 CPU 数据（CUGAN fp32）：640×640 → 2560×2560 **8.6 s**（tile 256，9 块）；320×320 → 1280×1280 **3.7 s**；CPU 兼容模式峰值内存约 0.87 GB（CUGAN）。
- CPU 自动规则：长边 ≤512 单块、>512 用 512；兼容模式固定 tile 256（8 GB 内存安全）。

## 12. QA（独立）

- `scripts/ui-smoke.ps1` v2 场景矩阵：**14/14 通过**（GPU 2、CPU 3、兼容性 4、批量 3、深浅色/回归 2），GPU 串行守卫有效。
- **P1 已修复复验**：批量 >8 张时预览缓存淘汰在 `foreach` 中改集合导致 UI 误报“处理失败”（引擎实际成功）；修复后 9 张用例 `成功 9 / 失败 0`、无异常日志。
- 未修 P2（记录于 `docs/UI-REVIEW.md` §1.2）：缩略图全尺寸解码、单项失败即时 InfoBar、跳过数入 QueueSummary、`_thumbnailGate.Dispose` 退出期竞态、自定义输出目录空值回退等，均不阻塞发布。

## 13. 发布产物（迭代 2）

| 项 | 结果 |
|---|---|
| `scripts/build.ps1 -Configuration Release -Publish` | **589.6 MB**（6 个模型 + .NET/WinAppSDK 运行时） |
| 发布版 GPU 批量 | 4 张（3 有效 + 1 损坏）→ 成功 3 / 失败 1，**6.2 s**，输出尺寸正确 |
| 发布版 CPU 批量 | 2 张 → 成功 2 / 失败 0，**13.1 s**，状态条 `CPU · Ryzen 9 8945HX` |
| 截图 | `docs/screenshots/31-published-batch-gpu.png`、`32-published-cpu-batch.png` |

## 14. 迭代 2 结论

- 定位已收窄为**二次元插画超分**，默认模型与默认列表均为动漫专用。
- **多图批处理**完成：队列、逐项状态/进度/失败隔离、批量输出目录/命名/冲突策略、完成后打开文件夹。
- **CPU 兼容模式**完成：无 GPU 自动降级 + 手动 CPU 模式 + 低内存分块 + 系统信息/日志入口；真实 CPU 超分与批量跑通。
- 发布版 `artifacts\Lumen\Lumen.exe`（589.6 MB）双击可运行，无网络、无安装、无账号。
- 未修 P2 与“FileOpenPicker/FolderPicker 未做 UIA 点击”作为后续迭代项记录在案。

---

## 15. 发布版调整：仅内置均衡模型（2026-10-17 晚）

- 应用只内置 **`realesrgan-anime-6b`**（原名 **RealESRGAN_x4plus_anime_6B**，BSD-3-Clause；应用内显示“动漫插画（均衡）”，`qualityTier=balanced`、`isDefault=true`）。其余模型文件已从 `models/` 移除，发布产物从 589.6 MB 降至 **374 MB**。
- UI 模型区新增“自行下载模型”说明与“打开模型目录”入口；用户可自行下载/添加 ONNX 模型（说明见 `README.md` 与 `docs/MODELS.md` §13）。
- 复核：单模型 GPU 1024×1024→4096×4096 **3.0 s**；CPU 兼容 512×512→2048×2048 约 34 s；`scripts/build.ps1 -Publish` 启动自检通过。
- 此前迭代 2 章节的模型清单/CUGAN 数据保留为开发期基准（对应模型可由用户自行下载转换）。
## 16. 最终定档：内置 Real-CUGAN 4× 保真（均衡）（2026-10-17 晚）

综合质量 / 速度 / 体积 / 许可，最终选择 **`realesr-cugan-4x-conservative`** 作为唯一内置模型（应用内显示“动漫插画（均衡）”）：

| 候选 | 动漫 PSNR / SSIM | GPU 1024²→4096² | CPU 512²→2048² | 体积 | 许可 |
|---|---|---|---|---|---|
| **Real-CUGAN 4× conservative（选定）** | **26.05 / 0.9405** | **2.8 s** | **9.6 s** | **5.65 MB** | MIT |
| RealESRGAN_x4plus_anime_6B | 23.50 / 0.9262 | 3.0 s | 34 s | 17.9 MB | BSD-3 |
| 4x-UltraSharp | 25.23 / 0.9352（插画） | 更慢（RRDB） | 更慢 | 64 MB | CC-BY-NC（非商业） |
| Real-CUGAN denoise3x | 24.37 / 0.9214 | 接近 | 接近 | 5.65 MB | MIT |

- 原始模型：Real-CUGAN 4× latest-conservative（官方权重 `up4x-latest-conservative.pth`，`updated_weights.zip`，bilibili/ailab，MIT）。
- 发布产物：**356.4 MB**（含 1 个模型）；`scripts/build.ps1 -Publish` 启动自检通过。
- 其他模型（同源 denoise3x、Real-ESRGAN anime-6B/x4plus/x4v3、4x-UltraSharp）不再内置，用户可自行下载/转换（`README.md` 与 `docs/MODELS.md` §13 有原始名、链接、许可与 ONNX 要求）。
---

## 17. 迭代 3：本体优化验收（2026-10-17）

- **代码**：Release 构建 0 警告 0 错误；`Lumen.Core.Tests` **128/128 通过**（新增 2 个批量流水线测试：保存失败隔离 / 取消语义）。
- **发布**：`scripts/build.ps1 -Configuration Release -Publish` 通过（含启动自检），`artifacts/Lumen` = **250.8 MB**（清理 pdb/lib 后；§18 布局调整后为 **263.2 MB**，含根目录启动器），无 `*.pdb`/`*.lib`/`DirectML.Debug.*`，内置模型 1 个（CUGAN 4×）。
- **引擎**：单图 1024² 1939→**1705 ms**（-12%）；1920×1326 3034→**2770 ms**（-9%）；CLI batch 6 张 22.7→**17.5 s**（-23%）；CUGAN tile 256/384/512/640 复核后确认 384 最优（规则不变）；2×/3× Lanczos3 降采样试验无稳定收益（2× +0.21dB、3× -0.15dB，慢约 6 倍）已回退 Mitchell。
- **UI**：添加 10×1920 图缩略图 263→**178 ms**、峰值 WorkingSet 374.8→**229.7 MB**；batch9 墙钟 102.05→**78.15 s**（-23%）；预览缓存 8→4、BGRA 预乘转换移到后台线程、进度按阶段/≥1%/≥100ms 节流、文件夹扫描后台化。
- **风险/未完成**：测试机在长时间 GPU 负载下两次崩溃，batch9 的 after2 UIA 延迟未重测（BGRA 转换已移出 UI 线程）；发布版 smoke 在最终构建后复跑（见 §17 记录下方）。
---

## 18. 发布布局调整：根目录只留 exe / models / output / README（2026-10-17 晚）

- 新增 `src/Lumen.Launcher`（WinExe，单文件自包含+Trim，约 12 MB）：根目录 `Lumen.exe` 只负责拉起 `runtime\Lumen.exe` 并转发命令行参数，工作目录设为安装根目录；找不到运行时文件时中文弹窗提示。
- 发布脚本改为：WinUI 程序与全部运行时依赖 → `runtime\`（设为隐藏）；根目录可见 `Lumen.exe`、`models\`、`output\`、`README.md`；新增 `-Zip` 参数输出 `artifacts\Lumen-win-x64.zip`。
- 模型发现：`AppPaths` 从 `runtime\` 向上找到根目录 `models\`，实测日志 `模型目录：...\artifacts\Lumen\models，可用模型 1 个`。
- 实测：发布 263.2 MB / zip 107.1 MB；启动自检通过（启动器 + runtime 主窗口）；`ui-smoke.ps1 -Scenario batch-run`（-ExePath 根目录 Lumen.exe）51 断言 0 失败、输出 2/2 校验通过、WM_CLOSE 正常退出；`ui-smoke.ps1` 已适配启动器（按窗口所属进程跟踪真实应用 pid）。
- 开发构建（`bin\...\win-x64\Lumen.exe`、`scripts/run.ps1`）不受影响。