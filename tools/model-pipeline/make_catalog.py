"""生成 models/<id>/model.json（含 sha256 / sizeBytes / 展示元数据）。

用法（在 tools/model-pipeline 目录下）：
  ..\\venv\\Scripts\\python.exe make_catalog.py
"""
import hashlib
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS_ROOT = os.path.abspath(os.path.join(HERE, '..', '..', 'models'))

MODELS = [
    {
        'id': 'realesrgan-x4plus',
        'displayName': '写实照片',
        'description': 'Real-ESRGAN 旗舰写实模型（23 个 RRDB 块），照片细节与质感最佳，速度较慢',
        'scale': 4,
        'arch': 'rrdb',
        'qualityTier': 'quality',
        'recommendedFor': ['photo'],
        'sourceUrl': 'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth',
        'license': 'BSD-3-Clause',
        'author': 'Xintao Wang / Real-ESRGAN',
    },
    {
        'id': '4x-ultrasharp',
        'displayName': '极致锐化',
        'description': '社区高质量 RRDB 模型，锐度与细节更强，适合照片与游戏截图；许可为非商业',
        'scale': 4,
        'arch': 'rrdb',
        'qualityTier': 'quality',
        'recommendedFor': ['photo'],
        'sourceUrl': 'https://huggingface.co/Kim2091/UltraSharp',
        'license': 'CC-BY-NC-SA-4.0',
        'author': 'Kim2091',
    },
    {
        'id': 'realesrgan-anime-6b',
        'displayName': '动漫插画',
        'description': '6 个 RRDB 块的动漫画风模型，线条干净、速度与质量均衡',
        'scale': 4,
        'arch': 'rrdb',
        'qualityTier': 'quality',
        'recommendedFor': ['anime'],
        'sourceUrl': 'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.2.4/RealESRGAN_x4plus_anime_6B.pth',
        'license': 'BSD-3-Clause',
        'author': 'Xintao Wang / Real-ESRGAN',
    },
    {
        'id': 'realesrgan-x4v3',
        'displayName': '极速通用',
        'description': 'SRVGGNetCompact 轻量模型，速度最快，适合实时预览与批量处理',
        'scale': 4,
        'arch': 'srvgg',
        'qualityTier': 'fast',
        'recommendedFor': ['general'],
        'sourceUrl': 'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesr-general-x4v3.pth',
        'license': 'BSD-3-Clause',
        'author': 'Xintao Wang / Real-ESRGAN',
    },
    {
        'id': 'realesr-cugan-4x-conservative',
        'displayName': 'CUGAN 保真',
        'description': 'Real-CUGAN 4x 保守/保真档（等价 noise=-1）：线条锐利、纹理保留好、处理痕迹少；适合干净的插画与动画截图',
        'scale': 4,
        'arch': 'cugan',
        'qualityTier': 'quality',
        'recommendedFor': ['anime'],
        'sourceUrl': 'https://github.com/bilibili/ailab/releases/download/Real-CUGAN/updated_weights.zip',
        'license': 'MIT',
        'author': 'bilibili (Real-CUGAN)',
    },
    {
        'id': 'realesr-cugan-4x-denoise',
        'displayName': 'CUGAN 去噪',
        'description': 'Real-CUGAN 4x 降噪档（官方 4x 最强去噪，noise=3）：适合噪点多/压缩痕迹重的动漫截图；干净插画可能损失细节',
        'scale': 4,
        'arch': 'cugan',
        'qualityTier': 'quality',
        'recommendedFor': ['anime'],
        'sourceUrl': 'https://github.com/bilibili/ailab/releases/download/Real-CUGAN/updated_weights.zip',
        'license': 'MIT',
        'author': 'bilibili (Real-CUGAN)',
    },
]

# 迭代 2：应用默认模型 = 最佳动漫模型（docs/ITERATION-2.md §2）
DEFAULT_ID = 'realesr-cugan-4x-conservative'


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def main():
    for meta in MODELS:
        d = os.path.join(MODELS_ROOT, meta['id'])
        fp32 = os.path.join(d, 'model.onnx')
        fp16 = os.path.join(d, 'model.fp16.onnx')
        assert os.path.isfile(fp32), f'缺少 {fp32}'
        digest = {'model.onnx': sha256(fp32)}
        files = {'fp32': 'model.onnx', 'fp16': None}
        if os.path.isfile(fp16):
            digest['model.fp16.onnx'] = sha256(fp16)
            files['fp16'] = 'model.fp16.onnx'
        out = {
            'id': meta['id'],
            'displayName': meta['displayName'],
            'description': meta['description'],
            'scale': meta['scale'],
            'arch': meta['arch'],
            'qualityTier': meta['qualityTier'],
            'recommendedFor': meta['recommendedFor'],
            'inputLayout': 'NCHW',
            'outputLayout': 'NCHW',
            'dynamicAxes': True,
            'files': files,
            'sha256': digest,
            'sizeBytes': os.path.getsize(fp32),
            'sourceUrl': meta['sourceUrl'],
            'license': meta['license'],
            'author': meta['author'],
        }
        if meta['id'] == DEFAULT_ID:
            out['isDefault'] = True
        with open(os.path.join(d, 'model.json'), 'w', encoding='utf-8') as f:
            json.dump(out, f, ensure_ascii=False, indent=2)
            f.write('\n')
        print(f"[{meta['id']}] model.json 写入完成 fp32={out['sha256']['model.onnx'][:16]}… "
              f"fp16={out['sha256'].get('model.fp16.onnx', '-')[:16]}… size={out['sizeBytes']:,}")


if __name__ == '__main__':
    main()


