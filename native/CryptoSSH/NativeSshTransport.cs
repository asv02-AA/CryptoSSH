using System.Text;
using System.Threading.Channels;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CryptoSSH;

internal sealed record SshPrompts(Func<string, string, bool, string?> Secret, Func<string, bool> Trust, Func<IReadOnlyList<TokenKey>, TokenKey?> ChooseToken, Func<IReadOnlyList<TokenDevice>, TokenDevice?>? ChooseDevice = null);

internal sealed class NativeSshTransport(Connection profile, Store store, SshPrompts prompts) : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<(string? text, int cols, int rows)> outbound = Channel.CreateBounded<(string?, int, int)>(256);
    private SshClient? client;
    private ShellStream? shell;
    private int columns = 80, rows = 24;
    public bool Connected => shell is not null && client?.IsConnected == true && !stop.IsCancellationRequested;
    public event Action<string>? Output;
    public event Action? Ready;
    private string Secret(string title, string message, bool echoed = false)
    { stop.Token.ThrowIfCancellationRequested(); return prompts.Secret(title, message, echoed) ?? throw new OperationCanceledException("Ввод отменён."); }
    public async Task Run(int cols, int height)
    {
        profile.Validate(); columns = cols; rows = height;
        var resources = new List<IDisposable>();
        try
        {
            AuthenticationMethod[] methods;
            switch (profile.Auth)
            {
                case "password":
                    var password = new PasswordAuthenticationMethod(profile.Username, Secret("Пароль SSH", $"{profile.Username}@{profile.Host}"));
                    var interactive = new KeyboardInteractiveAuthenticationMethod(profile.Username);
                    interactive.AuthenticationPrompt += (_, args) => { foreach (var prompt in args.Prompts) prompt.Response = Secret("Вход на сервер", prompt.Request, prompt.IsEchoed); };
                    methods = [password, interactive]; break;
                case "key":
                    PrivateKeyFile key;
                    try { key = new PrivateKeyFile(Rules.RequireFile(profile.KeyPath, "Закрытый SSH-ключ")); }
                    catch (SshPassPhraseNullOrEmptyException) { key = new PrivateKeyFile(profile.KeyPath, Secret("Парольная фраза ключа", profile.KeyPath)); }
                    resources.Add(key); methods = [new PrivateKeyAuthenticationMethod(profile.Username, key)]; break;
                case "agent": methods = [new PrivateKeyAuthenticationMethod(profile.Username, AgentKeySource.Load())]; break;
                case "rutoken": case "token":
                    var devices = TokenCatalog.Scan(store.Settings).Devices.Where(d => d.Initialized && d.CanSign).ToArray();
                    if (devices.Length == 0) { _ = TokenCatalog.Keys(store.Settings); throw new InvalidOperationException("Подключите токен с поддержкой RSA."); }
                    var device = TokenCatalog.ChooseForConnection(devices, prompts.ChooseDevice);
                    var keys = TokenProvider.Get(device.ProviderPath).Keys(device);
                    if (keys.Count == 0) throw new InvalidOperationException($"На выбранном токене {device.Vendor} ({device.Serial}) нет RSA SSH-ключей. Создайте ключ в разделе «Токены» или выберите другое устройство.");
                    var token = keys.Count == 1 ? keys[0] : prompts.ChooseToken(keys) ?? throw new OperationCanceledException();
                    methods = [new PrivateKeyAuthenticationMethod(profile.Username, TokenProvider.Get(token.ProviderPath).Source(token, () => Secret("PIN " + token.Vendor, $"{token.Label}\nСерийный номер: {token.Serial}")))]; break;
                default: throw new InvalidOperationException("Неизвестный способ входа.");
            }
            foreach (var method in methods) resources.Add(method);
            var info = new ConnectionInfo(profile.Host, profile.Port, profile.Username, methods) { Timeout = TimeSpan.FromSeconds(20), Encoding = Encoding.UTF8 };
            // Never negotiate obsolete SHA-1 host signatures or legacy group1 exchange.
            foreach (var name in info.HostKeyAlgorithms.Keys.Where(k => k is "ssh-rsa" or "ssh-dss").ToArray()) info.HostKeyAlgorithms.Remove(name);
            foreach (var name in info.KeyExchangeAlgorithms.Keys.Where(k => k.Contains("sha1", StringComparison.OrdinalIgnoreCase)).ToArray()) info.KeyExchangeAlgorithms.Remove(name);
            client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            Exception? trustError = null;
            client.HostKeyReceived += (_, args) =>
            {
                args.CanTrust = false;
                try { args.CanTrust = new KnownHostStore(store.KnownHosts).Verify(profile.Host, profile.Port, args.HostKey, prompts.Trust); }
                catch (Exception e) { trustError = e; }
            };
            using var cancellation = stop.Token.Register(() => _ = Task.Run(() => { try { client?.Dispose(); } catch { } }));
            try { await client.ConnectAsync(stop.Token).ConfigureAwait(false); }
            catch when (trustError is not null) { throw trustError; }
            stop.Token.ThrowIfCancellationRequested();
            shell = client.CreateShellStream("xterm-256color", (uint)columns, (uint)rows, 0, 0, 65536);
            Ready?.Invoke();
            var writer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var message in outbound.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                    {
                        if (message.text is { } text) { shell.Write(Encoding.UTF8.GetBytes(text)); shell.Flush(); }
                        else shell.ChangeWindowSize((uint)message.cols, (uint)message.rows, 0, 0);
                    }
                }
                catch { stop.Cancel(); throw; }
            });
            try
            {
                var bytes = new byte[16384]; var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)]; var decoder = Encoding.UTF8.GetDecoder();
                while (!stop.IsCancellationRequested)
                {
                    int count = shell.Read(bytes, 0, bytes.Length); if (count == 0) break;
                    int length = decoder.GetChars(bytes, 0, count, chars, 0, false);
                    if (length > 0) Output?.Invoke(new string(chars, 0, length));
                }
            }
            finally { outbound.Writer.TryComplete(); stop.Cancel(); try { await writer.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        }
        finally
        {
            shell?.Dispose(); client?.Dispose(); foreach (var resource in resources) resource.Dispose();
        }
    }
    public void Send(string text)
    {
        if (!Connected) return;
        if (!outbound.Writer.TryWrite((text, 0, 0))) throw new InvalidOperationException("Очередь ввода переполнена. Дождитесь ответа сервера.");
    }
    public void Resize(int cols, int height)
    { columns = cols; rows = height; if (Connected) outbound.Writer.TryWrite((null, cols, height)); }
    public void Dispose() { if (!stop.IsCancellationRequested) stop.Cancel(); outbound.Writer.TryComplete(); }
}
