using System.IO;

namespace CryptoSSH;

internal sealed record TokenDevice(string ProviderPath, string Vendor, ulong Slot, string Serial, string Label, string Model, bool Initialized, bool CanSign, int[] RsaSizes)
{
    public string Identity => $"{ProviderPath}|{Slot}|{Serial}";
    public override string ToString() => $"{Vendor} · {Label} · {Model} · {Serial} · слот {Slot}";
}
internal sealed record TokenScan(IReadOnlyList<TokenDevice> Devices, IReadOnlyList<string> Errors);
internal static class TokenCatalog
{
    internal static TokenDevice ChooseForConnection(IReadOnlyList<TokenDevice> devices, Func<IReadOnlyList<TokenDevice>, TokenDevice?>? choose)
    {
        if (devices.Count == 0) throw new InvalidOperationException("Нет доступных RSA-токенов.");
        if (devices.Count == 1) return devices[0];
        var selected = choose?.Invoke(devices) ?? throw new OperationCanceledException("Выбор токена отменён.");
        return devices.SingleOrDefault(d => d.Identity == selected.Identity) ?? throw new InvalidOperationException("Выбранное устройство отсутствует в списке токенов.");
    }
    internal static TokenDevice? SelectDevice(IReadOnlyList<TokenDevice> devices, string? previous)
    {
        var selected = devices.FirstOrDefault(d => d.Identity == previous);
        if (selected is not null) return selected;
        var rsa = devices.Where(d => d.Initialized && d.CanSign).ToArray();
        return rsa.Length == 1 ? rsa[0] : devices.Count == 1 ? devices[0] : null;
    }
    internal static IEnumerable<(string name, string path)> Modules(ClientSettings settings) =>
        new[] { ("Рутокен", settings.Provider), ("JaCarta", settings.JaCartaProvider) }
            .Where(m => !string.IsNullOrWhiteSpace(m.Item2))
            .DistinctBy(m => m.Item2, StringComparer.OrdinalIgnoreCase);

    public static TokenScan Scan(ClientSettings settings)
    {
        var devices = new List<TokenDevice>(); var errors = new List<string>();
        foreach (var (name, path) in Modules(settings))
        {
            try { devices.AddRange(TokenProvider.Get(path).Devices(name)); }
            catch (Exception e) { errors.Add($"{name}: {e.Message}"); }
        }
        return new(devices, errors);
    }
    public static List<TokenKey> Keys(ClientSettings settings)
    {
        var scan = Scan(settings); var keys = new List<TokenKey>(); var errors = scan.Errors.ToList();
        foreach (var device in scan.Devices.Where(d => d.Initialized && d.CanSign))
        {
            try { keys.AddRange(TokenProvider.Get(device.ProviderPath).Keys(device)); }
            catch (Exception e) { errors.Add($"{device.Vendor} ({device.Serial}): {e.Message}"); }
        }
        if (keys.Count == 0)
            throw new InvalidOperationException("Не найдены доступные RSA SSH-ключи. Укажите библиотеку PKCS#11 в настройках, подключите Рутокен или JaCarta с поддержкой RSA и создайте ключ в разделе «Токены»." +
                (errors.Count == 0 ? "" : "\n" + string.Join("\n", errors)));
        return keys;
    }
}
