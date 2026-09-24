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
| `Fonts/Data/*.bdf` | X.Org [`font-misc-misc`](https://gitlab.freedesktop.org/xorg/font/misc-misc)(6x13、6x13B、9x15、9x15B、10x20;按字符范围裁剪,字形未改) | 公有领域("Public domain font. Share and enjoy.") | 核心字体 `fixed` 等 |

## 第三方组件(Third-Party Components)

**无。** 本库没有任何运行时依赖。

> 新增依赖前请先确认其许可与 MIT 兼容(**不接受 GPL / LGPL / AGPL**),并同步更新本表。
