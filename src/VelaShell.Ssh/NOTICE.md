# NOTICE

VelaShell.Ssh
Copyright 2026 VelaShell Labs

This product includes software developed at VelaShell Labs.
Licensed under the MIT License — see [`LICENSE`](LICENSE).

---

## 关于独立性（Independence Statement）

**VelaShell.Ssh 是一套独立实现的 SSH 客户端库。**

它不是任何现有库的 fork，也不包含来自其它 SSH 实现的代码。
实现依据是 IETF RFC 与 OpenSSH 的协议文档 —— 每个协议实现文件的头部都注明了
它所实现的规范与章节，行为规格集中在 [`velashell-docs/zh/ssh/spec/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/ssh/spec)。

设计上，我们研究过并受益于以下项目所展示的思路。**它们的源码未被复制或改写：**

| 项目 | 许可 | 我们从中受益的地方 |
| --- | --- | --- |
| [OpenSSH](https://www.openssh.com/) | BSD | 协议扩展与互操作行为的事实标准；`PROTOCOL*` 系列文档是 RFC 之外最重要的依据 |
| [Tmds.Ssh](https://github.com/tmds/Tmds.Ssh) | MIT | .NET 上 async-first SSH 客户端的先行者；它证明了全托管、AOT 友好的 SSH 栈是可行的 |
| [libssh2](https://libssh2.org/) | BSD | 报文处理的公开参考实现 |
| [golang.org/x/crypto/ssh](https://pkg.go.dev/golang.org/x/crypto/ssh) | BSD | 同上 |

### 我们怎么保证这一点

1. **规范优先**：实现依据只能是 RFC / OpenSSH `PROTOCOL*` / IETF draft，不是他人源码。
2. **两阶段隔离**：先写行为规格（纯自然语言 + 报文字段表 + 时序图，零代码片段），
   再照规格实现；两个阶段不共享上下文。

> 需要说明的是：协议常量（消息号、算法名字符串、wire 布局、错误码）在所有正确的实现里
> **必然相同** —— 它们是协议规定的事实，表达方式唯一。

---

## 第三方组件（Third-Party Components）

本库在运行时依赖以下组件。它们各自的许可证条款适用于各自的代码：

| 组件 | 许可 | 用途 |
| --- | --- | --- |
| `BouncyCastle.Cryptography` | MIT（改写版） | BCL 缺失的密码学原语：raw ChaCha20 块函数、独立 Poly1305、Ed25519、X25519、DH 标准群、ML-KEM-768、sntrup761、Argon2id（`.ppk` v3） |

**它只用来做密码学原语，没有第二种用途。** BCL 有的一律走 BCL：
AES-GCM 与 AES、SHA-2、HMAC、ECDH、ECDSA、RSA、zlib
（AES 与 SHA 走 BCL 才吃得到硬件加速；zlib 同理，走原生实现）。

**清单里没有 `bcrypt_pbkdf`** —— BouncyCastle 的 `BlowfishEngine` 只暴露
`Init` + `ProcessBlock`，拿不到密钥编排内部。加密的 OpenSSH 私钥要用它，
所以这一处是本库**唯一**自己实现的密码学代码：`src/VelaShell.Ssh/Keys/BcryptPbkdf.cs`
（Blowfish 的密钥编排 + `bcrypt_pbkdf`）。

它是一个**有记录、范围钉死**的例外，不是纪律松了：只做这一个 KDF、
不导出任何分组加密能力，初始表由 π 的十六进制位现算而不是手抄，
验证对着真 `ssh-keygen` 生成的私钥做端到端比对。
理由与做法见 `velashell-docs/zh/ssh/design/architecture.md` §11.2.18。

**除此之外，本库不实现任何密码学原语。**

这是**唯一**的运行时依赖。`Microsoft.Extensions.Logging.Abstractions` 在 net11.0 随共享框架自带，
显式引用会触发 NU1510，因此不出现在依赖清单里。

> 新增依赖前请先确认其许可与 MIT 兼容（**不接受 GPL / LGPL / AGPL**），
> 并同步更新本表与 `src/Directory.Packages.props`。
