"""迭代 2 目视对比图：HR / Lanczos4 / anime-6b / CUGAN 保真 / CUGAN 去噪（1:1 裁剪）。

用法（tools/model-pipeline 目录）：
  ..\\venv\\Scripts\\python.exe make_visuals_anime.py
输出：results/vis_it2_anime1.png、vis_it2_anime2.png
"""
import os

import cv2
import numpy as np
import onnxruntime as ort
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
MODELS = os.path.join(ROOT, 'models')
SAMPLES = os.path.join(HERE, 'samples', 'anime')
RESULTS = os.path.join(HERE, 'results')

MODEL_ORDER = [
    ('realesrgan-anime-6b', 'anime-6b'),
    ('realesr-cugan-4x-conservative', 'CUGAN 保真'),
    ('realesr-cugan-4x-denoise', 'CUGAN 去噪'),
]


def run_model(model_id, lr_rgb):
    so = ort.SessionOptions()
    so.log_severity_level = 3
    s = ort.InferenceSession(os.path.join(MODELS, model_id, 'model.onnx'),
                             sess_options=so, providers=['DmlExecutionProvider'])
    x = np.transpose(lr_rgb.astype(np.float32) / 255.0, (2, 0, 1))[None]
    y = s.run(None, {s.get_inputs()[0].name: x})[0]
    return np.round(np.clip(y[0].transpose(1, 2, 0), 0, 1) * 255.0).astype(np.uint8)


def label(img, text):
    bar = np.zeros((26, img.shape[1], 3), np.uint8)
    cv2.putText(bar, text, (6, 19), cv2.FONT_HERSHEY_SIMPLEX, 0.52, (255, 255, 255), 1,
                cv2.LINE_AA)
    return np.vstack([bar, img])


def panel(img_path, crop, out_name):
    img = np.array(Image.open(img_path).convert('RGB'))
    h, w = img.shape[:2]
    w2, h2 = 1024, int(round(h * 1024.0 / w))
    hr = cv2.resize(img, (w2, h2), interpolation=cv2.INTER_AREA if w2 < w else cv2.INTER_CUBIC)
    h2 -= h2 % 4
    w2 -= w2 % 4
    hr = hr[:h2, :w2]
    x0, y0, size = crop
    hr_c = hr[y0:y0 + size, x0:x0 + size]
    lr_c = cv2.resize(hr_c, (size // 4, size // 4), interpolation=cv2.INTER_CUBIC)
    lanc = cv2.resize(lr_c, (size, size), interpolation=cv2.INTER_LANCZOS4)
    tiles = [('HR 参考', hr_c), ('Lanczos4', lanc)]
    for mid, name in MODEL_ORDER:
        out = run_model(mid, lr_c)
        tiles.append((name, out[:size, :size]))
    cols = [label(t, text) for text, t in tiles]
    gap = np.full((cols[0].shape[0], 6, 3), 255, np.uint8)
    row = cols[0]
    for c in cols[1:]:
        row = np.hstack([row, gap, c])
    os.makedirs(RESULTS, exist_ok=True)
    Image.fromarray(row).save(os.path.join(RESULTS, out_name))
    print('saved', out_name, row.shape)


def main():
    panel(os.path.join(SAMPLES, 'anime-01-schoolgirl-cc0-rawpixel.jpg'),
          (500, 200, 256), 'vis_it2_anime1.png')
    panel(os.path.join(SAMPLES, 'anime-02-knight-cc0-rawpixel.jpg'),
          (380, 340, 256), 'vis_it2_anime2.png')


if __name__ == '__main__':
    main()
