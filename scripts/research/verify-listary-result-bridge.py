"""复核引擎记录、中间接口、来源行及文件项标量字段的指令输出。"""

import argparse
import gzip
import hashlib
import json
from pathlib import Path


def verify(data):
    if data["binarySha256"] != "f728593f86597b95d9ee79c03f8e7d313ca4c79a4bab8cf31f3768febeb8063f":
        raise ValueError("目标身份不符")
    cases = data["cases"]
    if len(cases) != 128 or {(r["keyIndex"], r["tailIndex"], r["directory"]) for r in cases} != \
            {(k, t, d) for k in range(8) for t in range(8) for d in range(2)}:
        raise ValueError("字段组合覆盖不完整")
    keys = (0, 1, 0x108030390001e240, 0xda8030390001e240, (1 << 63)-1, 1 << 63, (1 << 64)-2, (1 << 64)-1)
    tails = (0, 1, 0xffffffff, 0x100000000, ((1 << 64)-1)//1000,
             ((1 << 64)-1)//1000+1, 1 << 63, (1 << 64)-1)
    scores = (0, 1, 2, 12345, 0xffffff, 0x7fffffff, 0x80000000, 0xffffffff)
    for case in cases:
        k, t = case["keyIndex"], case["tailIndex"]
        source, wire, row, item = (bytes.fromhex(case[field]) for field in
                                  ("nativeHex", "wireHex", "rowHex", "itemProjectionHex"))
        if (len(source), len(wire), len(row), len(item)) != (0x78, 0x70, 0x58, 0xb8):
            raise ValueError("记录长度不符")
        if int.from_bytes(source[0x58:0x60], "little") != tails[t] or \
                int.from_bytes(source[0x60:0x68], "little") != keys[k] or \
                int.from_bytes(source[0x6c:0x70], "little") != scores[k] or \
                source[0x70:0x73] != bytes((k & 1, (k >> 1) & 1, (k >> 2) & 1)):
            raise AssertionError("输入边界不符")
        expected_wire = bytearray(0x70)
        expected_wire[:0x58] = source[:0x58]
        expected_wire[0x58:0x60] = min(tails[t]*1000, (1 << 64)-1).to_bytes(8, "little")
        expected_wire[0x60:0x68] = source[0x60:0x68]
        expected_wire[0x68:0x6c] = source[0x6c:0x70]
        expected_wire[0x6c:0x6f] = bytes((source[0x70], source[0x72] ^ 1, source[0x71]))
        expected_row = bytes(expected_wire[:0x40] + expected_wire[0x58:0x6c] +
                             bytes((source[0x70], case["directory"], source[0x71], source[0x72] ^ 1)))
        # 文件项只核对选定区间写入的投影，不补写区间外的其他字段。
        expected_item = bytearray(0xb8)
        expected_item[:0x10] = expected_row[:0x10]
        expected_item[0x98:0xac] = expected_row[0x40:0x54]
        expected_item[0xae:0xb1] = expected_row[0x55:0x58]
        if wire != expected_wire or row != expected_row or item != expected_item or \
                not case["otherRowsUnchanged"] or case["destinationSlot"] != (k+t) % 4:
            raise AssertionError({"keyIndex": k, "tailIndex": t, "directory": case["directory"]})
    return dict(cases=128, allEqual=True, fullWireConverter=True, selectedRowAndItemScalarBlocks=True,
                externalHelperReplacements=0, cloneOwnershipVerified=False, fullLauncherVerified=False)


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
    print("结果字段对照通过：128 组输入，排序键经过三层适配保持不变")


if __name__ == "__main__":
    main()
