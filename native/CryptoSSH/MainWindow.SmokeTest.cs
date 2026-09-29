using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace CryptoSSH;
public partial class MainWindow
{
    public async void RunSmokeTest()
    {
        var directory = Path.Combine(store.DirectoryPath, "artifacts"); Directory.CreateDirectory(directory);
        try
        {
            store.Upsert(new() { Name = "Production API", Host = "192.0.2.1", Username = "admin", Group = "Работа", Favorite = true });
            ShowConnections(); await Task.Delay(300); Capture(Path.Combine(directory, "connections.png"));
            ShowToken(); await Task.Delay(2500); Capture(Path.Combine(directory, "tokens.png"));
            tokenBusy = true; TokenBusyChanged?.Invoke(); await Task.Delay(200); Capture(Path.Combine(directory, "token-busy.png"));
            tokenBusy = false; TokenBusyChanged?.Invoke();
            ShowAppearance(); await Task.Delay(200); Capture(Path.Combine(directory, "appearance.png"));
            var preview = AddSession(new TerminalSession("Терминал CryptoSSH", store.Data.Appearance));
            await Task.Delay(300);
            preview.Surface.IsReadOnly = false;
            var typed = new StringBuilder(); preview.Surface.Input += s => typed.Append(s);
            await NativeKeyboardTest.TypeAsync(preview, "keyboard_test\r");
            if (typed.ToString() != "keyboard_test\r") throw new Exception("WPF keyboard input failed: " + typed);
            preview.FeedFixture("\x1b[1;32mCryptoSSH 1.0.0\x1b[0m — собственный терминал\r\n\r\n\x1b[36madmin@server\x1b[0m:~$ ls -la\r\n\x1b[34mdrwxr-xr-x  projects/\x1b[0m\r\n-rw-r--r--  README.md\r\n\r\n\x1b[38;2;220;160;80mTruecolor 24-bit\x1b[0m  ·  Кириллица  ·  中\r\n\r\nadmin@server:~$ ");
            ShowDrawer(true); await Task.Delay(250); Capture(Path.Combine(directory, "own-terminal.png"));
            foreach (var palette in TerminalPalette.All) preview.ApplyAppearance(new() { Theme = palette.Name });
            preview.ApplyAppearance(store.Data.Appearance); ShowConnections(); SelectSession(preview);
            string report = "PASS: own WPF terminal renders output; native keyboard messages, all themes and session navigation work without a child process.\n";
            if (int.TryParse(Environment.GetEnvironmentVariable("RUTOKEN_SSH_FIXTURE_PORT"), out var port))
            {
                int trustPrompts = 0;
                var prompts = new SshPrompts((_, _, _) => "test-fixture-only", _ => { trustPrompts++; return true; }, _ => null);
                var module = Environment.GetEnvironmentVariable("CRYPTOSSH_TEST_PKCS11");
                if (module is not null) store.Data.CustomSettings = new() { Provider = module, JaCartaProvider = module };
                foreach (var route in module is null ? new[] { "password", "key" } : new[] { "password", "key", "rutoken", "token", "jacarta-test-route" })
                {
                    var auth = route == "jacarta-test-route" ? "token" : route;
                    if (route == "rutoken") store.Data.CustomSettings = new() { Provider = module!, JaCartaProvider = "" };
                    if (route == "jacarta-test-route") store.Data.CustomSettings = new() { Provider = "", JaCartaProvider = module! };
                    var profile = new Connection { Name = "Loopback " + auth, Host = "127.0.0.1", Port = port, Username = "fixture", Auth = auth, KeyPath = Environment.GetEnvironmentVariable("RUTOKEN_SSH_FIXTURE_KEY") ?? "" };
                    var text = new StringBuilder(); var session = AddSession(new TerminalSession(profile, store, prompts)); session.Output += s => text.Append(s);
                    async Task WaitFor(string marker)
                    {
                        for (int i = 0; i < 150 && !text.ToString().Contains(marker); i++) { if (session.Error is not null) throw new Exception(session.Error); await Task.Delay(100); }
                        if (!text.ToString().Contains(marker)) throw new Exception("SSH output missing: " + marker + "\n" + text);
                    }
                    await WaitFor("SSH_INTEGRATION_OK");
                    ShowConnections(); await Task.Delay(100); SelectSession(session);
                    session.ApplyAppearance(new() { UseCustomColors = true, BackgroundColor = "#18251F", ForegroundColor = "#E6EDC5" });
                    session.InsertSnippet(new() { Name = "probe", Command = "probe" }); await WaitFor("PROBE_EXECUTED");
                    await NativeKeyboardTest.TypeAsync(session, "exit\r");
                    for (int i = 0; i < 100 && session.IsRunning; i++) await Task.Delay(50);
                    if (session.IsRunning || session.Error is not null) throw new Exception("SSH failed to close: " + session.Error + "\n" + text);
                    report += "PASS: in-process SSH " + route + ", trusted host, WPF keyboard input and snippet executes automatically with Enter.\n";
                }
                if (trustPrompts != 0) throw new Exception("Host trust persistence failed");
            }
            File.WriteAllText(Path.Combine(directory, "smoke-result.txt"), report); closing = true; Close();
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(directory, "smoke-result.txt"), "FAIL: " + e); closing = true; Close(); Environment.ExitCode = 1; }
    }
}
