using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Rendering;

public sealed partial class VelaTerminalControl
{
    // 每四行一个独立的绘制记录。只在实时备用屏上保留,不随回滚历史增长。
    private const int RowsPerBand = 4;
    private readonly List<AlternateBand> _alternateBands = [];
    private (double X, double Y, double Scale, double Gutter, bool Reverse) _bandGeometry;
    internal int AlternateBandRenderCountForTest { get; private set; }

    private void UpdateAlternateBands(Size size, bool force)
    {
        TerminalScreen screen = Emulator.Screen;
        bool eligible = Emulator.IsAlternateScreen && _scrollOffset == 0 && !_foldModel.HasFolds
            && !HasSelectionAnchor && _selectionMatchQuery is null && _searchHighlights is null
            && _bellFlashUntil <= DateTime.UtcNow && size.Width > 0 && size.Height > 0
            && screen.Rows <= Math.Ceiling(size.Height / CellHeightForTest)
            && screen.Columns <= Math.Ceiling(size.Width / CellWidthForTest);
        int count = eligible ? (screen.Rows + RowsPerBand - 1) / RowsPerBand : 0;
        while (_alternateBands.Count > count)
        {
            int last = _alternateBands.Count - 1;
            VisualChildren.Remove(_alternateBands[last]);
            _alternateBands.RemoveAt(last);
        }
        if (count == 0)
        {
            return;
        }

        RefreshPixelGrid();
        (double OriginX, double OriginY, double Scale, double, bool ReverseVideo) geometry = (_pixels.OriginX, _pixels.OriginY, _pixels.Scale, GutterWidth(), Emulator.Modes.ReverseVideo);
        force |= _bandGeometry != geometry;
        _bandGeometry = geometry;
        for (int i = 0; i < count; i++)
        {
            bool added = i == _alternateBands.Count;
            if (added)
            {
                var child = new AlternateBand(this, i * RowsPerBand) { IsHitTestVisible = false, UseLayoutRounding = false };
                _alternateBands.Add(child);
                VisualChildren.Insert(i, child); // 光标 / IME 始终在所有正文行之上。
            }
            AlternateBand band = _alternateBands[i];
            if (force || added)
            {
                double y = ContentPadding + i * RowsPerBand * CellHeightForTest;
                Size bandSize = new(size.Width, Math.Min(RowsPerBand, screen.Rows - i * RowsPerBand) * CellHeightForTest);
                band.Measure(bandSize);
                band.Arrange(new Rect(new Point(0, y), bandSize));
            }
            if (force || added || !band.Matches(screen))
            {
                band.InvalidateVisual();
            }
        }
    }

    private sealed class AlternateBand(VelaTerminalControl owner, int first) : Control
    {
        // 精确比较上次实际绘制的格子,而非散列或时间戳:同一批输出可依次擦除、挪行、再写回。
        // OSC 8 的句柄不在 TerminalCell 内;带链接的行保守重绘,清掉最后一个链接也不能漏。
        private TerminalCell[] _snapshot = [];
        private bool _hadLinks;
        private int _columns;

        public bool Matches(TerminalScreen screen)
        {
            int rows = Math.Min(RowsPerBand, screen.Rows - first);
            if (_hadLinks || _columns != screen.Columns || _snapshot.Length != rows * _columns)
            {
                return false;
            }
            for (int i = 0; i < rows; i++)
            {
                TerminalRow row = screen.ActiveLine(first + i);
                if (row.HasLinks || row.Span.Length != _columns || !MemoryMarshal.AsBytes(row.Span)
                    .SequenceEqual(MemoryMarshal.AsBytes(_snapshot.AsSpan(i * _columns, _columns))))
                {
                    return false;
                }
            }
            return true;
        }

        public override void Render(DrawingContext context)
        {
            TerminalScreen screen = owner.Emulator.Screen;
            int rows = Math.Min(RowsPerBand, screen.Rows - first);
            if (rows <= 0 || !owner.Emulator.IsAlternateScreen)
            {
                return;
            }
            owner.AlternateBandRenderCountForTest++;
            owner.RefreshPixelGrid();
            _columns = screen.Columns;
            if (_snapshot.Length != rows * _columns)
            {
                _snapshot = new TerminalCell[rows * _columns];
            }
            _hadLinks = false;
            using DrawingContext.PushedState transform = context.PushTransform(Matrix.CreateTranslation(owner.ContentPadding + owner.GutterWidth(), owner.ContentPadding - Bounds.Y));
            List<SelectionSpan> spans = owner.SelectionSpans();
            for (int i = 0; i < rows; i++)
            {
                int sr = first + i;
                TerminalRow row = screen.ActiveLine(sr);
                owner.RenderLine(context, owner.Emulator.Palette, row, _columns, sr * owner.CellHeightForTest, sr, spans);
                Span<TerminalCell> saved = _snapshot.AsSpan(i * _columns, _columns);
                row.Span.CopyTo(saved);
                saved[row.Span.Length..].Clear();
                _hadLinks |= row.HasLinks;
            }
        }
    }
}
