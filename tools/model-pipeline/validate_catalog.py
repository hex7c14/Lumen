"""按 docs/ARCHITECTURE.md §4 的契约校验 models/*/model.json。

用法（在 tools/model-pipeline 目录下）：
  ..\\venv\\Scripts\\python.exe validate_catalog.py
"""
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS_ROOT = os.path.abspath(os.path.join(HERE, '..', '..', 'models'))

REQUIRED = ['id', 'displayName', 'description', 'scale', 'arch', 'qualityTier',
            'recommendedFor', 'inputLayout', 'outputLayout', 'dynamicAxes',
            'files', 'sha256', 'sizeBytes', 'sourceUrl', 'license', 'author']
TIERS = {'fast', 'balanced', 'quality'}
RECS = {'photo', 'anime', 'general'}


def main():
    ok = True
    paths = sorted(glob.glob(os.path.join(MODELS_ROOT, '*', 'model.json')))
    if not paths:
        raise SystemExit('models/*/model.json 为空')
    for mj in paths:
        d = os.path.dirname(mj)
        with open(mj, encoding='utf-8') as f:
            j = json.load(f)
        errs = []
        missing = [k for k in REQUIRED if k not in j]
        if missing:
            errs.append(f'缺字段 {missing}')
        if j.get('id') != os.path.basename(d):
            errs.append('id != 目录名')
        if j.get('qualityTier') not in TIERS:
            errs.append(f'qualityTier 非法: {j.get("qualityTier")}')
        if not set(j.get('recommendedFor', [])) <= RECS:
            errs.append('recommendedFor 非法')
        if j.get('dynamicAxes') is not True:
            errs.append('dynamicAxes 必须为 true')
        if j.get('inputLayout') != 'NCHW' or j.get('outputLayout') != 'NCHW':
            errs.append('布局必须 NCHW')
        files = j.get('files') or {}
        fp32 = files.get('fp32')
        if not fp32 or not os.path.isfile(os.path.join(d, fp32)):
            errs.append('fp32 文件缺失')
        for fname in files.values():
            if fname and not os.path.isfile(os.path.join(d, fname)):
                errs.append(f'文件缺失 {fname}')
        if set(j.get('sha256', {})) != {v for v in files.values() if v}:
            errs.append('sha256 key 集合与 files 不一致')
        if j.get('sizeBytes') != os.path.getsize(os.path.join(d, fp32)) if fp32 else True:
            errs.append('sizeBytes 与实际 fp32 大小不一致')
        status = 'OK ' if not errs else 'FAIL'
        print(f'[{status}] {j.get("id")}  tier={j.get("qualityTier")} rec={j.get("recommendedFor")}'
              + (f'  errors={errs}' if errs else ''))
        ok = ok and not errs
    print('\n全部通过' if ok else '\n存在不合规项')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
