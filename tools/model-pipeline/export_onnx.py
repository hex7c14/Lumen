"""把 Real-ESRGAN 系 .pth 权重导出为动态 H/W 轴的 ONNX（opset 17），并生成 fp16 版本。

用法示例：
  python export_onnx.py --name realesrgan-x4plus --pth weights/RealESRGAN_x4plus.pth \
      --arch rrdb --num-block 23 --out-dir ../../models/realesrgan-x4plus --scale 4

输出：
  <out-dir>/model.onnx       (float32, 输入输出 NCHW, 动态 batch/H/W)
  <out-dir>/model.fp16.onnx  (float16, 同样动态轴)
"""
import argparse
import os
import sys

import numpy as np
import onnx
import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from arch import OldRRDBNet, RRDBNet, SRVGGNetCompact  # noqa: E402

OPSET = 17
DYN_AXES = {'input': {0: 'batch', 2: 'height', 3: 'width'},
            'output': {0: 'batch', 2: 'height', 3: 'width'}}


def load_state(path):
    sd = torch.load(path, map_location='cpu', weights_only=True)
    if isinstance(sd, dict):
        for key in ('params_ema', 'params', 'state_dict', 'model'):
            if key in sd and isinstance(sd[key], dict):
                sd = sd[key]
                break
    cleaned = {}
    for k, v in sd.items():
        k = k.replace('module.', '')
        cleaned[k] = v
    if not any(k.startswith('conv_first.') for k in cleaned) and any(k.startswith('body.') for k in cleaned):
        pass
    return cleaned


def build_model(args):
    if args.arch == 'rrdb':
        net = RRDBNet(num_in_ch=3, num_out_ch=3, scale=4,
                      num_feat=args.num_feat, num_block=args.num_block, num_grow_ch=32)
    elif args.arch == 'rrdb-old':
        net = OldRRDBNet(nf=args.num_feat, nb=args.num_block, gc=32)
    elif args.arch == 'srvgg':
        net = SRVGGNetCompact(num_in_ch=3, num_out_ch=3, num_feat=args.num_feat,
                              num_conv=args.num_conv, upscale=4, act_type=args.act)
    else:
        raise ValueError(args.arch)
    sd = load_state(args.pth)
    if args.arch == 'rrdb-old':
        net.load_state_dict(sd, strict=True)  # key 与 OldRRDBNet 逐字对应
    else:
        missing, unexpected = net.load_state_dict(sd, strict=False)
        if missing or unexpected:
            raise SystemExit(f'state_dict mismatch: missing={missing[:5]} unexpected={unexpected[:5]}')
    return net.eval()


def check_torch_vs_onnx(fp32_path, torch_out, lr):
    import onnxruntime as ort
    sess = ort.InferenceSession(fp32_path, providers=['CPUExecutionProvider'])
    iname = sess.get_inputs()[0].name
    onnx_out = sess.run(None, {iname: lr})[0]
    mse = float(np.mean((onnx_out - torch_out) ** 2))
    psnr = 99.0 if mse == 0 else float(10 * np.log10(1.0 / mse))
    return psnr, onnx_out.shape


def convert_fp16(fp32_path, fp16_path):
    """优先 onnxconverter-common；失败则退回 onnxruntime.transformers。"""
    errors = []
    try:
        from onnxconverter_common import float16 as f16
        model = onnx.load(fp32_path)
        model16 = f16.convert_float_to_float16(model, keep_io_types=False)
        onnx.save(model16, fp16_path)
        return 'onnxconverter-common.keep_io_types=False'
    except Exception as e:  # noqa: BLE001
        errors.append(f'onnxconverter-common: {e!r}')
    try:
        from onnxruntime.transformers import float16 as ort_f16
        model = onnx.load(fp32_path)
        model16 = ort_f16.convert_float_to_float16(model, keep_io_types=False)
        onnx.save(model16, fp16_path)
        return 'onnxruntime.transformers.float16'
    except Exception as e:  # noqa: BLE001
        errors.append(f'onnxruntime.transformers: {e!r}')
    raise SystemExit('fp16 转换失败: ' + ' | '.join(errors))


def verify_fp16(fp16_path):
    import onnxruntime as ort
    sess = ort.InferenceSession(fp16_path, providers=['CPUExecutionProvider'])
    ins = sess.get_inputs()[0]
    outs = sess.get_outputs()[0]
    lr = (np.random.default_rng(0).random((1, 3, 48, 64), dtype=np.float32))
    out = sess.run(None, {ins.name: lr.astype(np.float16)})[0]
    if out.dtype != np.float16:
        raise SystemExit(f'fp16 输出 dtype 不是 float16: {out.dtype}')
    return ins.type, outs.type, tuple(out.shape), float(np.isfinite(out).mean())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--name', required=True)
    ap.add_argument('--pth', required=True)
    ap.add_argument('--arch', choices=['rrdb', 'rrdb-old', 'srvgg'], default='rrdb')
    ap.add_argument('--num-block', type=int, default=23)
    ap.add_argument('--num-conv', type=int, default=32)
    ap.add_argument('--num-feat', type=int, default=64)
    ap.add_argument('--act', default='prelu')
    ap.add_argument('--out-dir', required=True)
    ap.add_argument('--scale', type=int, default=4)
    args = ap.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    net = build_model(args)
    n_params = sum(p.numel() for p in net.parameters())
    print(f'[{args.name}] params={n_params:,} arch={args.arch}')

    lr = np.random.default_rng(42).random((1, 3, 32, 40), dtype=np.float32)
    with torch.no_grad():
        torch_out = net(torch.from_numpy(lr)).numpy()

    fp32_path = os.path.join(args.out_dir, 'model.onnx')
    torch.onnx.export(
        net, torch.from_numpy(lr), fp32_path,
        opset_version=OPSET,
        input_names=['input'], output_names=['output'],
        dynamic_axes=DYN_AXES,
        dynamo=False,
        do_constant_folding=True,
    )
    m = onnx.load(fp32_path)
    onnx.checker.check_model(m)
    # 再检查一下动态轴是否真的生效
    dims = m.graph.input[0].type.tensor_type.shape.dim
    for d in dims:
        if d.dim_param == '' and d.dim_value not in (3,):
            pass
    print(f'[{args.name}] fp32 onnx saved {os.path.getsize(fp32_path):,} bytes')

    psnr_cpu, shape = check_torch_vs_onnx(fp32_path, torch_out, lr)
    print(f'[{args.name}] torch vs onnx(CPU, fp32) PSNR={psnr_cpu:.2f} dB shape={shape}')
    if psnr_cpu < 40:
        raise SystemExit(f'[{args.name}] 导出与 torch 输出差异过大，疑似结构不匹配')

    fp16_path = os.path.join(args.out_dir, 'model.fp16.onnx')
    how = convert_fp16(fp32_path, fp16_path)
    print(f'[{args.name}] fp16 onnx saved {os.path.getsize(fp16_path):,} bytes via {how}')
    print(f'[{args.name}] fp16 io: in={verify_fp16(fp16_path)[0]} out={verify_fp16(fp16_path)[1]}')
    print(f'[{args.name}] dynamic axes: batch/H/W')


if __name__ == '__main__':
    main()
