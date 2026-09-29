using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CryptoSSH;

internal static class TerminalTests
{
    public static void Run(string root, Action<string, Action> check, Action<bool> assert, Action<Action> rejects)
    {
        check("VT shrinking preserves prompt and moves displaced rows into history", () =>
        {
            var b = new VtBuffer(20, 5); b.Feed("one\r\ntwo\r\nthree\r\nfour\r\n$ command");
            b.Resize(20, 3);
            assert(b.RowText(2) == "$ command" && b.Y == 2 && b.X == 9);
            assert(b.RowText(0) == "three" && b.History.Count == 2);
            assert(string.Concat(b.History[0].Select(c => c.Text)).TrimEnd() == "one");
            b.Feed("!"); assert(b.RowText(2) == "$ command!");
            b.Resize(20, 6); assert(b.RowText(2) == "$ command!" && b.Y == 2);
        });
        check("VT alternate resize preserves primary prompt and wide cell boundaries", () =>
        {
            var b = new VtBuffer(12, 5); b.Feed("one\r\ntwo\r\nthree\r\nfour\r\n$ cmd");
            b.Feed("\x1b[?1049h\x1b[5;1Heditor"); b.Resize(12, 3);
            assert(b.RowText(2) == "editor" && b.Y == 2 && b.History.Count == 2);
            b.Feed("\x1b[?1049l"); assert(b.RowText(2) == "$ cmd" && b.Y == 2 && b.X == 5);
            b = new VtBuffer(8, 3); b.Feed("abc中\x1b[?1049h"); b.Resize(4, 3); b.Feed("\x1b[?1049l");
            assert(b.Screen[0][3].Width == 1 && b.RowText(0) == "abc");
        });
        check("VT fragmented escapes, truecolor, cursor and erasure", () =>
        {
            var b = new VtBuffer(20, 5); b.Feed("hello\x1b["); b.Feed("2;3H\x1b[38;2;12;34;56mX");
            assert(b.Screen[1][2].Text == "X" && b.Screen[1][2].Style.Foreground == 0x10C2238);
            b.Feed("\x1b[1;1H\x1b[2K"); assert(b.RowText(0) == "");
            b.Feed("\x1b[0mA\x1b[31;1mB"); assert(b.Screen[0][1].Style.Foreground == 1 && b.Screen[0][1].Style.Bold);
            b.Feed("\x1b[0mC"); assert(b.Screen[0][2].Style.Foreground == -1 && !b.Screen[0][2].Style.Bold);
        });
        check("VT alternate screen restores shell, region scroll and bounded history", () =>
        {
            var b = new VtBuffer(12, 4); b.Feed("shell\x1b[?1049h\x1b[2Jeditor"); assert(b.Alternate && b.RowText(0) == "editor");
            b.Feed("\x1b[?1049l"); assert(!b.Alternate && b.RowText(0) == "shell" && b.X == 5);
            b.Feed("\x1b[2;3r\x1b[3;1Hrow\n"); assert(b.RowText(0) == "shell" && b.RowText(1).StartsWith("row") && b.History.Count == 0);
            b.Feed("\x1b[r\x1b[4;1H"); for (int i = 0; i < 5500; i++) b.Feed("line\r\n"); assert(b.History.Count <= 5000 && b.History.Count > 4000);
        });
        check("VT Unicode, combining marks, pending wrap and screen resize", () =>
        {
            var b = new VtBuffer(8, 4); b.Feed("Привет\r\n中e\u0301"); assert(b.Screen[1][0].Width == 2 && b.Screen[1][1].Width == 0 && b.Screen[1][2].Text == "e\u0301");
            b.Feed("\r\n12345678"); assert(b.Y == 2); b.Feed("9"); assert(b.Y == 3 && b.X == 1);
            b.Resize(15, 6); assert(b.Columns == 15 && b.RowText(0) == "Привет");
            b.Feed("\x1b[1;1H\ud83d"); b.Feed("\ude00"); assert(b.Screen[0][0].Text == "😀" && b.Screen[0][0].Width == 2);
        });
        check("VT insert/delete, device replies and safe OSC handling", () =>
        {
            var b = new VtBuffer(10, 4); b.Feed("abcd\x1b[1;2H\x1b[P"); assert(b.RowText(0) == "acd");
            b.Feed("\x1b[@X"); assert(b.RowText(0) == "aXcd"); string reply = ""; b.Reply += text => reply += text;
            b.Feed("\x1b[6n"); assert(reply == "\x1b[1;3R");
            b.Feed("\x1b[?2004h\x1b[?1h\x1b]52;c;c2VjcmV0\a"); assert(b.BracketedPaste && b.ApplicationCursor && b.RowText(0) == "aXcd");
        });
        check("Host trust: new, rejected, unchanged, changed and hashed legacy keys", () =>
        {
            using var rsa = RSA.Create(2048); var parameters = rsa.ExportParameters(false); var key = SshWire.RsaPublic(parameters.Modulus!, parameters.Exponent!);
            var path = Path.Combine(root, "trust", "known_hosts"); var hosts = new KnownHostStore(path); int prompts = 0;
            assert(hosts.Verify("server.example", 22, key, _ => throw new Exception("New host must be trusted automatically")) && File.Exists(path));
            assert(hosts.Verify("server.example", 22, key, _ => { prompts++; return true; }));
            assert(hosts.Verify("server.example", 22, key, _ => throw new Exception("Duplicate trust prompt")) && prompts == 0);
            var changed = key.Concat(new byte[] { 1 }).ToArray();
            assert(!hosts.Verify("server.example", 22, changed, _ => false));
            assert(hosts.Verify("server.example", 22, key, _ => throw new Exception("Rejected change persisted")));
            assert(hosts.Verify("server.example", 22, changed, message => { prompts++; return message.Contains("Ключ сервера изменился") && message.Contains(SshWire.Fingerprint(key)); }));
            assert(prompts == 1 && hosts.Verify("server.example", 22, changed, _ => throw new Exception("Replacement was not saved")));
            File.WriteAllText(path, $"*.example,alias.test ssh-rsa {Convert.ToBase64String(key)}\n");
            assert(hosts.Verify("server.example", 22, changed, _ => true));
            assert(hosts.Verify("other.example", 22, key, _ => throw new Exception("Wildcard lost")));
            assert(hosts.Verify("alias.test", 22, key, _ => throw new Exception("Alias lost")));
            assert(!hosts.Verify("server.example", 22, key, _ => false));
            var salt = RandomNumberGenerator.GetBytes(20); var hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes("[::1]:2222"));
            File.WriteAllText(path, $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)} ssh-rsa {Convert.ToBase64String(key)}\n");
            assert(hosts.Verify("::1", 2222, key, _ => throw new Exception("Hashed legacy key was ignored")));
        });
        check("External RSA signer negotiates SHA-2 and preserves SSH signature format", () =>
        {
            using var rsa = RSA.Create(2048); var p = rsa.ExportParameters(false); var blob = SshWire.RsaPublic(p.Modulus!, p.Exponent!);
            var source = new SigningKeySource(blob, (algorithm, input) => [.. SshWire.String(algorithm), .. SshWire.String(rsa.SignData(input, algorithm == "rsa-sha2-512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))]);
            foreach (var algorithm in source.HostKeyAlgorithms)
            {
                var input = Encoding.UTF8.GetBytes("SSH authentication test"); using var signature = new MemoryStream(algorithm.Sign(input));
                assert(Encoding.ASCII.GetString(SshWire.ReadString(signature)) == algorithm.Name);
                assert(rsa.VerifyData(input, SshWire.ReadString(signature), algorithm.Name == "rsa-sha2-512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }
        });
    }
}
