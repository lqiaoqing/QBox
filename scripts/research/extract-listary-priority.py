"""只读提取固定版本的默认表；地址与字段不适用于其他二进制。"""

import argparse
import hashlib
import json
import struct
from pathlib import Path

import pefile


EXPECTED_SHA256 = "f728593f86597b95d9ee79c03f8e7d313ca4c79a4bab8cf31f3768febeb8063f"


def extract(binary):
    data = binary.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != EXPECTED_SHA256:
        raise ValueError("二进制哈希不符，不能套用固定地址")
    pe = pefile.PE(data=data, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase

    def read(address, length):
        if not (base <= address < address + length <= base + pe.OPTIONAL_HEADER.SizeOfImage):
            raise ValueError("读取范围超出映像")
        result = pe.get_data(address - base, length)
        if len(result) != length:
            raise ValueError("读取不完整")
        return result

    def text(address, length):
        if not 0 < length < 256:
            raise ValueError("字符串长度异常")
        return read(address, length).decode("utf-8")

    copies = []
    for folders, names in ((0x1427C4328, 0x1427C44A8), (0x14280CCD8, 0x14280CE58)):
        paths = []
        for i in range(11):
            address = folders + i * 32
            priority, reserved, pointer, length = struct.unpack("<4Q", read(address, 32))
            if priority not in (1, 2) or reserved != 0:
                raise ValueError("路径记录布局异常")
            paths.append(dict(address=hex(address), priority=priority, path=text(pointer, length)))
        patterns = []
        for i in range(5):
            address = names + i * 40
            priority, reserved, pointer, length = struct.unpack("<4Q", read(address, 32))
            folders_flag, files_flag = read(address + 32, 2)
            if priority != 2 or reserved != 0 or folders_flag not in (0, 1) or files_flag not in (0, 1):
                raise ValueError("名称记录布局异常")
            # 0x14154a220 将此二字节复制到对象 +0x18；0x1402f7940 按两个字段名分别写出。
            patterns.append(dict(address=hex(address), priority=priority, pattern=text(pointer, length),
                                 applyToFolders=bool(folders_flag), applyToFiles=bool(files_flag)))
        copies.append(dict(folderTable=hex(folders), nameTable=hex(names), paths=paths, patterns=patterns))
    normalize = lambda rows: [{key: value for key, value in row.items() if key != "address"} for row in rows]
    for kind in ("paths", "patterns"):
        if normalize(copies[0][kind]) != normalize(copies[1][kind]):
            raise ValueError("两份默认表内容不一致")
    # 转换器按枚举跳表生成有符号字节；只核实本次实际出现的两个枚举。
    jump_table = 0x142CEEB24
    normal_jump = jump_table + struct.unpack("<i", read(jump_table + 4, 4))[0]
    uncommon_jump = jump_table + struct.unpack("<i", read(jump_table + 8, 4))[0]
    if read(normal_jump, 2) != bytes.fromhex("31c0") or read(uncommon_jump, 7) != bytes.fromhex("48c7c0ffffffff"):
        raise ValueError("优先级字节转换指令不符")
    return dict(binarySha256=digest, evidence="static PE tables and selected instruction references; not runtime observation",
                constructor="0x1402f7ed0", nameConverter="0x14154a220", nameSerializer="0x1402f7940",
                convertedPriorityBytes={"normalEnum1": 0, "uncommonEnum2": -1},
                limitations=["Does not establish path overlap precedence, ancestor name inheritance, launcher filtering or ranking-key generation."],
                copies=copies)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = extract(args.binary)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"已核对两份默认表：11 条路径、5 条名称规则；SHA-256 {result['binarySha256']}")


if __name__ == "__main__":
    main()
