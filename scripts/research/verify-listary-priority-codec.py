"""复核设置字节、中间枚举与原生类别的两级转换。"""

import argparse
import gzip
import hashlib
import json
from pathlib import Path


def verify(data):
    if data["binarySha256"] != "f728593f86597b95d9ee79c03f8e7d313ca4c79a4bab8cf31f3768febeb8063f":
        raise ValueError("目标身份不符")
    settings, codecs = data["settingCases"], data["codecCases"]
    if len(settings) != 256 or {r["settingByte"] for r in settings} != set(range(256)):
        raise ValueError("设置字节覆盖不完整")
    expected = {1: 0, 0: 1, 255: 2, 254: 3}
    for row in settings:
        value = row["settingByte"]
        if row["accepted"] != (value in expected) or value in expected and row["wireTier"] != expected[value]:
            raise AssertionError(row)
    if len(codecs) != 40 or {(r["length"], r["seed"]) for r in codecs} != \
            {(n, seed) for n in (0, 1, 2, 3, 4, 5, 7, 8, 9, 11) for seed in range(4)}:
        raise ValueError("向量分支覆盖不完整")
    count = 0
    for row in codecs:
        if not row["unrelatedFieldsUnchanged"] or len(row["vectors"]) != 4 or \
                {vector["vector"] for vector in row["vectors"]} != set(range(4)):
            raise AssertionError(row)
        for vector in row["vectors"]:
            source = [(row["seed"] + vector["vector"] + i) % 4 for i in range(row["length"])]
            if source != vector["input"] or vector["result"] != [[4, 2, 1, 0][value] for value in source]:
                raise AssertionError(vector)
            count += len(source)
    if count != 800:
        raise ValueError("类别字段覆盖不完整")
    return dict(settingCases=256, acceptedSettings=4, rejectedSettings=252, vectorCases=40,
                convertedElements=count, allEqual=True, externalHelperReplacements=0,
                mapping={"1": 4, "0": 2, "-1": 1, "-2": 0})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("oracle", type=Path)
    parser.add_argument("summary", type=Path)
    args = parser.parse_args()
    raw = args.oracle.read_bytes()
    if args.oracle.suffix == ".gz":
        raw = gzip.decompress(raw)
    summary = verify(json.loads(raw))
    summary["oracleJsonSha256"] = hashlib.sha256(raw).hexdigest()
    args.summary.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(f"转换对照通过：256 个设置字节、40 组向量、{summary['convertedElements']} 个类别字段")


if __name__ == "__main__":
    main()
