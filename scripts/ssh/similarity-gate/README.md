# 相似度门禁

> [`src/VelaShell.Ssh/AGENTS.md`](../../../src/VelaShell.Ssh/AGENTS.md) §2 纪律 6 的实现。
> 它存在的理由见 [`velashell-docs/zh/ssh/design/architecture.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/ssh/design/architecture.md) §2.4。

## 它做什么

把参照实现的 C# 源码拉到临时目录，对本仓 `src/` 做 **token 级 n-gram 指纹比对**，
超阈值就让构建红灯。

```bash
# 首次：拉取比对语料（只进 .corpus/，被 .gitignore 排除）
dotnet run scripts/ssh/similarity-gate/similarity-gate.cs -- --fetch

# 比对
dotnet run scripts/ssh/similarity-gate/similarity-gate.cs
```

报告落在 `.reports/similarity.json`（同样不进仓库）。

> [!WARNING]
> **`--fetch` 不是一次性的仪式，是比对的前提。**
> `.corpus/` 被 `.gitignore` 排除，所以在一台没跑过 `--fetch` 的机器上
> 直接比对，得到的是一个**漂亮而毫无意义的 `0.00%`** —— 它的真实含义是
> 「和 0 个文件比过了」。我在这个仓库里就这么骗过自己一次。
>
> 判断标准很简单：看输出第一行的「语料: N 个文件」。**N 是 0 就什么都没比。**
> 真跑过的样子是 `502 个文件, 262,929 个 token`。

## 当前实测（2026-09-22）

```
语料      Tmds.Ssh 197 个 .cs + SSH.NET 330 个 .cs（502 文件 / 262,929 token）
本仓      107 个 .cs / 95,995 token（2026-09-22 补完代理拨号器、发送泵、X11 路由之后）
覆盖率    0.23%   (阈值 1.00%)
超限文件  0       (阈值 40 token)
自检      99.81% 覆盖、174 文件超限、退出码 1
底噪      2.41%   (SSH.NET 对 Tmds.Ssh —— 两个彼此独立的实现)
```

**最后一行是这一页最有分量的数字。** 两个互不相干的 .NET SSH 库，
光是「都在直译同一批 RFC」就能重合 2.41%；我们对同一份语料是 **0.23%**，
比那个底噪还低一个数量级。

## 为什么是 token 级

改个变量名、换个大括号风格、把注释删掉 —— 文本 diff 就面目全非，而 token 序列纹丝不动。
抄袭检测（MOSS / JPlag）用的都是这一层。

**标识符刻意保留原文**：标识符的*组合*恰恰是纪律 3 要防的东西。
归一化成 `ID0`/`ID1` 会让「整套类型体系照搬、只改名字」通过检测 —— 那等于自废武功。

字符串与数字字面量折叠成占位符：协议里的算法名字符串必然相同（见 `NOTICE.md`），
留着只会制造大量无意义的命中。

## 参数

| 参数 | 默认 | 含义 |
| --- | :-: | --- |
| `GramSize` | 25 | 连续这么多 token 完全一致才算一次命中 |
| `--max-run` | 40 | 单文件允许的最长公共 token 串 |
| `--max-ratio` | 1.0 | 全仓被覆盖 token 的百分比上限（已标定，见下） |

退出码：**0** 通过 · **1** 门禁拒绝 · **2** 语料是空的（这次比对没有意义）。

## 阈值是怎么来的

### 已验证：检测器本身有效

自检方式是拿语料当作「我们的代码」去比对它自己 —— 一个好的检测器必须当场炸掉：

```
dotnet run scripts/ssh/similarity-gate/similarity-gate.cs \
    --ours <语料目录> --corpus <语料目录>
```

**2026-09-21 实测（Tmds.Ssh 对自身，197 文件 / 125,166 token）：**

| 指标 | 结果 |
| --- | --- |
| 覆盖率 | **99.81%** |
| 超限文件 | **174 / 197** |
| 退出码 | **1**（门禁拒绝） |

也就是说：**逐文件照抄一定会被拦下。**
（99.81% 而非 100%，是因为少数文件短于 25 个 token，构不成一个 k-gram。）

### 已验证：全量代码落地后仍有余量

功能全部落地之后（本仓 `src/` 83 个文件 / 70,662 token，语料 = Tmds.Ssh 197 个 .cs
+ SSH.NET 330 个 .cs，共 502 文件 / 262,929 token）重测：

| 阈值 `--max-run` | 覆盖率 | 超限文件 |
| :-: | :-: | :-: |
| 40（默认） | **0.16%** | **0** |
| 30 | 0.16% | 1 |
| 20（收紧一半） | 0.16% | 2 |

**收紧到 20 个 token 也只有 2 个文件越线，而那 2 条命中全是 `Stream` 子类的签名**：

```
SftpFileStream.cs      33 token: public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(...
SshPacketTransport.cs  28 token: public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ...
```

方法签名是基类定死的，`override` 的那一行本来就只有一种写法。
**换句话说：把阈值砍掉一半之后，剩下的重合里没有一条是「表达」。**
这比「0.16% 在阈值内」有说服力得多 —— 后者只说明没压线，
前者说明**没有可压的东西**。

### 一个真实的误报，以及它教会我们的事

加入内存传输层之后，门禁曾报出一条 38 token 的公共串：

```csharp
public override long Length => throw new NotSupportedException();
public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
```

这是**由 .NET BCL 的 API 形状逼出来的**：`Stream` 把 `Length` / `Position` 定义成
非抽象的虚成员，不可 Seek 的子类只能 override 并抛异常，没有「不实现」这个选项。
两边相同是因为两边都继承 `Stream`。

两个结论：

1. **38 很接近默认阈值 40。** 如果不处理，某段更长的 BCL 样板迟早会造成误报，
   而误报会让人开始习惯性地忽略门禁 —— 那才是真正的危险。
2. **正确的处理不是调高阈值，也不是把整个文件加进 `allowlist.txt`**
   （那会让同一文件里真正的问题一起失去监视）。

于是有了 [`boilerplate.cs.txt`](boilerplate.cs.txt)：门禁会把其中片段的 k-gram
**从比对索引里剔除**。它是一份「语言/框架强制的写法」清单，粒度到片段而非文件。

⚠️ 往里加片段的标准**比 `allowlist.txt` 更严**：必须是「除此之外写不出别的形式」，
而不是「大家习惯这么写」。习惯可以改，语言强制的形状不行。

### 已标定：底噪是 2.41%

`--max-ratio` 一度是个拍出来的 3.0。要标定它，得先回答一个问题：
**两个互相独立的 SSH 实现之间，token 重合度本来有多高？**
它们都在直译同一批 RFC，底噪必然不是零。

`sources.json` 里登记的 Tmds.Ssh 与 SSH.NET 没有共同血缘，正好拿来互比：

```bash
dotnet run scripts/ssh/similarity-gate/similarity-gate.cs -- --fetch
dotnet run scripts/ssh/similarity-gate/similarity-gate.cs -- \
    --ours scripts/ssh/similarity-gate/.corpus/ssh-net \
    --corpus scripts/ssh/similarity-gate/.corpus/tmds-ssh \
    --max-ratio 100 --max-run 100000
```

**2026-09-21 实测：`2.41%`**（330 文件对 185 文件）。

于是阈值从 3.0 改成了 **1.0**，理由不是「更严更好」：

| 阈值位置 | 它实际在说什么 |
| --- | --- |
| 3.0（底噪之上） | 「我们和一个无关实现的相似度，没超过两个无关实现之间的相似度」—— 几乎不传递信息 |
| **1.0（底噪之下）** | **一条针对本仓的早期警报线**：现在 0.23%，余量 4 倍有余；真涨到 1% 说明有东西变了，值得在它变成 3% 之前去看 |

> 注意这条线**不是用来判定别人抄没抄的** —— 一个诚实的独立实现完全可能到 2.4%。
> 它是用来盯住**我们自己**的漂移。
>
> 另外，门禁的主力指标始终是 `--max-run`（单文件最长公共串），
> 它对底噪不敏感：两个独立实现偶然写出连续 40 个相同 token 的概率极低，
> 而照抄一个函数必然超过它。

## 两级豁免

| 机制 | 粒度 | 用于 |
| --- | --- | --- |
| [`boilerplate.cs.txt`](boilerplate.cs.txt) | **片段** | 语言 / 框架强制的写法（`Stream` 的非 Seek 样板、Dispose 模式）。其 k-gram 从索引中剔除，**同一文件的其余部分照常监视** |
| [`allowlist.txt`](allowlist.txt) | **文件** | 整个文件都只是协议事实（消息编号表、算法名清单） |

**优先用前者。** 文件级豁免会让那个文件里真正的问题一起失去监视，
而协议库里最该盯的恰恰是那些「看起来像常量表、其实混了实现逻辑」的文件。

## 白名单（文件级）

[`allowlist.txt`](allowlist.txt) 列的是**必然与他人实现相同、且相同不构成侵权**的文件
—— 协议规定的事实，表达方式唯一。

⚠️ **加一行进来，就等于宣称「这个文件里没有表达性内容，只有协议事实」。**
每一行都必须写明理由。拿不准时不要加 —— 重写那一段的成本，
远低于把一个真问题藏进白名单的成本。

## 参考过某份代码之后，要比的是**那一份**

语料是 `sources.json` 里那几个仓库的**默认分支**。所以：

> **参考过一个未合并的 PR、一个 fork、一段博客里的代码之后，
> 跑常规门禁通过 —— 那什么都没证明。** 那些东西不在语料里，检测器看不到。

真要验，就临时拿那份东西建一个语料直接比：

```bash
# 例：把某个 PR 里新增的 .cs 抽到一个临时目录，再比
dotnet run scripts/ssh/similarity-gate/similarity-gate.cs -- --corpus <那个临时目录>
```

**2026-09-21 的一次实战**（压缩改走原生 zlib，参考了本项目作者自己提给上游的
[tmds/Tmds.Ssh#513](https://github.com/tmds/Tmds.Ssh/pull/513)）：

```
                     覆盖率    最长公共串（阈值 40）
第一版实现            0.44%    101 token   ← 超限
Stream 适配器重写后    0.27%     36 token   ← 通过
```

那 101 token 全是 `Stream` 子类的样板，而且写在参考之前的
`LinkCharacteristics.cs`（39 token）也同样命中 —— **内容确实没有表达性**。
但既然是刚看完才写的，就没法声称成员顺序没受影响，
所以按本仓风格重写了一遍，而不是加白名单。
**判据很朴素：重写之后它应当不比「参考之前就写好的文件」更像。**

临时语料同样**绝不进仓库**，用完即弃。

## 门禁红了怎么办


**先别急着加白名单。** 逐条看报告里的片段，分三种情况：

| 情况 | 处理 |
| --- | --- |
| 确实是协议规定的唯一表达（消息号表、算法名清单） | 加进 `allowlist.txt` 并写明理由 |
| 两边都在直译同一段 RFC 的算法描述 | 检查我们的写法是不是**真的**只有这一种；通常不是 —— 重写 |
| 其它 | 重写那一段。**这正是门禁要抓的东西** |

前两种要分清楚该用哪个工具 —— 它们的**作用域不一样**：

- `allowlist.txt` **放行整个文件**。所以只给纯码表用，而且要**先拆到最小**：
  `SftpProtocolCodes.cs` 就是从 `SftpConstants.cs` 拆出来的，
  为的是让后者里我们自己选的常量（块大小、上限、默认权限）继续受监视。
- `boilerplate.cs.txt` **只剔除那几个 k-gram**。同一个文件里真出了问题照样报。
  所以能用它的就别动白名单。

注意 `boilerplate.cs.txt` 里的片段要**连写法一起对上**：
带 `ValidateBufferArguments` 的大括号版本和表达式体版本是两串不同的 token，
各写各的 —— 少写一种，门禁就会在那一种上报误报。
