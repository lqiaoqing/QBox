"""将独立公式与原始 P-code 输出逐项对照，不运行或修改 Listary。"""

import argparse
import gzip
import hashlib
import json
from pathlib import Path


def combine(left, right):
    return max(left, right) if left >= 2 and right >= 2 else min(left, right)


def merge(row):
    if row["folder"] != 255:
        return row["folder"]
    value = row["inherited"]
    for kind in ("attribute", "name"):
        if row[kind] != 255:
            value = combine(value, row[kind])
    return value


def context_level(row):
    if row["contextDisabled"]:
        return 0
    best = 0
    for target, volume in row["scopes"]:
        if volume != row["volume"]:
            continue
        current = row["parent"]
        if current == target:
            best = 10
            continue
        depth = 0
        while 0 <= current < 16:
            current = current - 1 if current else -1
            depth += 1
            if current == target:
                best = max(best, 10 - min(depth, 9))
                break
    return best


def key(row):
    level = context_level(row)
    if row["priority"] < 2:
        lane = 0
    elif row["priority"] == 2:
        lane = 2 if row["directory"] or row["suffix"] else 1
    else:
        lane = 3
    # 原函数用 32 位加法及无符号截顶；负数压力样本不能按有符号 clamp 改写。
    score = min((row["score"] + 0x800000) & 0xFFFFFFFF, 0xFFFFFF)
    return ((level == 10) << 63) | ((level != 0) << 62) | (lane << 60) | (level << 56) | \
        (score << 32) | min(int(row["tail"]), 0xFFFFFFFF)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("oracle", type=Path)
    parser.add_argument("summary", type=Path)
    args = parser.parse_args()
    raw = args.oracle.read_bytes()
    if args.oracle.suffix == ".gz":
        raw = gzip.decompress(raw)
    data = json.loads(raw)
    if data["binarySha256"] != "f728593f86597b95d9ee79c03f8e7d313ca4c79a4bab8cf31f3768febeb8063f":
        raise ValueError("目标身份不符")
    if len(data["mergeCases"]) != 500 or len(data["keyCases"]) != 780:
        raise ValueError("对照样本不完整")
    for index, row in enumerate(data["mergeCases"]):
        if merge(row) != row["result"]:
            raise AssertionError(("merge", index, row, merge(row)))
    for index, row in enumerate(data["keyCases"]):
        if key(row) != int(row["key"]):
            raise AssertionError(("key", index, row, key(row)))
    summary = dict(binarySha256=data["binarySha256"], mergeCases=500, keyCases=780, allEqual=True,
                   oracleJsonSha256=hashlib.sha256(raw).hexdigest(),
                   externalHelperReplacements=0, originalProcessExecuted=False,
                   scope="Selected computation only; not full default-rule evaluation or full Launcher equivalence")
    args.summary.parent.mkdir(parents=True, exist_ok=True)
    args.summary.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print("原始指令对照通过：500 组规则合并、780 组排序键；未替代外部辅助函数")


if __name__ == "__main__":
    main()
