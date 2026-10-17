"""校验导出的 ONNX 模型：

1) 每个 models/<id> 的 fp32 与 fp16 模型输出形状正确（scale=4）、fp16 与 fp32 的差异（PSNR/最大误差）；
2) 与社区/官方参考 ONNX 交叉验证，防止网络结构重新实现出错。

用法（在 tools/model-pipeline 目录下，参考 ONNX 缺失时自动跳过）：
  ..\\venv\\Scripts\\python.exe verify_models.py
"""
import os
import sys

import numpy as np
import onnxruntime as ort

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS_ROOT = os.path.abspath(os.path.join(HERE, '..', '..', 'models'))
W = os.path.join(HERE, 'weights')


def psnr01(a, b):
    mse = float(np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2))
    return 99.0 if mse == 0 else float(10 * np.log10(1.0 / mse))


def load(path):
    so = ort.SessionOptions()
    so.log_severity_level = 3
    return ort.InferenceSession(path, sess_options=so, providers=['CPUExecutionProvider'])


def run(sess, x):
    it = sess.get_inputs()[0].type
    x = x.astype(np.float16) if 'float16' in it else x.astype(np.float32)
    return sess.run(None, {sess.get_inputs()[0].name: x})[0].astype(np.float32)


def main():
    rng = np.random.default_rng(7)
    x_dyn = rng.random((1, 3, 64, 80), dtype=np.float32)
    x_64 = rng.random((1, 3, 64, 64), dtype=np.float32)
    ok = True

    print('=== fp32/fp16 一致性（输入 64x80，随机） ===')
    for name in sorted(os.listdir(MODELS_ROOT)):
        d = os.path.join(MODELS_ROOT, name)
        fp32 = os.path.join(d, 'model.onnx')
        fp16 = os.path.join(d, 'model.fp16.onnx')
        if not os.path.isfile(fp32):
            continue
        a = run(load(fp32), x_dyn)
        shape_ok = a.shape == (1, 3, 256, 320)
        line = f'{name:24s} fp32 out={a.shape}'
        if os.path.isfile(fp16):
            b = run(load(fp16), x_dyn)
            line += f'  fp16-vs-fp32 PSNR={psnr01(a, b):.2f} dB maxdiff={np.max(np.abs(a - b)):.5f}'
            ok = ok and b.shape == a.shape
        print(line + ('' if shape_ok else '  [形状错误]'))
        ok = ok and shape_ok

    print('\n=== 奇数尺寸动态轴检查（63x79 -> 252x316） ===')
    x_odd = rng.random((1, 3, 63, 79), dtype=np.float32)
    for name in sorted(os.listdir(MODELS_ROOT)):
        fp32 = os.path.join(MODELS_ROOT, name, 'model.onnx')
        if not os.path.isfile(fp32):
            continue
        out = run(load(fp32), x_odd)
        good = out.shape == (1, 3, 252, 316) and np.isfinite(out).all()
        ok = ok and good
        print(f'[{"PASS" if good else "FAIL"}] {name:34s} out={out.shape}')

    print('\n=== 与参考 ONNX 交叉验证（迭代 1 三个模型，可选） ===')
    refs = [
        # (模型 id, 参考路径, 输入, 说明)
        ('realesrgan-x4plus', os.path.join(W, 'ref_x4plus_imgdesignart.onnx'), x_64,
         'imgdesignart/realesrgan-x4-onnx（静态 64x64）'),
        ('realesrgan-x4v3', os.path.join(W, 'ref_x4v3_coderviking.onnx'), x_dyn,
         'CoderViking/realesr-general-x4v3-onnx（动态）'),
        ('4x-ultrasharp', os.path.join(W, '4x-UltraSharp-fp32-opset17.onnx'), x_dyn,
         'Kim2091/UltraSharp 官方 ONNX（动态）'),
    ]
    for mid, ref, x, desc in refs:
        if not os.path.isfile(ref):
            print(f'[SKIP] {mid}: 参考文件不存在 {os.path.basename(ref)}')
            continue
        ours = os.path.join(MODELS_ROOT, mid, 'model.onnx')
        a = run(load(ours), x)
        b = run(load(ref), x)
        p = psnr01(a, b)
        md = float(np.max(np.abs(a - b)))
        good = p > 60
        ok = ok and good
        print(f'[{"PASS" if good else "FAIL"}] {mid} vs {desc}: PSNR={p:.2f} dB maxdiff={md:.6f}')

    print('\n全部通过' if ok else '\n存在失败项')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
