# 内置位图字体

X 服务端的核心字体(OpenFont / ListFonts / 字形光标)用的数据。全部是上游的**原样字节** —— 以**数据文件**随库分发,
不是代码,不涉及净室规程(见 `../../AGENTS.md` §2 纪律 4)。由 [`scripts/xserver/fonts/build-fonts.cs`](../../../../scripts/xserver/fonts/build-fonts.cs)
从固定的上游提交 / 版本生成,**不要手改**。下载的内容按脚本里固定的 SHA-256 核对(Unifont 核对发布的 `.bdf.gz`;X.Org 的仓库核对从归档里
挑出来的文件 —— GitLab 现做的归档不保证逐字节稳定),全部对上才动这个目录。要换版本或加字体,改脚本里的固定值再重跑(对不上时会打印实际的摘要):

```bash
dotnet run scripts/xserver/fonts/build-fonts.cs      # 在仓库根目录跑
```

## 布局

照 X.Org 安装后的字体目录分三个目录,先后就是字体路径的先后(同名字体前面的目录优先):

| 目录 | 内容 |
| --- | --- |
| `misc/` | misc-fixed 全套(4x6 … 10x20、粗体 / 斜体、`12x13ja` / `18x18ja` / `18x18ko`、`k14`)、`nil2`、`cursor`、GNU Unifont;X.Org 的 `fonts.alias` |
| `75dpi/` | Adobe 的 Courier、Helvetica、New Century Schoolbook、Symbol、Times,8 / 10 / 12 / 14 / 18 / 24 磅,75 dpi |
| `100dpi/` | 同上,100 dpi |

- 每份 BDF 逐字节未动,只用 Brotli 压缩成 `<原文件名>.br`(整套约 5 MB,原样约 52 MB)。服务端第一次打开某个字体时才解压、解析。
- 每个目录一份 mkfontdir 格式的 `fonts.dir`:第一行是条数,之后每行「文件名 XLFD」(小写)。ISO10646-1 的字体还以它**完整覆盖**
  (ASCII 可见字符齐全、那个字符集定义了的可见字符一个不缺)的单字节字符集的名字出现,与 `mkfontdir -e` 一样 ——
  ISO8859-1 是 Unicode 的前 256 个码位,其余按 `charsets.txt`。
- `charsets.txt`:ISO8859-2 / 3 / 4 / 5 / 6 / 7 / 8 / 9 / 13 / 15、KOI8-R / KOI8-U 的 0x80–0xFF → Unicode,由 .NET 的代码页表导出
  (.NET 没有 ISO8859-10 / 11 / 14 / 16,这几个字符集的名字不提供)。
- `misc/fonts.alias`:X.Org [`font-alias`](https://gitlab.freedesktop.org/xorg/font/alias) 的 `misc/fonts.alias` 原文。目标不在这里的别名
  (Sony、JIS、ISAS、OPEN LOOK 的字体)服务端不列出;8x16 / 12x24 / 9x18 / 9x18bold 由 `FontCatalog` 另外补上。

## 来源与许可

| 数据 | 上游 | 许可 |
| --- | --- | --- |
| `misc/` 的 misc-fixed、`nil2` | X.Org [`font-misc-misc`](https://gitlab.freedesktop.org/xorg/font/misc-misc) @ `c4e2af05`(Markus Kuhn 维护的 ucs-fonts 等) | 公有领域:"Public domain font. Share and enjoy." |
| `misc/cursor.bdf.br` | X.Org [`font-cursor-misc`](https://gitlab.freedesktop.org/xorg/font/cursor-misc) @ `73b20953` | "These glyphs are unencumbered" |
| `75dpi/` | X.Org [`font-adobe-75dpi`](https://gitlab.freedesktop.org/xorg/font/adobe-75dpi) @ `6189f2a6` | Adobe / DEC 的许可声明(允许使用、复制、修改、分发与出售,须保留版权与许可声明;原文在每份 BDF 的 COMMENT 里) |
| `100dpi/` | X.Org [`font-adobe-100dpi`](https://gitlab.freedesktop.org/xorg/font/adobe-100dpi) @ `13f867e9` | 同上 |
| `misc/unifont.bdf.br` | [GNU Unifont](https://unifoundry.com/unifont/) 18.0.01 的 `unifont-18.0.01.bdf.gz`(SHA-256 固定在脚本里) | 双许可(SIL OFL 1.1 / GPLv2+ 带字体嵌入例外),**本库按 SIL OFL 1.1 使用** |
| `misc/fonts.alias` | X.Org [`font-alias`](https://gitlab.freedesktop.org/xorg/font/alias) @ `ebeee85f` | Cronyx 的许可声明(允许使用、修改、复制、分发与出售,须保留版权与条款) |

许可原文在 `LICENSE-xorg-fonts.txt`(各上游仓库的 COPYING)与 `LICENSE-unifont.txt`(Unifont 的版权行与 OFL 1.1 全文),
两份都随数据一起嵌进程序集 —— OFL 要求字体的每一份拷贝都带着许可。

**没有带的**:B&H 的 Lucida(`font-bh-*`,许可要求在用户文档与代码注释里附带特定声明)、Bitstream / Utopia 等其余 X.Org 字体、
可缩放字体。请求这些字族照旧 BadName;同一字族里没有的字号退到最接近的。
