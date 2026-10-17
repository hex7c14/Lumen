# 模型管线（model-pipeline）

把超分模型权重导出为 Lumen 可用的 ONNX（动态 H/W、opset 17、fp32 + fp16），生成
`models/<id>/model.json`，并跑 DirectML/CPU 基准与质量评估。

- 迭代 1：Real-ESRGAN 系 4 个模型（照片/动漫/极速）
- 迭代 2：Real-CUGAN 4x 两个变体（动漫专精，默认模型所在）

## 0. 环境（本机已实测）

- Python 3.12（用 uv 装到用户目录，不需要管理员）：
  ```powershell
  # uv.exe 下载: https://github.com/astral-sh/uv/releases (uv-x86_64-pc-windows-msvc.zip)
  .\uv\uv.exe python install 3.12
  .\uv\uv.exe venv --python 3.12 venv
  ```
- 依赖：
  ```powershell
  .\uv\uv.exe pip install --python venv\Scripts\python.exe numpy pillow opencv-python-headless onnx onnxconverter-common onnxruntime-directml
  .\uv\uv.exe pip install --python venv\Scripts\python.exe torch --index-url https://download.pytorch.org/whl/cpu
  ```
  实测版本：Python 3.12.12 / torch 2.14.1+cpu / onnx 1.23.2 / onnxruntime-directml 1.24.4 / opencv 5.0.0.93。
  `onnxruntime.get_available_providers()` 含 `DmlExecutionProvider`（RTX 5060 Laptop, 8GB, 驱动 617.42）。

## 1. 下载源权重

| 文件 | 来源 | 大小 | 许可 |
| --- | --- | --- | --- |
| RealESRGAN_x4plus.pth | https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth | 67,040,989 | BSD-3-Clause |
| RealESRGAN_x4plus_anime_6B.pth | https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.2.4/RealESRGAN_x4plus_anime_6B.pth | 17,938,799 | BSD-3-Clause |
| realesr-general-x4v3.pth | https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesr-general-x4v3.pth | 4,885,111 | BSD-3-Clause |
| 4x-UltraSharp.pth | https://huggingface.co/Kim2091/UltraSharp/resolve/main/4x-UltraSharp.pth | 66,961,958 | CC-BY-NC-SA-4.0（非商业） |
| Real-CUGAN 4x 权重（zip） | https://github.com/bilibili/ailab/releases/download/Real-CUGAN/updated_weights.zip | 54,001,450（含 up4x-latest-*.pth 各 5,636,403） | MIT |

下载命令示例：`Invoke-WebRequest -Uri <url> -OutFile weights\<name> -UseBasicParsing`；
CUGAN 的 zip 解压到 `weights/cugan_weights/updated_weights/`。源文件 sha256 见 `weights/` 实际文件。

## 2. 导出 ONNX（fp32 + fp16，动态 batch/H/W，opset 17）

### 2.1 Real-ESRGAN 系（迭代 1）

```powershell
$py = "venv\Scripts\python.exe"
& $py export_onnx.py --name realesrgan-x4plus   --pth weights/RealESRGAN_x4plus.pth        --arch rrdb     --num-block 23 --out-dir ../../models/realesrgan-x4plus
& $py export_onnx.py --name realesrgan-anime-6b --pth weights/RealESRGAN_x4plus_anime_6B.pth --arch rrdb     --num-block 6  --out-dir ../../models/realesrgan-anime-6b
& $py export_onnx.py --name 4x-ultrasharp       --pth weights/4x-UltraSharp.pth              --arch rrdb-old --num-block 23 --out-dir ../../models/4x-ultrasharp
& $py export_onnx.py --name realesrgan-x4v3     --pth weights/realesr-general-x4v3.pth       --arch srvgg    --num-conv 32 --num-feat 64 --act prelu --out-dir ../../models/realesrgan-x4v3
```

说明：
- `arch.py` 中网络结构从 BasicSR / Real-ESRGAN 官方代码内联（Apache-2.0 / BSD-3-Clause），仅保留推理路径。
- `rrdb-old`：4x-UltraSharp.pth 是旧版 ESRGAN 结构（key 形如 `model.1.sub.0.RDB1.conv1.0.weight`），
  用 `OldRRDBNet` 映射；导出结果已与 Kim2091 官方 ONNX 交叉验证（见 §4）。
- `srvgg`：SRVGGNetCompact 官方前向包含“最近邻上采样输入作为全局残差”（`out + base`），
  漏掉该项会导致输出接近全黑——本脚本已实现，导出的 x4v3 与社区参考 ONNX 逐元素一致。
- fp16 由 `onnxconverter-common.float16.convert_float_to_float16(keep_io_types=False)` 生成。

### 2.2 Real-CUGAN 4x（迭代 2，动漫默认）

```powershell
& $py export_cugan.py --variant conservative --out-dir ../../models/realesr-cugan-4x-conservative
& $py export_cugan.py --variant denoise3x  --out-dir ../../models/realesr-cugan-4x-denoise
```

要点：
- **官方 Pro 只有 2x/3x**（`weights_pro/` 无 4x），4x 只有 `up4x-latest-*` 三档：
  conservative（保真，等价 noise=-1）、no-denoise、denoise3x（去噪）；4x 无 noise=1 档。
  因此导出 conservative + denoise3x 两个变体（`VARIANTS` 里可选 no-denoise）。
- 官方实现 `UpCunet4x.forward(tile_mode=0)` 内部会反射 pad 19 并把尺寸补到偶数（奇数输入多补 1），
  网络输出再裁剪到 4×、加最近邻残差、clamp。为兼容任意 H/W，脚本导出「卷积 core」后
  **用 ONNX 图拼装前后处理**：`Shape→Gather→Mod→Concat→Pad(reflect)`（动态 parity pad）、
  `Resize`（4x 残差）、动态 `Slice` 裁剪、`Add→Clip`。奇/偶尺寸均与官方 PyTorch 对齐
  （验证 PSNR 85–91 dB、最大差 1/255）。
- **DirectML 坑**：PyTorch 会把 `F.pad(x, (-4,-4,-4,-4))` 这类负 padding 导出成带负 pads 的
  `Pad` 节点。ONNX CPU 能算，但 **DirectML 会输出全 0**（表现为结果≈最近邻放大）。脚本在导出时
  把负 padding 改写为静态 `Slice`，并断言 core 图中不再有 `Pad` 节点（只剩前处理的正向 reflect Pad）。

## 3. 生成 model.json

```powershell
& $py make_catalog.py     # 填 sha256/sizeBytes 与展示元数据；DEFAULT_ID 控制 isDefault
& $py validate_catalog.py # 按 docs/ARCHITECTURE.md §4 校验字段/文件/id
```

## 4. 与参考 ONNX 交叉验证

`weights/` 中放了三份社区参考 ONNX（可选，缺失时脚本跳过）：

| 参考 | 大小 | 用途 |
| --- | --- | --- |
| ref_x4plus_imgdesignart.onnx | 67,051,787 | RealESRGAN_x4plus（静态 64×64） |
| ref_x4v3_coderviking.onnx | 4,866,417 | realesr-general-x4v3（动态轴） |
| 4x-UltraSharp-fp32-opset17.onnx | 67,002,795 | 4x-UltraSharp（Kim2091 官方，动态轴） |

`& $py verify_models.py` 输出：各模型 fp32/fp16 一致性、奇数尺寸（63×79→252×316）动态轴检查、
以及每份参考 ONNX 的 PSNR/maxdiff。

## 5. 基准与质量评估

```powershell
# 迭代 1：全部模型（照片+动漫）
& $py bench.py            # GPU 基准/质量 -> results/bench.json（--quick、--models 可选）
& $py measure_vram.py     # 显存占用（独立子进程）-> results/vram.json
& $py make_visuals.py     # 迭代 1 目视对比图 -> results/vis_photo_compare.png 等

# 迭代 2：动漫模型专项
& $py bench_anime.py          # GPU 基准 + 质量（SKIP_CPU=1 时跳过 CPU）-> results/anime_bench.json
& $py measure_cpu_anime.py    # CPU fp32 单块耗时（独立进程）-> results/anime_cpu.json
& $py quality_anime.py        # 质量 + 1:1 目视对比图 -> results/anime_quality.json、vis_it2_anime*.png

# 汇总生成文档
& $py make_report.py      # -> ../../docs/MODELS.md
```

`bench.py` 指标：单 tile 256/512 耗时（warmup 后中位数，含 CPU↔GPU 拷贝）、fp16 vs fp32 输出
PSNR/SSIM、模型/Lanczos 与 HR 的 PSNR/SSIM。显存由 `measure_vram.py` 单独测（避免同进程多次
加载导致基线漂移）。`bench_anime.py`/`measure_cpu_anime.py` 同理，分别给 GPU 与 CPU 数字。

## 6. 评估集

见 `samples/README.md`：3 张公开照片（Unsplash License）+ 3 张 CC0 插画（迭代 1 用 2 张，
迭代 2 追加 anime-03），来源与许可均已记录。

## 7. 目录

```
tools/model-pipeline/
  arch.py                 # 推理网络结构（RRDBNet / OldRRDBNet / SRVGGNetCompact）
  export_onnx.py          # Real-ESRGAN 系 pth -> onnx(fp32/fp16)
  export_cugan.py         # Real-CUGAN 4x pth -> onnx（parity-safe 图拼装 + 负 padding 修 DML bug）
  make_catalog.py         # 生成 models/<id>/model.json（含 isDefault）
  validate_catalog.py     # 契约校验
  verify_models.py        # 一致性 + 奇数尺寸 + 参考 ONNX 交叉验证
  bench.py                # 迭代 1 GPU 基准与质量
  bench_anime.py          # 迭代 2 动漫模型 GPU 基准与质量
  measure_vram.py         # 显存占用测量（独立子进程）
  measure_cpu_anime.py    # 迭代 2 动漫模型 CPU fp32 单块耗时
  quality_anime.py        # 迭代 2 质量对比 + 目视裁剪图
  make_visuals.py         # 迭代 1 目视对比图
  make_visuals_anime.py   # 迭代 2 目视对比图（GPU 版，quality_anime 也能生成）
  make_report.py          # 由 results 生成 docs/MODELS.md（含迭代 2 章节）
  weights/                # 源 .pth、CUGAN 官方权重、参考 onnx（不进 models/）
  samples/                # 评估图（photo/ anime/）
  results/                # bench.json、vram.json、anime_*.json、目视对比图
  uv/ venv/               # 本地 Python 工具链（不提交）
```
