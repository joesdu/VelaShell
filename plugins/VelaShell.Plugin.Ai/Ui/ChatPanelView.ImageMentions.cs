using System.Text;
using Avalonia.Controls;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

public partial class ChatPanelView
{
    private const string GenerateImageMention = "@imagegen";
    private const string EditImageMention = "@imageedit";
    private readonly List<string> _imageMentionCandidates = [];
    private int PickerCandidateCount => _commandCandidates.Count + _imageMentionCandidates.Count + _fileCandidates.Count;

    private static bool TryFindToolToken(string text, int caret, out int start, out string query)
    {
        start = -1;
        query = "";
        for (int i = caret - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i])) return false;
            if (text[i] != '@') continue;
            if (i > 0 && !char.IsWhiteSpace(text[i - 1])) return false;
            start = i;
            query = text[(i + 1)..caret];
            return true;
        }
        return false;
    }

    private bool CanMentionImages => _settings.Mode != ChatMode.Plan
        && _settings.ImageToolsEnabled
        && ActiveProviderForRequest is { } chat && !AiSettingsStore.IsImageModel(chat)
        && ((_settings.FindModel(_settings.ImageModelId) is { } image && AiSettingsStore.IsImageModel(image))
            || _settings.ResolveModels().Any(model => model.Provider.Id == chat.Provider.Id && AiSettingsStore.IsImageModel(model)));

    private void UpdateImageMentionCandidates(string query)
    {
        _imageMentionCandidates.Clear();
        if (_settings.Mode == ChatMode.Plan
            || ActiveProviderForRequest is { } chat && AiSettingsStore.IsImageModel(chat)
            || query.Contains('/') || query.Contains('~'))
        {
            return;
        }

        foreach (string name in new[] { GenerateImageMention, EditImageMention })
        {
            string label = _loc[name == GenerateImageMention ? "ImageMentionGenerate" : "ImageMentionEdit"];
            if (query.Length == 0 || name[1..].Contains(query, StringComparison.OrdinalIgnoreCase)
                || label.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _imageMentionCandidates.Add(name);
            }
        }
    }

    private Border BuildImageMentionRow(string mention, int index)
    {
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new TextBlock
        {
            Classes = { "fileName" },
            Text = mention + " · " + _loc[mention == GenerateImageMention ? "ImageMentionGenerate" : "ImageMentionEdit"]
        });
        body.Children.Add(new TextBlock
        {
            Classes = { "dim" },
            Text = _loc[mention == GenerateImageMention ? "ImageMentionGenerateHint" : "ImageMentionEditHint"]
        });
        var row = new Border
        {
            Name = mention == GenerateImageMention ? "ImageGenerateMention" : "ImageEditMention",
            Classes = { "fileRow" },
            Child = body,
            Tag = index
        };
        row.PointerPressed += (_, e) => { e.Handled = true; AcceptCandidate(index); };
        return row;
    }

    private void AcceptImageMention(int index)
    {
        string text = InputBox.Text ?? "";
        int caret = Math.Clamp(InputBox.CaretOffset, 0, text.Length);
        if (_fileTokenStart < 0 || _fileTokenStart >= caret) return;
        string replacement = _imageMentionCandidates[index] + " ";
        _pickerSuspended = true;
        try
        {
            InputBox.Document.Replace(_fileTokenStart, caret - _fileTokenStart, replacement);
            InputBox.CaretOffset = _fileTokenStart + replacement.Length;
        }
        finally { _pickerSuspended = false; }
        CloseFilePicker();
        if (!CanMentionImages) SetStatus(_loc["ImageMentionUnavailable"]);
        InputBox.TextArea.Focus();
    }

    private static bool TryImageMentionAt(string text, int index, out int length, out bool edit)
    {
        length = 0;
        edit = false;
        if (index < 0 || index >= text.Length || (index > 0 && !char.IsWhiteSpace(text[index - 1]))) return false;
        foreach (string mention in new[] { GenerateImageMention, EditImageMention })
        {
            int end = index + mention.Length;
            if (end <= text.Length && text.AsSpan(index, mention.Length).Equals(mention, StringComparison.OrdinalIgnoreCase)
                && (end == text.Length || char.IsWhiteSpace(text[end])))
            {
                length = mention.Length;
                edit = mention == EditImageMention;
                return true;
            }
        }
        return false;
    }

    private static string ExpandImageMentions(string text, out bool mentioned)
    {
        mentioned = false;
        var expanded = new StringBuilder();
        for (int i = 0; i < text.Length;)
        {
            if (TryImageMentionAt(text, i, out int length, out bool edit))
            {
                mentioned = true;
                expanded.Append(edit
                    ? "[Use edit_image for this request with the relevant image IDs from this conversation. Ask for an attached image if no source is available.]"
                    : "[Use generate_image for this request, incorporating the relevant visual requirements from this conversation.]");
                i += length;
            }
            else
            {
                expanded.Append(text[i++]);
            }
        }
        return mentioned ? expanded.ToString() : text;
    }
}
