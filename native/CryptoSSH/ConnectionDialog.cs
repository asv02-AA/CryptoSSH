using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace CryptoSSH;
public sealed class ConnectionDialog : Window
{
    public Connection Result { get; private set; }
    public ConnectionDialog(Connection? original = null)
    {
        Result = original is null ? new() : original with { };
        Background = Ui.Brush("#292D40"); Foreground = Ui.Brush("#E5E8F1"); FontFamily = new("Segoe UI"); FontSize = 13;
        Title = original is null ? "Новое подключение" : "Изменить подключение"; Width = 560; Height = 750; MinHeight = 650; ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(28) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(Ui.Text("БИБЛИОТЕКА СЕРВЕРОВ", 10, "#9B8EBE"));
        panel.Children.Add(Ui.Text(Title, 25, margin: new Thickness(0, 10, 0, 24)));
        var name = Ui.Field(panel, "Название подключения", Result.Name);
        var host = Ui.Field(panel, "Сервер · IP-адрес или домен", Result.Host);
        var user = Ui.Field(panel, "Пользователь", Result.Username);
        var port = Ui.Field(panel, "Порт", Result.Port.ToString());
        panel.Children.Add(Ui.Label("Способ входа"));
        var auth = new ComboBox { Margin = new Thickness(0, 0, 0, 13), ItemsSource = new[] { "Токен · Рутокен / JaCarta (авто)", "Пароль", "Файл SSH-ключа", "SSH-агент Windows" }, SelectedIndex = Array.IndexOf(new[] { "token", "password", "key", "agent" }, Result.Auth == "rutoken" ? "token" : Result.Auth) };
        panel.Children.Add(auth);
        var keyPanel = new StackPanel(); panel.Children.Add(keyPanel);
        var key = Ui.Field(keyPanel, "Закрытый SSH-ключ", Result.KeyPath);
        var pick = Ui.Button("Выбрать файл ключа…", () => { var dialog = new OpenFileDialog { Title = "Закрытый SSH-ключ" }; if (dialog.ShowDialog(this) == true) key.Text = dialog.FileName; });
        keyPanel.Children.Add(pick);
        void UpdateAuth() => keyPanel.Visibility = auth.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        auth.SelectionChanged += (_, _) => UpdateAuth(); UpdateAuth();
        panel.Children.Add(Ui.Text("Пароль, PIN и парольная фраза вводятся в диалоге и не сохраняются.", 11, "#A79BBF", new Thickness(0, 5, 0, 16)));
        var group = Ui.Field(panel, "Группа · необязательно", Result.Group);
        var note = Ui.Field(panel, "Заметка · необязательно", Result.Notes);
        var favorite = new CheckBox { Content = "Добавить в избранное", IsChecked = Result.Favorite }; panel.Children.Add(favorite);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        var save = Ui.Button("Сохранить подключение", () =>
        {
            try
            {
                if (!int.TryParse(port.Text, out var portNumber)) throw new ArgumentException("Укажите числовой порт.");
                var candidate = Result with { Name = name.Text, Host = host.Text, Username = user.Text, Port = portNumber, Auth = new[] { "token", "password", "key", "agent" }[auth.SelectedIndex], KeyPath = key.Text, Group = group.Text, Notes = note.Text, Favorite = favorite.IsChecked == true };
                candidate.Validate(); Result = candidate; DialogResult = true;
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Проверьте подключение", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }, true); save.Margin = new Thickness(10, 0, 0, 0); save.IsDefault = true; buttons.Children.Add(save); panel.Children.Add(buttons);
        Loaded += (_, _) => name.Focus();
    }
}
public static class Ui
{
    public static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    public static TextBlock Text(string value, double size = 13, string color = "#E5E8F1", Thickness? margin = null) => new() { Text = value, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, Margin = margin ?? new() };
    public static TextBlock Label(string value) => Text(value, 11, "#AAB4CA", new Thickness(0, 0, 0, 8));
    public static TextBox Field(Panel panel, string label, string value)
    {
        panel.Children.Add(Label(label)); var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 15) }; panel.Children.Add(box); return box;
    }
    public static Button Button(string title, Action action, bool primary = false)
    {
        var button = new Button { Content = title }; if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "Primary"); button.Click += (_, _) => action(); return button;
    }
    public static Border Card(UIElement content, Thickness? margin = null) => new() { Child = content, Background = Brush("#292D40"), BorderBrush = Brush("#353B50"), BorderThickness = new(1), CornerRadius = new(11), Padding = new(23), Margin = margin ?? new(0, 0, 0, 20) };
}
