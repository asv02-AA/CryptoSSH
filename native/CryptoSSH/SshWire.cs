using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace CryptoSSH;

internal static class SshWire
{
    public static byte[] UInt(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }
    public static byte[] String(byte[] value) => [.. UInt((uint)value.Length), .. value];
    public static byte[] String(string value) => String(Encoding.UTF8.GetBytes(value));
    public static byte[] Mpint(byte[] value)
    {
        int i = 0; while (i < value.Length - 1 && value[i] == 0) i++;
        var bytes = value[i..]; return String(bytes.Length > 0 && bytes[0] >= 128 ? [0, .. bytes] : bytes);
    }
    public static byte[] RsaPublic(byte[] modulus, byte[] exponent) => [.. String("ssh-rsa"), .. Mpint(exponent), .. Mpint(modulus)];
    public static uint ReadUInt(Stream input) { Span<byte> b = stackalloc byte[4]; input.ReadExactly(b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
    public static byte[] ReadString(Stream input)
    {
        var length = ReadUInt(input); if (length > 1024 * 1024) throw new InvalidDataException("Слишком большой пакет SSH.");
        var b = new byte[length]; input.ReadExactly(b); return b;
    }
    public static string KeyType(byte[] blob) { using var stream = new MemoryStream(blob); return Encoding.ASCII.GetString(ReadString(stream)); }
    public static string Fingerprint(byte[] key) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
}

internal sealed class SigningKeySource : IPrivateKeySource
{
    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }
    public SigningKeySource(byte[] publicKey, Func<string, byte[], byte[]> sign)
    {
        var type = SshWire.KeyType(publicKey);
        string[] names = type == "ssh-rsa" ? ["rsa-sha2-512", "rsa-sha2-256"] : [type];
        HostKeyAlgorithms = names.Select(name => (HostAlgorithm)new SigningAlgorithm(name, publicKey, sign)).ToArray();
    }
    private sealed class SigningAlgorithm(string name, byte[] publicKey, Func<string, byte[], byte[]> signer) : HostAlgorithm(name)
    {
        public override byte[] Data => publicKey;
        public override byte[] Sign(byte[] data) => signer(Name, data);
        public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException("Источник предназначен для аппаратной/агентской подписи, не для проверки сервера.");
    }
}

internal sealed class KnownHostStore(string path)
{
    private static readonly object Gate = new();
    private static bool Match(string pattern, string host)
    {
        if (pattern.StartsWith("|1|"))
        {
            var p = pattern.Split('|'); if (p.Length != 4) throw new InvalidDataException("Повреждён known_hosts.");
            return CryptographicOperations.FixedTimeEquals(HMACSHA1.HashData(Convert.FromBase64String(p[2]), Encoding.UTF8.GetBytes(host)), Convert.FromBase64String(p[3]));
        }
        return Regex.IsMatch(host, "\\A" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    // New hosts are trusted automatically. Changed keys require explicit approval.
    public bool Verify(string host, int port, byte[] key, Func<string, bool> approve)
    {
        string name = port == 22 ? host : $"[{host}]:{port}";
        bool Matches(string field)
        {
            var names = field.Split(',');
            return !names.Any(p => p.StartsWith('!') && Match(p[1..], name)) && names.Any(p => !p.StartsWith('!') && Match(p, name));
        }
        (bool exact, List<string> fingerprints) Check(string[] lines)
        {
            bool exact = false; var fingerprints = new List<string>();
            foreach (var line in lines)
            {
                var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts[0].StartsWith('#')) continue;
                bool marker = parts[0].StartsWith('@'); int offset = marker ? 1 : 0;
                if (parts.Length <= offset || !Matches(parts[offset])) continue;
                if (parts.Length < offset + 3) throw new InvalidDataException("Повреждена запись сервера в known_hosts.");
                if (marker) throw new InvalidOperationException("Специальная запись " + parts[0] + " требует отдельной проверки в настройках ключей серверов.");
                var saved = Convert.FromBase64String(parts[offset + 2]);
                fingerprints.Add(SshWire.Fingerprint(saved));
                exact |= CryptographicOperations.FixedTimeEquals(saved, key);
            }
            return (exact, fingerprints);
        }
        string[] Read() => File.Exists(path) ? File.ReadAllLines(path) : [];
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string[] lines; (bool exact, List<string> fingerprints) state;
            lock (Gate) { lines = Read(); state = Check(lines); }
            if (state.exact) return true;
            if (state.fingerprints.Count > 0 && !approve($"Ключ сервера изменился: {name}\n\nПрежний отпечаток:\n{string.Join("\n", state.fingerprints.Distinct())}\n\nНовый отпечаток:\n{SshWire.Fingerprint(key)}\n\nЭто может быть переустановка сервера или подмена соединения. Продолжить подключение и запомнить новый ключ?")) return false;
            lock (Gate)
            {
                if (!Read().SequenceEqual(lines)) continue;
                var updated = new List<string>();
                foreach (var line in lines)
                {
                    var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3 || parts[0].StartsWith('#') || parts[0].StartsWith('@') || !Matches(parts[0])) { updated.Add(line); continue; }
                    // Preserve other hosts sharing this record, including wildcard patterns.
                    var names = parts[0].Split(',').Where(p => p.StartsWith('!') || !Match(p, name) || p.Contains('*') || p.Contains('?')).ToList();
                    if (names.Any(p => !p.StartsWith('!')))
                    {
                        if (names.Any(p => !p.StartsWith('!') && Match(p, name))) names.Insert(0, "!" + name);
                        parts[0] = string.Join(',', names); updated.Add(string.Join(' ', parts));
                    }
                }
                updated.Add($"{name} {SshWire.KeyType(key)} {Convert.ToBase64String(key)}");
                Rules.AtomicWrite(path, string.Join('\n', updated) + "\n");
                return true;
            }
        }
        throw new IOException("Список ключей изменяется другим подключением. Повторите подключение.");
    }
}
