"""Real-CUGAN 4x（官方 latest 权重）导出为可直接用于 Lumen 的 ONNX。

背景 / 关键点：
- 官方权重只有 2x/3x 有 Pro 版本（weights_pro），4x 只有 latest 三档：
  conservative（保真，等价 noise=-1）、no-denoise、denoise3x（去噪，noise=3）。MIT 许可。
- 官方 `UpCunet4x.forward(x, tile_mode, cache_mode, alpha, pro)` 的 tile_mode=0 路径内部：
  先反射 pad 19，再把尺寸补到偶数（奇数输入多补 1），网络输出 4*(P-38)（P 为补齐后尺寸），
  最后裁剪到 4*H/4*W、加上最近邻 4x 残差、clamp 到 [0,255]。
- 偶数输入时补 1 逻辑不触发；但**奇数输入**（例如 683 高的图）需要 parity pad，否则
  torch 导出的图会把补齐量固化。这里采用「导出 core（只含卷积路径）+ ONNX 图拼装
  pre/post（Shape→Mod→拼 pads→Pad、动态 Slice 裁剪、Resize 残差、Add→Clamp）」的方式，
  得到对任意 H/W 都正确的动态轴模型。

用法（tools/model-pipeline 目录）：
  ..\\venv\\Scripts\\python.exe export_cugan.py --variant conservative --out-dir ../../models/realesr-cugan-4x-conservative
  ..\\venv\\Scripts\\python.exe export_cugan.py --variant denoise3x --out-dir ../../models/realesr-cugan-4x-denoise
"""
import argparse
import os
import sys

import numpy as np
import onnx
import torch
from onnx import TensorProto, helper, numpy_helper
from torch import nn
from torch.nn import functional as F

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, 'weights'))
from cugan_upcunet_v3 import UpCunet4x  # noqa: E402

WEIGHTS = os.path.join(HERE, 'weights', 'cugan_weights', 'updated_weights')
VARIANTS = {
    'conservative': 'up4x-latest-conservative.pth',
    'no-denoise': 'up4x-latest-no-denoise.pth',
    'denoise3x': 'up4x-latest-denoise3x.pth',
}
OPSET = 17

# ---------------- 导出兼容性补丁 ----------------
# PyTorch 会把负 padding（F.pad(x, (-4,-4,-4,-4)) 裁剪）导出成“Pad + 负 pads”，
# ONNX 规范要求 pads 非负；onnxruntime CPU 能容忍，但 DirectML EP 会把结果算成全 0。
# 导出时把「四边全负」的 F.pad 改写成等价的切片，导出成静态 Slice，CPU/DML 都正确。
_ORIG_PAD = F.pad


def _pad_export_fix(x, pad, *args, **kwargs):
    if isinstance(pad, (tuple, list)) and len(pad) == 4 and all(isinstance(p, int) and p < 0
                                                                for p in pad):
        left, right, top, bottom = (-p for p in pad)
        return x[:, :, top:x.shape[2] - bottom, left:x.shape[3] - right]
    return _ORIG_PAD(x, pad, *args, **kwargs)


class _PadFix:
    def __enter__(self):
        F.pad = _pad_export_fix
        return self

    def __exit__(self, *exc):
        F.pad = _ORIG_PAD
        return False



class CuganCore(nn.Module):
    """4x 卷积路径（不含 padding 前处理/裁剪/残差/clamp）。输出尺寸 = 4*P - 152。"""

    def __init__(self, base: UpCunet4x):
        super().__init__()
        self.base = base

    def forward(self, y):
        b = self.base
        z = b.unet1.forward(y)
        z0 = b.unet2.forward(z, 1.0)
        z1 = F.pad(z, (-20, -20, -20, -20))
        z = torch.add(z0, z1)
        z = b.conv_final(z)
        z = F.pad(z, (-1, -1, -1, -1))
        z = b.ps(z)
        return z


def load_base(variant):
    pth = os.path.join(WEIGHTS, VARIANTS[variant])
    sd = torch.load(pth, map_location='cpu', weights_only=True)
    is_pro = 'pro' in sd
    if is_pro:
        del sd['pro']
    base = UpCunet4x()
    base.load_state_dict(sd, strict=True)
    return base.eval(), is_pro, pth


def build_core_onnx(base, core_path):
    core = CuganCore(base).eval()
    dummy = torch.rand(1, 3, 64, 64)
    with _PadFix():
        torch.onnx.export(
            core, dummy, core_path, opset_version=OPSET,
            input_names=['core_in'], output_names=['core_out'],
            dynamic_axes={'core_in': {0: 'b', 2: 'ph', 3: 'pw'},
                          'core_out': {0: 'b', 2: 'oh', 3: 'ow'}},
            dynamo=False, do_constant_folding=True,
        )
    m = onnx.load(core_path)
    onnx.checker.check_model(m)
    # 负 padding 必须已被改写成 Slice（否则 DirectML 会算成全 0）
    bad = [n.name for n in m.graph.node if n.op_type == 'Pad']
    if bad:
        raise SystemExit(f'导出后仍存在 Pad 节点（应已改写为 Slice）：{bad}')
    # 确认尺寸关系：P=64 -> 4*64-152 = 104
    import onnxruntime as ort
    s = ort.InferenceSession(core_path, providers=['CPUExecutionProvider'])
    out = s.run(None, {'core_in': np.zeros((1, 3, 64, 80), np.float32)})[0]
    assert out.shape == (1, 3, 104, 168), out.shape
    return m


def assemble(core_model, out_path):
    """把 core 与动态 parity pad / crop / residual / clamp 拼成最终模型。"""
    g = core_model.graph
    core_nodes = []
    rename = {'core_in': 'padded', 'core_out': 'raw'}
    for node in g.node:
        for i, name in enumerate(node.input):
            if name in rename:
                node.input[i] = rename[name]
        for i, name in enumerate(node.output):
            if name in rename:
                node.output[i] = rename[name]
        core_nodes.append(node)

    inits = list(g.initializer)
    init_names = {t.name for t in inits}

    def add_init(name, arr):
        if name in init_names:
            return
        inits.append(numpy_helper.from_array(arr, name))
        init_names.add(name)

    i64 = np.int64
    add_init('c_idx_h', np.array([2], dtype=i64))
    add_init('c_idx_w', np.array([3], dtype=i64))
    add_init('c_two', np.array([2], dtype=i64))
    add_init('c_zero_scalar', np.array([0], dtype=i64))
    add_init('c_nineteen', np.array([19], dtype=i64))
    # begin pads: [0,0,19,19]
    add_init('c_begin', np.array([0, 0, 19, 19], dtype=i64))
    # end 基础（H/W 的 19 由 19+parity 计算，N/C 为 0）
    add_init('c_end_nc', np.array([0, 0], dtype=i64))
    add_init('c_slice_starts', np.array([0, 0, 0, 0], dtype=i64))
    add_init('c_big', np.array([2 ** 62, 2 ** 62], dtype=i64))
    add_init('c_four', np.array([4], dtype=i64))
    add_init('c_zero4', np.array([0], dtype=i64))
    # Resize scales: [1,1,4,4]
    add_init('c_scales', np.array([1.0, 1.0, 4.0, 4.0], dtype=np.float32))
    add_init('c_roi', np.zeros((0,), dtype=np.float32))

    pre = [
        helper.make_node('Shape', ['input'], ['in_shape']),
        helper.make_node('Gather', ['in_shape', 'c_idx_h'], ['h0'], axis=0),
        helper.make_node('Gather', ['in_shape', 'c_idx_w'], ['w0'], axis=0),
        helper.make_node('Mod', ['h0', 'c_two'], ['hp'], fmod=0),
        helper.make_node('Mod', ['w0', 'c_two'], ['wp'], fmod=0),
        helper.make_node('Add', ['c_nineteen', 'hp'], ['e_h']),
        helper.make_node('Add', ['c_nineteen', 'wp'], ['e_w']),
        # end = [0,0,19+hp,19+wp]
        helper.make_node('Concat', ['c_end_nc', 'e_h', 'e_w'], ['end_pad'], axis=0),
        helper.make_node('Concat', ['c_begin', 'end_pad'], ['pads'], axis=0),
        helper.make_node('Pad', ['input', 'pads'], ['padded'], mode='reflect'),
    ]
    post = [
        # residual: Resize(input, roi, scales) 4x nearest
        helper.make_node('Resize', ['input', 'c_roi', 'c_scales'], ['base4'],
                         mode='nearest', coordinate_transformation_mode='asymmetric',
                         nearest_mode='floor'),
        # crop raw -> [4h, 4w]
        helper.make_node('Gather', ['in_shape', 'c_idx_h'], ['ch']),
        helper.make_node('Gather', ['in_shape', 'c_idx_w'], ['cw']),
        helper.make_node('Mul', ['ch', 'c_four'], ['h4']),
        helper.make_node('Mul', ['cw', 'c_four'], ['w4']),
        helper.make_node('Concat', ['c_big', 'h4', 'w4'], ['slice_ends'], axis=0),
        helper.make_node('Slice', ['raw', 'c_slice_starts', 'slice_ends'], ['cropped']),
        helper.make_node('Add', ['cropped', 'base4'], ['sum']),
        helper.make_node('Clip', ['sum', 'c_zero_f', 'c_one_f'], ['output']),
    ]
    add_init('c_zero_f', np.array(0.0, dtype=np.float32))
    add_init('c_one_f', np.array(1.0, dtype=np.float32))

    inp = helper.make_tensor_value_info('input', TensorProto.FLOAT, ['batch', 3, 'height', 'width'])
    out = helper.make_tensor_value_info('output', TensorProto.FLOAT, ['batch', 3, 'height_x4', 'width_x4'])
    graph = helper.make_graph(pre + core_nodes + post, 'cugan4x_parity_safe', [inp], [out], inits)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid('', OPSET)])
    model.ir_version = 8
    model.producer_name = 'Lumen model-pipeline (CUGAN export)'
    onnx.checker.check_model(model)
    onnx.save(model, out_path)
    return model


def official_reference(base, x):
    """官方 tile_mode=0 路径（非 pro）：返回 float [0,1] NCHW（uint8/255）。"""
    with torch.no_grad():
        y = base(x, 0, 0, 1, False)
    return y.float() / 255.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--variant', required=True, choices=list(VARIANTS))
    ap.add_argument('--out-dir', required=True)
    args = ap.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    base, is_pro, pth = load_base(args.variant)
    n_params = sum(p.numel() for p in base.parameters())
    print(f'[cugan-{args.variant}] params={n_params:,} pro={is_pro} pth={os.path.basename(pth)}')
    assert not is_pro, '4x 官方权重没有 pro 版本'

    core_path = os.path.join(args.out_dir, '_core.onnx')
    core_model = build_core_onnx(base, core_path)
    model = assemble(core_model, os.path.join(args.out_dir, 'model.onnx'))
    os.remove(core_path)
    fp32_path = os.path.join(args.out_dir, 'model.onnx')
    print(f'[cugan-{args.variant}] fp32 onnx {os.path.getsize(fp32_path):,} bytes')

    # ---- 数值验证：ONNX vs 官方 PyTorch（偶数 / 奇数尺寸都测） ----
    import onnxruntime as ort
    so = ort.SessionOptions()
    so.log_severity_level = 3
    sess = ort.InferenceSession(fp32_path, sess_options=so, providers=['CPUExecutionProvider'])

    def psnr255(a, b):
        mse = float(np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2))
        return 99.0 if mse == 0 else float(10 * np.log10(255.0 ** 2 / mse))

    for (h, w) in [(64, 80), (63, 79), (65, 81), (97, 128), (128, 128)]:
        x = torch.from_numpy(np.random.default_rng(h * 1000 + w).random((1, 3, h, w), dtype=np.float32))
        ref = official_reference(base, x)[0].numpy()          # [0,1]
        got = sess.run(None, {'input': x.numpy()})[0][0]      # [0,1]
        ref_u8 = np.clip(np.round(ref * 255.0), 0, 255)
        got_u8 = np.clip(np.round(got * 255.0), 0, 255)
        p = psnr255(ref_u8, got_u8)
        md = float(np.max(np.abs(ref_u8 - got_u8)))
        print(f'  in {h}x{w}: shape={got.shape} PSNR={p:.2f} dB maxdiff={md:.0f}/255')
        assert got.shape == (3, h * 4, w * 4), got.shape
        assert p > 40, f'数值验证失败 {h}x{w}'

    # ---- fp16 ----
    from onnxconverter_common import float16 as f16
    fp16_path = os.path.join(args.out_dir, 'model.fp16.onnx')
    m = onnx.load(fp32_path)
    m16 = f16.convert_float_to_float16(m, keep_io_types=False)
    onnx.save(m16, fp16_path)
    print(f'[cugan-{args.variant}] fp16 onnx {os.path.getsize(fp16_path):,} bytes')
    s16 = ort.InferenceSession(fp16_path, providers=['CPUExecutionProvider'])
    x = np.random.default_rng(0).random((1, 3, 64, 80), dtype=np.float32)
    a = sess.run(None, {'input': x})[0]
    b = s16.run(None, {'input': x.astype(np.float16)})[0]
    mse = float(np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2))
    print(f'[cugan-{args.variant}] fp16-vs-fp32 PSNR={99 if mse == 0 else 10 * np.log10(1 / mse):.2f} dB '
          f'maxdiff={np.max(np.abs(a - b)) * 255:.2f}/255')


if __name__ == '__main__':
    main()

