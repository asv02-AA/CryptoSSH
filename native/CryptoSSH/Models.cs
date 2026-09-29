using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CryptoSSH;
public record Connection
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Auth { get; set; } = "token";
    public string KeyPath { get; set; } = "";
    public string Group { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
    public void Validate()
    {
        Name = Rules.Text(Name, "Название", 100); Host = Rules.Text(Host, "Сервер", 256); Username = Rules.Text(Username, "Пользователь", 100);
        KeyPath = Rules.Text(KeyPath, "Путь к ключу", 1024); Group = Rules.Text(Group, "Группа", 60); Notes = Rules.Text(Notes, "Заметка", 500);
        if (Name.Length == 0 || !(System.Net.IPAddress.TryParse(Host, out _) || Regex.IsMatch(Host, @"^[a-zA-Z0-9][a-zA-Z0-9._-]*$")) || !Regex.IsMatch(Username, @"^[a-zA-Z0-9_][a-zA-Z0-9_.@\\-]*$")) throw new ArgumentException("Укажите название, адрес сервера без схемы и имя пользователя.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Порт должен быть от 1 до 65535.");
        if (!new[] { "token", "rutoken", "password", "key", "agent" }.Contains(Auth)) throw new ArgumentException("Неизвестный способ входа.");
        if (Auth == "key" && !Path.IsPathFullyQualified(KeyPath)) throw new ArgumentException("Выберите файл закрытого SSH-ключа.");
    }
}
public record ClientSettings
{
    internal static string Discover(string name)
    {
        var path = Path.Combine(Environment.SystemDirectory, name);
        return File.Exists(path) ? path : "";
    }
    internal static string OptionalLibrary(string? path, string name) =>
        string.IsNullOrWhiteSpace(path) ? "" : Rules.RequireFile(path.Trim(), name);
    internal void MigrateBundledPaths()
    {
        string Migrate(string? path, string name) => string.Equals(path,
            Path.Combine(AppContext.BaseDirectory, "vendor", name), StringComparison.OrdinalIgnoreCase)
            ? Discover(name) : path ?? "";
        Provider = Migrate(Provider, "rtpkcs11ecp.dll");
        JaCartaProvider = Migrate(JaCartaProvider, "jcPKCS11-2.dll");
    }
    public string Provider { get; set; } = Discover("rtpkcs11ecp.dll");
    public string JaCartaProvider { get; set; } = Discover("jcPKCS11-2.dll");
}
public record SavedData
{
    public int Version { get; set; } = 1;
    public List<Connection> Connections { get; set; } = [];
    public ClientSettings? CustomSettings { get; set; }
    public TerminalAppearance Appearance { get; set; } = new();
    public List<Snippet> Snippets { get; set; } = [];
}
public sealed class Store
{
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "connections.json");
    public string KnownHosts => Path.Combine(DirectoryPath, "known_hosts");
    public SavedData Data { get; private set; }
    public ClientSettings Settings => Data.CustomSettings ?? new ClientSettings();
    public Store(string? directory = null)
    {
        // Keep the established data directory so upgrading the brand preserves profiles
        // and, critically, the previously trusted server keys without a migration race.
        DirectoryPath = directory ?? Environment.GetEnvironmentVariable("RUTOKEN_SSH_TEST_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RutokenSSH");
        Directory.CreateDirectory(DirectoryPath);
        try
        {
            Data = File.Exists(FilePath) ? JsonSerializer.Deserialize<SavedData>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Пустой файл настроек") : new();
            if (Data.Version != 1 || Data.Connections is null) throw new InvalidDataException("Неизвестный формат настроек");
            foreach (var item in Data.Connections) item.Validate();
            Data.CustomSettings?.MigrateBundledPaths();
            Data.Appearance.Validate();
            foreach (var item in Data.Snippets) item.Validate();
        }
        catch (Exception e) { throw new InvalidDataException($"Не удалось прочитать {FilePath}. Исходный файл сохранён. Исправьте его или переименуйте перед запуском.\n{e.Message}"); }
    }
    public void Save() => Rules.AtomicWrite(FilePath, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
    public void Upsert(Connection connection)
    {
        connection.Validate();
        var old = Data.Connections.FindIndex(p => p.Id == connection.Id);
        if (old < 0) Data.Connections.Add(connection); else Data.Connections[old] = connection;
        Save();
    }
    public void Delete(Guid id) { Data.Connections.RemoveAll(p => p.Id == id); Save(); }
}
public static class Rules
{
    public static string Text(string? value, string name, int max)
    {
        if (value is null || value.Length > max || value.Any(char.IsControl)) throw new ArgumentException($"Некорректное поле: {name}.");
        return value.Trim();
    }
    public static void AtomicWrite(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, value, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string RequireFile(string path, string name)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new FileNotFoundException($"{name} не найден. Проверьте путь в настройках: {path}");
        return path;
    }
    public static void ValidateGeneration(string slot, string id, string label, int bits)
    {
        label = Text(label, "Метка ключа", 32);
        if (!Regex.IsMatch(slot, @"^(0x[0-9a-fA-F]+|[0-9]+)$") || !Regex.IsMatch(id, @"^(?:[0-9a-fA-F]{2}){1,16}$")) throw new ArgumentException("Слот: номер или 0x0. ID: пары HEX-цифр, например 01.");
        if (label.Length == 0 || !new[] { 2048, 3072, 4096 }.Contains(bits)) throw new ArgumentException("Укажите метку и длину RSA 2048, 3072 или 4096.");
    }
}
