# 对照样本来源

`ascii-baseline.json` 的 12 组与 `ascii-corpus.json` 的 673 组来自 Listary 7.1.0.47 的 `0x141ec5a50` 原始指令受控模拟。目标 SHA-256 为 `f728593f86597b95d9ee79c03f8e7d313ca4c79a4bab8cf31f3768febeb8063f`。

查询对象与 ASCII 大小写等价集合由研究脚本构造，内存辅助函数、字符计数及 MOVMSKPS 有模型。所有记录必须标记 completed=true；不能跳过失败样本。673 组中包含结构化边界、128 组开发随机样本和 128 组留出随机样本。两组种子分别为 0x51424f58 和 0x5eed2211。

这些是内部对齐分值，不是实时 Launcher 结果、完整 Unicode / 拼音等价证据或性能数据。测试程序独立读取 JSON，产品不需要原始程序或研究工具。
