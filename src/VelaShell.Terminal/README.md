# VelaShell.Terminal

> 自研 VT 终端引擎 + Avalonia 自绘渲染控件。

`VelaShell.Terminal` 是 VelaShell 的心脏：一个从零实现的、不依赖任何第三方终端控件的终端仿真器与渲染层。它将远端字节流解析为屏幕状态，再由自定义 Avalonia `Control` 直接绘制字形、选区、滚动与光标。

## 🧬 架构分层

```text
远端字节流 ──► VtParser ──► TerminalEmulator ──► TerminalScreen (主/备屏 + 滚动区)
                (状态机)      (IVtActions)          │
                                                   ├─ ScrollbackBuffer  (回滚缓冲)
                                                   └─ VelaTerminalControl (Avalonia 自绘渲染)
用户输入 ──► InputEncoder / MouseEncoder ──► 编码为终端序列 ──► 远端
```

## 🗂️ 目录结构

| 路径 | 职责 |
|------|------|
| `Emulation/VtParser.cs` | DEC ANSI / VT / Xterm **状态机**：解析 CSI / OSC / DCS / ESC 转义序列，驱动 `IVtActions`。 |
| `Emulation/TerminalEmulator.cs` | 仿真核心：实现 `IVtActions`，维护光标、字符集、模式（`TerminalModes`）、主/备屏切换与滚动区。 |
| `Emulation/TerminalScreen.cs` `TerminalRow.cs` `TerminalCell.cs` | 屏幕/行/单元格数据结构，`CellFlags` 描述粗体、下划线、反显等属性。 |
| `Emulation/TerminalColor.cs` `TerminalPalette.cs` | 256 色 / 真彩色调色板与 SGR 颜色解析。 |
| `Emulation/CharWidth.cs` `Charsets.cs` | CJK 双宽字符宽度计算、DEC 线绘字符集映射。 |
| `Emulation/InputEncoder.cs` `MouseEncoder.cs` | 键盘（含应用光标键模式）与鼠标协议（X10 / SGR 等）的输入编码。 |
| `Emulation/Utf8Sink.cs` `InputEncoder` | 动态编码切换与 UTF-8 增量解码。 |
| `Rendering/VelaTerminalControl.cs` | **自绘渲染控件**（热点文件）：直接绘制字形、选区、光标、滚动条；处理输入事件与命中测试。 |
| `Rendering/GutterLayout.cs` `GutterFoldModel.cs` | 侧栏（行号 / 时间戳）布局与折叠模型。 |
| `Rendering/TerminalPaletteOverrides.cs` | 主题层对调色板的运行时覆盖。 |
| `ScrollbackBuffer.cs` `TerminalLine.cs` | 回滚历史缓冲与逻辑行。 |
| `BufferSearch.cs` `SearchMatch.cs` | 缓冲区文本搜索与匹配高亮。 |
| `Semantics/SemanticMatcher.cs` | 语义识别（如 URL / 路径检测）。 |
| `Input/TerminalInputTracker.cs` | 输入焦点与按键跟踪。 |
| `EchoSuppressor.cs` | 本地回显抑制（避免输入被双重显示）。 |
| `SshTerminalBridge.cs` | 将 Core 的 SSH Shell 抽象桥接到终端引擎的 I/O。 |
| `ITerminalEmulator.cs` | 仿真器对外接口。 |

## 🔑 核心思路

- **自绘而非复用**：不依赖已废弃或功能受限的第三方终端控件，直接用 Avalonia 底层绘图 API 渲染，完全掌控性能与视觉细节。
- **状态机驱动**：`VtParser` 严格按 DEC/Xterm 规范实现转义序列状态机，仿真逻辑（`TerminalEmulator`）与解析逻辑解耦，便于逐条对照规范测试。
- **主/备屏与滚动区**：完整支持全屏应用（vim、htop）所需的备用屏幕、滚动区域与光标保存/恢复语义。
- **正确的宽字符处理**：`CharWidth` 保证 CJK 与 Emoji 的双宽对齐，避免终端错位。

## 🔗 依赖关系

- **引用**：`VelaShell.Core`（SSH Shell 抽象）、`Avalonia`（仅渲染控件用）。
- **被引用**：`VelaShell.Presentation`、`VelaShell`（App）。
- `InternalsVisibleTo` 暴露给 [`tests/VelaShell.Terminal.Tests`](../../tests/VelaShell.Terminal.Tests)，可对 `internal` 引擎细节做白盒测试。

> 编译需 `AllowUnsafeBlocks`（渲染热路径使用指针以减少分配）。
