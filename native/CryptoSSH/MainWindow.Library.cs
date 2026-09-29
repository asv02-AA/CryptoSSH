using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.RegularExpressions;

namespace CryptoSSH;
public partial class MainWindow
{
    private bool sortRecent;
    private void ShowConnections()
    {
        Navigate("connections", "Подключения");
        var searchRow = new DockPanel { Height = 42, Margin = new(0, 0, 0, 18) };
        var quick = Ui.Button("Быстрое подключение", QuickConnect); DockPanel.SetDock(quick, Dock.Right); quick.Margin = new(10, 0, 0, 0); searchRow.Children.Add(quick);
        var query = new TextBox { Text = search, ToolTip = "Поиск по имени, адресу, пользователю или группе", VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(query, "Поиск подключений");
        var queryGrid = new Grid(); queryGrid.Children.Add(query);
        var placeholder = Ui.Text("Найти подключение, адрес или группу…", 13, "#9DA8C4", new(12, 0, 0, 0)); placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false; queryGrid.Children.Add(placeholder);
        searchRow.Children.Add(queryGrid); Page.Children.Add(searchRow);
        var tools = new DockPanel { Margin = new(0, 0, 0, 25) };
        var order = Ui.Button(sortRecent ? "По последнему запуску ↓" : "По имени ↓", () => { sortRecent = !sortRecent; ShowConnections(); }); DockPanel.SetDock(order, Dock.Right); tools.Children.Add(order);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Ui.Button("+  Новый сервер", () => Edit(), true));
        var all = Ui.Button("Все", () => { filter = "all"; ShowConnections(); }); all.Margin = new(10, 0, 6, 0); actions.Children.Add(all);
        actions.Children.Add(Ui.Button("☆  Избранное", () => { filter = "favorites"; ShowConnections(); })); tools.Children.Add(actions); Page.Children.Add(tools);
        var results = new StackPanel(); Page.Children.Add(results);
        void Render()
        {
            placeholder.Visibility = query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            results.Children.Clear();
            var groups = store.Data.Connections.Where(p => p.Group.Length > 0).GroupBy(p => p.Group).OrderBy(g => g.Key).ToList();
            if (groups.Count > 0 && search.Length == 0 && filter == "all")
            {
                results.Children.Add(Ui.Text("Группы", 13, margin: new(0, 0, 0, 14)));
                var groupCards = new WrapPanel { Margin = new(0, 0, 0, 14) };
                foreach (var group in groups)
                {
                    var body = new DockPanel(); var badge = Badge("▦", "#126A91"); DockPanel.SetDock(badge, Dock.Left); body.Children.Add(badge);
                    var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; details.Children.Add(Ui.Text(group.Key, 13)); details.Children.Add(Ui.Text($"Серверов: {group.Count()}", 10, "#9DA8C4", new(0, 3, 0, 0))); body.Children.Add(details);
                    var button = Ui.Button("", () => { filter = group.Key; ShowConnections(); }); button.Content = body; button.Width = 262; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Background = Ui.Brush("#292D40"); button.BorderThickness = new(0); button.Padding = new(12); button.Margin = new(0, 0, 12, 12); groupCards.Children.Add(button);
                }
                results.Children.Add(groupCards);
            }
            var profiles = store.Data.Connections.Where(p => (filter == "all" || filter == "favorites" && p.Favorite || p.Group == filter) && $"{p.Name} {p.Host} {p.Username} {p.Group}".Contains(search, StringComparison.OrdinalIgnoreCase));
            profiles = sortRecent ? profiles.OrderByDescending(p => p.LastUsed) : profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
            var list = profiles.ToList();
            results.Children.Add(Ui.Text((filter == "all" ? "Серверы" : filter == "favorites" ? "Избранное" : filter) + $"   /   {list.Count}", 13, margin: new(0, 0, 0, 14)));
            if (list.Count == 0)
            {
                var empty = new StackPanel { Margin = new(30, 50, 30, 50), HorizontalAlignment = HorizontalAlignment.Center };
                empty.Children.Add(Ui.Text(store.Data.Connections.Count == 0 ? "Ваши серверы — в одном месте" : "Ничего не найдено", 24));
                empty.Children.Add(Ui.Text("Сохраните адрес и способ входа. Открытые подключения появятся сверху.", 12, "#9DA8C4", new(0, 12, 0, 24)));
                var add = Ui.Button("+  Добавить подключение", () => Edit(), true); add.HorizontalAlignment = HorizontalAlignment.Left; empty.Children.Add(add); results.Children.Add(empty);
            }
            else { var cards = new WrapPanel(); foreach (var p in list) cards.Children.Add(ConnectionCard(p)); results.Children.Add(cards); }
        }
        query.TextChanged += (_, _) => { search = query.Text; Render(); }; Render();
        GroupList.Children.Clear();
        foreach (var group in store.Data.Connections.Select(p => p.Group).Where(g => g.Length > 0).Distinct().Order().Take(7))
        { var button = Ui.Button("▦   " + group, () => { filter = group; ShowConnections(); }); button.SetResourceReference(StyleProperty, "Nav"); button.ToolTip = group; GroupList.Children.Add(button); }
    }
    private static Border Badge(string label, string color) => new()
    {
        Width = 38, Height = 38, CornerRadius = new(10), Background = Ui.Brush(color), Margin = new(0, 0, 12, 0),
        Child = new TextBlock { Text = label, FontSize = 19, FontFamily = new("Consolas"), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
    };
    private Border ConnectionCard(Connection connection)
    {
        var row = new DockPanel(); var menu = new ContextMenu();
        void Action(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Action("Подключиться", () => _ = Execute(() => { Connect(connection); return Task.CompletedTask; }));
        Action("Изменить", () => Edit(connection));
        Action("Дублировать", () => Edit(connection with { Id = Guid.NewGuid(), Name = connection.Name.Length > 90 ? connection.Name[..90] + " — копия" : connection.Name + " — копия", LastUsed = null }));
        Action(connection.Favorite ? "Убрать из избранного" : "В избранное", () => { store.Upsert(connection with { Favorite = !connection.Favorite }); ShowConnections(); });
        Action("Удалить", () => { if (MessageBox.Show(this, $"Удалить подключение «{connection.Name}»?", "Подключение", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { store.Delete(connection.Id); ShowConnections(); } });
        var more = Ui.Button("⋯", () => { menu.IsOpen = true; }); more.Padding = new(5); more.BorderThickness = new(0); more.Background = Brushes.Transparent; more.ToolTip = "Действия с подключением"; more.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(more, Dock.Right); row.Children.Add(more); menu.PlacementTarget = more;
        var connectBody = new DockPanel(); var badge = Badge(connection.Auth is "rutoken" or "token" ? "◇" : ">_", connection.Auth is "rutoken" or "token" ? "#7060B8" : "#267A9B"); DockPanel.SetDock(badge, Dock.Left); connectBody.Children.Add(badge);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; var title = Ui.Text((connection.Favorite ? "★  " : "") + connection.Name, 13); title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; info.Children.Add(title);
        var address = Ui.Text($"{connection.Username}@{connection.Host}", 10, "#9AA6C2", new(0, 4, 0, 0)); address.TextWrapping = TextWrapping.NoWrap; address.TextTrimming = TextTrimming.CharacterEllipsis; info.Children.Add(address); connectBody.Children.Add(info);
        var connect = Ui.Button("", () => _ = Execute(() => { Connect(connection); return Task.CompletedTask; })); connect.Content = connectBody; connect.Padding = new(0); connect.BorderThickness = new(0); connect.Background = Brushes.Transparent; connect.HorizontalContentAlignment = HorizontalAlignment.Stretch; connect.ToolTip = $"{connection.Name}\n{connection.Username}@{connection.Host}:{connection.Port}\n{AuthName(connection.Auth)}\n{connection.Notes}"; row.Children.Add(connect);
        return new Border { Child = row, ContextMenu = menu, Width = 262, Height = 72, CornerRadius = new(12), Background = Ui.Brush("#292D40"), Padding = new(12), Margin = new(0, 0, 12, 12) };
    }
    internal static Connection ParseQuickConnection(string address)
    {
        var match = Regex.Match(address.Trim(), @"^(?<user>[a-zA-Z0-9_][a-zA-Z0-9_.\\-]*)@(?<host>\[[0-9a-fA-F:]+\]|[a-zA-Z0-9][a-zA-Z0-9._-]*)(?::(?<port>[0-9]{1,5}))?$");
        if (!match.Success) throw new ArgumentException("Формат: user@host:22 или user@[IPv6]:22.");
        var host = match.Groups["host"].Value.Trim('[', ']');
        var result = new Connection { Name = host, Host = host, Username = match.Groups["user"].Value, Port = match.Groups["port"].Success ? int.Parse(match.Groups["port"].Value) : 22, Auth = "password" };
        result.Validate(); return result;
    }
    private void QuickConnect()
    {
        var dialog = new Window { Owner = this, Title = "Быстрое подключение", Width = 510, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(24) }; dialog.Content = panel;
        panel.Children.Add(Ui.Text("Адрес сервера", 20)); var address = Ui.Field(panel, "user@host:22", "");
        panel.Children.Add(Ui.Text("Далее выберите способ входа и сохраните подключение.", 12, "#9DA8C4", new(0, 0, 0, 18)));
        void Open() { try { var profile = ParseQuickConnection(address.Text); dialog.Close(); Edit(profile); } catch (Exception e) { MessageBox.Show(dialog, e.Message, "Адрес сервера"); } }
        panel.Children.Add(Ui.Button("Продолжить", Open, true)); address.KeyDown += (_, e) => { if (e.Key == Key.Enter) Open(); }; dialog.Loaded += (_, _) => address.Focus(); dialog.ShowDialog();
    }
}
