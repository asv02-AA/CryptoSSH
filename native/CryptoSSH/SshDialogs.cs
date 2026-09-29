using System.Windows;
using System.Windows.Controls;

namespace CryptoSSH;

internal sealed class CredentialDialog : ThemedDialog
{
    public string? Value { get; private set; }
    public CredentialDialog(Window owner, string title, string message, bool echoed) : base(owner, title, 510)
    {
        Body.Children.Add(Ui.Text(message, 13, "#B8C3DB", new(0, 0, 0, 18)));
        var hidden = new PasswordBox { Background = Ui.Brush("#30364B"), Foreground = Ui.Brush("#E4E8F2"), BorderBrush = Ui.Brush("#4B5572"), Padding = new(10), Margin = new(0, 0, 0, 20) };
        var visible = new TextBox { Margin = new(0, 0, 0, 20) };
        Body.Children.Add(echoed ? visible : hidden);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        var connect = Ui.Button("Продолжить", () => { Value = echoed ? visible.Text : hidden.Password; hidden.Clear(); visible.Clear(); DialogResult = true; }, true); connect.IsDefault = true; connect.Margin = new(10, 0, 0, 0); actions.Children.Add(connect); Body.Children.Add(actions);
        Loaded += (_, _) => { if (echoed) visible.Focus(); else hidden.Focus(); }; Closed += (_, _) => { hidden.Clear(); visible.Clear(); };
    }
    public static string? Ask(Window owner, string title, string message, bool echoed = false)
    { var dialog = new CredentialDialog(owner, title, message, echoed); return dialog.ShowDialog() == true ? dialog.Value : null; }
}

internal sealed class TokenChoiceDialog : ThemedDialog
{
    public TokenKey? Value { get; private set; }
    public TokenChoiceDialog(Window owner, IReadOnlyList<TokenKey> keys, string title = "Выберите SSH-ключ токена", string action = "Использовать ключ") : base(owner, title, 780)
    {
        var list = new ComboBox { ItemsSource = keys, SelectedIndex = 0, Margin = new(0, 0, 0, 20) }; Body.Children.Add(list);
        var choose = Ui.Button(action, () => { Value = (TokenKey)list.SelectedItem; DialogResult = true; }, true); choose.IsDefault = true; Body.Children.Add(choose);
    }
}

internal sealed class TokenDeviceDialog : ThemedDialog
{
    public TokenDevice? Value { get; private set; }
    public TokenDeviceDialog(Window owner, string connection, IReadOnlyList<TokenDevice> devices) : base(owner, "Выберите токен для подключения", 820)
    {
        Body.Children.Add(Ui.Text($"Подключение: {connection}\nВыберите устройство по модели, метке и серийному номеру. PIN потребуется только для него.", 13, "#B8C3DB", new(0, 0, 0, 18)));
        var list = new ComboBox { ItemsSource = devices, SelectedIndex = -1, Margin = new(0, 0, 0, 20) }; Body.Children.Add(list);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        var use = Ui.Button("Использовать токен", () => { if (list.SelectedItem is TokenDevice device) { Value = device; DialogResult = true; } }, true);
        use.IsDefault = true; use.IsEnabled = false; use.Margin = new(10, 0, 0, 0); actions.Children.Add(use); Body.Children.Add(actions);
        list.SelectionChanged += (_, _) => use.IsEnabled = list.SelectedItem is TokenDevice;
        Loaded += (_, _) => list.Focus();
    }
}
