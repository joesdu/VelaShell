# VelaShell.Controls

> 可复用控件库 + 设计 Token —— 应用的视觉基元与主题基础设施。

`VelaShell.Controls` 提供跨视图复用的自定义 Avalonia 控件，以及**设计 Token 化**的资源字典（颜色、间距、字体、图标）。它让全应用的外观从单一来源驱动，杜绝硬编码颜色，从而支持深色/浅色/系统主题与品牌定制的运行时切换。它是纯控件层，不引用任何其他 VelaShell 项目。

## 🗂️ 目录结构

| 路径 | 职责 |
|------|------|
| `Controls/LucideIcon.cs` | Lucide 图标控件：以路径数据渲染矢量图标，随主题着色。 |
| `Controls/StatusMetricChip.cs` | 状态指标胶囊控件：状态栏中展示 CPU/内存/网速等单项指标。 |
| `Themes/VelaTokens.axaml` `VelaShellTokens.axaml` | **设计 Token 定义**：颜色、间距、圆角、字体等语义化资源，主题切换的单一真源。 |
| `Themes/Icons.axaml` | 图标几何路径资源字典。 |
| `Themes/Generic.axaml` | 自定义控件的默认模板与样式（`ControlTheme`）。 |
| `DependencyInjection/ControlsServiceCollectionExtensions.cs` | 控件相关服务的 DI 注册入口。 |
| `Properties/AssemblyInfo.cs` | Avalonia 主题程序集元数据。 |

## 🔑 核心思路

- **Token 化设计**：所有视觉常量以语义命名的资源形式集中在 `Themes/*.axaml`，界面引用 Token 而非字面值，实现「改一处、变全局」与运行时换肤。
- **无硬编码颜色**：控件模板一律绑定 Token，深色/浅色主题只需替换 Token 值集合。
- **框架级复用**：控件不含业务逻辑，只关注呈现，可被任意 ViewModel/视图消费。

## 🔗 依赖关系

- **引用**：`Avalonia`、`Avalonia.Themes.Fluent`、`Microsoft.Extensions.DependencyInjection.Abstractions`。**不引用任何其他 VelaShell 项目。**
- **被引用**：`VelaShell`（App）。

> 启用 `AvaloniaUseCompiledBindingsByDefault`。测试见 [`tests/VelaShell.Controls.Tests`](../../tests/VelaShell.Controls.Tests)。
