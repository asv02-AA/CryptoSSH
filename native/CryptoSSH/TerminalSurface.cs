using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CryptoSSH;

internal sealed class TerminalSurface : FrameworkElement, IDisposable
{
    public VtBuffer Buffer { get; } = new();
    public event Action<string>? Input;
    public event Action<int, int>? Resized;
    public event Action? ScrollChanged;
    public bool IsReadOnly { get; set; }
    public int ScrollOffset { get; private set; }
    public int MaxScroll => Buffer.Alternate ? 0 : Buffer.History.Count;
    private TerminalAppearance appearance = new();
    private TerminalPalette palette = TerminalPalette.All[0];
    private double cellWidth = 8.4, cellHeight = 19;
    private bool cursorOn = true, selecting;
    private (int x, int y)? anchor, selectionEnd;
    private readonly DispatcherTimer blink;
    private readonly Dictionary<int, Brush> colors = new();
    public TerminalSurface()
    {
        Focusable = true; ClipToBounds = true; Cursor = Cursors.IBeam;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Buffer.Reply += text => Input?.Invoke(text);
        blink = new DispatcherTimer(TimeSpan.FromMilliseconds(550), DispatcherPriority.Background, (_, _) => { cursorOn = !cursorOn; if (IsKeyboardFocused) InvalidateVisual(); }, Dispatcher);
        Unloaded += (_, _) => blink.Stop(); Loaded += (_, _) => { blink.Start(); MeasureCells(); UpdateSize(); };
    }
    public void ApplyAppearance(TerminalAppearance value)
    {
        appearance = value with { }; palette = TerminalPalette.For(value); colors.Clear(); MeasureCells(); UpdateSize(); InvalidateVisual();
    }
    private Typeface Typeface(VtStyle style) => new(new FontFamily(appearance.Font), style.Italic ? FontStyles.Italic : FontStyles.Normal, style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
    private FormattedText Text(string value, VtStyle style, Brush color) => new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface(style), appearance.Size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private void MeasureCells()
    {
        var text = Text("M", new(-1, -1), Brushes.White); cellWidth = Math.Max(4, text.WidthIncludingTrailingWhitespace); cellHeight = Math.Ceiling(text.Height + 3);
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); UpdateSize(); }
    private void UpdateSize()
    {
        int cols = Math.Clamp((int)(ActualWidth / cellWidth), 2, 500), rows = Math.Clamp((int)(ActualHeight / cellHeight), 2, 200);
        if (ActualWidth < 20 || ActualHeight < 20 || cols == Buffer.Columns && rows == Buffer.Rows) return;
        Buffer.Resize(cols, rows); ScrollOffset = Math.Min(ScrollOffset, MaxScroll); ClearSelection(); Resized?.Invoke(cols, rows); ScrollChanged?.Invoke(); InvalidateVisual();
    }
    public void Feed(string value)
    {
        int before = Buffer.History.Count; bool wasAlternate = Buffer.Alternate; Buffer.Feed(value);
        if (ScrollOffset > 0) ScrollOffset = Math.Clamp(ScrollOffset + Buffer.History.Count - before, 0, MaxScroll);
        if (wasAlternate != Buffer.Alternate || Buffer.History.Count < before) { ScrollOffset = 0; ClearSelection(); }
        ScrollChanged?.Invoke(); InvalidateVisual();
    }
    public void ScrollTo(int offset) { ScrollOffset = Math.Clamp(offset, 0, MaxScroll); ScrollChanged?.Invoke(); InvalidateVisual(); }
    private int FirstLine => Buffer.Alternate ? 0 : Buffer.History.Count - ScrollOffset;
    private VtCell[] Line(int absolute) => !Buffer.Alternate && absolute < Buffer.History.Count ? Buffer.History[Math.Max(0, absolute)] : Buffer.Screen[Math.Clamp(absolute - (Buffer.Alternate ? 0 : Buffer.History.Count), 0, Buffer.Rows - 1)];
    private Brush ColorBrush(int value, bool foreground)
    {
        if (value < 0) return Ui.Brush(foreground ? palette.Foreground : palette.Background);
        if (colors.TryGetValue(value, out var brush)) return brush;
        Color color;
        if (value < 16) color = Ui.Brush(palette.Colors[value]).Color;
        else if (value < 232) { int n = value - 16; byte Level(int c) => (byte)(c == 0 ? 0 : 55 + c * 40); color = Color.FromRgb(Level(n / 36), Level(n / 6 % 6), Level(n % 6)); }
        else if (value < 256) { byte n = (byte)(8 + (value - 232) * 10); color = Color.FromRgb(n, n, n); }
        else color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        var result = new SolidColorBrush(color); result.Freeze(); return colors[value] = result;
    }
    private bool Selected(int x, int y)
    {
        if (anchor is not { } a || selectionEnd is not { } b) return false;
        if ((a.y, a.x).CompareTo((b.y, b.x)) > 0) (a, b) = (b, a);
        return (y, x).CompareTo((a.y, a.x)) >= 0 && (y, x).CompareTo((b.y, b.x)) < 0;
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Ui.Brush(palette.Background), null, new Rect(RenderSize));
        int first = FirstLine;
        for (int y = 0; y < Buffer.Rows; y++)
        {
            var line = Line(first + y); int limit = Math.Min(Buffer.Columns, line.Length);
            for (int x = 0; x < limit;)
            {
                var cell = line[x]; var style = cell.Style; bool selected = Selected(x, first + y);
                int foreground = style.Foreground, background = style.Background;
                Brush fg = ColorBrush(foreground, true), bg = ColorBrush(background, false);
                if (style.Reverse) (fg, bg) = (bg, fg);
                if (selected) bg = Ui.Brush(palette.Selection);
                int end = x + Math.Max(1, cell.Width); var text = new StringBuilder(cell.Text);
                // Batch ordinary single-width cells; wide/combining glyphs are rendered separately.
                if (cell.Width == 1 && cell.Text.Length == 1)
                    while (end < limit && line[end].Width == 1 && line[end].Text.Length == 1 && line[end].Style == style && Selected(end, first + y) == selected) { text.Append(line[end].Text); end++; }
                dc.DrawRectangle(bg, null, new Rect(x * cellWidth, y * cellHeight, (end - x) * cellWidth, cellHeight));
                if (cell.Width != 0)
                {
                    dc.PushClip(new RectangleGeometry(new Rect(x * cellWidth, y * cellHeight, (end - x) * cellWidth, cellHeight)));
                    dc.DrawText(Text(text.ToString(), style, fg), new Point(x * cellWidth, y * cellHeight));
                    if (style.Underline) dc.DrawLine(new Pen(fg, 1), new Point(x * cellWidth, (y + 1) * cellHeight - 3), new Point(end * cellWidth, (y + 1) * cellHeight - 3));
                    dc.Pop();
                }
                x = end;
            }
        }
        if (ScrollOffset == 0 && Buffer.CursorVisible && !IsReadOnly && (cursorOn || appearance.Cursor != "Полоса") && IsKeyboardFocused)
        {
            var rect = new Rect(Buffer.X * cellWidth, Buffer.Y * cellHeight, cellWidth, cellHeight);
            if (appearance.Cursor == "Полоса") rect.Width = 2;
            else if (appearance.Cursor == "Подчёркивание") { rect.Y += cellHeight - 2; rect.Height = 2; }
            var brush = Ui.Brush(palette.Foreground); brush.Opacity = appearance.Cursor == "Блок" ? .5 : 1; dc.DrawRectangle(brush, null, rect);
        }
    }
    public void SendText(string text) { if (IsReadOnly) return; ScrollTo(0); cursorOn = true; Input?.Invoke(text); }
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Text.Length > 0) { SendText((Keyboard.Modifiers == ModifierKeys.Alt ? "\x1b" : "") + e.Text); e.Handled = true; }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); var key = e.Key == Key.System ? e.SystemKey : e.Key; var mods = Keyboard.Modifiers;
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C) { Copy(); e.Handled = true; return; }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.V) { Paste(); e.Handled = true; return; }
        if (mods == ModifierKeys.Shift && key is Key.PageUp or Key.PageDown) { ScrollTo(ScrollOffset + (key == Key.PageUp ? 1 : -1) * Math.Max(1, Buffer.Rows - 2)); e.Handled = true; return; }
        string? sequence = null;
        if (mods == ModifierKeys.Control && key is >= Key.A and <= Key.Z) sequence = ((char)(key - Key.A + 1)).ToString();
        else if (mods == ModifierKeys.Control && key is Key.Space or Key.D2) sequence = "\0";
        else if (mods == ModifierKeys.Control && key == Key.OemOpenBrackets) sequence = "\x1b";
        else if (mods == ModifierKeys.Control && key == Key.Oem5) sequence = "\x1c";
        else if (mods == ModifierKeys.Control && key == Key.OemCloseBrackets) sequence = "\x1d";
        else
        {
            int modifier = 1 + (mods.HasFlag(ModifierKeys.Shift) ? 1 : 0) + (mods.HasFlag(ModifierKeys.Alt) ? 2 : 0) + (mods.HasFlag(ModifierKeys.Control) ? 4 : 0);
            string CursorKey(char suffix) => modifier > 1 ? $"\x1b[1;{modifier}{suffix}" : (Buffer.ApplicationCursor ? "\x1bO" : "\x1b[") + suffix;
            string Tilde(int number) => modifier > 1 ? $"\x1b[{number};{modifier}~" : $"\x1b[{number}~";
            sequence = key switch
            {
                Key.Enter => "\r", Key.Back => "\x7f", Key.Escape => "\x1b", Key.Tab => mods.HasFlag(ModifierKeys.Shift) ? "\x1b[Z" : "\t",
                Key.Up => CursorKey('A'), Key.Down => CursorKey('B'), Key.Right => CursorKey('C'), Key.Left => CursorKey('D'), Key.Home => CursorKey('H'), Key.End => CursorKey('F'),
                Key.Insert => Tilde(2), Key.Delete => Tilde(3), Key.PageUp => Tilde(5), Key.PageDown => Tilde(6),
                Key.F1 => "\x1bOP", Key.F2 => "\x1bOQ", Key.F3 => "\x1bOR", Key.F4 => "\x1bOS", Key.F5 => Tilde(15), Key.F6 => Tilde(17), Key.F7 => Tilde(18), Key.F8 => Tilde(19), Key.F9 => Tilde(20), Key.F10 => Tilde(21), Key.F11 => Tilde(23), Key.F12 => Tilde(24), _ => null
            };
        }
        if (sequence is not null) { SendText(sequence); e.Handled = true; }
    }
    public void Copy() { var text = SelectionText(); if (text.Length > 0) Clipboard.SetText(text); }
    public void Paste()
    {
        if (IsReadOnly || !Clipboard.ContainsText()) return; var text = Clipboard.GetText();
        if (text.IndexOfAny(['\r', '\n']) >= 0 && !ConfirmDialog.Ask(Window.GetWindow(this), "Вставить несколько строк?", "Вставка может выполнить команды в текущей сессии.", "Вставить")) return;
        // Prevent embedded terminal control sequences from escaping a bracketed paste.
        text = text.Replace("\x1b", "").Replace("\0", "");
        SendText(Buffer.BracketedPaste ? "\x1b[200~" + text + "\x1b[201~" : text.Replace("\r\n", "\r").Replace('\n', '\r'));
    }
    private (int x, int y) Position(MouseEventArgs e)
    { var p = e.GetPosition(this); return (Math.Clamp((int)(p.X / cellWidth), 0, Buffer.Columns), FirstLine + Math.Clamp((int)(p.Y / cellHeight), 0, Buffer.Rows - 1)); }
    private bool ReportMouse(MouseEventArgs e, int button, bool release = false)
    {
        if (Buffer.MouseMode == 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || ScrollOffset > 0 || IsReadOnly) return false;
        var position = e.GetPosition(this); int x = Math.Clamp((int)(position.X / cellWidth) + 1, 1, Buffer.Columns), y = Math.Clamp((int)(position.Y / cellHeight) + 1, 1, Buffer.Rows);
        button += Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 16 : 0;
        if (Buffer.MouseSgr) Input?.Invoke($"\x1b[<{button};{x};{y}" + (release ? "m" : "M"));
        else if (x <= 223 && y <= 223) Input?.Invoke("\x1b[M" + (char)(32 + (release ? 3 : button)) + (char)(32 + x) + (char)(32 + y));
        return true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        if (ReportMouse(e, 0)) { e.Handled = true; return; }
        anchor = selectionEnd = Position(e); selecting = true; CaptureMouse(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (selecting) { selectionEnd = Position(e); InvalidateVisual(); }
        else if (Buffer.MouseMode == 1003 || Buffer.MouseMode == 1002 && e.LeftButton == MouseButtonState.Pressed) ReportMouse(e, e.LeftButton == MouseButtonState.Pressed ? 32 : 35);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonUp(e); if (selecting) { selectionEnd = Position(e); selecting = false; ReleaseMouseCapture(); InvalidateVisual(); } else ReportMouse(e, 0, true); e.Handled = true; }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    { base.OnMouseWheel(e); if (!ReportMouse(e, e.Delta > 0 ? 64 : 65)) ScrollTo(ScrollOffset + (e.Delta > 0 ? 3 : -3)); e.Handled = true; }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); cursorOn = true; if (Buffer.FocusReporting) Input?.Invoke("\x1b[I"); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); if (Buffer.FocusReporting) Input?.Invoke("\x1b[O"); InvalidateVisual(); }
    private void ClearSelection() { anchor = selectionEnd = null; selecting = false; if (IsMouseCaptured) ReleaseMouseCapture(); }
    public string SelectionText()
    {
        if (anchor is not { } a || selectionEnd is not { } b) return "";
        if ((a.y, a.x).CompareTo((b.y, b.x)) > 0) (a, b) = (b, a);
        var lines = new List<string>();
        for (int y = a.y; y <= b.y; y++)
        {
            var line = Line(y); int start = y == a.y ? a.x : 0, end = Math.Min(y == b.y ? b.x : line.Length, line.Length);
            lines.Add(string.Concat(line.Skip(start).Take(Math.Max(0, end - start)).Where(c => c.Width != 0).Select(c => c.Text)).TrimEnd());
        }
        return string.Join(Environment.NewLine, lines);
    }
    public void Dispose() { blink.Stop(); Input = null; Resized = null; }
}
