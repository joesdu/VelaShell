using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Ai.Ui;

public partial class ChatPanelView
{
    private readonly HashSet<Action> _imagePreviewClosers = [];

    private void CloseImagePreviews()
    {
        foreach (Action close in _imagePreviewClosers.ToArray()) close();
    }

    private ConversationImages ImagesFor(Conversation conversation)
        => conversation.Images ??= new ConversationImages(_generatedImages);

    private async Task<bool> ApproveImageAsync(CommandJobRunScope run, ApprovalMode approval,
        string detail, CancellationToken token)
    {
        if (run.HasDeniedExecution) return false;
        if (approval == ApprovalMode.Bypass) return true;
        bool allowed = await RequestApprovalAsync(new ApprovalRequest("generate_image", detail)).WaitAsync(token);
        if (!allowed) run.MarkExecutionDenied();
        return allowed;
    }

    private List<ChatGeneratedImage> RegisterInputImages(ChatMessage message)
    {
        var references = new List<ChatGeneratedImage>();
        foreach (DataContent image in message.Contents.OfType<DataContent>().Where(c => c.HasTopLevelMediaType("image")))
        {
            // 只登记改图接口收得下的格式:GIF 照样作为视觉输入发给聊天模型,但不落盘、不给 ID(见 IsEditableInput)
            if (!ImageGenerationService.IsEditableInput(image.MediaType)) continue;
            try
            {
                ChatGeneratedImage reference = _generatedImages.Save(image);
                ImagesFor(Cur).Register(reference);
                references.Add(reference);
            }
            catch (Exception ex)
            {
                _context.Log.Warn($"Saving input image for future edits failed: {ex.Message}");
            }
        }
        if (references.Count > 0)
            message.Contents.Add(new TextContent("\nAttached image IDs (use only these exact IDs for edits): " + string.Join(", ", references.Select(r => r.FileName))));
        return references;
    }

    internal sealed partial class AssistantBubble
    {
        private readonly List<ChatGeneratedImage> _images = [];
        public bool HasImages => _images.Count > 0;
        internal IEnumerable<string> ImageIds => _images.Select(image => image.FileName);

        public void AddImage(DataContent content)
        {
            if (_images.Count >= GeneratedImageStore.MaxImagesPerTurn) return;
            try
            {
                ChatGeneratedImage reference = _owner._generatedImages.Save(content);
                _images.Add(reference);
                _owner.ImagesFor(_owner.Cur).Register(reference);
                ShowImage(reference, content);
            }
            catch (Exception ex)
            {
                _owner._context.Log.Warn($"Displaying generated image failed: {ex.Message}");
                _owner.AddNoticeCard(this, _owner._loc["GeneratedImageUnavailable"]);
            }
        }

        internal ImageToolTurn? ImageTools { get; set; }

        private void ShowToolImage(string result)
        {
            if (ImageTools?.ResolveResult(result) is not { } reference || _images.Contains(reference)) return;
            try
            {
                _images.Add(reference);
                ShowImage(reference, _owner._generatedImages.Load(reference));
            }
            catch (Exception ex)
            {
                _owner._context.Log.Warn($"Loading tool image failed: {ex.Message}");
                _owner.AddNoticeCard(this, _owner._loc["GeneratedImageUnavailable"]);
            }
        }

        private void RestoreImages(ChatTurnMeta meta)
        {
            foreach (ChatGeneratedImage reference in (meta.Images ?? []).Take(GeneratedImageStore.MaxImagesPerTurn))
            {
                try
                {
                    _images.Add(reference);
                    _owner.ImagesFor(_owner.Cur).Register(reference);
                    ShowImage(reference, _owner._generatedImages.Load(reference));
                }
                catch (Exception ex)
                {
                    _owner._context.Log.Warn($"Restoring generated image failed: {ex.Message}");
                    _owner.AddNoticeCard(this, _owner._loc["GeneratedImageUnavailable"]);
                }
            }
        }

        private void ShowImage(ChatGeneratedImage reference, DataContent content)
        {
            MarkThinkingDone();
            SetPhase("PhaseWriting");
            var body = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            var preview = new Image
            {
                MaxWidth = 640,
                MaxHeight = 480,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            // Keep the compressed bytes rather than a permanently decoded full-size bitmap in history.
            Bitmap? bitmap = null;
            void Decode()
            {
                if (bitmap is not null) return;
                using var stream = new MemoryStream(content.Data.ToArray(), writable: false);
                bitmap = Bitmap.DecodeToWidth(stream, 1024);
                preview.Source = bitmap;
            }
            Decode();
            preview.DetachedFromVisualTree += (_, _) =>
            {
                preview.Source = null;
                bitmap?.Dispose();
                bitmap = null;
            };
            preview.AttachedToVisualTree += (_, _) =>
            {
                try { Decode(); }
                catch (Exception ex) { _owner._context.Log.Warn($"Decoding generated image failed: {ex.Message}"); }
            };
            Button copy = ImageAction("ImageCopyAction", "AiIcon.copy", _owner._loc["CopyGeneratedImage"]);
            copy.Click += async (_, _) => await CopyImageAsync(reference, copy);
            Button save = ImageAction("ImageSaveAction", "AiIcon.image-down", _owner._loc["SaveGeneratedImage"]);
            save.Click += async (_, _) => await SaveImageAsync(reference);
            Button full = ImageAction("ImageFullScreenAction", "AiIcon.fullscreen", _owner._loc["ViewFullImage"]);
            full.Click += (_, _) => OpenImagePreview(reference);
            var actions = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8),
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { copy, save, full }
            };
            body.Children.Add(preview);
            body.Children.Add(actions);
            AddCard(new Border { Padding = new Thickness(0, 4), Child = body });
        }

        private static Button ImageAction(string name, string icon, string label)
        {
            var button = new Button { Name = name, Content = MakeIcon(icon, "VelaTextPrimary", 16) };
            button[!ThemeProperty] = new DynamicResourceExtension("AiImageFloatingActionTheme");
            ToolTip.SetTip(button, label);
            AutomationProperties.SetName(button, label);
            return button;
        }

        private void OpenImagePreview(ChatGeneratedImage reference)
        {
            Bitmap bitmap;
            try
            {
                DataContent content = _owner._generatedImages.Load(reference);
                using var stream = new MemoryStream(content.Data.ToArray(), writable: false);
                bitmap = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                _owner._context.Log.Warn($"Loading full image failed: {ex.Message}");
                _owner.AddNoticeCard(this, _owner._loc["GeneratedImageUnavailable"]);
                return;
            }

            double scaling = TopLevel.GetTopLevel(_owner)?.RenderScaling ?? 1;
            (double availableWidth, double availableHeight) = WindowFit.Fit(10000, 10000, _owner);
            if (availableWidth == 10000 && availableHeight == 10000)
                (availableWidth, availableHeight) = (1600, 1200);
            Size initialSize = ImagePreviewView.InitialWindowSize(bitmap.PixelSize,
                new Size(availableWidth, availableHeight), scaling);
            var view = new ImagePreviewView(bitmap, _owner._loc, scaling, source => CopyImageAsync(reference, source));
            IPluginPanel? panel = null;
            bool closed = false;
            void Cleanup()
            {
                if (closed) return;
                closed = true;
                view.ReleaseImage();
                bitmap.Dispose();
                _owner.DetachedFromVisualTree -= OwnerDetached;
                _owner._imagePreviewClosers.Remove(ClosePreview);
            }
            void ClosePreview()
            {
                if (panel is not null) _ = panel.CloseAsync();
                Cleanup();
            }
            void OwnerDetached(object? sender, VisualTreeAttachmentEventArgs e) => ClosePreview();
            _owner._imagePreviewClosers.Add(ClosePreview);
            _owner.DetachedFromVisualTree += OwnerDetached;
            _ = _owner.OpenAsync(_owner._loc["ImagePreviewTitle"], initialSize.Width, initialSize.Height, [], () => view,
                opened =>
                {
                    panel = opened;
                    if (closed) _ = opened.CloseAsync();
                }, Cleanup);
        }

        private async Task CopyImageAsync(ChatGeneratedImage reference, Control source)
        {
            try
            {
                if (TopLevel.GetTopLevel(source)?.Clipboard is not { } clipboard)
                    throw new InvalidOperationException(_owner._loc["ImageClipboardUnavailable"]);
                byte[] bytes = _owner._generatedImages.Load(reference).Data.ToArray();
                using var validationStream = new MemoryStream(bytes, writable: false);
                using var validation = new Bitmap(validationStream);
                var data = new DataTransfer();
                // The clipboard may request pixels after the preview closes. Decode independently on each request.
                data.Add(DataTransferItem.Create(DataFormat.Bitmap, () =>
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    return new Bitmap(stream);
                }));
                await clipboard.SetDataAsync(data);
                ToolTip.SetTip(source, _owner._loc["Copied"]);
                _owner.StatusText.Text = _owner._loc["Copied"];
            }
            catch (Exception ex)
            {
                _owner._context.Log.Warn($"Copying generated image failed: {ex.Message}");
                ToolTip.SetTip(source, $"{_owner._loc["Error"]}: {ex.Message}");
                _owner.StatusText.Text = $"{_owner._loc["Error"]}: {ex.Message}";
            }
        }

        private async Task SaveImageAsync(ChatGeneratedImage reference)
        {
            try
            {
                if (TopLevel.GetTopLevel(_owner)?.StorageProvider is not { } storage) return;
                IStorageFile? target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = _owner._loc["SaveGeneratedImage"],
                    SuggestedFileName = reference.FileName,
                    ShowOverwritePrompt = true
                });
                if (target is null) return;
                using (target)
                {
                    DataContent content = _owner._generatedImages.Load(reference);
                    await using Stream stream = await target.OpenWriteAsync();
                    stream.SetLength(0);
                    await stream.WriteAsync(content.Data);
                }
            }
            catch (Exception ex)
            {
                _owner._context.Log.Warn($"Saving generated image failed: {ex.Message}");
                _owner.StatusText.Text = $"{_owner._loc["Error"]}: {ex.Message}";
            }
        }
    }
}
