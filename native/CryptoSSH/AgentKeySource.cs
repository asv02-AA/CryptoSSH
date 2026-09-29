using System.IO;
using System.IO.Pipes;
using System.Text;
using Renci.SshNet;

namespace CryptoSSH;

internal static class AgentKeySource
{
    // Talks to the agent service's documented binary protocol; never starts ssh.exe.
    private static byte[] Exchange(byte[] request)
    {
        using var pipe = new NamedPipeClientStream(".", "openssh-ssh-agent", PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
        pipe.WriteAsync(SshWire.String(request), timeout.Token).AsTask().GetAwaiter().GetResult();
        var header = new byte[4]; pipe.ReadExactlyAsync(header, timeout.Token).AsTask().GetAwaiter().GetResult();
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > 1024 * 1024) throw new InvalidDataException("Слишком большой ответ SSH-агента.");
        var response = new byte[length]; pipe.ReadExactlyAsync(response, timeout.Token).AsTask().GetAwaiter().GetResult(); return response;
    }
    public static IPrivateKeySource[] Load()
    {
        using var response = new MemoryStream(Exchange([11]));
        if (response.ReadByte() != 12) throw new InvalidOperationException("SSH-агент не предоставил список ключей.");
        var count = SshWire.ReadUInt(response); if (count > 64) throw new InvalidOperationException("В SSH-агенте слишком много ключей (более 64).");
        var keys = new List<IPrivateKeySource>();
        for (int i = 0; i < count; i++)
        {
            var publicKey = SshWire.ReadString(response); _ = SshWire.ReadString(response);
            var type = SshWire.KeyType(publicKey);
            if (type != "ssh-rsa" && type != "ssh-ed25519" && !type.StartsWith("ecdsa-sha2-nistp")) continue;
            keys.Add(new SigningKeySource(publicKey, (algorithm, data) =>
            {
                uint flags = algorithm == "rsa-sha2-512" ? 4u : algorithm == "rsa-sha2-256" ? 2u : 0u;
                using var result = new MemoryStream(Exchange([13, .. SshWire.String(publicKey), .. SshWire.String(data), .. SshWire.UInt(flags)]));
                if (result.ReadByte() != 14) throw new InvalidOperationException("SSH-агент отказал в подписи.");
                var signature = SshWire.ReadString(result);
                if (SshWire.KeyType(signature) != algorithm) throw new InvalidDataException("SSH-агент вернул неверный алгоритм подписи.");
                return signature;
            }));
        }
        if (keys.Count == 0) throw new InvalidOperationException("В агенте нет подходящих SSH-ключей.");
        return keys.ToArray();
    }
}
