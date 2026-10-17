"""Lumen 超分模型 DirectML 基准 + 质量评估（可复现脚本）。

用法（在 tools/model-pipeline 目录下）：
  ..\\venv\\Scripts\\python.exe bench.py                 # 全量
  ..\\venv\\Scripts\\python.exe bench.py --quick         # 少跑几次
  ..\\venv\\Scripts\\python.exe bench.py --models realesrgan-x4plus,realesrgan-x4v3
结果写入 results/bench.json，并在 stdout 打印摘要。

测量口径（重要）：
- 每次只加载一个 session（先 fp32 全部测完并释放，再 fp16），避免两个 DirectML session
  同时驻留占满 8GB 显存导致个别模型异常变慢；这与应用中“一次只用一个精度”的用法一致。
- 计时：单 tile LR 256x256 / 512x512，warmup 2 次后取中位数，含 CPU<->GPU 拷贝与输出回读。
- 质量：样本图缩放到 1024 长边并裁到 4 的倍数作 HR，bicubic 4 倍下采样得 LR；
  模型 4x 输出与 HR 比 PSNR/SSIM，同时给 OpenCV Lanczos4 基线。
- fp16 误差：同一 256 tile 输入分别过 fp32/fp16 模型（各自单独加载），以 fp32 输出为参考。
- 显存：本脚本不测显存，见 measure_vram.py（独立进程测量，结果 results/vram.json）。
"""
import argparse
import gc
import json
import os
import statistics
import subprocess
import sys
import time

import cv2
import numpy as np
import onnxruntime as ort
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS_ROOT = os.path.abspath(os.path.join(HERE, '..', '..', 'models'))
SAMPLES_ROOT = os.path.join(HERE, 'samples')
RESULTS = os.path.join(HERE, 'results')


# ---------------- 指标 ----------------
def psnr(a, b):
    mse = float(np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2))
    if mse <= 0:
        return 99.0
    return float(10.0 * np.log10((255.0 ** 2) / mse))


def ssim(a, b):
    a = a.astype(np.float64)
    b = b.astype(np.float64)
    c1 = (0.01 * 255) ** 2
    c2 = (0.03 * 255) ** 2
    k, sigma = (11, 11), 1.5
    mu1 = cv2.GaussianBlur(a, k, sigma)
    mu2 = cv2.GaussianBlur(b, k, sigma)
    mu1s, mu2s, mu12 = mu1 * mu1, mu2 * mu2, mu1 * mu2
    s1 = cv2.GaussianBlur(a * a, k, sigma) - mu1s
    s2 = cv2.GaussianBlur(b * b, k, sigma) - mu2s
    s12 = cv2.GaussianBlur(a * b, k, sigma) - mu12
    return float(np.mean(((2 * mu12 + c1) * (2 * s12 + c2)) /
                         ((mu1s + mu2s + c1) * (s1 + s2 + c2))))


# ---------------- 模型/推理 ----------------
def find_models(models_root):
    models = []
    for name in sorted(os.listdir(models_root)):
        d = os.path.join(models_root, name)
        fp32 = os.path.join(d, 'model.onnx')
        fp16 = os.path.join(d, 'model.fp16.onnx')
        if not os.path.isfile(fp32):
            continue
        meta = {}
        with open(os.path.join(d, 'model.json'), encoding='utf-8') as f:
            meta = json.load(f)
        models.append({'id': name, 'dir': d, 'fp32': fp32,
                       'fp16': fp16 if os.path.isfile(fp16) else None, 'meta': meta})
    return models


def make_session(path):
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.log_severity_level = 3
    t0 = time.perf_counter()
    sess = ort.InferenceSession(path, sess_options=so, providers=['DmlExecutionProvider'])
    return sess, time.perf_counter() - t0


def run_once(sess, x_np):
    """输入 float32 NCHW [0,1]；按模型 IO dtype 自动转换。返回 float32 NCHW。"""
    itype = sess.get_inputs()[0].type
    x = x_np.astype(np.float16) if 'float16' in itype else x_np.astype(np.float32)
    out = sess.run(None, {sess.get_inputs()[0].name: x})[0]
    return out.astype(np.float32)


def timeit(sess, x_np, runs):
    """warmup 2 次，再计时 runs 次；返回 (统计, 最后一次输出)。"""
    run_once(sess, x_np)
    last = run_once(sess, x_np)
    ts = []
    for _ in range(runs):
        t0 = time.perf_counter()
        last = run_once(sess, x_np)
        ts.append(time.perf_counter() - t0)
    return {'median_s': round(statistics.median(ts), 4),
            'min_s': round(min(ts), 4), 'max_s': round(max(ts), 4), 'runs': runs}, last


def measure(sess, timing_inputs, runs256, runs512):
    out = {'fp32' if 'float16' not in sess.get_inputs()[0].type else 'fp16': {}}
    key = next(iter(out))
    last_out = None
    for tag, x in timing_inputs.items():
        nr = runs256 if tag == '256' else runs512
        stats, last = timeit(sess, x, nr)
        out[key][tag] = stats
        if tag == '256':
            last_out = last
    return out, last_out


def load_sample(path):
    return np.array(Image.open(path).convert('RGB'))


def make_pair(img, mode):
    """返回 (HR uint8, LR uint8)，HR 尺寸为 4 的倍数。"""
    h, w = img.shape[:2]
    if mode == 'photo':
        scale = 1024.0 / min(h, w)          # 短边 1024
        w2, h2 = int(round(w * scale)), int(round(h * scale))
    else:
        w2, h2 = 1024, int(round(h * 1024.0 / w))  # 长边 1024
    hr = cv2.resize(img, (w2, h2), interpolation=cv2.INTER_AREA if w2 < w else cv2.INTER_CUBIC)
    h2 -= h2 % 4
    w2 -= w2 % 4
    hr = hr[:h2, :w2]
    lr = cv2.resize(hr, (w2 // 4, h2 // 4), interpolation=cv2.INTER_CUBIC)
    return hr, lr


def norm(lr_u8):
    return np.transpose(lr_u8.astype(np.float32) / 255.0, (2, 0, 1))[None]


def to_hwc_u8(out_nchw):
    out = np.clip(out_nchw[0].transpose(1, 2, 0), 0, 1) * 255.0
    return np.round(out).astype(np.uint8)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--quick', action='store_true', help='少跑几次（更快）')
    ap.add_argument('--models', default=None, help='只测指定 id（逗号分隔）')
    args = ap.parse_args()

    os.makedirs(RESULTS, exist_ok=True)
    models = find_models(MODELS_ROOT)
    if args.models:
        want = set(args.models.split(','))
        models = [m for m in models if m['id'] in want]
    if not models:
        raise SystemExit('未找到模型')

    samples = []
    for cat in ('photo', 'anime'):
        for fn in sorted(os.listdir(os.path.join(SAMPLES_ROOT, cat))):
            samples.append({'category': cat, 'path': os.path.join(SAMPLES_ROOT, cat, fn)})

    report = {'env': {}, 'method': 'single-session; warmup2; median; DML',
              'fp16_vs_fp32': {}, 'quality': {},
              'models': {m['id']: m['meta'] for m in models},
              'samples': [os.path.relpath(s['path'], RESULTS) for s in samples]}
    report['env'] = {
        'onnxruntime': ort.__version__,
        'providers': ort.get_available_providers(),
        'gpu': subprocess.run(['nvidia-smi', '--query-gpu=name,memory.total,driver_version',
                               '--format=csv,noheader'], capture_output=True, text=True,
                              timeout=10).stdout.strip(),
        'cv2': cv2.__version__,
    }

    ref = load_sample(samples[1]['path'])
    h, w = ref.shape[:2]
    c = ref[h // 2 - 512:h // 2 + 512, w // 2 - 512:w // 2 + 512]
    timing_inputs = {'256': norm(cv2.resize(c, (256, 256), interpolation=cv2.INTER_AREA))}
    pairs = {}
    for s in samples:
        img = load_sample(s['path'])
        hr, lr = make_pair(img, 'photo' if s['category'] == 'photo' else 'anime')
        pairs[s['path']] = (hr, lr)
        if '512' not in timing_inputs:
            timing_inputs['512'] = norm(cv2.resize(lr, (512, 512), interpolation=cv2.INTER_AREA))

    print('=== 环境 ===')
    print(json.dumps(report['env'], ensure_ascii=False, indent=1))
    print(f'样本数: {len(samples)}  模型数: {len(models)}')
    runs256 = 3 if args.quick else 6
    runs512 = 2 if args.quick else 4

    for m in models:
        mid = m['id']
        print(f'\n=== {mid} ===')
        entry = {'load_fp32_s': None, 'load_fp16_s': None,
                 'fp32': {}, 'fp16': {}}
        out32_256 = out16_256 = None

        # ---- fp32（独占 session） ----
        sess, load = make_session(m['fp32'])
        print(f'fp32 加载: {load:.2f}s')
        entry['load_fp32_s'] = round(load, 3)
        r, out32_256 = measure(sess, timing_inputs, runs256, runs512)
        entry['fp32'] = r['fp32']
        print('fp32', json.dumps(r['fp32']))
        del sess
        gc.collect()

        # ---- fp16（独占 session） ----
        if m['fp16']:
            sess16, load16 = make_session(m['fp16'])
            print(f'fp16 加载: {load16:.2f}s')
            entry['load_fp16_s'] = round(load16, 3)
            r, out16_256 = measure(sess16, timing_inputs, runs256, runs512)
            entry['fp16'] = r['fp16']
            print('fp16', json.dumps(r['fp16']))
            del sess16
            gc.collect()

        # ---- fp16 vs fp32（256 tile 真实内容） ----
        if out32_256 is not None and out16_256 is not None:
            u32 = np.clip(out32_256[0].transpose(1, 2, 0), 0, 1) * 255.0
            u16 = np.clip(out16_256[0].transpose(1, 2, 0), 0, 1) * 255.0
            report['fp16_vs_fp32'][mid] = {
                'psnr_db': round(psnr(u32, u16), 2),
                'ssim': round(ssim(u32, u16), 4),
                'max_abs_diff_255': round(float(np.max(np.abs(u32 - u16))), 2),
            }
            print(f'fp16 vs fp32: PSNR={report["fp16_vs_fp32"][mid]["psnr_db"]} dB '
                  f'SSIM={report["fp16_vs_fp32"][mid]["ssim"]} '
                  f'maxdiff={report["fp16_vs_fp32"][mid]["max_abs_diff_255"]}/255')

        # ---- 质量：模型 vs HR / Lanczos vs HR（fp32、fp16 各自独占 session） ----
        q = {}
        for precision, path in (('fp32', m['fp32']), ('fp16', m['fp16'])):
            if not path:
                continue
            sess, _ = make_session(path)
            for s in samples:
                base = os.path.basename(s['path'])
                hr, lr = pairs[s['path']]
                x = norm(lr)
                try:
                    out = to_hwc_u8(run_once(sess, x))
                except Exception as e:  # noqa: BLE001
                    print(f'  {precision} 推理失败 {base}: {e!r}')
                    continue
                out = out[:hr.shape[0], :hr.shape[1]]
                row = q.setdefault(base, {'category': s['category'],
                                          'hr_size': [int(hr.shape[1]), int(hr.shape[0])]})
                row[f'psnr_{precision}'] = round(psnr(out, hr), 2)
                row[f'ssim_{precision}'] = round(ssim(out, hr), 4)
                if precision == 'fp32':
                    lz = cv2.resize(lr, (hr.shape[1], hr.shape[0]),
                                    interpolation=cv2.INTER_LANCZOS4)
                    row['psnr_lanczos'] = round(psnr(lz, hr), 2)
                    row['ssim_lanczos'] = round(ssim(lz, hr), 4)
            del sess
            gc.collect()
        report['quality'][mid] = q
        for base, row in q.items():
            print(f'  {base}: fp32 {row.get("psnr_fp32")}/{row.get("ssim_fp32")}'
                  f'  fp16 {row.get("psnr_fp16")}/{row.get("ssim_fp16")}'
                  f'  lanczos {row.get("psnr_lanczos")}/{row.get("ssim_lanczos")}')
        report['timing'] = report.get('timing', {})
        report['timing'][mid] = entry

    with open(os.path.join(RESULTS, 'bench.json'), 'w', encoding='utf-8') as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    print(f'\n结果已写入 {os.path.join(RESULTS, "bench.json")}')


if __name__ == '__main__':
    main()
