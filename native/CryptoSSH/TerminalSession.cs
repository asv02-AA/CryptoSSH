using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CryptoSSH;
internal sealed class TerminalSession : Grid, IDisposable
{
    public string Title { get; }
    public bool IsRunning { get; private set; } = true;
    public string? Error { get; private set; }
    public event Action? Finished;
    public event Action<string>? Output;
    internal TerminalSurface Surface { get; } = new() { Margin = new(12, 2, 10, 12) };
    private readonly TextBlock status = new() { Text = "Подключение…", Foreground = Ui.Brush("#9DA8C4"), FontSize = 11, Margin = new Thickness(14, 10, 0, 10) };
    private readonly ScrollBar scroll = new() { Orientation = Orientation.Vertical, Minimum = 0, Width = 11 };
    private readonly NativeSshTransport? transport;
    private bool started, disposed, updatingScroll;
    public TerminalSession(Connection profile, Store store, SshPrompts? prompts = null) : this(profile.Name, store.Data.Appearance)
    {
        prompts ??= new SshPrompts(
            (title, message, echoed) => Dispatcher.Invoke(() => disposed ? null : CredentialDialog.Ask(Window.GetWindow(this), title, message, echoed)),
            message => Dispatcher.Invoke(() => !disposed && ConfirmDialog.Ask(Window.GetWindow(this), "Ключ сервера изменился", message, "Продолжить и сохранить")),
            keys => Dispatcher.Invoke(() => { if (disposed) return null; var dialog = new TokenChoiceDialog(Window.GetWindow(this), keys); return dialog.ShowDialog() == true ? dialog.Value : null; }),
            devices => Dispatcher.Invoke(() => { if (disposed) return null; var dialog = new TokenDeviceDialog(Window.GetWindow(this), profile.Name, devices); return dialog.ShowDialog() == true ? dialog.Value : null; }));
        transport = new NativeSshTransport(profile, store, prompts);
        transport.Output += text => Dispatcher.Invoke(() => { if (!disposed) { Surface.Feed(text); Output?.Invoke(text); } });
        transport.Ready += () => Dispatcher.Invoke(() => { if (!disposed) { status.Text = ""; status.Visibility = Visibility.Collapsed; Surface.IsReadOnly = false; Surface.Focus(); transport.Resize(Surface.Buffer.Columns, Surface.Buffer.Rows); } });
        Surface.Input += text => { try { transport.Send(text); } catch (Exception e) { status.Visibility = Visibility.Visible; status.Text = e.Message; } };
        Surface.Resized += (cols, rows) => transport.Resize(cols, rows);
        Loaded += async (_, _) =>
        {
            if (started || disposed) return; started = true;
            int cols = Surface.Buffer.Columns, rows = Surface.Buffer.Rows;
            try { await Task.Run(() => transport.Run(cols, rows)); }
            catch (Exception e) when (disposed || e is OperationCanceledException) { }
            catch (Exception e) { Error = e.Message; status.Visibility = Visibility.Visible; status.Text = "Ошибка: " + e.Message; status.TextWrapping = TextWrapping.Wrap; }
            finally { IsRunning = false; Surface.IsReadOnly = true; Surface.InvalidateVisual(); if (Error is null) { status.Visibility = Visibility.Visible; status.Text = "Сессия завершена"; } Finished?.Invoke(); }
        };
    }
    // In-process terminal fixture, without a shell process or network connection.
    internal TerminalSession(string title, TerminalAppearance appearance)
    {
        Title = title;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new()); Children.Add(status);
        var area = new Grid(); area.ColumnDefinitions.Add(new()); area.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        area.Children.Add(Surface); SetColumn(scroll, 1); area.Children.Add(scroll); SetRow(area, 1); Children.Add(area);
        scroll.ValueChanged += (_, _) => { if (!updatingScroll) Surface.ScrollTo((int)(scroll.Maximum - scroll.Value)); };
        Surface.ScrollChanged += () => { updatingScroll = true; scroll.Maximum = Surface.MaxScroll; scroll.ViewportSize = Surface.Buffer.Rows; scroll.Value = scroll.Maximum - Surface.ScrollOffset; updatingScroll = false; };
        ApplyAppearance(appearance); Surface.IsReadOnly = true;
    }
    internal void FeedFixture(string text) { Surface.Feed(text); status.Text = "Собственный терминал CryptoSSH · тестовый экран"; }
    public void ApplyAppearance(TerminalAppearance value) { value.Validate(); Background = Ui.Brush(TerminalPalette.For(value).Background); Surface.ApplyAppearance(value); }
    public void FocusTerminal() => Surface.Focus();
    public void InsertSnippet(Snippet snippet)
    {
        snippet.Validate(); if (!IsRunning || transport?.Connected != true) throw new InvalidOperationException("Нет подключённой сессии для вставки команды.");
        transport.Send(snippet.Command + "\r"); Surface.Focus();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; IsRunning = false; Surface.IsReadOnly = true; Surface.Dispose(); transport?.Dispose();
    }
}
