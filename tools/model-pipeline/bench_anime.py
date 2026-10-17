"""迭代 2：动漫超分模型 GPU/CPU 基准 + 质量评估。

- 评测对象：models/*/model.json 中 recommendedFor 含 "anime" 的模型
  （realesrgan-anime-6b、realesr-cugan-4x-conservative、realesr-cugan-4x-denoise）。
- GPU：DirectML，256/512 tile，fp32/fp16，单独 session，warmup 后取中位数；
- CPU：CPUExecutionProvider，fp32，128/256/512 tile（给 UI/引擎的 CPU 兼容模式当参考）；
- 质量：samples/anime/* 上 HR vs 模型输出 PSNR/SSIM，与 Lanczos4 基线对比；fp16 vs fp32 差异。
结果写 results/anime_bench.json。
"""
import gc
import json
import os
import statistics
import time

import cv2
import numpy as np
import onnxruntime as ort
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
MODELS_ROOT = os.path.join(ROOT, 'models')
SAMPLES = os.path.join(HERE, 'samples', 'anime')
RESULTS = os.path.join(HERE, 'results')
SAMPLE_NAMES = ('anime-01-schoolgirl-cc0-rawpixel.jpg',
                'anime-02-knight-cc0-rawpixel.jpg',
                'anime-03-schoolgirl2-cc0-rawpixel.jpg')


def psnr(a, b):
    mse = float(np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2))
    return 99.0 if mse <= 0 else float(10 * np.log10(255.0 ** 2 / mse))


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


def load_models():
    out = []
    for name in sorted(os.listdir(MODELS_ROOT)):
        d = os.path.join(MODELS_ROOT, name)
        mj = os.path.join(d, 'model.json')
        if not os.path.isfile(os.path.join(d, 'model.onnx')) or not os.path.isfile(mj):
            continue
        meta = json.load(open(mj, encoding='utf-8'))
        if 'anime' in meta.get('recommendedFor', []):
            out.append({'id': name, 'dir': d, 'meta': meta})
    return out


def make_session(path, provider):
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.log_severity_level = 3
    t0 = time.perf_counter()
    sess = ort.InferenceSession(path, sess_options=so, providers=[provider])
    return sess, time.perf_counter() - t0


def run_once(sess, x):
    it = sess.get_inputs()[0].type
    inp = x.astype(np.float16) if 'float16' in it else x.astype(np.float32)
    out = sess.run(None, {sess.get_inputs()[0].name: inp})[0]
    return out.astype(np.float32)


def timeit(sess, x, runs, warmups=1):
    for _ in range(warmups):
        run_once(sess, x)
    ts = []
    for _ in range(runs):
        t0 = time.perf_counter()
        run_once(sess, x)
        ts.append(time.perf_counter() - t0)
    return {'median_s': round(statistics.median(ts), 3),
            'min_s': round(min(ts), 3), 'runs': runs}


def to_u8(out):
    return np.round(np.clip(out[0].transpose(1, 2, 0), 0, 1) * 255.0).astype(np.uint8)


def norm(lr):
    return np.transpose(lr.astype(np.float32) / 255.0, (2, 0, 1))[None]


def make_pair(img):
    h, w = img.shape[:2]
    w2, h2 = 1024, int(round(h * 1024.0 / w))
    hr = cv2.resize(img, (w2, h2), interpolation=cv2.INTER_AREA if w2 < w else cv2.INTER_CUBIC)
    h2 -= h2 % 4
    w2 -= w2 % 4
    hr = hr[:h2, :w2]
    lr = cv2.resize(hr, (w2 // 4, h2 // 4), interpolation=cv2.INTER_CUBIC)
    return hr, lr


def main():
    os.makedirs(RESULTS, exist_ok=True)
    models = load_models()
    print('动漫模型:', [m['id'] for m in models])
    rng = np.random.default_rng(0)
    timing_inputs = {
        '256': rng.random((1, 3, 256, 256), dtype=np.float32),
        '512': rng.random((1, 3, 512, 512), dtype=np.float32),
    }
    pairs = {}
    for fn in SAMPLE_NAMES:
        img = np.array(Image.open(os.path.join(SAMPLES, fn)).convert('RGB'))
        pairs[fn] = make_pair(img)

    report = {'models': {m['id']: m['meta'] for m in models},
              'samples': list(SAMPLE_NAMES),
              'env': {'onnxruntime': ort.__version__,
                      'providers': ort.get_available_providers(),
                      'cpu': os.environ.get('PROCESSOR_IDENTIFIER', ''),
                      'cpu_threads': os.cpu_count()}}

    for m in models:
        mid = m['id']
        print(f'\n=== {mid} ===')
        e = {'load_fp32_s': None, 'load_fp16_s': None, 'gpu': {}, 'cpu': {}, 'quality': {}}
        # ---------- GPU fp32 ----------
        sess, load = make_session(os.path.join(m['dir'], 'model.onnx'), 'DmlExecutionProvider')
        e['load_fp32_s'] = round(load, 2)
        gpu32 = {}
        q32 = None
        for tile in (256, 512):
            gpu32[str(tile)] = timeit(sess, timing_inputs[str(tile)], 3)
            print(f'  GPU fp32 tile{tile}: {gpu32[str(tile)]}')
        # ---------- 质量 fp32 ----------
        for fn in SAMPLE_NAMES:
            hr, lr = pairs[fn]
            out = to_u8(run_once(sess, norm(lr)))[:hr.shape[0], :hr.shape[1]]
            e['quality'].setdefault(fn, {'category': 'anime', 'hr_size': [hr.shape[1], hr.shape[0]]})
            e['quality'][fn]['psnr_fp32'] = round(psnr(out, hr), 2)
            e['quality'][fn]['ssim_fp32'] = round(ssim(out, hr), 4)
            if fn == SAMPLE_NAMES[0]:
                q32 = out
        # fp16 vs fp32（256 tile 的动漫内容）
        del sess
        gc.collect()
        sess16, load16 = make_session(os.path.join(m['dir'], 'model.fp16.onnx'), 'DmlExecutionProvider')
        e['load_fp16_s'] = round(load16, 2)
        gpu16 = {}
        for tile in (256, 512):
            gpu16[str(tile)] = timeit(sess16, timing_inputs[str(tile)], 3)
            print(f'  GPU fp16 tile{tile}: {gpu16[str(tile)]}')
        e['gpu'] = {'fp32': gpu32, 'fp16': gpu16}
        hr0, lr0 = pairs[SAMPLE_NAMES[0]]
        o16 = to_u8(run_once(sess16, norm(lr0)))[:hr0.shape[0], :hr0.shape[1]]
        o32 = q32
        e['fp16_vs_fp32'] = {
            'psnr_db': round(psnr(o32, o16), 2),
            'ssim': round(ssim(o32, o16), 4),
            'max_abs_diff_255': round(float(np.max(np.abs(o32.astype(int) - o16.astype(int)))), 1),
        }
        print(f'  fp16 vs fp32: {e["fp16_vs_fp32"]}')
        for fn in SAMPLE_NAMES:
            hr, lr = pairs[fn]
            out = to_u8(run_once(sess16, norm(lr)))[:hr.shape[0], :hr.shape[1]]
            e['quality'][fn]['psnr_fp16'] = round(psnr(out, hr), 2)
            e['quality'][fn]['ssim_fp16'] = round(ssim(out, hr), 4)
        # ---------- GPU fp32 输出写盘（目视裁剪用） ----------
        hr, lr = pairs[SAMPLE_NAMES[0]]
        img = Image.fromarray(q32)
        img.save(os.path.join(RESULTS, f'anim2_{mid}_full.png'))
        del sess16
        gc.collect()

        # ---------- CPU fp32 ----------
        if os.environ.get('SKIP_CPU') == '1':
            report['models'][mid] = m['meta']
            report['models'][mid]['bench'] = e
            continue
        so = ort.SessionOptions()
        so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        so.log_severity_level = 3
        t0 = time.perf_counter()
        try:
            cpu = ort.InferenceSession(os.path.join(m['dir'], 'model.onnx'),
                                       sess_options=so, providers=['CPUExecutionProvider'])
            e['load_cpu_s'] = round(time.perf_counter() - t0, 2)
            for tile, runs in ((128, 3), (256, 3), (512, 2)):
                t = timeit(cpu, timing_inputs[str(tile)] if tile in (256, 512)
                           else rng.random((1, 3, 128, 128), dtype=np.float32),
                           runs, warmups=1)
                e['cpu'][str(tile)] = t
                print(f'  CPU fp32 tile{tile}: {t}')
        except Exception as ex:  # noqa: BLE001
            print('  CPU 失败:', ex)
        gc.collect()
        report['models'][mid] = m['meta']
        report['models'][mid]['bench'] = e

    # Lanczos 基线（质量表用）
    lanc = {}
    for fn in SAMPLE_NAMES:
        hr, lr = pairs[fn]
        lz = cv2.resize(lr, (hr.shape[1], hr.shape[0]), interpolation=cv2.INTER_LANCZOS4)
        lanc[fn] = {'psnr': round(psnr(lz, hr), 2), 'ssim': round(ssim(lz, hr), 4)}
    report['lanczos'] = lanc

    with open(os.path.join(RESULTS, 'anime_bench.json'), 'w', encoding='utf-8') as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    print(f'\n写入 {os.path.join(RESULTS, "anime_bench.json")}')

    # ---------- 汇总 ----------
    print('\n===== 汇总 =====')
    for mid, meta in report['models'].items():
        e = meta['bench']
        g512 = e['gpu']['fp32']['512']['median_s']
        c512 = e['cpu'].get('512', {}).get('median_s')
        q = e['quality']
        p = sum(v['psnr_fp32'] for v in q.values()) / len(q)
        s = sum(v['ssim_fp32'] for v in q.values()) / len(q)
        print(f'{mid:34s} GPU512={g512:6.2f}s GPU256={e["gpu"]["fp32"]["256"]["median_s"]:5.2f}s '
              f'CPU512={c512 if c512 else "-"}s PSNR={p:6.2f} SSIM={s:.4f}')
    lp = sum(v['psnr'] for v in lanc.values()) / len(lanc)
    ls = sum(v['ssim'] for v in lanc.values()) / len(lanc)
    print(f'{"Lanczos4":34s} PSNR={lp:6.2f} SSIM={ls:.4f}')


if __name__ == '__main__':
    main()
