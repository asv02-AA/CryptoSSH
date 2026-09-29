using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CryptoSSH;
public partial class MainWindow
{
    private bool drawerAppearance;
    private void SnippetsClick(object sender, RoutedEventArgs e) => ShowSnippets();
    private void HostsClick(object sender, RoutedEventArgs e) => ShowKnownHosts();
    private void AppearanceClick(object sender, RoutedEventArgs e) => ShowAppearance();
    private void TerminalSnippetsClick(object sender, RoutedEventArgs e) => ShowDrawer(false);
    private void TerminalAppearanceClick(object sender, RoutedEventArgs e) => ShowDrawer(true);
    private void RefreshTabs()
    {
        SessionButtons.Children.Clear();
        var library = Ui.Button("▦  Подключения", () => { filter = "all"; ShowConnections(); }); library.Padding = new(14, 7, 14, 7); library.Margin = new(0, 0, 8, 0); library.Background = Ui.Brush(currentPage == "terminal" ? "#24293A" : "#3B425C"); library.BorderThickness = new(0); SessionButtons.Children.Add(library);
        foreach (var session in sessions)
        {
            var active = currentPage == "terminal" && session == selected;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var tab = Ui.Button((session.IsRunning ? "●  " : "○  ") + session.Title, () => SelectSession(session)); tab.ToolTip = session.Title + (session.IsRunning ? " — активна" : " — завершена"); tab.MaxWidth = 220; tab.Padding = new(12, 7, 7, 7); tab.Background = Brushes.Transparent; tab.BorderThickness = new(0); row.Children.Add(tab);
            var close = Ui.Button("×", () => CloseSession(session)); close.ToolTip = "Закрыть вкладку"; close.Padding = new(7); close.Background = Brushes.Transparent; close.BorderThickness = new(0); row.Children.Add(close);
            SessionButtons.Children.Add(new Border { Child = row, Background = Ui.Brush(active ? "#3B425C" : "#24293A"), CornerRadius = new(7), Margin = new(0, 0, 6, 0), BorderThickness = new(0, 0, 0, active ? 2 : 0), BorderBrush = Ui.Brush("#93A9FF") });
        }
        var add = Ui.Button("+", () => Edit()); add.Padding = new(12, 7, 12, 7); add.ToolTip = "Новое подключение"; add.BorderThickness = new(0); add.Background = Brushes.Transparent; SessionButtons.Children.Add(add);
    }
    private void CloseSession(TerminalSession session)
    {
        if (session.IsRunning && !ConfirmDialog.Ask(this, "Закрыть подключение?", $"Сессия «{session.Title}» будет завершена.\nДля повторного входа потребуется подключиться заново.", "Завершить сессию")) { session.FocusTerminal(); return; }
        session.Dispose(); sessions.Remove(session); SessionHost.Children.Remove(session);
        if (selected == session) selected = sessions.LastOrDefault();
        if (currentPage == "terminal") ShowTerminals(); else RefreshTabs();
    }
    private void SelectSession(TerminalSession session) { selected = session; ShowTerminals(); Dispatcher.BeginInvoke(() => session.FocusTerminal()); }
    private void ShowTerminals()
    {
        if (selected is null) { ShowConnections(); return; }
        Navigate("terminal", selected.Title); TerminalTitle.Text = selected.Title;
        if (TerminalDrawer.Visibility == Visibility.Visible) ShowDrawer(drawerAppearance);
        foreach (var session in sessions) session.Visibility = session == selected ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowDrawer(bool appearance)
    {
        drawerAppearance = appearance;
        TerminalDrawer.Visibility = Visibility.Visible; DrawerContent.Children.Clear();
        var header = new DockPanel { Margin = new(0, 0, 0, 18) }; var close = Ui.Button("×", () => { TerminalDrawer.Visibility = Visibility.Collapsed; selected?.FocusTerminal(); }); close.Padding = new(8, 3, 8, 3); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(Ui.Text(appearance ? "Оформление" : "Сохранённые команды", 16)); DrawerContent.Children.Add(header);
        if (appearance) BuildAppearance(DrawerContent, compact: true);
        else
        {
            DrawerContent.Children.Add(Ui.Text("Нажатие на команду сразу выполняет её в активном терминале.", 11, "#9DA8C4", new(0, 0, 0, 18)));
            if (store.Data.Snippets.Count == 0) DrawerContent.Children.Add(Ui.Text("Пока нет команд. Создайте первую в библиотеке.", 12, "#A4AEC5"));
            foreach (var snippet in store.Data.Snippets)
            {
                var body = new StackPanel(); body.Children.Add(Ui.Text(snippet.Name, 13)); var command = Ui.Text(snippet.Command, 11, "#A1ADCA", new(0, 5, 0, 0)); command.MaxHeight = 55; body.Children.Add(command);
                var insert = Ui.Button("", () => _ = Execute(() => { if (selected is null) throw new InvalidOperationException("Выберите подключение."); selected.InsertSnippet(snippet); return Task.CompletedTask; })); insert.Content = body; insert.HorizontalContentAlignment = HorizontalAlignment.Left; insert.Margin = new(0, 0, 0, 10); DrawerContent.Children.Add(insert);
            }
            DrawerContent.Children.Add(Ui.Button("Управление командами", ShowSnippets));
        }
    }
    private void ShowAppearance()
    {
        Navigate("appearance", "Оформление"); Heading("ВАШ ТЕРМИНАЛ", "Оформление", "Цвета, шрифт и курсор. Изменения применяются сразу ко всем вкладкам.");
        var card = new StackPanel(); BuildAppearance(card, compact: false); Page.Children.Add(Ui.Card(card));
    }
    private void BuildAppearance(StackPanel panel, bool compact)
    {
        var previewPanel = panel;
        if (!compact)
        {
            var layout = new Grid(); layout.ColumnDefinitions.Add(new() { Width = new GridLength(340) }); layout.ColumnDefinitions.Add(new());
            var controls = new StackPanel(); previewPanel = new StackPanel { Margin = new(28, 0, 0, 0) };
            layout.Children.Add(controls); Grid.SetColumn(previewPanel, 1); layout.Children.Add(previewPanel); panel.Children.Add(layout); panel = controls;
        }
        var value = store.Data.Appearance;
        panel.Children.Add(Ui.Label("Режим оформления"));
        var mode = new ComboBox { ItemsSource = new[] { "Готовая тема", "Свои цвета" }, SelectedIndex = value.UseCustomColors ? 1 : 0, Margin = new(0, 0, 0, 16) }; panel.Children.Add(mode);
        panel.Children.Add(Ui.Label("Цветовая тема"));
        var theme = new ComboBox { ItemsSource = TerminalPalette.All.Select(p => p.Name).ToArray(), SelectedItem = value.Theme, Margin = new(0, 0, 0, 16) }; panel.Children.Add(theme);
        var custom = new StackPanel { Visibility = value.UseCustomColors ? Visibility.Visible : Visibility.Collapsed };
        var background = new ColorField("Фон терминала", value.BackgroundColor); var foreground = new ColorField("Цвет текста", value.ForegroundColor);
        custom.Children.Add(background); custom.Children.Add(foreground);
        custom.Children.Add(Ui.Text("Цветной вывод команд использует палитру выбранной темы.", 11, "#9DA8C4", new(0, 0, 0, 16))); panel.Children.Add(custom);
        panel.Children.Add(Ui.Label("Шрифт")); var font = new ComboBox { ItemsSource = TerminalAppearance.Fonts, SelectedItem = value.Font, Margin = new(0, 0, 0, 16) }; panel.Children.Add(font);
        var dimensions = new Grid { Margin = new(0, 0, 0, 20) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var left = new StackPanel { Margin = new(0, 0, 8, 0) }; left.Children.Add(Ui.Label("Размер")); var size = new ComboBox { ItemsSource = Enumerable.Range(10, 19).ToArray(), SelectedItem = value.Size }; left.Children.Add(size); dimensions.Children.Add(left);
        var right = new StackPanel(); right.Children.Add(Ui.Label("Курсор")); var cursor = new ComboBox { ItemsSource = TerminalAppearance.Cursors, SelectedItem = value.Cursor }; right.Children.Add(cursor); Grid.SetColumn(right, 1); dimensions.Children.Add(right); panel.Children.Add(dimensions);
        previewPanel.Children.Add(Ui.Text("Предпросмотр", 11, "#A1ADC6", new(0, 0, 0, 8)));
        var preview = new Border { CornerRadius = new(10), Padding = new(compact ? 12 : 24), MinHeight = compact ? 155 : 215 };
        var sample = new StackPanel(); preview.Child = sample; previewPanel.Children.Add(preview);
        var swatches = new WrapPanel { Margin = new(0, 14, 0, 14) }; previewPanel.Children.Add(swatches);
        void Preview(TerminalAppearance settings)
        {
            var palette = TerminalPalette.For(settings); preview.Background = Ui.Brush(palette.Background); sample.Children.Clear(); swatches.Children.Clear();
            foreach (var (text, color) in new[] { ("user@server ~", palette.Colors[2]), ("$ ls -la", palette.Foreground), ("drwxr-xr-x  projects/", palette.Colors[4]), ("-rw-r--r--  README.md", palette.Foreground), ("$ " + (settings.Cursor == "Блок" ? "█" : settings.Cursor == "Полоса" ? "▏" : "_"), palette.Colors[6]) })
            { var line = Ui.Text(text, settings.Size, color, new(0, 0, 0, 5)); line.FontFamily = new(settings.Font); line.TextWrapping = TextWrapping.NoWrap; line.TextTrimming = TextTrimming.CharacterEllipsis; sample.Children.Add(line); }
            foreach (var color in palette.Colors.Take(8)) swatches.Children.Add(new Border { Width = compact ? 23 : 36, Height = 8, Background = Ui.Brush(color), CornerRadius = new(3), Margin = new(0, 0, 5, 0) });
        }
        void Save()
        {
            if (theme.SelectedItem is not string t || font.SelectedItem is not string f || size.SelectedItem is not int s || cursor.SelectedItem is not string c) return;
            _ = Execute(() => { var settings = new TerminalAppearance { Theme = t, Font = f, Size = s, Cursor = c, UseCustomColors = mode.SelectedIndex == 1, BackgroundColor = background.Value, ForegroundColor = foreground.Value }; settings.Validate(); store.Data.Appearance = settings; store.Save(); foreach (var session in sessions) session.ApplyAppearance(settings); Preview(settings); return Task.CompletedTask; });
        }
        mode.SelectionChanged += (_, _) => { custom.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; Save(); };
        background.Changed += Save; foreground.Changed += Save;
        theme.SelectionChanged += (_, _) => Save(); font.SelectionChanged += (_, _) => Save(); size.SelectionChanged += (_, _) => Save(); cursor.SelectionChanged += (_, _) => Save(); Preview(value);
        previewPanel.Children.Add(Ui.Text("Сохраняется автоматически. Ctrl+Shift+C — копировать, Ctrl+Shift+V — вставить.", 11, "#9DA8C4"));
    }
    private void ShowSnippets()
    {
        Navigate("snippets", "Команды"); Heading("БИБЛИОТЕКА", "Сохранённые команды", "Частые команды доступны в боковой панели каждого терминала.", Ui.Button("+  Новая команда", () => EditSnippet(), true));
        if (store.Data.Snippets.Count == 0) Page.Children.Add(Ui.Text("Сохраните, например, команду просмотра журналов или проверки свободного места.", 13, "#A1ADC6"));
        foreach (var snippet in store.Data.Snippets)
        {
            var row = new DockPanel(); var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
            actions.Children.Add(Ui.Button("Изменить", () => EditSnippet(snippet)));
            var delete = Ui.Button("Удалить", () => { if (MessageBox.Show(this, $"Удалить команду «{snippet.Name}»?", "Команды", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { store.Data.Snippets.Remove(snippet); store.Save(); ShowSnippets(); } }); delete.Margin = new(8, 0, 0, 0); actions.Children.Add(delete);
            var details = new StackPanel(); details.Children.Add(Ui.Text(snippet.Name, 16)); var command = Ui.Text(snippet.Command, 12, "#A1B5E8", new(0, 8, 18, 0)); command.FontFamily = new("Consolas"); details.Children.Add(command); row.Children.Add(details); Page.Children.Add(Ui.Card(row));
        }
    }
    private void EditSnippet(Snippet? original = null)
    {
        var dialog = new Window { Owner = this, Title = original is null ? "Новая команда" : "Изменить команду", Width = 570, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(24) }; dialog.Content = panel;
        var name = Ui.Field(panel, "Название", original?.Name ?? ""); var command = Ui.Field(panel, "Команда (одна строка)", original?.Command ?? ""); command.FontFamily = new("Consolas");
        panel.Children.Add(Ui.Text("Команды хранятся локально в открытом виде. Не добавляйте пароли и секреты.", 11, "#9DA8C4", new(0, 0, 0, 18)));
        panel.Children.Add(Ui.Button("Сохранить", () => { try { var snippet = new Snippet { Id = original?.Id ?? Guid.NewGuid(), Name = name.Text, Command = command.Text }; snippet.Validate(); var index = store.Data.Snippets.FindIndex(s => s.Id == snippet.Id); if (index < 0) store.Data.Snippets.Add(snippet); else store.Data.Snippets[index] = snippet; store.Save(); dialog.Close(); ShowSnippets(); } catch (Exception e) { MessageBox.Show(dialog, e.Message, "Команда"); } }, true)); dialog.ShowDialog();
    }
    private void ShowKnownHosts()
    {
        Navigate("hosts", "Настройки / Ключи серверов"); Page.Children.Add(Ui.Button("← Настройки", ShowSettings)); Heading("ПРОВЕРКА ПОДЛИННОСТИ", "Ключи серверов", "Публичные ключи серверов, которым вы доверились при подключении.");
        Page.Children.Add(Ui.Text("Новые ключи серверов принимаются и сохраняются автоматически. Ваш закрытый ключ с токена не извлекается.", 13, "#A8B4CE", new(0, 0, 0, 20)));
        Page.Children.Add(new TextBox { IsReadOnly = true, AcceptsReturn = true, Text = File.Exists(store.KnownHosts) ? File.ReadAllText(store.KnownHosts) : "Пока нет подтверждённых серверов.", FontFamily = new("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Height = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Page.Children.Add(Ui.Text("Если ключ изменится, появится предупреждение. Можно продолжить и сохранить новый ключ либо отменить подключение.", 11, "#9DA8C4", new(0, 16, 0, 0)));
    }
}
