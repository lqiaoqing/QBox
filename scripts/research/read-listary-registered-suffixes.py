"""只读保存 HKCR 注册后缀快照，供 Listary 排序标记研究使用。"""

import argparse
import hashlib
import json
import winreg
from datetime import datetime, timezone
from pathlib import Path


def snapshot():
    names = []
    skipped = 0
    index = 0
    while True:
        try:
            name = winreg.EnumKey(winreg.HKEY_CLASSES_ROOT, index)
        except OSError as error:
            if error.winerror == 259:
                break
            raise
        index += 1
        if not name.startswith("."):
            continue
        try:
            name.encode("utf-8", errors="strict")
        except UnicodeError:
            skipped += 1
            continue
        names.append(name.lower())
    # 当前注册表全部为 ASCII；遇到其他字符时显式保留未核实的规范化边界。
    names.sort()
    return dict(capturedUtc=datetime.now(timezone.utc).isoformat(), hive="HKEY_CLASSES_ROOT",
                rule="nonempty subkey name starts with dot, valid UTF-16, lowercase",
                allAscii=all(name.isascii() for name in names), skippedInvalidUnicode=skipped,
                registeredSuffixes=names, unicodeNormalizationVerified=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("summary", type=Path)
    args = parser.parse_args()
    result = snapshot()
    args.snapshot.parent.mkdir(parents=True, exist_ok=True)
    args.summary.parent.mkdir(parents=True, exist_ok=True)
    raw = (json.dumps(result, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    args.snapshot.write_bytes(raw)
    suffixes = result["registeredSuffixes"]
    examples = (".exe", ".lnk", ".pdf", ".txt", ".zip", ".png", ".h", ".pyc", ".siproj", ".code-workspace")
    summary = {key: value for key, value in result.items() if key != "registeredSuffixes"}
    summary.update(count=len(suffixes), distinctCount=len(set(suffixes)), snapshotSha256=hashlib.sha256(raw).hexdigest(),
                   examples={name: name in suffixes for name in examples},
                   scope="Read-only registry snapshot; does not query a running Listary cache or prove full ranking equivalence")
    args.summary.write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"只读注册后缀：{len(suffixes)}，全部 ASCII：{result['allAscii']}")


if __name__ == "__main__":
    main()
