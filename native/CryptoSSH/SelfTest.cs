using System.IO;
using System.Text.Json;

namespace CryptoSSH;
public static class SelfTest
{
    public static int Run()
    {
        var root = Environment.GetEnvironmentVariable("RUTOKEN_SSH_TEST_DATA") ?? Path.Combine(Path.GetTempPath(), "CryptoSSH-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var results = new List<string>();
        void Check(string name, Action test) { try { test(); results.Add("PASS " + name); } catch (Exception e) { results.Add("FAIL " + name + ": " + e.Message); } }
        void Assert(bool result) { if (!result) throw new Exception("Assertion failed"); }
        void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection"); }
        Check("Profile validation and command injection", () => { foreach (var host in new[] { "-oProxyCommand=calc", "server & calc", "host\nwhoami", "ssh://host" }) Throws(() => new Connection { Name = "x", Username = "user", Host = host }.Validate()); Throws(() => new Connection { Name = "x", Username = "user", Host = "host", Port = 0 }.Validate()); });
        Check("Save, rename, reload and delete", () => { var data = new Store(Path.Combine(root, "profiles")); var p = new Connection { Name = "Первый сервер", Host = "localhost", Username = "user" }; data.Upsert(p); data.Upsert(p with { Name = "Переименован", Favorite = true }); var reloaded = new Store(data.DirectoryPath); Assert(reloaded.Data.Connections.Single().Name == "Переименован"); reloaded.Delete(p.Id); Assert(new Store(data.DirectoryPath).Data.Connections.Count == 0); });
        Check("Corrupt data is never silently overwritten", () => { var folder = Path.Combine(root, "corrupt"); Directory.CreateDirectory(folder); var path = Path.Combine(folder, "connections.json"); File.WriteAllText(path, "{invalid"); Throws(() => new Store(folder)); Assert(File.ReadAllText(path) == "{invalid"); });
        Check("0.1.x data loads with new defaults and retains trusted hosts", () => { var folder = Path.Combine(root, "legacy"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "connections.json"), "{\"Version\":1,\"Connections\":[{\"Name\":\"Old server\",\"Host\":\"localhost\",\"Username\":\"user\"}]}"); File.WriteAllText(Path.Combine(folder, "known_hosts"), "localhost ssh-ed25519 AQAB\n"); var data = new Store(folder); Assert(data.Data.Connections.Single().Name == "Old server" && data.Data.Appearance.Theme == "Midnight" && data.Data.Snippets.Count == 0); data.Save(); Assert(File.ReadAllText(data.KnownHosts) == "localhost ssh-ed25519 AQAB\n"); });
        Check("Appearance and snippets persist with validation", () => { var data = new Store(Path.Combine(root, "appearance")); data.Data.Appearance = new() { Theme = "Nord", Size = 18, Cursor = "Блок" }; data.Data.Snippets.Add(new() { Name = "Disk", Command = "df -h" }); data.Save(); var loaded = new Store(data.DirectoryPath); Assert(loaded.Data.Appearance.Theme == "Nord" && loaded.Data.Appearance.Size == 18 && loaded.Data.Snippets.Single().Command == "df -h"); Throws(() => new TerminalAppearance { Size = 999 }.Validate()); Throws(() => new Snippet { Name = "Bad", Command = "echo ok\rwhoami" }.Validate()); Throws(() => new Snippet { Name = "Bad", Command = "echo ok\u001b[A" }.Validate()); foreach (var theme in TerminalPalette.All) { Assert(theme.Colors.Length == 16); } });
        Check("Quick connection handles IPv6 and rejects command input", () => { Assert(MainWindow.ParseQuickConnection("user@[::1]:2222").Port == 2222); Assert(MainWindow.ParseQuickConnection("user@example.org").Host == "example.org"); foreach (var input in new[] { "user@host;whoami", "user@host:0", "user@host:65536", "user@-oProxyCommand=calc", "ssh user@host" }) Throws(() => MainWindow.ParseQuickConnection(input)); });
        Check("Custom colors persist and presets can be restored without losing them", () =>
        {
            var data = new Store(Path.Combine(root, "custom-colors"));
            data.Data.Appearance = new() { Theme = "Nord", UseCustomColors = true, BackgroundColor = "#123456", ForegroundColor = "#ABCDEF" }; data.Save();
            var appearance = new Store(data.DirectoryPath).Data.Appearance; var palette = TerminalPalette.For(appearance);
            Assert(appearance.UseCustomColors && palette.Background == "#123456" && palette.Foreground == "#ABCDEF");
            var preset = TerminalPalette.For(appearance with { UseCustomColors = false }); Assert(preset.Background == "#2E3440" && appearance.BackgroundColor == "#123456");
            Assert(palette.Colors.SequenceEqual(preset.Colors));
            foreach (var color in new[] { "transparent", "#123", "#00123456", "#GG0000", "#123456\n" }) Throws(() => new TerminalAppearance { BackgroundColor = color }.Validate());
            var legacy = JsonSerializer.Deserialize<TerminalAppearance>("{\"Theme\":\"Dracula\",\"Size\":16}")!; legacy.Validate(); Assert(!legacy.UseCustomColors && TerminalPalette.For(legacy).Background == "#282A36");
        });
        Check("Proprietary libraries are not bundled", () =>
        {
            foreach (var name in new[] { "rtpkcs11ecp.dll", "jcPKCS11-2.dll", "ssh.exe" })
                Assert(!Directory.EnumerateFiles(AppContext.BaseDirectory, name, SearchOption.AllDirectories).Any());
        });
        Check("Either vendor or no vendor can be configured independently", () =>
        {
            var single = new ClientSettings { Provider = "test-rt.dll", JaCartaProvider = "" };
            Assert(TokenCatalog.Modules(single).Single().name == "Рутокен");
            single = new() { Provider = "", JaCartaProvider = "test-jc.dll" };
            Assert(TokenCatalog.Modules(single).Single().name == "JaCarta");
            Assert(TokenCatalog.Scan(new() { Provider = "", JaCartaProvider = "" }).Errors.Count == 0);
            Assert(ClientSettings.OptionalLibrary("  ", "unused") == "");
            Throws(() => ClientSettings.OptionalLibrary(Path.Combine(root, "absent.dll"), "missing"));
            var data = new Store(Path.Combine(root, "one-provider"));
            data.Data.CustomSettings = single; data.Save();
            Assert(new Store(data.DirectoryPath).Settings.Provider == "" && new Store(data.DirectoryPath).Settings.JaCartaProvider == "test-jc.dll");
            var legacy = new ClientSettings { Provider = Path.Combine(AppContext.BaseDirectory, "vendor", "rtpkcs11ecp.dll"), JaCartaProvider = "" };
            legacy.MigrateBundledPaths();
            Assert(legacy.Provider == ClientSettings.Discover("rtpkcs11ecp.dll") && legacy.JaCartaProvider == "");
        });
        Check("Token profiles migrate and unavailable providers report diagnostics", () =>
        {
            var settings = JsonSerializer.Deserialize<ClientSettings>("{\"Provider\":\"C:\\\\legacy.dll\"}")!;
            Assert(settings.JaCartaProvider == ClientSettings.Discover("jcPKCS11-2.dll"));
            var scan = TokenCatalog.Scan(new() { Provider = Path.Combine(root, "missing-rt.dll"), JaCartaProvider = Path.Combine(root, "missing-jc.dll") });
            Assert(scan.Devices.Count == 0 && scan.Errors.Count == 2);
            var data = new Store(Path.Combine(root, "token-profiles"));
            foreach (var auth in new[] { "rutoken", "token" }) data.Upsert(new() { Name = auth, Host = "localhost", Username = "user", Auth = auth });
            Assert(new Store(data.DirectoryPath).Data.Connections.Count == 2);
        });
        Check("Combined GOST/PKI token selects RSA slot; multiple RSA devices require selection", () =>
        {
            var gost = new TokenDevice("test.dll", "JaCarta", 1, "test1", "GOST", "GOST", true, false, []);
            var rsa = gost with { Slot = 2, Model = "PKI", CanSign = true, RsaSizes = [2048] };
            var second = rsa with { Slot = 3, Serial = "test2" };
            Assert(TokenCatalog.SelectDevice(new[] { gost, rsa }, null) == rsa);
            Assert(TokenCatalog.SelectDevice(new[] { gost, rsa, second }, null) is null);
            Assert(TokenCatalog.SelectDevice(new[] { gost, rsa, second }, second.Identity) == second);
            Assert(TokenCatalog.SelectDevice(Array.Empty<TokenDevice>(), rsa.Identity) is null);
            int choices = 0;
            Assert(TokenCatalog.ChooseForConnection(new[] { rsa }, _ => throw new Exception("Single device prompted")) == rsa);
            Assert(TokenCatalog.ChooseForConnection(new[] { rsa, second }, all => { choices++; return all[1]; }) == second && choices == 1);
            Throws(() => TokenCatalog.ChooseForConnection(new[] { rsa, second }, _ => null));
        });
        TerminalTests.Run(root, Check, Assert, Throws);
        File.WriteAllLines(Path.Combine(root, "self-test.txt"), results); return results.Any(x => x.StartsWith("FAIL")) ? 1 : 0;
    }
}
