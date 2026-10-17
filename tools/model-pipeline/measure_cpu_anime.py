"""迭代 2：CPU（fp32）单块耗时测量（每次一个模型一个独立进程，数字稳定，UI/引擎可引用）。

用法（tools/model-pipeline 目录）：
  ..\\venv\\Scripts\\python.exe measure_cpu_anime.py
结果：results/anime_cpu.json
"""
import json
import os
import statistics
import subprocess
import sys
import time

import numpy as np
import onnxruntime as ort

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
MODELS_ROOT = os.path.join(ROOT, 'models')
RESULTS = os.path.join(HERE, 'results')
TILES = (128, 256, 512)


def measure_one(mid):
    path = os.path.join(MODELS_ROOT, mid, 'model.onnx')
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.log_severity_level = 3
    t0 = time.perf_counter()
    sess = ort.InferenceSession(path, sess_options=so, providers=['CPUExecutionProvider'])
    load = time.perf_counter() - t0
    name = sess.get_inputs()[0].name
    out = {'load_s': round(load, 2), 'threads_default': os.cpu_count()}
    rng = np.random.default_rng(0)
    for tile in TILES:
        x = rng.random((1, 3, tile, tile), dtype=np.float32)
        runs = 3 if tile <= 256 else 2
        # 大 tile 先跑一次确认可跑通并计入 warmup
        sess.run(None, {name: x})
        ts = []
        for _ in range(runs):
            t0 = time.perf_counter()
            sess.run(None, {name: x})
            ts.append(time.perf_counter() - t0)
        out[str(tile)] = {'median_s': round(statistics.median(ts), 2),
                          'min_s': round(min(ts), 2), 'runs': runs}
        print(f'{mid:34s} CPU fp32 {tile}: {out[str(tile)]}', flush=True)
    return out


def main():
    if len(sys.argv) >= 3 and sys.argv[1] == '--one':
        print(json.dumps(measure_one(sys.argv[2])))
        return
    os.makedirs(RESULTS, exist_ok=True)
    models = []
    for name in sorted(os.listdir(MODELS_ROOT)):
        mj = os.path.join(MODELS_ROOT, name, 'model.json')
        if not os.path.isfile(mj):
            continue
        meta = json.load(open(mj, encoding='utf-8'))
        if 'anime' in meta.get('recommendedFor', []):
            models.append(name)
    print('动漫模型:', models, flush=True)
    out = {}
    for mid in models:
        cp = subprocess.run([sys.executable, os.path.abspath(__file__), '--one', mid],
                            capture_output=True, text=True, timeout=600)
        if cp.returncode != 0:
            print('FAIL', mid, cp.stderr[-800:])
            continue
        out[mid] = json.loads(cp.stdout.strip().splitlines()[-1])
    with open(os.path.join(RESULTS, 'anime_cpu.json'), 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    print('\n===== CPU(fp32) 汇总（Ryzen 9 8945HX，ORT 默认线程）=====')
    for mid, v in out.items():
        print(f'{mid:34s} 128={v["128"]["median_s"]}s 256={v["256"]["median_s"]}s '
              f'512={v["512"]["median_s"]}s load={v["load_s"]}s')
    print(f'写入 {os.path.join(RESULTS, "anime_cpu.json")}')


if __name__ == '__main__':
    main()
