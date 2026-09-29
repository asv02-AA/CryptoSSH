using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CryptoSSH;
public partial class MainWindow : Window
{
    private readonly Store store;
    private string currentPage = "connections", filter = "all", search = "";
    private readonly List<TerminalSession> sessions = [];
    private TerminalSession? selected;
    private bool tokenBusy, closing;
    public MainWindow(Store store)
    {
        this.store = store; InitializeComponent(); ShowConnections();
        SourceInitialized += (_, _) => { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); };
        Closing += OnClosing;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!closing && sessions.Any(s => s.IsRunning) && !ConfirmDialog.Ask(this, "Закрыть CryptoSSH?", "Все активные подключения будут завершены.\nСохранённые профили останутся в библиотеке.", "Завершить и выйти")) { args.Cancel = true; return; }
        closing = true; tokenTimer?.Stop(); tokenTimer = null; foreach (var session in sessions) session.Dispose();
    }
    private void ConnectionsClick(object sender, RoutedEventArgs e) { filter = "all"; ShowConnections(); }
    private void FavoritesClick(object sender, RoutedEventArgs e) { filter = "favorites"; ShowConnections(); }
    private void TokenClick(object sender, RoutedEventArgs e) => ShowToken();
    private void SettingsClick(object sender, RoutedEventArgs e) => ShowSettings();
    private void Navigate(string page, string name)
    {
        tokenTimer?.Stop(); tokenTimer = null; TokenBusyChanged = null; currentPage = page; Breadcrumb.Text = name; Page.Children.Clear();
        PageScroll.Visibility = page == "terminal" ? Visibility.Collapsed : Visibility.Visible;
        TerminalArea.Visibility = page == "terminal" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { ConnectionsNav, TokenNav, SettingsNav, SnippetsNav, AppearanceNav })
        { button.Background = Brushes.Transparent; button.Foreground = Ui.Brush("#B7C0D6"); }
        var active = page switch { "token" => TokenNav, "settings" => SettingsNav, "snippets" => SnippetsNav, "hosts" => SettingsNav, "appearance" => AppearanceNav, "connections" => ConnectionsNav, _ => null };
        if (active is not null) { active.Background = Ui.Brush("#3C435D"); active.Foreground = Brushes.White; }
        RefreshTabs();
    }
    private void Heading(string eyebrow, string title, string subtitle, Button? action = null)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 28) };
        if (action is not null) { DockPanel.SetDock(action, Dock.Right); action.VerticalAlignment = VerticalAlignment.Center; dock.Children.Add(action); }
        var stack = new StackPanel(); stack.Children.Add(Ui.Text(eyebrow, 9, "#8C7FA6")); stack.Children.Add(Ui.Text(title, 25, margin: new Thickness(0, 10, 0, 9))); stack.Children.Add(Ui.Text(subtitle, 12, "#929DB3")); dock.Children.Add(stack); Page.Children.Add(dock);
    }
    private async Task Execute(Func<Task> action)
    {
        Status.Text = "Выполняется операция…";
        try { await action(); Status.Text = "Готов к работе"; }
        catch (Exception e) { Status.Text = "Операция не выполнена"; if (!closing) MessageBox.Show(this, e.Message, "CryptoSSH", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async Task TokenAction(Func<Task> action)
    {
        if (tokenBusy) return;
        tokenBusy = true; TokenProgress.Visibility = Visibility.Visible; TokenBusyChanged?.Invoke(); try { await action(); } finally { tokenBusy = false; TokenProgress.Visibility = Visibility.Collapsed; TokenBusyChanged?.Invoke(); }
    }
    private void Edit(Connection? connection = null)
    {
        var dialog = new ConnectionDialog(connection) { Owner = this };
        if (dialog.ShowDialog() == true) { store.Upsert(dialog.Result); ShowConnections(); Status.Text = "Подключение сохранено"; }
    }
    private static string AuthName(string auth) => auth switch { "token" or "rutoken" => "Токен · автоматическое определение", "password" => "Пароль", "key" => "Файл SSH-ключа", _ => "SSH-агент Windows" };
    private void Connect(Connection p)
    {
        var settings = store.Settings;
        if (p.Auth == "key") Rules.RequireFile(p.KeyPath, "Закрытый SSH-ключ");
        AddSession(new TerminalSession(p, store));
        store.Upsert(p with { LastUsed = DateTimeOffset.Now });
    }
    private TerminalSession AddSession(TerminalSession session)
    {
        if (sessions.Count >= 12) { session.Dispose(); throw new InvalidOperationException("Закройте один из терминалов: допустимо не более 12 вкладок."); }
        sessions.Add(session); SessionHost.Children.Add(session);
        session.Finished += () => { if (!closing) { Status.Text = "Терминал завершён: " + session.Title; RefreshTabs(); } };
        SelectSession(session); return session;
    }
    private void ShowSettings()
    {
        Navigate("settings", "Настройки"); Heading("ПОД ВАШИ ЗАДАЧИ", "Настройки", "Драйвер нужного токена устанавливается отдельно. Для входа по паролю или файлу ключа он не нужен.");
        var panel = new StackPanel(); panel.Children.Add(Ui.Text("Компоненты подключения", 18)); panel.Children.Add(Ui.Text("Укажите одну или обе библиотеки PKCS#11 для Windows x64. Оставьте поле пустым, чтобы отключить ненужного производителя.", 12, "#909DB4", new(0, 12, 0, 24)));
        TextBox FileField(string name, string value, string pattern)
        {
            panel.Children.Add(Ui.Label(name + (string.IsNullOrWhiteSpace(value) ? "   ·   Не подключён" : File.Exists(value) ? "   ·   Доступен" : "   ·   Не найден")));
            var row = new DockPanel { Margin = new(0, 0, 0, 18) }; var input = new TextBox { Text = value };
            var browse = Ui.Button("…", () => { var dialog = new OpenFileDialog { Filter = pattern }; if (dialog.ShowDialog(this) == true) input.Text = dialog.FileName; }); browse.Margin = new(8, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse);
            var clear = Ui.Button("Очистить", () => input.Clear()); clear.Margin = new(8, 0, 0, 0); DockPanel.SetDock(clear, Dock.Right); row.Children.Add(clear); row.Children.Add(input); panel.Children.Add(row); return input;
        }
        panel.Children.Add(Ui.Text("Встроенный SSH-движок: SSH.NET. Собственный терминал CryptoSSH. Внешние SSH-процессы не запускаются.", 12, "#A1B5E8", new(0, 0, 0, 18)));
        var provider = FileField("Рутокен · PKCS#11", store.Settings.Provider, "PKCS#11 library|*.dll");
        var jacarta = FileField("JaCarta · PKCS#11", store.Settings.JaCartaProvider, "PKCS#11 library|*.dll");
        var actions = new WrapPanel { Margin = new(0, 9, 0, 0) }; actions.Children.Add(Ui.Button("Сохранить настройки", () => _ = Execute(() => { store.Data.CustomSettings = new() { Provider = ClientSettings.OptionalLibrary(provider.Text, "Рутокен"), JaCartaProvider = ClientSettings.OptionalLibrary(jacarta.Text, "JaCarta") }; store.Save(); ShowSettings(); return Task.CompletedTask; }), true));
        var reset = Ui.Button("Найти установленные библиотеки", () => { store.Data.CustomSettings = null; store.Save(); ShowSettings(); }); reset.Margin = new(10, 0, 0, 0); actions.Children.Add(reset); panel.Children.Add(actions); Page.Children.Add(Ui.Card(panel));
        var token = new StackPanel(); token.Children.Add(Ui.Text("Встроенная поддержка Рутокен и JaCarta", 18)); token.Children.Add(Ui.Text("Установите официальные драйверы Рутокен или Единый Клиент JaCarta с сайта производителя. Системные библиотеки определяются автоматически; для другой папки выберите DLL выше. Достаточно библиотеки одного производителя. Подключённые устройства определяются автоматически.", 12, "#909DB4", new(0, 12, 0, 0))); Page.Children.Add(Ui.Card(token));
        var security = new StackPanel(); security.Children.Add(Ui.Text("Данные и безопасность", 18)); security.Children.Add(Ui.Text("Пароли, PIN-коды и парольные фразы не сохраняются. Новые ключи серверов сохраняются автоматически. При изменении ключа появится предупреждение с возможностью продолжить подключение.", 12, "#909DB4", new(0, 12, 0, 15))); security.Children.Add(Ui.Text("Папка данных: " + store.DirectoryPath, 11, "#B0A3CB", new(0, 0, 0, 15))); security.Children.Add(Ui.Text("Независимый клиент. Рутокен — продукт компании «Актив», JaCarta — «Аладдин Р.Д.». Лицензии сторонних компонентов находятся в папке приложения.", 10, "#72819A")); security.Children.Add(Ui.Button("Ключи серверов", ShowKnownHosts)); Page.Children.Add(Ui.Card(security));
    }
    private void Capture(string path, FrameworkElement? element = null)
    {
        UpdateLayout(); var content = element ?? (FrameworkElement)Content; content.UpdateLayout(); var target = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); target.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
