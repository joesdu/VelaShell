# 内置位图字体

这几份 BDF 取自 X.Org 的 [`font-misc-misc`](https://gitlab.freedesktop.org/xorg/font/misc-misc)
(Markus Kuhn 维护的 ucs-fonts),版权声明原文为 **"Public domain font. Share and enjoy."**
—— 以**数据文件**随库分发,不是代码,不涉及净室规程(见 `../../AGENTS.md`)。

| 文件 | 原字体(XLFD) |
| --- | --- |
| `6x13.bdf` | `-Misc-Fixed-Medium-R-SemiCondensed--13-120-75-75-C-60-ISO10646-1`(`fixed` 的本体) |
| `6x13B.bdf` | 同上的 Bold |
| `9x15.bdf` / `9x15B.bdf` | `-Misc-Fixed-Medium/Bold-R-Normal--15-140-75-75-C-90-ISO10646-1` |
| `10x20.bdf` | `-Misc-Fixed-Medium-R-Normal--20-200-75-75-C-100-ISO10646-1` |

**裁剪过**:原文件各有数千个字形(0.5–1 MB),这里只保留 U+0000–024F(拉丁及扩展)、
U+2000–206F(通用标点)、U+20AC(欧元)、U+2190–21FF(箭头)、U+2500–25FF(制表符与方块)。
裁剪脚本只删字形、改 `CHARS` 计数,字形本身逐字节未动。要更多字符时从上游重新裁剪,不要手改。
