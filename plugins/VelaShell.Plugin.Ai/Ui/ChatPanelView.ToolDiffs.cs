using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 改文件的工具卡片:头部附 <c>+N −M</c>,展开区画 git 风格的差异(<see cref="DiffView" />),
/// 并能「在标签页中打开」成停靠文档。
/// </summary>
/// <remarks>
/// 差异先从参数算(补丁的 oldText / newText 或 edits 数组、写文件的整份 content),历史回放时照样有;
/// 运行中收到工具箱的 <see cref="FileChange" />(前后全文)后换成整份文件的真实差异,带真实行号。
/// </remarks>
public partial class ChatPanelView
{
    internal sealed partial class ToolCard
    {
        private readonly StackPanel _diffStats;

        /// <summary>收到的文件改动(前后全文);没有为 null。</summary>
        internal FileChange? FileChange { get; private set; }

        /// <summary>当前可画的差异(测试用)。</summary>
        internal ToolFileDiff? Diff { get; private set; }

        /// <summary>这张卡的参数是否指向 <paramref name="path" />。</summary>
        internal bool MatchesPath(string path, string? oldPath)
            => ToolDiff.PathsOf(_argumentsJson).Any(p => ToolDiff.SamePath(p, path)
                                                        || (oldPath is { Length: > 0 } && ToolDiff.SamePath(p, oldPath)));

        /// <summary>挂上工具箱报来的文件改动:换成整份前后文的差异,已展开就当场重画。</summary>
        internal void AttachFileChange(FileChange change)
        {
            FileChange = change;
            if (ToolDiff.FromChange(change) is { } diff)
            {
                Diff = diff;
            }
            UpdateDiffStats();
            if (_details.IsVisible)
            {
                RenderDetails();
            }
        }

        private void UpdateDiffStats()
        {
            _diffStats.Children.Clear();
            if (Diff is null)
            {
                _diffStats.IsVisible = false;
                return;
            }
            _diffStats.IsVisible = true;
            TextBlock added = DiffView.Text("+" + Diff.Added, "VelaStatusConnected");
            added.Name = "ToolDiffAdded";
            TextBlock removed = DiffView.Text("−" + Diff.Removed, "VelaError");
            removed.Name = "ToolDiffRemoved";
            _diffStats.Children.Add(added);
            _diffStats.Children.Add(removed);
        }

        /// <summary>展开区:有差异时先画差异,其余参数与结果照旧逐行排在下面。</summary>
        private void RenderDetails()
        {
            // 解开 JSON 逐行排:中文与 < ' + 不再是 \uXXXX,脚本里的换行真的换行(见 ToolDisplay)
            var sb = new StringBuilder();
            sb.Append(ToolDisplay.Readable(Diff is null
                ? _argumentsJson
                : ToolDisplay.WithoutKeys(_argumentsJson, ToolDiff.IsDiffPayloadKey)));
            if (_result.Length > 0)
            {
                sb.Append("\n────────\n");
                sb.Append(Truncate(ToolDisplay.Readable(_result), 8000));
            }
            _detailsText.Text = sb.ToString();
            _details.Content = null;
            if (Diff is null)
            {
                _details.MaxHeight = 220;
                _details.Content = _detailsText;
                return;
            }
            var open = new Button
            {
                Name = "OpenDiffInTab",
                Content = DiffView.Icon("AiIcon.fullscreen", "VelaTextSecondary", 12),
                Padding = new Thickness(4, 2),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            open[!ThemeProperty] = new DynamicResourceExtension("AiGhostIconButtonTheme");
            ToolTip.SetTip(open, _owner._loc["DiffOpenInTab"]);
            AutomationProperties.SetName(open, _owner._loc["DiffOpenInTab"]);
            open.Click += (_, e) =>
            {
                e.Handled = true;
                OpenDiffDocument();
            };
            string? path = FileChange?.Path ?? Diff.Path;
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new DiffView(Diff, _owner._loc, DiffViewMode.Inline,
                path is null ? null : Path.GetFileName(path.Replace('\\', '/')), action: open));
            panel.Children.Add(_detailsText);
            _details.MaxHeight = 420;
            _details.Content = panel;
        }

        /// <summary>把这张卡的差异开成停靠文档;有文件改动时与「已修改文件」列表里那个文件共用同一个标签。</summary>
        internal void OpenDiffDocument()
        {
            if (Diff is null) return;
            if (FileChange is { } change)
            {
                string key = ChangedFileList.KeyOf(change.Location, change.Path);
                // 「已修改文件」里有这个文件就用合并后的那份(与文件条打开的是同一个标签、同一份内容)
                ChangedFileEntry? entry = _owner._active.ChangedFiles.Find(change.Location, change.Path);
                _owner.OpenDiffDocument(key, entry is not null
                    ? _owner.DocumentOf(entry)
                    : _owner.DocumentOf(change.Location, change.Path, change.OldPath, change.Kind, change.Before, change.After, Diff));
                return;
            }
            // 只有参数里的片段(历史回放、或没收到改动事件):只比对片段;知道在哪儿就能「载入完整文件」
            string path = Diff.Path ?? ToolName;
            string? location = GuessLocation();
            _owner.OpenDiffDocument("fragment\n" + ToolName + "\n" + path, new FileDiffDocument(path,
                location is null ? null : _owner.DescribeLocation(location), null, Diff)
            { RawLocation = location });
        }

        /// <summary>
        /// 只有参数时推算文件在哪儿:本地取参数里的 workspaceId,没有就用默认工作区;
        /// 远端取参数里的 sessionId,没有就用当前对话绑的那台会话。都推不出来为 null。
        /// </summary>
        private string? GuessLocation()
        {
            Dictionary<string, string> args = ScalarArguments(_argumentsJson);
            if (ToolName.EndsWith("_local_file", StringComparison.Ordinal))
            {
                string? workspace = args.GetValueOrDefault("workspaceId") is { Length: > 0 } w ? w : _owner._settings.LocalDefaultWorkspaceId;
                return string.IsNullOrEmpty(workspace) ? null : "local:" + workspace;
            }
            if (ToolName.EndsWith("_remote_file", StringComparison.Ordinal))
            {
                string? session = args.GetValueOrDefault("sessionId") is { Length: > 0 } s ? s : _owner._active.SessionKey;
                return string.IsNullOrEmpty(session) ? null : "remote:" + session;
            }
            return null;
        }

        private static Dictionary<string, string> ScalarArguments(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return result;
                foreach (System.Text.Json.JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String && property.Value.GetString() is { } value)
                        result[property.Name] = value;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 参数解不开:当作没有
            }
            return result;
        }
    }

    internal sealed partial class AssistantBubble
    {
        /// <summary>这条回复里的工具卡片(测试用)。</summary>
        internal IReadOnlyCollection<ToolCard> ToolCardsForTest() => _toolCards.Values;

        /// <summary>
        /// 把工具箱报来的文件改动挂到发起它的那张卡上:同名工具里最近一张还没挂过、且路径对得上的;
        /// 路径都对不上就挂最近一张同名的。
        /// </summary>
        internal bool AttachFileChange(FileChange change)
        {
            ToolCard? fallback = null;
            foreach (ToolCard card in _toolCards.Values.Reverse())
            {
                if (card.ToolName != change.ToolName || card.FileChange is not null) continue;
                if (card.MatchesPath(change.Path, change.OldPath))
                {
                    card.AttachFileChange(change);
                    return true;
                }
                fallback ??= card;
            }
            fallback?.AttachFileChange(change);
            return fallback is not null;
        }
    }
}
