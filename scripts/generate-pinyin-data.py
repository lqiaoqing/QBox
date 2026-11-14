import hashlib
import json
import re
import unicodedata
from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = root / 'third_party/pinyin-data/pinyin.txt'
commit = 'fe29ad32bdf352b31a02b9936e537f575432e9a6'
digest = hashlib.sha256(source.read_bytes()).hexdigest()
if digest != '2342a6f98c85dbc3c8d26aef31744876a36326c6466e51e9a0f16898c36c2011':
    raise RuntimeError('读音数据与固定提交不一致，停止生成')
rows = []
for line in source.read_text(encoding='utf-8').splitlines():
    match = re.match(r'U\+([A-F0-9]+):\s*([^#]+)', line)
    if not match:
        continue
    readings = []
    for value in match[2].strip().split(','):
        # ü 系列先保留为键盘常用的 v，再移除声调组合符，避免与普通 u 混淆。
        value = re.sub('[üǖǘǚǜ]', 'v', value.lower())
        value = ''.join(char for char in unicodedata.normalize('NFD', value) if unicodedata.category(char) != 'Mn')
        if re.fullmatch('[a-z]+', value) and value not in readings:
            readings.append(value)
    if readings:
        rows.append(f'{match[1]}\t{",".join(readings)}')
target = root / 'src/QBox.Core/Matching/Data/pinyin.tsv'
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(f'# pinyin-data {commit}\n# MIT; see third_party/pinyin-data/LICENSE\n' + '\n'.join(rows) + '\n', encoding='utf-8')
metadata = {'repository': 'https://github.com/mozillazg/pinyin-data', 'commit': commit,
    'license': 'MIT', 'inputSha256': digest, 'outputSha256': hashlib.sha256(target.read_bytes()).hexdigest(), 'characters': len(rows)}
(source.parent/'manifest.json').write_text(json.dumps(metadata, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
print(json.dumps(metadata, ensure_ascii=False))
