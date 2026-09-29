using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CryptoSSH;

// A separate owned window stays above the terminal's native HWND.
public class ThemedDialog : Window
{
    protected StackPanel Body { get; } = new() { Margin = new(24, 4, 24, 24) };
    public ThemedDialog(Window owner, string title, double width = 460)
    {
        Owner = owner; Title = title; Width = width; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Background = Ui.Brush("#262A3C");
        var panel = new StackPanel();
        var header = new DockPanel { Margin = new(24, 18, 16, 18), LastChildFill = true };
        var close = Ui.Button("×", Close); close.ToolTip = "Закрыть"; close.Padding = new(9, 3, 9, 3); close.Background = Brushes.Transparent; close.BorderThickness = new(0); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var heading = Ui.Text(title, 18); heading.FontWeight = FontWeights.SemiBold; heading.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(heading);
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == header || e.OriginalSource == heading) DragMove(); };
        panel.Children.Add(header); panel.Children.Add(Body);
        Content = new Border { Child = panel, Background = Ui.Brush("#262A3C"), BorderBrush = Ui.Brush("#4B5572"), BorderThickness = new(1) };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
}

public sealed class ConfirmDialog : ThemedDialog
{
    internal Button CancelButton { get; }
    internal Button ConfirmButton { get; }
    public ConfirmDialog(Window owner, string title, string message, string confirm) : base(owner, title)
    {
        Body.Children.Add(Ui.Text(message, 13, "#B8C3DB", new(0, 0, 0, 25)));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        CancelButton = Ui.Button("Отмена", () => DialogResult = false); CancelButton.IsCancel = true; CancelButton.IsDefault = true;
        ConfirmButton = Ui.Button(confirm, () => DialogResult = true, true); ConfirmButton.Margin = new(10, 0, 0, 0);
        actions.Children.Add(CancelButton); actions.Children.Add(ConfirmButton); Body.Children.Add(actions);
        Loaded += (_, _) => CancelButton.Focus();
    }
    public static bool Ask(Window owner, string title, string message, string confirm) => new ConfirmDialog(owner, title, message, confirm).ShowDialog() == true;
}

public sealed class ColorPickerDialog : ThemedDialog
{
    public string Value { get; private set; }
    public ColorPickerDialog(Window owner, string title, string initial) : base(owner, title, 430)
    {
        Value = initial; var color = Ui.Brush(initial).Color;
        var swatch = new Border { Height = 65, CornerRadius = new(8), Background = Ui.Brush(initial), Margin = new(0, 0, 0, 18) }; Body.Children.Add(swatch);
        var sliders = new List<Slider>();
        foreach (var (name, channel) in new[] { ("Красный", color.R), ("Зелёный", color.G), ("Синий", color.B) })
        {
            Body.Children.Add(Ui.Label(name));
            var slider = new Slider { Minimum = 0, Maximum = 255, Value = channel, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new(0, 0, 0, 15) };
            sliders.Add(slider); Body.Children.Add(slider);
        }
        var label = Ui.Text(Value, 15, "#CAD6F5", new(0, 0, 0, 18)); label.FontFamily = new("Consolas"); Body.Children.Add(label);
        foreach (var slider in sliders) slider.ValueChanged += (_, _) => { Value = $"#{(int)sliders[0].Value:X2}{(int)sliders[1].Value:X2}{(int)sliders[2].Value:X2}"; swatch.Background = Ui.Brush(Value); label.Text = Value; };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        var apply = Ui.Button("Выбрать", () => DialogResult = true, true); apply.IsDefault = true; apply.Margin = new(10, 0, 0, 0); actions.Children.Add(apply); Body.Children.Add(actions);
    }
}

public sealed class ColorField : StackPanel
{
    public string Value { get; private set; }
    public event Action? Changed;
    public ColorField(string title, string initial)
    {
        Value = initial; Children.Add(Ui.Label(title));
        var row = new DockPanel(); var input = new TextBox { Text = initial, MaxLength = 7, FontFamily = new("Consolas"), Padding = new(8, 6, 8, 6), VerticalContentAlignment = VerticalAlignment.Center };
        var swatch = new Border { Width = 30, Height = 23, Background = Ui.Brush(initial), CornerRadius = new(4) };
        var choose = Ui.Button("", () => { var dialog = new ColorPickerDialog(Window.GetWindow(this), title, Value); if (dialog.ShowDialog() == true) input.Text = dialog.Value; }); choose.Content = swatch; choose.Padding = new(7); choose.Margin = new(0, 0, 8, 0); choose.ToolTip = "Выбрать цвет RGB"; DockPanel.SetDock(choose, Dock.Left); row.Children.Add(choose); row.Children.Add(input); Children.Add(row);
        System.Windows.Automation.AutomationProperties.SetName(input, title + " в формате HEX");
        var hint = Ui.Text("", 10, "#F0ABAB", new(0, 4, 0, 0)); Children.Add(hint); Margin = new(0, 0, 0, 10);
        input.TextChanged += (_, _) =>
        {
            if (!TerminalAppearance.IsColor(input.Text)) { hint.Text = "Формат: #RRGGBB, например #102030"; return; }
            hint.Text = ""; Value = input.Text.ToUpperInvariant(); swatch.Background = Ui.Brush(Value); Changed?.Invoke();
        };
    }
}
