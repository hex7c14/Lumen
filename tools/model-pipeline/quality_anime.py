"""迭代 2：动漫模型质量评估（CPU 推理即可，结果与 GPU 数值等价）。

在 samples/anime 的 3 张图上，以 4x 下采样出的 LR 为输入，比较模型 4x 输出与 HR 的 PSNR/SSIM，
并与 Lanczos4 基线对比；同时算 fp16 vs fp32 差异、生成 1:1 目视对比图。
结果：results/anime_quality.json、results/vis_it2_anime1.png、vis_it2_anime2.png
"""
import json
import os

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


def make_pair(img):
    h, w = img.shape[:2]
    w2, h2 = 1024, int(round(h * 1024.0 / w))
    hr = cv2.resize(img, (w2, h2), interpolation=cv2.INTER_AREA if w2 < w else cv2.INTER_CUBIC)
    h2 -= h2 % 4
    w2 -= w2 % 4
    hr = hr[:h2, :w2]
    lr = cv2.resize(hr, (w2 // 4, h2 // 4), interpolation=cv2.INTER_CUBIC)
    return hr, lr


def load_models():
    out = []
    for name in sorted(os.listdir(MODELS_ROOT)):
        mj = os.path.join(MODELS_ROOT, name, 'model.json')
        if not os.path.isfile(mj):
            continue
        meta = json.load(open(mj, encoding='utf-8'))
        if 'anime' in meta.get('recommendedFor', []):
            out.append({'id': name, 'dir': os.path.join(MODELS_ROOT, name), 'meta': meta})
    return out


def session(path, provider):
    so = ort.SessionOptions()
    so.log_severity_level = 3
    return ort.InferenceSession(path, sess_options=so, providers=[provider])


def run(sess, lr, dtype=np.float32):
    x = np.transpose(lr.astype(np.float32) / 255.0, (2, 0, 1))[None].astype(dtype)
    out = sess.run(None, {sess.get_inputs()[0].name: x})[0]
    out = out.astype(np.float32)
    return np.round(np.clip(out[0].transpose(1, 2, 0), 0, 1) * 255.0).astype(np.uint8)


def label(img, text):
    bar = np.zeros((26, img.shape[1], 3), np.uint8)
    cv2.putText(bar, text, (6, 19), cv2.FONT_HERSHEY_SIMPLEX, 0.52, (255, 255, 255), 1,
                cv2.LINE_AA)
    return np.vstack([bar, img])


def visual_panel(models, img_path, crop, out_name):
    img = np.array(Image.open(img_path).convert('RGB'))
    hr, _ = make_pair(img)
    x0, y0, size = crop
    hr_c = hr[y0:y0 + size, x0:x0 + size]
    lr_c = cv2.resize(hr_c, (size // 4, size // 4), interpolation=cv2.INTER_CUBIC)
    lanc = cv2.resize(lr_c, (size, size), interpolation=cv2.INTER_LANCZOS4)
    tiles = [('HR 参考', hr_c), ('Lanczos4', lanc)]
    for m in models:
        sess = session(os.path.join(m['dir'], 'model.onnx'), 'CPUExecutionProvider')
        out = run(sess, lr_c)
        tiles.append((m['meta']['displayName'], out[:size, :size]))
    cols = [label(t, text) for text, t in tiles]
    gap = np.full((cols[0].shape[0], 6, 3), 255, np.uint8)
    row = cols[0]
    for c in cols[1:]:
        row = np.hstack([row, gap, c])
    Image.fromarray(row).save(os.path.join(RESULTS, out_name))
    print('saved', out_name, row.shape)


def main():
    os.makedirs(RESULTS, exist_ok=True)
    models = load_models()
    print('动漫模型:', [m['id'] for m in models])
    pairs = {}
    for fn in SAMPLE_NAMES:
        img = np.array(Image.open(os.path.join(SAMPLES, fn)).convert('RGB'))
        pairs[fn] = make_pair(img)

    report = {'models': {}, 'samples': list(SAMPLE_NAMES), 'lanczos': {}}
    lanc = {}
    for fn in SAMPLE_NAMES:
        hr, lr = pairs[fn]
        lz = cv2.resize(lr, (hr.shape[1], hr.shape[0]), interpolation=cv2.INTER_LANCZOS4)
        lanc[fn] = {'psnr': round(psnr(lz, hr), 2), 'ssim': round(ssim(lz, hr), 4)}
    report['lanczos'] = lanc
    print('Lanczos:', lanc)

    for m in models:
        mid = m['id']
        s32 = session(os.path.join(m['dir'], 'model.onnx'), 'CPUExecutionProvider')
        s16 = session(os.path.join(m['dir'], 'model.fp16.onnx'), 'CPUExecutionProvider')
        rows = {}
        outs32, outs16 = {}, {}
        for fn in SAMPLE_NAMES:
            hr, lr = pairs[fn]
            o32 = run(s32, lr, np.float32)[:hr.shape[0], :hr.shape[1]]
            o16 = run(s16, lr, np.float16)[:hr.shape[0], :hr.shape[1]]
            outs32[fn], outs16[fn] = o32, o16
            rows[fn] = {
                'psnr_fp32': round(psnr(o32, hr), 2),
                'ssim_fp32': round(ssim(o32, hr), 4),
                'psnr_fp16': round(psnr(o16, hr), 2),
                'ssim_fp16': round(ssim(o16, hr), 4),
            }
        fp = outs32[SAMPLE_NAMES[0]]
        f16 = outs16[SAMPLE_NAMES[0]]
        report['models'][mid] = {
            'meta': m['meta'],
            'quality': rows,
            'fp16_vs_fp32': {
                'psnr_db': round(psnr(fp, f16), 2),
                'ssim': round(ssim(fp, f16), 4),
                'max_abs_diff_255': int(np.max(np.abs(fp.astype(int) - f16.astype(int)))),
            },
        }
        avgp = sum(v['psnr_fp32'] for v in rows.values()) / len(rows)
        avgs = sum(v['ssim_fp32'] for v in rows.values()) / len(rows)
        print(f'{mid:34s} PSNR={avgp:6.2f} SSIM={avgs:.4f} fp16-vs-fp32={report["models"][mid]["fp16_vs_fp32"]}')

    with open(os.path.join(RESULTS, 'anime_quality.json'), 'w', encoding='utf-8') as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    print('写入 results/anime_quality.json')

    # 目视对比（CPU 推理，慢一点但结果与 GPU 数值等价）
    visual_panel(models, os.path.join(SAMPLES, SAMPLE_NAMES[0]), (500, 200, 256),
                 'vis_it2_anime1.png')
    visual_panel(models, os.path.join(SAMPLES, SAMPLE_NAMES[1]), (380, 340, 256),
                 'vis_it2_anime2.png')


if __name__ == '__main__':
    main()
