using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.XServer;

namespace VelaShell.Services.XServer;

/// <summary>
/// X 窗口截图(xs_plan F20):把一个顶层窗口此刻的像素做成位图,复制到剪贴板或存成 PNG。只截窗口自己的内容(不含系统边框;
/// 菜单之类的弹层是另外的顶层窗口,不在里面)。非矩形窗口形状以外是透明的,带 alpha 的窗口保留 alpha。录屏没做(要编码器)。
/// </summary>
internal static class WindowScreenshot
{
    /// <summary>截图;窗口已经没有像素(销毁了)时返回 null。</summary>
    public static WriteableBitmap? Capture(XTopLevelWindow handle)
    {
        XTopLevelSnapshot s = handle.Snapshot;
        uint[] pixels = new uint[Math.Max(1, s.Width) * Math.Max(1, s.Height)];
        (int width, int height) = handle.CopyPixels(pixels);
        if (width == 0 || height == 0)
        {
            return null;
        }
        if (width * height > pixels.Length)
        {
            pixels = new uint[width * height];   // 快照之后客户端改了尺寸:按缓冲此刻的尺寸再拷一次
            (width, height) = handle.CopyPixels(pixels);
            if (width == 0 || height == 0 || width * height > pixels.Length)
            {
                return null;
            }
        }
        bool alpha = s.HasAlpha || s.Shape is not null;
        WriteableBitmap bitmap = new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888,
            alpha ? AlphaFormat.Premul : AlphaFormat.Opaque);
        using ILockedFramebuffer frame = bitmap.Lock();
        for (int y = 0; y < height; y++)
        {
            Span<uint> row = pixels.AsSpan(y * width, width);
            if (!s.HasAlpha)
            {
                for (int x = 0; x < width; x++)
                {
                    row[x] |= 0xFF000000;   // 深度 24 的窗口高 8 位无意义
                }
            }
            if (s.Shape is { } shape)
            {
                ClipRow(row, y, shape);
            }
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(row)
                .CopyTo(FrameRow(frame, y, width * 4));
        }
        return bitmap;
    }

    private static unsafe Span<byte> FrameRow(ILockedFramebuffer frame, int y, int length) =>
        new((byte*)frame.Address + ((long)y * frame.RowBytes), length);

    /// <summary>形状(SHAPE 的边界形状)以外清成全透明。</summary>
    private static void ClipRow(Span<uint> row, int y, IReadOnlyList<XRect> shape)
    {
        int covered = 0;
        Span<bool> inside = row.Length <= 4096 ? stackalloc bool[row.Length] : new bool[row.Length];
        foreach (XRect r in shape)
        {
            if (y < r.Y || y >= r.Y + r.Height)
            {
                continue;
            }
            for (int x = Math.Max(0, r.X); x < Math.Min(row.Length, r.X + r.Width); x++)
            {
                covered += inside[x] ? 0 : 1;
                inside[x] = true;
            }
        }
        if (covered == row.Length)
        {
            return;
        }
        for (int x = 0; x < row.Length; x++)
        {
            if (!inside[x])
            {
                row[x] = 0;
            }
        }
    }

    /// <summary>截图复制到系统剪贴板(图片)。</summary>
    public static async Task<bool> CopyAsync(TopLevel owner, XTopLevelWindow handle)
    {
        if (owner.Clipboard is not { } clipboard || Capture(handle) is not { } bitmap)
        {
            return false;
        }
        DataTransfer data = new();
        DataTransferItem item = new();
        item.SetBitmap(bitmap);
        data.Add(item);
        await clipboard.SetDataAsync(data);
        return true;
    }

    /// <summary>截图存成 PNG:让用户选位置,默认文件名取窗口标题。用户取消时返回 false。</summary>
    public static async Task<bool> SaveAsync(TopLevel owner, XTopLevelWindow handle)
    {
        if (Capture(handle) is not { } bitmap)
        {
            return false;
        }
        using (bitmap)
        {
            XTopLevelSnapshot s = handle.Snapshot;
            IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Get("XServer_SaveScreenshotTitle"),
                SuggestedFileName = FileNameFor(s.Title.Length > 0 ? s.Title : s.ClassName),
                DefaultExtension = "png",
                FileTypeChoices = [FilePickerFileTypes.ImagePng],
            });
            if (file is null)
            {
                return false;
            }
            await using Stream stream = await file.OpenWriteAsync();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            return true;
        }
    }

    /// <summary>默认文件名:标题里不能进文件名的字符换成下划线,截到 80 个字符;空标题用「X window」之类的本地化名字。</summary>
    internal static string FileNameFor(string title)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string name = new([.. title.Take(80).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]);
        return (name.Trim().Length > 0 ? name.Trim() : Strings.Get("XServer_ScreenshotDefaultName")) + ".png";
    }
}
