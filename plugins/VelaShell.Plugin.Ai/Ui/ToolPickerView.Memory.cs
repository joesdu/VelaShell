using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Chat.Memory;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 「配置工具」的「记忆」页:总开关、查看 / 搜索 / 新增 / 编辑 / 删除 / 全部清除。
/// </summary>
/// <remarks>
/// <para>
/// 记忆由模型在对话里经 <c>save_memory</c> 等工具保存,也可以在这里手动添加;只在桌面面板生效(见 <see cref="MemoryStore" />)。
/// 这一页是用户看清「它记住了我什么」的地方,所以每条都写明是谁存的、什么时候改的,删除与清空都要确认。
/// </para>
/// <para>
/// 编辑就地展开在那一行(或列表顶上,新增时):标题 + 内容两个输入框,保存时同样过 <see cref="MemoryStore" /> 的校验
/// (长度、不许存机密),出错原因写在输入框下面。窗口开着时模型又存了记忆,列表当场刷新;正在编辑时只刷新左栏计数,不打断输入。
/// </para>
/// </remarks>
public sealed partial class ToolPickerView
{
    /// <summary>正在编辑的记忆 id;<see cref="NewMemoryKey" /> = 新增;null = 没在编辑。</summary>
    private string? _editingMemory;

    private const string NewMemoryKey = "+new";

    private MemoryStore Memories => MemoryStore.For(_context.DataDirectory);

    /// <summary>窗口挂上 / 拆下时订阅 / 退订记忆的变化(记忆实例是全局的,不退订会把整个窗口留在内存里)。</summary>
    private void WatchMemories()
    {
        void OnChanged() => Dispatcher.UIThread.Post(() =>
        {
            if (_editingMemory is null) Rebuild();
            else RebuildNav();
        });
        AttachedToVisualTree += (_, _) => Memories.Changed += OnChanged;
        DetachedFromVisualTree += (_, _) => Memories.Changed -= OnChanged;
    }

    private void AddMemoryActions()
    {
        Button add = HeaderButton("MemoryAdd", _loc["MemoryAdd"]);
        add.Click += (_, _) =>
        {
            _editingMemory = NewMemoryKey;
            RenderContent();
        };
        Button clear = HeaderButton("MemoryClearAll", _loc["MemoryClearAll"]);
        clear.IsEnabled = Memories.List().Count > 0;
        clear.Click += async (_, _) =>
        {
            if (!await _confirm.AskAsync(_loc["MemoryClearAllTitle"], _loc["MemoryClearAllBody"], _loc["MemoryClearAll"], _loc["Cancel"])) return;
            Memories.Clear();
            _editingMemory = null;
            Rebuild();
        };
        _pageActions.Children.Add(add);
        _pageActions.Children.Add(clear);
    }

    private void BuildMemoryPage()
    {
        // 总开关:关掉时既不放记忆清单也不给工具,已存的原样保留
        if (Query.Length == 0)
        {
            _content.Children.Add(SourceRow("memory:enabled", _loc["MemoryEnabled"], _loc["MemoryEnabledHint"], null, null,
                _settings.MemoryEnabled, on =>
                {
                    _settings.MemoryEnabled = on;
                    AfterChange();
                }));
        }
        if (_editingMemory == NewMemoryKey) _content.Children.Add(MemoryEditor(null));

        IReadOnlyList<MemoryEntry> entries = Memories.List();
        var rows = new List<Control>();
        foreach (MemoryEntry entry in entries)
        {
            if (!Matches(entry.Title, entry.Content)) continue;
            rows.Add(_editingMemory == entry.Id ? MemoryEditor(entry) : MemoryRow(entry));
        }
        if (rows.Count > 0)
        {
            AddSection(_loc["MemorySaved"], rows);
        }
        else if (entries.Count == 0 && _editingMemory is null)
        {
            _content.Children.Add(new TextBlock
            {
                Name = "MemoryEmpty",
                Classes = { "dim" },
                Text = _loc["MemoryEmpty"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(6, 10, 6, 4)
            });
        }
    }

    private Border MemoryRow(MemoryEntry entry)
    {
        Button edit = HeaderButton("MemoryEdit", _loc["MemoryEdit"]);
        edit.Click += (_, _) =>
        {
            _editingMemory = entry.Id;
            RenderContent();
        };
        Button delete = HeaderButton("MemoryDelete", _loc["MemoryDelete"]);
        delete.Click += async (_, _) =>
        {
            if (!await _confirm.AskAsync(_loc.F("MemoryDeleteTitle", entry.Title), entry.Content, _loc["MemoryDelete"], _loc["Cancel"])) return;
            Memories.Delete(entry.Id);
            Rebuild();
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(edit);
        actions.Children.Add(delete);

        Border row = SourceRow("memory:" + entry.Id, entry.Title, entry.Content, null, actions, null, null);
        // 第二行:谁存的、什么时候改的(副标题只放内容,太挤)
        string origin = entry.Origin == "user" ? _loc["MemoryOriginUser"] : _loc["MemoryOriginModel"];
        string when = entry.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        ((StackPanel)row.Child!).Children.Add(new TextBlock
        {
            Name = "MemoryMeta",
            Classes = { "sourceSub" },
            Text = $"{origin} · {when} · {entry.Id}",
            Margin = new Avalonia.Thickness(26, 0, 8, 6)
        });
        ToolTip.SetTip(row, entry.Content);
        return row;
    }

    /// <summary>就地编辑(<paramref name="entry" /> 为 null = 新增)。</summary>
    private Border MemoryEditor(MemoryEntry? entry)
    {
        var title = new TextBox
        {
            Name = "MemoryTitleBox",
            Text = entry?.Title ?? "",
            PlaceholderText = _loc["MemoryTitlePlaceholder"],
            MaxLength = MemoryStore.MaxTitleLength,
            Height = 30,
            MinHeight = 30
        };
        var content = new TextBox
        {
            Name = "MemoryContentBox",
            Text = entry?.Content ?? "",
            PlaceholderText = _loc["MemoryContentPlaceholder"],
            MaxLength = MemoryStore.MaxContentLength,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72
        };
        var error = new TextBlock { Name = "MemoryEditError", Classes = { "sourceSub" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, IsVisible = false };
        error[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaError");

        Button save = HeaderButton("MemorySave", _loc["MemorySave"]);
        save.Click += (_, _) =>
        {
            try
            {
                if (entry is null) Memories.Add(title.Text ?? "", content.Text ?? "", "user");
                else Memories.Update(entry.Id, title.Text ?? "", content.Text ?? "", "user");
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or IOException or UnauthorizedAccessException)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
                return;
            }
            _editingMemory = null;
            Rebuild();
        };
        Button cancel = HeaderButton("MemoryCancel", _loc["Cancel"]);
        cancel.Click += (_, _) =>
        {
            _editingMemory = null;
            RenderContent();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(title);
        stack.Children.Add(content);
        stack.Children.Add(error);
        stack.Children.Add(buttons);
        var editor = new Border
        {
            Name = "MemoryEditor",
            Classes = { "card" },
            Padding = new Avalonia.Thickness(10),
            Margin = new Avalonia.Thickness(0, 4, 0, 6),
            Child = stack
        };
        Dispatcher.UIThread.Post(() => title.Focus());
        return editor;
    }
}
