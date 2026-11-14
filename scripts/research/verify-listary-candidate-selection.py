"""核对候选有界选择指令的分区、身份保留和两种比较模式。"""

import argparse
import collections
import gzip
import hashlib
import json
import struct
from pathlib import Path


def verify(data):
    if data["binarySha256"] != "f728593f86597b95d9ee79c03f8e7d313ca4c79a4bab8cf31f3768febeb8063f":
        raise ValueError("目标身份不符")
    coverage = {(n, k, mode, seed) for n in (1, 2, 3, 8, 16, 17, 51, 64, 127, 256)
                for k in {0, min(1, n-1), n//2, n-1, min(50, n-1)}
                for mode in (0, 1, 2) for seed in range(3)}
    cases = data["cases"]
    if len(cases) != len(coverage) or {(c["count"], c["limit"], c["mode"], c["seed"]) for c in cases} != coverage:
        raise ValueError("组合覆盖不完整")
    entries = data["executedFunctionEntries"]
    allowed = {"141fb7f20", "141f8d210", "141fb6fa0", "141fb92b0", "141fbe120", "141fb6db0"}
    if entries.get("141fb7f20") != len(cases) or not set(entries).issubset(allowed) or \
            any(not isinstance(count, int) or count <= 0 for count in entries.values()):
        raise ValueError("原始函数执行计数不符")
    for case in cases:
        before = bytes.fromhex(case["inputHex"])
        after = bytes.fromhex(case["outputHex"])
        if len(before) != case["count"]*24 or len(before) != len(after) or not case["guardsUnchanged"]:
            raise ValueError("记录长度或边界不符")
        source = list(struct.iter_unpack("<IHHQiI", before))
        result = list(struct.iter_unpack("<IHHQiI", after))
        keys = (0, 1, 0x108030390001e240, 0xda8030390001e240, (1 << 63)-1, 1 << 63, (1 << 64)-1)
        scores = (0, 1, 2, 12345, (1 << 31)-1, -(1 << 31), -1)
        seed = case["seed"]
        expected_input = [(i+1, (i*7+seed) % 4, 0, keys[(i+seed) % 7], scores[(i*3+seed) % 7], 0x51a71e)
                          for i in range(case["count"])]
        if collections.Counter(source) != collections.Counter(expected_input):
            raise AssertionError("原始输入被改写或缺失")
        if collections.Counter(source) != collections.Counter(result):
            raise AssertionError("选择算法改变了候选身份或字段")
        key = lambda r: (-r[4], r[1], r[0]) if case["mode"] == 2 else (-r[3], r[1], r[0])
        expected = sorted(source, key=key)
        k = case["limit"]
        # 原函数只保证 nth 分区，不保证前 k 项内部有序。
        if sorted(result[:k], key=key) != expected[:k] or result[k] != expected[k] or \
                sorted(result[k+1:], key=key) != expected[k+1:]:
            raise AssertionError({field: case[field] for field in ("count", "limit", "mode", "seed")})
    return dict(cases=len(cases), allEqual=True, identityPermutationPreserved=True, prefixPartitionEqual=True,
                keyModes=[0, 1], signedScoreMode=2, externalHelperReplacements=0,
                fullQueryExecutionVerified=False, invalidIndexPanicVerified=False,
                executedFunctionEntries=data["executedFunctionEntries"])


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
    print(f"候选选择对照通过：{summary['cases']} 组，前 k 项集合与独立完整排序一致")


if __name__ == "__main__":
    main()
