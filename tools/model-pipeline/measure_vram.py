"""单独测量每个模型/精度的 DirectML 显存占用（估算）。

为什么单独测：onnxruntime 的 DML session 释放后，驱动/运行时可能仍缓存显存，
若在同一进程里连续测多个模型，nvidia-smi 的基线会漂移。这里每个测量都在一个
独立子进程里进行：进程启动后先记录空闲基线，加载 session，再推理并轮询峰值。

用法（tools/model-pipeline 目录）：
  ..\\venv\\Scripts\\python.exe measure_vram.py
输出：results/vram.json + stdout 表格；unit = MiB（nvidia-smi memory.used，含桌面等其它进程噪声）
"""
import json
import os
import statistics
import subprocess
import sys
import threading
import time

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS_ROOT = os.path.abspath(os.path.join(HERE, '..', '..', 'models'))
RESULTS = os.path.join(HERE, 'results')


def gpu_used_mb():
    out = subprocess.run(['nvidia-smi', '--query-gpu=memory.used',
                          '--format=csv,noheader,nounits'],
                         capture_output=True, text=True, timeout=10).stdout.strip().splitlines()
    return int(out[0])


class Sampler(threading.Thread):
    def __init__(self, interval=0.05):
        super().__init__(daemon=True)
        self.interval = interval
        self.peak = 0
        self.stop_flag = threading.Event()

    def run(self):
        while not self.stop_flag.is_set():
            try:
                self.peak = max(self.peak, gpu_used_mb())
            except Exception:  # noqa: BLE001
                pass
            time.sleep(self.interval)

    def stop(self):
        self.stop_flag.set()
        self.join(timeout=5)


def stable_mb(max_wait_s=25.0, tol_mb=30, need=4):
    """等 nvidia-smi 读数稳定：连续 need 次采样（间隔 0.7s）极差 <= tol_mb。"""
    t0 = time.time()
    hist = []
    while time.time() - t0 < max_wait_s:
        hist.append(gpu_used_mb())
        if len(hist) >= need and max(hist[-need:]) - min(hist[-need:]) <= tol_mb:
            return int(sum(hist[-need:]) / need)
        time.sleep(0.7)
    return gpu_used_mb()


def measure_one(model_id, precision, tile, runs=5):
    import onnxruntime as ort
    fname = 'model.onnx' if precision == 'fp32' else 'model.fp16.onnx'
    path = os.path.join(MODELS_ROOT, model_id, fname)
    idle = stable_mb()
    so = ort.SessionOptions()
    so.log_severity_level = 3
    sess = ort.InferenceSession(path, sess_options=so, providers=['DmlExecutionProvider'])
    loaded = stable_mb()
    itype = sess.get_inputs()[0].type
    x = np.random.default_rng(0).random((1, 3, tile, tile), dtype=np.float32)
    if 'float16' in itype:
        x = x.astype(np.float16)
    name = sess.get_inputs()[0].name
    sess.run(None, {name: x})  # warmup（含 DML 图编译）
    post_warmup = stable_mb()
    ts = []
    smp = Sampler()
    smp.start()
    for _ in range(runs):
        t0 = time.perf_counter()
        sess.run(None, {name: x})
        ts.append(time.perf_counter() - t0)
    smp.stop()
    return {
        'idle_mb': idle,
        'after_load_mb': loaded,
        'post_warmup_mb': post_warmup,
        'peak_mb': smp.peak,
        'load_delta_mb': loaded - idle,
        'warmup_delta_mb': post_warmup - idle,
        'infer_peak_delta_mb': smp.peak - idle,
        'median_run_s': round(statistics.median(ts), 3),
    }


def main():
    if len(sys.argv) >= 5 and sys.argv[1] == '--one':
        r = measure_one(sys.argv[2], sys.argv[3], int(sys.argv[4]))
        print(json.dumps(r))
        return

    os.makedirs(RESULTS, exist_ok=True)
    models = sorted(d for d in os.listdir(MODELS_ROOT)
                    if os.path.isfile(os.path.join(MODELS_ROOT, d, 'model.onnx')))
    out = {}
    for mid in models:
        precisions = ['fp32']
        if os.path.isfile(os.path.join(MODELS_ROOT, mid, 'model.fp16.onnx')):
            precisions.append('fp16')
        out[mid] = {}
        for prec in precisions:
            out[mid][prec] = {}
            for tile in (256, 512):
                stable_mb()  # 父进程先等上一子进程的显存彻底回收
                cp = subprocess.run([sys.executable, os.path.abspath(__file__), '--one',
                                     mid, prec, str(tile)],
                                    capture_output=True, text=True, timeout=180)
                if cp.returncode != 0:
                    print(f'FAIL {mid} {prec} {tile}: {cp.stderr[-500:]}')
                    continue
                r = json.loads(cp.stdout.strip().splitlines()[-1])
                out[mid][prec][str(tile)] = r
                print(f'{mid:22s} {prec} {tile}: idle={r["idle_mb"]}MB '
                      f'加载后+{r["load_delta_mb"]}MB 推理峰值+{r["infer_peak_delta_mb"]}MB '
                      f'({r["median_run_s"]}s/次)')
    with open(os.path.join(RESULTS, 'vram.json'), 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    print(f'\n结果已写入 {os.path.join(RESULTS, "vram.json")}')


if __name__ == '__main__':
    main()

