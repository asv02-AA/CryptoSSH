using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;

namespace CryptoSSH;

internal static class TokenTests
{
    public static int Run()
    {
        var root = Environment.GetEnvironmentVariable("RUTOKEN_SSH_TEST_DATA") ?? throw new InvalidOperationException("An isolated test directory is required.");
        Directory.CreateDirectory(root);
        try
        {
            var module = Environment.GetEnvironmentVariable("CRYPTOSSH_TEST_PKCS11") ?? "";
            if (Path.GetFileName(module) != "softhsm2-x64.dll" || !File.Exists(Environment.GetEnvironmentVariable("SOFTHSM2_CONF"))) throw new InvalidOperationException("Only a separately configured SoftHSM test module is allowed.");
            var provider = TokenProvider.Get(module); var match = Regex.Match(provider.ListSlots(), @"Слот (\d+)");
            if (!match.Success) throw new Exception("No software test token");
            var settings = new ClientSettings { Provider = Path.Combine(root, "absent-rutoken.dll"), JaCartaProvider = module };
            var scan = TokenCatalog.Scan(settings);
            var device = scan.Devices.Single(d => d.Initialized);
            if (scan.Errors.Count != 1 || device.Vendor != "JaCarta" || !device.RsaSizes.Contains(2048)) throw new Exception("Provider fallback or capabilities failed");
            bool replaced = false;
            try { provider.Generate(device with { Serial = "removed-device" }, "01", "Wrong token", 2048, "test-fixture-only"); } catch (InvalidOperationException) { replaced = true; }
            if (!replaced) throw new Exception("Replaced device was accepted");
            provider.Generate(device, "01", "CryptoSSH fixture", 2048, "test-fixture-only");
            bool rejected = false; try { provider.Generate(match.Groups[1].Value, "01", "Duplicate", 2048, "test-fixture-only"); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("Duplicate key ID was accepted");
            var key = TokenCatalog.Keys(settings).Single(k => k.Id.SequenceEqual(new byte[] { 1 }));
            if (key.Vendor != "JaCarta" || key.ProviderPath != Path.GetFullPath(module)) throw new Exception("Key provider routing failed");
            var duplicate = TokenCatalog.Scan(new() { Provider = module, JaCartaProvider = module });
            if (duplicate.Devices.Count(d => d.Initialized) != 1) throw new Exception("Duplicate module loaded twice");
            using var input = new MemoryStream(key.PublicKey); _ = SshWire.ReadString(input); var exponent = SshWire.ReadString(input); var modulus = SshWire.ReadString(input);
            static byte[] Unsigned(byte[] b) => b.Length > 1 && b[0] == 0 ? b[1..] : b;
            using var rsa = RSA.Create(); rsa.ImportParameters(new() { Exponent = Unsigned(exponent), Modulus = Unsigned(modulus) });
            // Reproduce duplicate public objects without modifying a hardware token.
            var factories = new Pkcs11InteropFactories();
            using var fixtureLibrary = factories.Pkcs11LibraryFactory.LoadPkcs11Library(factories, module, AppType.MultiThreaded);
            using (var session = fixtureLibrary.GetSlotList(SlotsType.WithTokenPresent).Single(s => s.SlotId == device.Slot).OpenSession(SessionType.ReadWrite))
            {
                session.Login(CKU.CKU_USER, "test-fixture-only");
                var f = session.Factories.ObjectAttributeFactory;
                var attrs = new List<IObjectAttribute> { f.Create(CKA.CKA_CLASS, CKO.CKO_PUBLIC_KEY), f.Create(CKA.CKA_KEY_TYPE, CKK.CKK_RSA), f.Create(CKA.CKA_TOKEN, true), f.Create(CKA.CKA_ID, key.Id), f.Create(CKA.CKA_LABEL, "Duplicate public fixture"), f.Create(CKA.CKA_MODULUS, Unsigned(modulus)), f.Create(CKA.CKA_PUBLIC_EXPONENT, Unsigned(exponent)) };
                try { session.CreateObject(attrs); } finally { foreach (var a in attrs) a.Dispose(); session.Logout(); }
            }
            if (provider.Keys(device).Count != 1) throw new Exception("Duplicate public keys were not grouped");
            var source = provider.Source(key, () => "test-fixture-only");
            foreach (var algorithm in source.HostKeyAlgorithms)
            {
                var data = RandomNumberGenerator.GetBytes(96); using var signature = new MemoryStream(algorithm.Sign(data));
                _ = SshWire.ReadString(signature);
                if (!rsa.VerifyData(data, SshWire.ReadString(signature), algorithm.Name == "rsa-sha2-512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new Exception("PKCS#11 signature did not verify");
            }
            // Distinct key pairs may legitimately share CKA_ID (the reported device case).
            using (var session = fixtureLibrary.GetSlotList(SlotsType.WithTokenPresent).Single(s => s.SlotId == device.Slot).OpenSession(SessionType.ReadWrite))
            {
                session.Login(CKU.CKU_USER, "test-fixture-only");
                var f = session.Factories.ObjectAttributeFactory;
                var pub = new List<IObjectAttribute> { f.Create(CKA.CKA_TOKEN, true), f.Create(CKA.CKA_ID, key.Id), f.Create(CKA.CKA_LABEL, "Second key same ID"), f.Create(CKA.CKA_VERIFY, true), f.Create(CKA.CKA_MODULUS_BITS, 2048UL), f.Create(CKA.CKA_PUBLIC_EXPONENT, new byte[] { 1, 0, 1 }) };
                var priv = new List<IObjectAttribute> { f.Create(CKA.CKA_TOKEN, true), f.Create(CKA.CKA_ID, key.Id), f.Create(CKA.CKA_PRIVATE, true), f.Create(CKA.CKA_SENSITIVE, true), f.Create(CKA.CKA_EXTRACTABLE, false), f.Create(CKA.CKA_SIGN, true) };
                try { using var mechanism = session.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS_KEY_PAIR_GEN); session.GenerateKeyPair(mechanism, pub, priv, out _, out _); }
                finally { foreach (var a in pub.Concat(priv)) a.Dispose(); session.Logout(); }
            }
            var sameIdKeys = provider.Keys(device);
            if (sameIdKeys.Count != 2 || sameIdKeys.Any(k => !k.Id.SequenceEqual(key.Id))) throw new Exception("Distinct keys with same ID were lost");
            foreach (var selected in sameIdKeys)
                foreach (var algorithm in provider.Source(selected, () => "test-fixture-only").HostKeyAlgorithms)
                    _ = algorithm.Sign(RandomNumberGenerator.GetBytes(96)); // Provider verifies against that exact selected public key.
            using (var session = fixtureLibrary.GetSlotList(SlotsType.WithTokenPresent).Single(s => s.SlotId == device.Slot).OpenSession(SessionType.ReadWrite))
            {
                session.Login(CKU.CKU_USER, "test-fixture-only");
                // Object handles are session-specific: find the second pair by its modulus.
                var f = session.Factories.ObjectAttributeFactory;
                var other = sameIdKeys.Single(k => !k.PublicKey.SequenceEqual(key.PublicKey));
                using var wire = new MemoryStream(other.PublicKey); _ = SshWire.ReadString(wire); _ = SshWire.ReadString(wire); var otherModulus = Unsigned(SshWire.ReadString(wire));
                var attrs = new List<IObjectAttribute> { f.Create(CKA.CKA_KEY_TYPE, CKK.CKK_RSA), f.Create(CKA.CKA_MODULUS, otherModulus) };
                try { foreach (var handle in session.FindAllObjects(attrs)) session.DestroyObject(handle); }
                finally { foreach (var a in attrs) a.Dispose(); session.Logout(); }
            }
            File.WriteAllText(Path.Combine(root, "token.pub"), provider.Export(device));
            File.WriteAllText(Path.Combine(root, "token-test.txt"), "PASS: distinct RSA key pairs sharing ID, duplicate public objects, deduplication and verified SHA-2 signatures; auto-detection, provider isolation, JaCarta route, device replacement rejection, generation and export. Software token only.\n"); return 0;
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(root, "token-test.txt"), "FAIL: " + e); return 1; }
    }
}
