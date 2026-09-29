using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CryptoSSH;

public partial class MainWindow
{
    private DispatcherTimer? tokenTimer;
    private Action? TokenBusyChanged;
    private void ShowToken()
    {
        Navigate("token", "Токены");
        Heading("АППАРАТНАЯ ЗАЩИТА", "Рутокен и JaCarta", "Подключите токен — CryptoSSH автоматически определит устройство и его возможности.");
        var devicePanel = new StackPanel();
        devicePanel.Children.Add(Ui.Label("Подключённое устройство"));
        var devices = new ComboBox { MinWidth = 400, Margin = new(0, 0, 0, 12) }; devicePanel.Children.Add(devices);
        var details = Ui.Text("Поиск токенов…", 12, "#9DABC5"); devicePanel.Children.Add(details);
        var errors = Ui.Text("", 11, "#E9B574", new(0, 12, 0, 0)); devicePanel.Children.Add(errors);
        Page.Children.Add(Ui.Card(devicePanel));

        var activity = new StackPanel { Visibility = tokenBusy ? Visibility.Visible : Visibility.Collapsed, Margin = new(0, 0, 0, 18) };
        activity.Children.Add(Ui.Text("Операция с токеном… Не извлекайте устройство.", 13, "#A1B5E8", new(0, 0, 0, 10)));
        activity.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5, Foreground = Ui.Brush("#8194EB"), Background = Ui.Brush("#30364B") });
        Page.Children.Add(activity);

        var form = new StackPanel(); form.Children.Add(Ui.Text("Создать SSH-ключ", 18));
        form.Children.Add(Ui.Text("Закрытый ключ остаётся внутри выбранного устройства. PIN не сохраняется.", 12, "#909DB4", new(0, 12, 0, 18)));
        var label = Ui.Field(form, "Метка ключа", "MySSHKey");
        var id = Ui.Field(form, "ID ключа (HEX)", "01");
        var rsaLabel = Ui.Label("Длина RSA, поддерживаемая устройством"); form.Children.Add(rsaLabel);
        var unsupported = Ui.Text("", 13, "#E9B574", new(0, 0, 0, 14)); form.Children.Add(unsupported);
        var useRsa = Ui.Button("Выбрать RSA-слот этого токена", () =>
        {
            if (devices.SelectedItem is TokenDevice current)
                devices.SelectedItem = devices.Items.OfType<TokenDevice>().FirstOrDefault(d => d.Serial == current.Serial && d.ProviderPath == current.ProviderPath && d.RsaSizes.Length > 0);
        }); form.Children.Add(useRsa);
        var bits = new ComboBox { Margin = new(0, 0, 0, 18) }; form.Children.Add(bits);
        var result = Ui.Text("", 12, "#A1B5E8", new(0, 12, 0, 0));
        var generate = Ui.Button("＋  Создать SSH-ключ", () => _ = Execute(() => TokenAction(async () =>
        {
            if (devices.SelectedItem is not TokenDevice device || bits.SelectedItem is not int size)
                throw new InvalidOperationException("Выберите токен с поддержкой генерации RSA.");
            string keyId = id.Text.Trim(), keyLabel = label.Text.Trim();
            Rules.ValidateGeneration(device.Slot.ToString(), keyId, keyLabel, size);
            if (!ConfirmDialog.Ask(this, "Создать ключ на токене?", $"{device}\nRSA {size}, ID {keyId}, метка {keyLabel}.\nЗакрытый ключ будет создан внутри этого устройства.", "Создать ключ")) return;
            var pin = CredentialDialog.Ask(this, "PIN " + device.Vendor, $"{device.Label}\nСерийный номер: {device.Serial}");
            if (pin is null) return;
            try
            {
                await Task.Run(() => TokenProvider.Get(device.ProviderPath).Generate(device, keyId, keyLabel, size, pin));
                result.Text = "Ключ создан. Экспортируйте публичную часть и добавьте её в authorized_keys на сервере.";
            }
            finally { pin = ""; }
        })), true);
        generate.IsEnabled = false;
        form.Children.Add(generate); form.Children.Add(result); Page.Children.Add(Ui.Card(form));

        var exportStatus = Ui.Text("", 12, "#A1B5E8", new(0, 10, 0, 0));
        var export = Ui.Button("↓  Экспорт публичного ключа", () => _ = Execute(() => TokenAction(async () =>
        {
            if (devices.SelectedItem is not TokenDevice device) throw new InvalidOperationException("Подключите и выберите токен.");
            var keys = await Task.Run(() => TokenProvider.Get(device.ProviderPath).Keys(device));
            if (keys.Count == 0) throw new InvalidOperationException("На выбранном токене нет публичных RSA SSH-ключей.");
            var choice = new TokenChoiceDialog(this, keys, "Экспорт существующего SSH-ключа", "Сохранить публичную часть…");
            if (choice.ShowDialog() != true || choice.Value is not TokenKey key) return;
            var dialog = new SaveFileDialog { Title = "Сохранить публичный SSH-ключ", FileName = (device.Vendor == "JaCarta" ? "jacarta-" : "rutoken-") + Convert.ToHexString(key.Id) + ".pub", Filter = "SSH public key (*.pub)|*.pub", OverwritePrompt = true };
            if (dialog.ShowDialog(this) == true) { Rules.AtomicWrite(dialog.FileName, key.PublicLine); exportStatus.Text = "Публичная часть сохранена: " + dialog.FileName + "\nДобавьте строку из файла в ~/.ssh/authorized_keys на новом сервере."; }
        })));
        export.IsEnabled = false;
        export.Margin = new(0, 16, 0, 0); export.HorizontalAlignment = HorizontalAlignment.Left;
        devicePanel.Children.Add(export);
        devicePanel.Children.Add(Ui.Text("Для уже созданных ключей: выберите ключ и сохраните .pub для нового сервера. Повторная генерация не нужна.", 12, "#909DB4", new(0, 10, 0, 0)));
        devicePanel.Children.Add(exportStatus);

        void UpdateSelection()
        {
            var device = devices.SelectedItem as TokenDevice;
            bool supported = device?.RsaSizes.Length > 0;
            rsaLabel.Visibility = bits.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
            unsupported.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
            unsupported.Text = device is null ? "Выберите устройство для генерации ключа." : !device.CanSign
                ? $"{device.Model}: RSA недоступен. Этот слот предназначен для других алгоритмов (например, ГОСТ). Для SSH выберите RSA-слот JaCarta Laser или другой совместимый токен."
                : "Генерация RSA на этом слоте недоступна. Проверьте инициализацию и доступность записи на устройство.";
            useRsa.Visibility = device is not null && !supported && devices.Items.OfType<TokenDevice>().Any(d => d.Serial == device.Serial && d.ProviderPath == device.ProviderPath && d.RsaSizes.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
            int previous = bits.SelectedItem is int n ? n : 2048;
            bits.ItemsSource = device?.RsaSizes ?? [];
            bits.SelectedItem = device?.RsaSizes.Contains(previous) == true ? previous : device?.RsaSizes.FirstOrDefault();
            generate.IsEnabled = device?.RsaSizes.Length > 0; export.IsEnabled = device is { Initialized: true, CanSign: true };
            details.Text = device is null ? "Токены не найдены. Подключите Рутокен или JaCarta к USB-порту. Список обновляется автоматически." :
                $"{device.Model} · серийный номер {device.Serial}\n" + (!device.Initialized ? "Устройство не инициализировано. Инициализация выполняется средствами производителя." :
                !device.CanSign ? "Этот токен не поддерживает RSA-подпись для SSH. Для ГОСТ-ключей данный способ SSH-входа недоступен." :
                device.RsaSizes.Length == 0 ? "RSA-подпись доступна. Генерация нового RSA-ключа недоступна." : "RSA: " + string.Join(", ", device.RsaSizes) + " бит. Готов к работе.");
        }
        devices.SelectionChanged += (_, _) => UpdateSelection();
        TokenBusyChanged = () =>
        {
            activity.Visibility = tokenBusy ? Visibility.Visible : Visibility.Collapsed;
            devicePanel.IsEnabled = form.IsEnabled = !tokenBusy;
        };
        TokenBusyChanged(); UpdateSelection();
        bool scanning = false; string? snapshot = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; tokenTimer = timer;
        async Task Refresh()
        {
            if (scanning || tokenBusy || closing || tokenTimer != timer) return;
            scanning = true;
            try
            {
                var scan = await Task.Run(() => TokenCatalog.Scan(store.Settings));
                if (closing || tokenTimer != timer || tokenBusy) return;
                errors.Text = string.Join("\n", scan.Errors);
                var nextSnapshot = System.Text.Json.JsonSerializer.Serialize(scan.Devices);
                if (nextSnapshot == snapshot) return;
                snapshot = nextSnapshot;
                var previous = (devices.SelectedItem as TokenDevice)?.Identity;
                devices.ItemsSource = scan.Devices;
                devices.SelectedItem = TokenCatalog.SelectDevice(scan.Devices, previous);
                UpdateSelection();
                if (scan.Devices.Count > 1 && devices.SelectedItem is null) details.Text = "Обнаружено несколько устройств. Выберите токен для генерации и экспорта.";
            }
            catch (Exception e) { if (tokenTimer == timer) errors.Text = e.Message; }
            finally { scanning = false; }
        }
        timer.Tick += async (_, _) => await Refresh(); timer.Start(); _ = Refresh();
    }
}
