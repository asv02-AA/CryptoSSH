using System.Windows.Media;

namespace CryptoSSH;

public record TerminalAppearance
{
    public string Theme { get; set; } = "Midnight";
    public string Font { get; set; } = "Cascadia Mono";
    public int Size { get; set; } = 14;
    public string Cursor { get; set; } = "Полоса";
    public bool UseCustomColors { get; set; }
    public string BackgroundColor { get; set; } = "#10141D";
    public string ForegroundColor { get; set; } = "#DCE4F2";
    public static bool IsColor(string? value) => value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, "\\A#[0-9A-Fa-f]{6}\\z");
    public static readonly string[] Fonts = ["Cascadia Mono", "Consolas", "Courier New"];
    public static readonly string[] Cursors = ["Полоса", "Блок", "Подчёркивание"];
    public void Validate()
    {
        if (!TerminalPalette.All.Any(p => p.Name == Theme) || !Fonts.Contains(Font) || Size is < 10 or > 28 || !Cursors.Contains(Cursor) || !IsColor(BackgroundColor) || !IsColor(ForegroundColor))
            throw new ArgumentException("Некорректные настройки оформления терминала.");
    }
}

public record TerminalPalette(string Name, string Background, string Foreground, string Selection, string[] Colors)
{
    public static readonly TerminalPalette[] All =
    [
        new("Midnight", "#10141D", "#DCE4F2", "#354767", ["#222938", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#C0CAF5", "#565F89", "#FF899D", "#B5E28C", "#F5CB88", "#9CBDFF", "#D3B4FF", "#A4E4FF", "#FFFFFF"]),
        new("Dracula", "#282A36", "#F8F8F2", "#44475A", ["#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2", "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"]),
        new("Nord", "#2E3440", "#D8DEE9", "#4C566A", ["#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0", "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4"]),
        new("Solarized", "#002B36", "#93A1A1", "#073642", ["#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5", "#586E75", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3"]),
        new("Daylight", "#F4F6FC", "#273449", "#CBD8F1", ["#283244", "#B52B45", "#277548", "#91640B", "#275EBD", "#8053AD", "#167C89", "#C0C9D8", "#627188", "#C82F50", "#318453", "#A17015", "#366DD0", "#9362BD", "#218993", "#FFFFFF"])
    ];
    public static TerminalPalette For(TerminalAppearance appearance)
    {
        var preset = All.Single(p => p.Name == appearance.Theme);
        return appearance.UseCustomColors ? preset with { Background = appearance.BackgroundColor, Foreground = appearance.ForegroundColor } : preset;
    }

}

public record Snippet
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public void Validate()
    {
        Name = Rules.Text(Name, "Название команды", 80);
        Command = Rules.Text(Command, "Команда (одна строка)", 2000);
        if (Name.Length == 0 || Command.Length == 0) throw new ArgumentException("Введите название и команду.");
    }
}
