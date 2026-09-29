using System.Globalization;
using System.Text;

namespace CryptoSSH;

internal readonly record struct VtStyle(int Foreground = -1, int Background = -1, bool Bold = false, bool Italic = false, bool Underline = false, bool Reverse = false);
internal readonly record struct VtCell(string Text, int Width, VtStyle Style);

// Incremental VT parser and screen model. No process, HWND, or Windows Terminal dependency.
internal sealed class VtBuffer
{
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool ApplicationCursor { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool Alternate { get; private set; }
    public bool MouseSgr { get; private set; }
    public int MouseMode { get; private set; }
    public bool FocusReporting { get; private set; }
    public event Action<string>? Reply;
    public event Action<string>? TitleChanged;
    public IReadOnlyList<VtCell[]> History => history;
    public VtCell[][] Screen { get; private set; }
    private readonly List<VtCell[]> history = [];
    private VtCell[][]? primary;
    private (int x, int y, VtStyle style) primaryCursor, saved;
    private VtStyle style = new(-1, -1);
    private int top, bottom, state;
    private bool wrap = true, pendingWrap, origin, insert, lineDrawing;
    private readonly StringBuilder sequence = new();
    private char highSurrogate;
    public VtBuffer(int columns = 80, int rows = 24)
    {
        Columns = columns; Rows = rows; bottom = rows - 1; saved = (0, 0, new(-1, -1)); Screen = Enumerable.Range(0, rows).Select(_ => BlankLine()).ToArray();
    }
    private VtCell Blank => new(" ", 1, style);
    private VtCell[] BlankLine() => Enumerable.Repeat(Blank, Columns).ToArray();
    private void ClearCell(int row, int col)
    {
        if (col < 0 || col >= Columns) return;
        var line = Screen[row];
        if (line[col].Width == 0 && col > 0) line[col - 1] = Blank;
        if (line[col].Width == 2 && col + 1 < Columns) line[col + 1] = Blank;
        line[col] = Blank;
    }
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 2, 500); rows = Math.Clamp(rows, 2, 200);
        if (Columns == columns && Rows == rows) return;
        VtCell[][] ResizeScreen(VtCell[][] source, int cursorRow, bool keepHistory, out int displaced)
        {
            // Keep the cursor's row visible when the window shrinks. Only primary-screen
            // lines enter scrollback; editor/alternate-screen output must not leak into it.
            displaced = Math.Max(0, cursorRow - rows + 1);
            if (keepHistory) for (int y = 0; y < displaced; y++) AddHistory(source[y]);
            var result = new VtCell[rows][];
            for (int y = 0; y < rows; y++)
            {
                var line = Enumerable.Repeat(Blank, columns).ToArray();
                if (y + displaced < source.Length)
                    Array.Copy(source[y + displaced], line, Math.Min(columns, source[y + displaced].Length));
                // A double-width glyph cannot occupy half a cell at the new right edge.
                if (line[^1].Width == 2) line[^1] = Blank;
                result[y] = line;
            }
            return result;
        }
        Screen = ResizeScreen(Screen, Y, !Alternate, out int shifted); Y -= shifted;
        if (primary is not null)
        {
            primary = ResizeScreen(primary, primaryCursor.y, true, out int primaryShift);
            primaryCursor = (Math.Min(primaryCursor.x, columns - 1), primaryCursor.y - primaryShift, primaryCursor.style);
        }
        Columns = columns; Rows = rows; X = Math.Min(X, columns - 1); Y = Math.Min(Y, rows - 1); top = 0; bottom = rows - 1; pendingWrap = false;
    }
    private void AddHistory(VtCell[] line)
    { history.Add(line); if (history.Count > 5000) history.RemoveRange(0, 500); }
    private void Scroll(int amount)
    {
        for (int i = 0; i < Math.Min(Math.Abs(amount), bottom - top + 1); i++)
        {
            if (amount > 0)
            {
                if (!Alternate && top == 0 && bottom == Rows - 1) AddHistory(Screen[0]);
                for (int y = top; y < bottom; y++) Screen[y] = Screen[y + 1]; Screen[bottom] = BlankLine();
            }
            else { for (int y = bottom; y > top; y--) Screen[y] = Screen[y - 1]; Screen[top] = BlankLine(); }
        }
    }
    private void Index() { pendingWrap = false; if (Y == bottom) Scroll(1); else Y = Math.Min(Y + 1, Rows - 1); }
    private void ReverseIndex() { pendingWrap = false; if (Y == top) Scroll(-1); else Y = Math.Max(0, Y - 1); }
    private static int RuneWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        int u = rune.Value;
        return u >= 0x1100 && (u <= 0x115f || u is 0x2329 or 0x232a || u is >= 0x2e80 and <= 0xa4cf || u is >= 0xac00 and <= 0xd7a3 || u is >= 0xf900 and <= 0xfaff || u is >= 0xfe10 and <= 0xfe6f || u is >= 0xff00 and <= 0xff60 || u is >= 0xffe0 and <= 0xffe6 || u is >= 0x1f300 and <= 0x1faff || u is >= 0x20000 and <= 0x3ffff) ? 2 : 1;
    }
    private void Put(Rune rune)
    {
        int width = RuneWidth(rune); string text = rune.ToString();
        if (lineDrawing && rune.Value < 128) text = (char)rune.Value switch { 'q' => "─", 'x' => "│", 'l' => "┌", 'k' => "┐", 'm' => "└", 'j' => "┘", 't' => "├", 'u' => "┤", 'v' => "┴", 'w' => "┬", 'n' => "┼", _ => text };
        if (width == 0)
        {
            int previous = pendingWrap ? X : X - 1;
            if (previous >= 0) { if (Screen[Y][previous].Width == 0) previous--; var c = Screen[Y][Math.Max(0, previous)]; if (c.Text.Length < 32) Screen[Y][Math.Max(0, previous)] = c with { Text = c.Text + text }; } return;
        }
        if (pendingWrap && wrap || width == 2 && X == Columns - 1 && wrap) { X = 0; Index(); }
        pendingWrap = false;
        if (width == 2 && X == Columns - 1) { ClearCell(Y, X); return; }
        if (insert) { var line = Screen[Y]; Array.Copy(line, X, line, X + width, Columns - X - width); }
        ClearCell(Y, X); if (width == 2) ClearCell(Y, X + 1);
        Screen[Y][X] = new(text, width, style); if (width == 2) Screen[Y][X + 1] = new("", 0, style);
        X += width; if (X >= Columns) { X = Columns - 1; pendingWrap = true; }
    }
    public void Feed(string text)
    {
        foreach (char c in text)
        {
            if (state == 7) { if (c == '\x1b') state = 8; continue; }
            if (state == 8) { state = c == '\\' ? 0 : 7; continue; }
            if (state == 4) { if (c == '\\') { Osc(); state = 0; } else state = 3; continue; }
            if (state == 3) { if (c == '\a') { Osc(); state = 0; } else if (c == '\x1b') state = 4; else if (sequence.Length < 4096) sequence.Append(c); continue; }
            if (c == '\x1b') { state = 1; sequence.Clear(); continue; }
            if (c is '\x18' or '\x1a') { state = 0; continue; }
            if (state == 5) { lineDrawing = c == '0'; state = 0; continue; }
            if (state == 6) { state = 0; continue; }
            if (state == 1)
            {
                state = 0;
                switch (c)
                {
                    case '[': state = 2; break; case ']': state = 3; break;
                    case 'P': case '^': case '_': state = 7; break;
                    case '(': state = 5; break; case ')': case '*': case '+': case '#': case '%': state = 6; break;
                    case '7': saved = (X, Y, style); break; case '8': Restore(); break;
                    case 'D': Index(); break; case 'E': X = 0; Index(); break; case 'M': ReverseIndex(); break;
                    case 'c': Reset(); break; case '=': ApplicationCursor = true; break; case '>': ApplicationCursor = false; break;
                }
                continue;
            }
            if (state == 2)
            {
                if (c is >= '@' and <= '~') { Csi(c, sequence.ToString()); sequence.Clear(); state = 0; }
                else if (sequence.Length < 256) sequence.Append(c); else { state = 0; sequence.Clear(); }
                continue;
            }
            switch (c)
            {
                case '\r': X = 0; pendingWrap = false; continue;
                case '\n': case '\v': case '\f': Index(); continue;
                case '\b': X = Math.Max(0, X - 1); pendingWrap = false; continue;
                case '\t': X = Math.Min(Columns - 1, (X / 8 + 1) * 8); pendingWrap = false; continue;
                case '\x0e': lineDrawing = true; continue; case '\x0f': lineDrawing = false; continue;
            }
            if (char.IsControl(c)) continue;
            if (char.IsHighSurrogate(c)) { highSurrogate = c; continue; }
            if (char.IsLowSurrogate(c)) { if (highSurrogate != 0) { Put(new Rune(highSurrogate, c)); highSurrogate = '\0'; } continue; }
            highSurrogate = '\0'; Put(new Rune(c));
        }
    }
    private void Restore() { X = Math.Clamp(saved.x, 0, Columns - 1); Y = Math.Clamp(saved.y, 0, Rows - 1); style = saved.style; pendingWrap = false; }
    private void Reset()
    {
        style = new(-1, -1); X = Y = top = 0; bottom = Rows - 1; wrap = CursorVisible = true;
        pendingWrap = origin = insert = ApplicationCursor = BracketedPaste = Alternate = MouseSgr = FocusReporting = false; MouseMode = 0; primary = null;
        Screen = Enumerable.Range(0, Rows).Select(_ => BlankLine()).ToArray();
    }
    private void Osc()
    {
        var value = sequence.ToString(); sequence.Clear(); var split = value.IndexOf(';');
        if (split > 0 && value[..split] is "0" or "2") TitleChanged?.Invoke(new string(value[(split + 1)..].Where(c => !char.IsControl(c)).Take(120).ToArray()));
        // Clipboard writes (OSC 52), external links and shell integration escapes are ignored.
    }
    private void SetAlternate(bool enabled)
    {
        if (enabled == Alternate) return;
        if (enabled) { primary = Screen; primaryCursor = (X, Y, style); Screen = Enumerable.Range(0, Rows).Select(_ => BlankLine()).ToArray(); X = Y = 0; }
        else { Screen = primary ?? Screen; X = Math.Clamp(primaryCursor.x, 0, Columns - 1); Y = Math.Clamp(primaryCursor.y, 0, Rows - 1); style = primaryCursor.style; primary = null; }
        Alternate = enabled; top = 0; bottom = Rows - 1; pendingWrap = false;
    }
    private void Csi(char command, string parameters)
    {
        bool privateMode = parameters.StartsWith('?'); bool secondary = parameters.StartsWith('>');
        var pieces = parameters.TrimStart('?', '>').TrimEnd(' ', '!', '"').Split(';');
        int[] p = pieces.Select(s => int.TryParse(s, out int v) ? Math.Clamp(v, 0, 100000) : 0).ToArray();
        int P(int i, int fallback = 1) => i < p.Length && p[i] != 0 ? p[i] : fallback;
        int minY = origin ? top : 0, maxY = origin ? bottom : Rows - 1;
        if (command is not ('m' or 'h' or 'l')) pendingWrap = false;
        switch (command)
        {
            case 'A': Y = Math.Max(minY, Y - P(0)); break;
            case 'B': case 'e': Y = Math.Min(maxY, Y + P(0)); break;
            case 'C': case 'a': X = Math.Min(Columns - 1, X + P(0)); break;
            case 'D': X = Math.Max(0, X - P(0)); break;
            case 'E': Y = Math.Min(maxY, Y + P(0)); X = 0; break;
            case 'F': Y = Math.Max(minY, Y - P(0)); X = 0; break;
            case 'G': case '`': X = Math.Clamp(P(0) - 1, 0, Columns - 1); break;
            case 'Z': X = Math.Max(0, ((Math.Max(0, X - 1) / 8) - P(0) + 1) * 8); break;
            case 'd': Y = Math.Clamp(minY + P(0) - 1, minY, maxY); break;
            case 'H': case 'f': Y = Math.Clamp(minY + P(0) - 1, minY, maxY); X = Math.Clamp(P(1) - 1, 0, Columns - 1); break;
            case 'J':
                if (P(0, 0) == 3) history.Clear();
                else for (int row = 0; row < Rows; row++) for (int col = 0; col < Columns; col++)
                    if (p[0] == 2 || p[0] == 0 && (row > Y || row == Y && col >= X) || p[0] == 1 && (row < Y || row == Y && col <= X)) ClearCell(row, col);
                break;
            case 'K': for (int col = 0; col < Columns; col++) if (p[0] == 2 || p[0] == 0 && col >= X || p[0] == 1 && col <= X) ClearCell(Y, col); break;
            case 'X': for (int col = X; col < Math.Min(Columns, X + P(0)); col++) ClearCell(Y, col); break;
            case '@': { int n = Math.Min(P(0), Columns - X); var line = Screen[Y]; Array.Copy(line, X, line, X + n, Columns - X - n); for (int col = X; col < X + n; col++) line[col] = Blank; break; }
            case 'P': { int n = Math.Min(P(0), Columns - X); var line = Screen[Y]; Array.Copy(line, X + n, line, X, Columns - X - n); for (int col = Columns - n; col < Columns; col++) line[col] = Blank; break; }
            case 'L': case 'M': if (Y >= top && Y <= bottom) { int previous = top; top = Y; Scroll(command == 'L' ? -P(0) : P(0)); top = previous; } break;
            case 'S': Scroll(P(0)); break; case 'T': Scroll(-P(0)); break;
            case 'r': if (!privateMode) { int a = Math.Clamp(P(0) - 1, 0, Rows - 1), b = Math.Clamp(P(1, Rows) - 1, 0, Rows - 1); if (a < b) { top = a; bottom = b; X = 0; Y = origin ? top : 0; } } break;
            case 's': saved = (X, Y, style); break; case 'u': Restore(); break;
            case 'n': if (p[0] == 5) Reply?.Invoke("\x1b[0n"); else if (p[0] == 6) Reply?.Invoke($"\x1b[{Y + 1};{X + 1}R"); break;
            case 'c': Reply?.Invoke(secondary ? "\x1b[>0;300;0c" : "\x1b[?1;2c"); break;
            case 't': if (p[0] == 18) Reply?.Invoke($"\x1b[8;{Rows};{Columns}t"); break;
            case 'h': case 'l':
                foreach (int mode in p)
                {
                    bool enable = command == 'h';
                    if (!privateMode) { if (mode == 4) insert = enable; continue; }
                    switch (mode)
                    {
                        case 1: ApplicationCursor = enable; break; case 6: origin = enable; X = 0; Y = enable ? top : 0; break;
                        case 7: wrap = enable; break; case 25: CursorVisible = enable; break;
                        case 47: case 1047: case 1049: SetAlternate(enable); break;
                        case 1048: if (enable) saved = (X, Y, style); else Restore(); break;
                        case 2004: BracketedPaste = enable; break; case 1006: MouseSgr = enable; break;
                        case 1000: case 1002: case 1003: MouseMode = enable ? mode : 0; break;
                        case 1004: FocusReporting = enable; break;
                    }
                }
                break;
            case 'm': Sgr(parameters); break;
        }
    }
    private void Sgr(string text)
    {
        // Colon notation (38:2::R:G:B) and the traditional semicolon form.
        var p = text.Replace("::", ":").Replace(':', ';').Split(';').Select(v => int.TryParse(v, out var n) ? n : 0).ToArray();
        for (int i = 0; i < p.Length; i++)
        {
            int v = p[i];
            switch (v)
            {
                case 0: style = new(-1, -1); break;
                case 1: style = style with { Bold = true }; break; case 22: style = style with { Bold = false }; break;
                case 3: style = style with { Italic = true }; break; case 23: style = style with { Italic = false }; break;
                case 4: style = style with { Underline = true }; break; case 24: style = style with { Underline = false }; break;
                case 7: style = style with { Reverse = true }; break; case 27: style = style with { Reverse = false }; break;
                case 39: style = style with { Foreground = -1 }; break; case 49: style = style with { Background = -1 }; break;
                case >= 30 and <= 37: style = style with { Foreground = v - 30 }; break;
                case >= 40 and <= 47: style = style with { Background = v - 40 }; break;
                case >= 90 and <= 97: style = style with { Foreground = v - 90 + 8 }; break;
                case >= 100 and <= 107: style = style with { Background = v - 100 + 8 }; break;
                case 38: case 48:
                    int color = -1;
                    if (i + 2 < p.Length && p[i + 1] == 5) { color = Math.Clamp(p[i + 2], 0, 255); i += 2; }
                    else if (i + 4 < p.Length && p[i + 1] == 2) { color = 0x1000000 | Math.Clamp(p[i + 2], 0, 255) << 16 | Math.Clamp(p[i + 3], 0, 255) << 8 | Math.Clamp(p[i + 4], 0, 255); i += 4; }
                    if (v == 38) style = style with { Foreground = color }; else style = style with { Background = color }; break;
            }
        }
    }
    public string RowText(int y) => string.Concat(Screen[y].Where(c => c.Width != 0).Select(c => c.Text)).TrimEnd();
}
