# VelaShell.Terminal.Tests

> [`VelaShell.Terminal`](../../src/VelaShell.Terminal) VT 引擎与渲染层的单元测试。

以白盒方式（引擎经 `InternalsVisibleTo` 暴露 `internal`）逐条对照 VT/Xterm 规范验证仿真正确性，是全项目最密集的测试集之一。

## 覆盖范围

- **仿真核心** — `Emulation/TerminalEmulatorTests`、`AltScreenCursorTests`（主/备屏与光标）、`ResizePreservationTests`（缩放保留）、`CatOutputNewlineTests`（换行语义）。
- **缓冲与滚动** — `ScrollbackBufferTests`、`ScrollOffsetTests`、`BufferSearchTests`。
- **输入编码** — `MouseEncoderTests`、`Input/TerminalInputTrackerTests`、`EchoSuppressorTests`（本地回显抑制）。
- **侧栏渲染** — `GutterLayoutTests`、`GutterFoldTests`、`GutterFoldUiTests`、`LineTimestampTests`。
- **桥接与语义** — `TerminalBridgeTests`（SSH ↔ 引擎）、`SemanticMatcherTests`。

## 运行

```bash
dotnet test tests/VelaShell.Terminal.Tests/
```
