# NOTICE

VelaShell.XServer
Copyright 2026 VelaShell Labs

This product includes software developed at VelaShell Labs.
Licensed under the MIT License — see [`LICENSE`](LICENSE).

---

## 关于独立性(Independence Statement)

**VelaShell.XServer 是一个独立实现的 X11 服务端。**

它不是任何现有 X 服务端的 fork,也不包含来自其它 X 服务端的代码。实现依据是 X.Org 发布的
*X Window System Protocol, X Version 11* 及各扩展的协议规范、ICCCM 与 EWMH —— 每个协议实现文件的
头部都注明了它所实现的规范与章节;架构与决策记录在
[`velashell-docs/zh/xserver/design/architecture.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/xserver/design/architecture.md)。

GLX 与间接渲染用的软件 GL(`Gl/`)同样是独立实现,不包含来自任何 OpenGL / GLX 实现(Mesa 等)的代码:
依据是 Khronos 发布的 *OpenGL Graphics with the X Window System* 1.4、*GLX Extensions for OpenGL Protocol
Specification* 1.3、*The OpenGL Graphics System* 1.5,操作码与枚举值取自 Khronos 注册表的 `gl.xml` / `glx.xml`。

协议常量(操作码、事件码、错误码、掩码位、预定义原子、线上布局)在所有正确的实现里**必然相同** ——
它们是协议规定的事实,表达方式唯一。

---

## 随库分发的数据(Bundled Data)

| 数据 | 来源 | 许可 | 用途 |
| --- | --- | --- | --- |
| `Fonts/Data/misc/`(misc-fixed、`nil2`) | X.Org [`font-misc-misc`](https://gitlab.freedesktop.org/xorg/font/misc-misc)(全套,字形未改) | 公有领域("Public domain font. Share and enjoy.") | 核心字体 `fixed`、`5x7` … `10x20`、中日韩的 `12x13ja` / `18x18ja` / `18x18ko` / `k14` |
| `Fonts/Data/misc/cursor.bdf.br` | X.Org [`font-cursor-misc`](https://gitlab.freedesktop.org/xorg/font/cursor-misc) | "These glyphs are unencumbered" | cursor 字体(字形光标的位图) |
| `Fonts/Data/75dpi/`、`Fonts/Data/100dpi/` | X.Org [`font-adobe-75dpi`](https://gitlab.freedesktop.org/xorg/font/adobe-75dpi) / [`font-adobe-100dpi`](https://gitlab.freedesktop.org/xorg/font/adobe-100dpi)(字形未改) | Copyright 1984-1989, 1994 Adobe Systems Incorporated;Copyright 1988, 1994 Digital Equipment Corporation —— 允许使用、复制、修改、分发与出售,须保留版权与许可声明(原文在每份 BDF 的 COMMENT 与 `Fonts/Data/LICENSE-xorg-fonts.txt`) | Courier、Helvetica、New Century Schoolbook、Symbol、Times |
| `Fonts/Data/misc/unifont.bdf.br` | [GNU Unifont](https://unifoundry.com/unifont/) 18.0.01(字形未改) | 双许可,**本库按 SIL Open Font License 1.1 使用**(版权行与许可全文在 `Fonts/Data/LICENSE-unifont.txt`,随字体嵌进程序集);字体数据本身按 OFL 分发,不随本库的 MIT | 覆盖整个基本多文种平面(中日韩等)的兜底字体 |
| `Fonts/Data/misc/fonts.alias` | X.Org [`font-alias`](https://gitlab.freedesktop.org/xorg/font/alias) 的 `misc/fonts.alias`(原样) | Copyright (C) 1994-95 Cronyx Ltd. —— 允许使用、修改、复制、分发与出售,须保留版权与这些条款(原文在 `Fonts/Data/LICENSE-xorg-fonts.txt`) | 字体别名 |

字体数据由 `scripts/xserver/fonts/build-fonts.cs` 从固定的上游提交 / 版本生成(BDF 原样、Brotli 压缩),见 `Fonts/Data/README.md`。
| `Resources/Data/rgb.txt` | X.Org [`rgb`](https://gitlab.freedesktop.org/xorg/app/rgb) 的 `rgb.txt`(原样) | MIT / X11(Copyright 1985, 1989, 1998 The Open Group;Copyright (c) 1994, 2008, Oracle and/or its affiliates —— 许可原文见上游 `COPYING`) | 颜色名(AllocNamedColor、LookupColor) |

## 第三方组件(Third-Party Components)

**无。** 本库没有任何运行时依赖。

> 新增依赖前请先确认其许可与 MIT 兼容(**不接受 GPL / LGPL / AGPL**),并同步更新本表。
