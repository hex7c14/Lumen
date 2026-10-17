"""生成 docs/MODELS.md 用的目视对比裁剪图（结果存 results/vis_*.png）。

用法（tools/model-pipeline 目录）：
  ..\\venv\\Scripts\\python.exe make_visuals.py
"""
import os

import cv2
import numpy as np
import onnxruntime as ort
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
MODELS = os.path.join(ROOT, 'models')
RESULTS = os.path.join(HERE, 'results')
SAMPLES = os.path.join(HERE, 'samples')


def run_model(model_id, lr_bgr):
    so = ort.SessionOptions()
    so.log_severity_level = 3
    s = ort.InferenceSession(os.path.join(MODELS, model_id, 'model.onnx'),
                             sess_options=so, providers=['DmlExecutionProvider'])
    rgb = cv2.cvtColor(lr_bgr, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
    x = np.transpose(rgb, (2, 0, 1))[None]
    y = s.run(None, {s.get_inputs()[0].name: x})[0]
    out = np.clip(y[0].transpose(1, 2, 0), 0, 1) * 255.0
    return cv2.cvtColor(np.round(out).astype(np.uint8), cv2.COLOR_RGB2BGR)


def label(img, text):
    bar = np.zeros((26, img.shape[1], 3), np.uint8)
    cv2.putText(bar, text, (6, 19), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (255, 255, 255), 1,
                cv2.LINE_AA)
    return np.vstack([bar, img])


def panel(hr, lr, models, crop, title_path):
    x0, y0, size = crop
    # HR 裁剪区（size x size），LR 对应 1/4
    hr_c = hr[y0:y0 + size, x0:x0 + size]
    lr_c = cv2.resize(hr_c, (size // 4, size // 4), interpolation=cv2.INTER_CUBIC)
    lr_c = cv2.resize(hr_c, (size // 4, size // 4), interpolation=cv2.INTER_CUBIC)
    lanc = cv2.resize(lr_c, (size, size), interpolation=cv2.INTER_LANCZOS4)
    tiles = [(f'HR ref {size}px', hr_c), ('Lanczos4', lanc)]
    for mid in models:
        out = run_model(mid, lr_c)
        tiles.append((mid, out[:size, :size]))
    cols = [label(t, text) for text, t in tiles]
    gap = np.full((cols[0].shape[0], 6, 3), 255, np.uint8)
    row = cols[0]
    for c in cols[1:]:
        row = np.hstack([row, gap, c])
    os.makedirs(RESULTS, exist_ok=True)
    Image.fromarray(cv2.cvtColor(row, cv2.COLOR_BGR2RGB)).save(os.path.join(RESULTS, title_path))
    print('saved', title_path, row.shape)


def main():
    # 照片：山景（细节/纹理丰富）
    img = np.array(Image.open(os.path.join(SAMPLES, 'photo',
                                           'photo-01-mountain-alejandro-escamilla.jpg')).convert('RGB'))
    img = cv2.cvtColor(img, cv2.COLOR_RGB2BGR)
    hr = cv2.resize(img, (1536, 1024), interpolation=cv2.INTER_AREA)
    panel(hr, None, ['realesrgan-x4plus', '4x-ultrasharp', 'realesrgan-x4v3'],
          (700, 380, 256), 'vis_photo_compare.png')
    # 插画：动漫模型 vs 写实模型
    img = np.array(Image.open(os.path.join(SAMPLES, 'anime',
                                           'anime-01-schoolgirl-cc0-rawpixel.jpg')).convert('RGB'))
    img = cv2.cvtColor(img, cv2.COLOR_RGB2BGR)
    hr = cv2.resize(img, (1024, 680), interpolation=cv2.INTER_AREA)
    panel(hr, None, ['realesrgan-anime-6b', 'realesrgan-x4plus', '4x-ultrasharp'],
          (500, 200, 256), 'vis_anime_compare.png')


if __name__ == '__main__':
    main()
