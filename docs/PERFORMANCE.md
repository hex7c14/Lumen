# Lumen 引擎性能实测（task-6）

> 环境：Windows 11 26200 · NVIDIA RTX 5060 Laptop 8GB · Ryzen 9 8945HX · 32GB RAM
> 后端：DirectML，模型 `realesrgan-x4plus`（fp16，23×RRDB）与 `realesrgan-x4v3`（fp16，SRVGG）。
> 数据来源：`Lumen.Cli bench`（Release 构建、真实推理、合成测试图，`iters=2`，表格取第 2 轮即缓存会话后的稳定态），
> 输入为 size×size 正方形、输出为其 4 倍边长。原始日志见 `var/out/perf/`。
> 测试期间机器由多 Agent 共享，且 ui-dev 的 WinUI 真实导出曾与最后几组 benchmark 重叠（GPU 100%），
> 下表中标注 `*` 的条目可能受并发影响，仅作趋势参考；未标注的条目均在 GPU 空闲窗口测得。

## 1. tile 扫描（x4plus fp16，uniform padding 关闭）

端到端稳定态总耗时 / 单块均值：

| 输入 | tile=256 | tile=384 | tile=512 | tile=768 |
|---|---|---|---|---|
| 512×512 | 5.48 s / 9 块 / 608 ms | 2.33 s / 4 块 / 580 ms | **0.82 s / 1 块 / 813 ms** | 0.86 s / 1 块 / 856 ms |
| 1024×1024 | 12.82 s / 25 块 / 512 ms | **6.20 s / 9 块 / 689 ms** | 11.34 s / 9 块 / 1259 ms | 72.8 s / 4 块 / 18207 ms `*` |
| 1600×1600 | 37.80 s / 64 块 / 590 ms | **18.68 s / 25 块 / 746 ms** | 29.98 s / 16 块 / 1872 ms `*` | 未测（8GB 上 thrash，实验中止） |

结论：

- **每块有约 0.3–0.5 s 的固定开销**（DirectML 每次 `Run` 的图准备/形状处理 + 张量拷贝），
  块越小固定开销占比越高：1600 图上 tile=256 的 64 块大部分时间花在固定开销上。
- **tile=384 是大图最优值**：比 256 快约 2 倍；512 块在 8GB 显存上开始出现非线性变慢（显存压力），
  768 块直接 thrash（1024 图 4 块要 72 s），**不可用**。
- **小图（长边 ≤512）应直接单块推理**：512 图用 512 块 0.82 s，比 384 块的 2.33 s 快 2.8 倍，比 256 块快 6.7 倍。

### 自动 tile 规则（已实现于 `TilePlanner.ResolveAutoTileSize(backend, arch, width, height)`）

| 条件 | 自动 tile |
|---|---|
| DirectML，长边 ≤ 512 | 长边（单块，不切） |
| DirectML，长边 > 512 | 384（RRDB 与 SRVGG 实测均为最优） |
| CPU，长边 ≤ 512 | 长边（单块，不切；CUGAN 512 图 4.29 s） |
| CPU，长边 > 512 | 512（单块 768/1024 峰值 5.5–16.8GB，弃用，见 §7） |
| CPU 兼容模式（任意尺寸） | 256（峰值内存 0.87–2.18GB，8GB RAM 安全） |

## 2. 形状一致性优化（边块反射 padding，已实现为默认开启）

把每个块的输入张量**反射填充（reflect-101）到统一尺寸 `(tile+2*pad)²`**，使 DirectML 在一次运行中
只看到一种输入形状（此前边块会退化成 280×304 等不同形状）。质量上只影响图像最外缘的上下文估计，
核心区域与裁剪逻辑不变。

| 配置 | uniform 关 | uniform 开 | 收益 |
|---|---|---|---|
| 512 图 / tile=256 | 4.59 s | **3.08 s** | **−32.9%** |
| 1024 图 / tile=384 | 7.62 s | **6.53 s** | **−14.4%** |
| 1600 图 / tile=384 | 23.66 s | **17.59 s** | **−25.6%** |

三项平均约 **−24%**，超过 15% 门槛，因此实现为**默认开启**的可选项：
- 引擎：`OnnxUpscalerService.PadTilesToUniformShape = true`（默认）；设为 `false` 可回退旧行为。
- CLI：`upscale` / `bench` 增加 `--uniform-pad on|off`（默认 on）。

### 质量验证（uniform 开，x4plus，512×512 图 → 2048×2048）

- **分块（tile=256，9 块）vs 单块整图（tile=512）PSNR = 50.62 dB**（均值差 0.354/255，最大差 42/255），
  满足 ≥50 dB 门槛；接缝列/行处的差异（0.682 / 1.189）与相邻控制列/行（0.475 / 0.754）同量级，
  2× 放大目视无接缝。
- **alpha 路径**（x4v3，120×80 透明图，uniform 开）：输出 alpha 正确重采样为 0/±128/255 三段，
  `alpha=0` 的 50,240 个像素 RGB 全为 0（无垃圾值/黑边）。
- 此前（uniform 关）同法测得 PSNR 50.62 dB、接缝指标一致，说明优化没有引入质量回退。

## 3. x4v3（快速档）tile 结论

| 输入 1024×1024 | tile=384 | tile=512 |
|---|---|---|
| 稳定态总耗时 | **0.76 s**（9 块，83 ms/块） | 1.36 s（9 块，150 ms/块） |

轻量模型同样 384 优于 512（每块固定开销仍占主导），故 SRVGG 也使用同一自动规则（大图 384、小图单块）。

## 4. fp16 vs fp32（x4plus，tile=384，uniform 开）

| 输入 | fp16 | fp32 | fp16 收益 |
|---|---|---|---|
| 512×512 | **2.94 s** | 3.74 s | **−21.3%** |
| 1600×1600 | **17.59 s** | 32.77 s | **−46.3%** |

- 在本引擎（tile=384 + 形状统一的 DirectML 路径）上 **fp16 稳定快于 fp32**；8GB 显存下 384 块
  + fp16 峰值可容纳，未出现 model-ops Python 基准里 512 块的显存抖动。
- 数值精度：同图 fp16 vs fp32 输出 PSNR **56.96 dB**（最大差 4/255），可视为等价。
- **建议：AppSettings 默认保持 `PreferFp16 = true`**；用户可在参数面板关闭（CPU 后端引擎强制 fp32）。

## 5. 会话缓存策略（避免双 session 竞争）

- 同模型 fp32 + fp16 两个 DirectML 会话同时留在缓存会争抢显存，实测偶发极端变慢。
- 现在的策略：**新建某精度会话时，先释放同一模型路径下其他精度的缓存会话**（`EvictOtherPrecisionsLocked`）；
  并且整次 `UpscaleAsync`（会话获取 + 全部分块推理）由 `_runGate` 串行化，
  保证淘汰会话时绝不会有推理正在使用它。切换 fp16 开关后，旧精度会话立即释放。
- `ClearSessionCache()` 仍可一次性释放全部会话。

## 6. 复现方式

```powershell
# tile 扫描（替换 size/tile）
dotnet run --project src/Lumen.Cli -c Release -- bench --model realesrgan-x4plus --size 1600 --iters 2 --backend gpu --tile 384

# 形状优化对比
dotnet run --project src/Lumen.Cli -c Release -- bench --model realesrgan-x4plus --size 1600 --iters 2 --backend gpu --tile 384 --uniform-pad off

# 质量回归（分块 vs 整图 PSNR / alpha）
dotnet run --project src/Lumen.Cli -c Release -- upscale --input var/smoke/seam-512.png --output var/out/perf/seam-tiled-uniform.png --model realesrgan-x4plus --tile 256 --backend gpu
dotnet run --project src/Lumen.Cli -c Release -- upscale --input var/smoke/seam-512.png --output var/out/perf/seam-single-uniform.png --model realesrgan-x4plus --tile 512 --pad 8 --backend gpu

# CPU 调优 / 内存实测（迭代 2，§7）
dotnet run --project src/Lumen.Cli -c Release -- bench --model realesr-cugan-4x-conservative --size 512 --iters 2 --backend cpu --tile 512
dotnet run --project src/Lumen.Cli -c Release -- bench --model realesrgan-anime-6b --size 512 --iters 1 --backend cpu --tile 256
dotnet run --project src/Lumen.Cli -c Release -- batch --inputs var/smoke/batch/anime --output-dir var/out/batch-cpu-final --model realesr-cugan-4x-conservative --backend cpu
dotnet run --project src/Lumen.Cli -c Release -- batch --inputs var/smoke/batch/anime --output-dir var/out/batch-cpu-compat-final --model realesr-cugan-4x-conservative --backend cpu --cpu-compat on
```

## 7. CPU 模式与兼容模式（迭代 2，含进程峰值内存）

环境：Ryzen 9 8945HX（16 核 / 32 逻辑线程）、32GB RAM、CPU 后端（强制 fp32）、Release 构建。
`bench --iters N` 含会话创建（CUGAN ~0.1s，anime-6b ~1s）。
峰值内存由启动子进程并 150ms 轮询 `WorkingSet64` / `PrivateMemorySize64` 得到（含 .NET 运行时与 managed 缓冲）。
原始日志：`var/out/perf/cpu-sweep.txt`、`cpu-accept.txt`、`cugan-cpu.txt`、`cugan-cpu-memory.txt`、`cugan-cpu-memory256.txt`、`cugan-cpu-final.txt`。

### 7.1 单块内存/耗时实测（决定性数据）

| 模型 / 输入 / 配置 | 耗时 | 峰值 WorkingSet | 峰值 Private | 结论 |
|---|---|---|---|---|
| CUGAN 512 图，单块（tile=512） | **4.29 s** | 2361 MB | 3303 MB | 正常 CPU 模式采用 |
| CUGAN 768 图，单块（tile=768） | 8.45 s | 5509 MB | 6579 MB | 弃用（内存过高） |
| CUGAN 1024 图，单块（tile=1024） | 13.77 s | 8958 MB | **13096 MB** | 弃用（>2.5GB 数倍） |
| CUGAN 1024 图，tile=512（9 块） | 34.5 s | 2444 MB | 3356 MB | 大图安全上限 |
| CUGAN 512 图，tile=256（9 块） | 12.15 s | **820 MB** | **865 MB** | CPU 兼容模式采用 |
| anime-6b 512 图，单块 | 15.7 s | 3967 MB | 6416 MB | 重模型内存偏高，文档提示 |
| anime-6b 768 图，单块 | 32.5 s | 8199 MB | 11732 MB | 弃用 |
| anime-6b 1024 图，单块 | **失败：bad allocation** | 6308 MB | 16804 MB | 明确弃用（32GB 机器也会 OOM） |
| anime-6b 512 图，tile=256（9 块） | 40.6 s | 1199 MB | **2182 MB** | CPU 兼容模式采用 |

- CPU 上 fp32 中间激活随块尺寸平方增长：**768/1024 单块内存 5.5–16.8GB**，必须弃用；
  512 单块（CUGAN 3.3GB）可接受，但 8GB RAM 机器应走兼容模式（256 块，0.87GB）。
- CUGAN 比 anime-6b 快约 2.5–4 倍（同配置），是新默认动漫模型，CPU 也可用。

### 7.2 早期 tile/线程对比（anime-6b，384×384，旧 256 规则）

| tile | 块数 | 总耗时（iters=1） |
|---|---|---|
| 128 | 25 | 43.33 s |
| 192 | 9 | 30.36 s |
| 256 | 4 | 18.59 s |

| IntraOp 线程（tile=256） | 总耗时 |
|---|---|
| ORT 自动（0） | 18.59 / 19.39 s |
| **8** | **16.16 s** |
| 16 | 19.78 s |

- 32 逻辑线程机器上 8 线程比自动快约 13%，16 线程超订阅反而更慢；
  CPU 兼容模式在未指定时用 `CpuThreads = min(8, 核数/2)`（高级设置可覆盖）。

### 7.3 最终 CPU 自动规则（已实现于 `TilePlanner`）

| 场景 | 规则 |
|---|---|
| CPU 自动，长边 ≤ 512 | 单块（`tile=长边`），例如 CUGAN 512 图 4.29 s |
| CPU 自动，长边 > 512 | `tile=512`（1024 图 CUGAN 34.5 s / 3.4GB；避免 768/1024 单块的 5.5–16.8GB） |
| CPU 兼容模式（`CpuCompatibilityMode=true`） | 固定 `tile=256`（CUGAN 512 图 12.2 s / 0.87GB；anime-6b 2.18GB），稳定优先 |

- 早期 256 规则对比（512 图 CUGAN）：旧规则 12.15 s → 新规则 **4.29 s（快 2.8 倍）**。
- 单块 768/1024 虽然更快（8.4 s / 13.8 s），但峰值内存 5.5–13.1GB；anime-6b 1024 单块直接
  `bad allocation`——因此**正常与兼容模式都不允许 768/1024 单块**，以保护 8GB RAM 机器。

### 7.4 真实 CPU 验收（最终规则，`realesr-cugan-4x-conservative`）

输入 `anime-03-crop.jpg` 512×341（动漫插画），4× 输出 **2048×1364**：

| 模式 | 耗时 | 分块 |
|---|---|---|
| CPU 自动（单块） | **3.4 s**（batch 进程总 3.6 s） | 1 块，tile=512 |
| CPU 兼容（稳定优先） | **9.9 s**（batch 进程总 10.2 s） | 6 块，tile=256 |
| 参照：GPU DirectML fp16（同图同模型） | ~0.8 s | 1 块 |

- 输出尺寸/命名/失败隔离与 GPU 路径一致（`var/out/batch-cpu-final*`、`batch-cpu-compat-final*`）。
- 旧模型 `realesrgan-anime-6b` 兼容模式同图 27.8 s（历史数据，`cpu-accept.txt`）。

### 7.5 CPU 兼容模式定义

- 后端强制 CPU + fp32（`ExecutionBackend.Cpu` 本身强制 fp32）。
- 自动分块固定 **256**（峰值内存 CUGAN 0.87GB / anime-6b 2.18GB，8GB RAM 可安全运行）。
- 并发：单会话串行；IntraOp 线程默认上限 8（`CpuThreads=0` 且兼容模式时）。
- 任意支持 SSE4.1 的 x64 Windows 可跑；无 DirectML 时 UI 自动切此模式。

---

## 8. 本体优化（2026-10-17，迭代 3）

> 测试机：RTX 5060 Laptop 8GB + Ryzen 9 8945HX + DirectML；CUGAN 4×（`realesr-cugan-4x-conservative`）。原始日志：`var/out/perf2/`（baseline-pre/after-all/writeback/cugan-tile-sweep）。优化前后用同一组输入图；部分数字在系统两次崩溃前完成记录，崩溃后已复核代码与测试。

### 8.1 结果总览

| 项目 | 优化前 | 优化后 | 变化 |
|---|---|---|---|
| 单图 1024×1024 → 4096×4096（引擎总耗时，含预览、不含写盘） | 1939 ms | **1705 ms** | **-12%** |
| 单图 1920×1326 → 7680×5304（同上） | 3034 ms | **2770 ms** | **-9%** |
| CLI batch 6 张（GPU，含写盘；含 2 张 1920 与 1 张 1920×2327） | 22.7 s | **17.5 s** | **-23%** |
| 发布包体积 | 356.4 MB | **250.8 MB**（§18 根目录布局后为 263.2 MB） | **-29.6%** |
| UI 添加 10×1920 图：缩略图就绪 / 峰值 WorkingSet | 263 ms / 374.8 MB | **178 ms / 229.7 MB** | -32% / -39% |
| UI 批量 9×1920 图墙钟（Debug exe，仅相对比较） | 102.05 s | **78.15 s** | -23% |

### 8.2 引擎：张量写回内联

- `TileInference` 改用 `RunAndConsume`：在 ORT 结果对象释放前直接消费输出张量；float/half→byte 写回去掉每像素/每通道委托调用，改为“一次分支 + 内联循环”，数值语义不变（128/128 测试通过）。
- 效果：4096² 输出约 5000 万通道值，写回 CPU 开销下降；1024² 端到端 1939→1705 ms（-12%），1920 3034→2770 ms（-9%）。
- 说明：单独写回对比为 1780 ms（噪声范围内），整体收益还包含结果对象生命周期缩短带来的 GC/拷贝减少。

### 8.3 分块参数复核（CUGAN，未改规则）

- 1024²：tile 256 / 384 / 512 / 640 = 997 / **806** / 1183 / 862 ms（块均 39/88/130/212 ms，吞吐 16.8/20.8/14.2/19.5 MPix/s）。
- 1920×1326：tile 256 / 384 / 512 / 640 = 3678 / **2794** / 2960 / 3381 ms。
- 结论：现有自动规则（长边 >512 用 tile=384）已是最优，保持不改。

### 8.4 批量流水线 + CLI 预览开关

- `BatchUpscalerService`：当前图推理完成后，把 **保存（PNG 编码 + 落盘）与下一张的解码/推理重叠**；最多 1 张结果待保存；保存失败仍按单项 Failed 记录，不取消整批。
- CLI `batch` 默认不再生成 UI 预览（`KeepPreview=false`，可用 `--keep-preview` 打开）。
- 效果：6 张批量 22.7 s → 17.5 s（-23%）；输出尺寸/命名/失败隔离与之前一致（测试覆盖）。

### 8.5 2×/3× 降采样试验（**未采用，已回退**）

- 方案：TargetScale<4 的最终降采样由 Skia Mitchell 换成 Core 的 Lanczos3。
- 口径：HR →(1/scale 下采样) LR → 模型 4× → 降采样回 HR 尺寸，比较 PSNR/SSIM（1024² 中心裁剪，动漫图）：
  - 2×：Mitchell 33.18 dB / 0.9883（42 ms）→ Lanczos3 33.39 dB / 0.9886（257 ms）：**+0.21 dB / +0.0003**
  - 3×：Mitchell 31.75 dB / 0.9806（32 ms）→ Lanczos3 31.60 dB / 0.9804（125 ms）：**-0.15 dB / -0.0002**
- 结论：无稳定画质收益、降采样耗时约 6 倍，**已回退 Mitchell**；LanczosResampler 保留给 CLI/质量评估使用。引擎 TargetScale=2 的路径已逐像素验证与 Mitchell 回退后的输出一致（3× 因裁剪尺寸差 1px 未做逐像素断言，属测试口径问题）。

### 8.6 发布包清理

- `scripts/build.ps1 -Publish` 新增清理：`*.pdb`、`*.lib`、`DirectML.Debug.*`（实测 7 个文件、105.6 MB）。
- 这些仅是调试符号/链接文件，运行不需要；发布包 **356.4 → 250.8 MB**（§18 根目录布局加入约 12 MB 启动器后为 263.2 MB），并自动启动自检通过。

### 8.7 未采用/后续项

- DirectML Graph Capture、CPU/GPU 双缓冲解码并行：本轮未做（风险/收益比不划算，留待后续）。
- 预览缓存命中率与低内存机器策略：见 `docs/UI-NOTES.md` §11。