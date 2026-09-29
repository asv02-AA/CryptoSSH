using System.Globalization;
using System.Security.Cryptography;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using Renci.SshNet;

namespace CryptoSSH;

internal record TokenKey(ulong Slot, string Serial, string Label, byte[] Id, byte[] PublicKey)
{
    public string ProviderPath { get; init; } = "";
    public string Vendor { get; init; } = "Токен";
    public string PublicLine => "ssh-rsa " + Convert.ToBase64String(PublicKey) + " " + new string(Label.Select(c => char.IsControl(c) ? '_' : c).ToArray()) + "\n";
    public override string ToString() => $"{Vendor} · {Label} · {Serial} · слот {Slot} · ID {Convert.ToHexString(Id)} · {SshWire.Fingerprint(PublicKey)}";
}

internal sealed class TokenProvider
{
    private static readonly Dictionary<string, TokenProvider> Providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly IPkcs11Library library;
    private readonly string path;
    private TokenProvider(string path) { this.path = path; library = new Pkcs11InteropFactories().Pkcs11LibraryFactory.LoadPkcs11Library(new Pkcs11InteropFactories(), path, AppType.MultiThreaded); }
    public static TokenProvider Get(string path)
    {
        path = System.IO.Path.GetFullPath(Rules.RequireFile(path, "Библиотека PKCS#11"));
        lock (Providers) { if (!Providers.TryGetValue(path, out var value)) Providers[path] = value = new(path); return value; }
    }
    public List<TokenDevice> Devices(string vendor)
    {
        lock (gate) return library.GetSlotList(SlotsType.WithTokenPresent).Select(slot =>
        {
            var info = slot.GetTokenInfo(); var mechanisms = slot.GetMechanismList();
            bool sign = mechanisms.Contains(CKM.CKM_RSA_PKCS) && slot.GetMechanismInfo(CKM.CKM_RSA_PKCS).MechanismFlags.Sign;
            int[] sizes = [];
            if (sign && info.TokenFlags.TokenInitialized && !info.TokenFlags.WriteProtected && mechanisms.Contains(CKM.CKM_RSA_PKCS_KEY_PAIR_GEN))
            {
                var generation = slot.GetMechanismInfo(CKM.CKM_RSA_PKCS_KEY_PAIR_GEN);
                if (generation.MechanismFlags.GenerateKeyPair) sizes = new[] { 2048, 3072, 4096 }.Where(n => (ulong)n >= generation.MinKeySize && (ulong)n <= generation.MaxKeySize).ToArray();
            }
            return new TokenDevice(path, vendor, slot.SlotId, info.SerialNumber.Trim(), info.Label.Trim(), info.Model.Trim(), info.TokenFlags.TokenInitialized, sign, sizes);
        }).ToList();
    }
    private ISlot CheckedSlot(TokenDevice device)
    {
        if (!string.Equals(path, device.ProviderPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Библиотека выбранного токена изменилась.");
        return library.GetSlotList(SlotsType.WithTokenPresent).SingleOrDefault(s => s.SlotId == device.Slot && s.GetTokenInfo().SerialNumber.Trim() == device.Serial)
            ?? throw new InvalidOperationException("Выбранный токен извлечён или заменён. Обновите список устройств.");
    }
    public string ListSlots()
    {
        lock (gate)
        {
            var slots = library.GetSlotList(SlotsType.WithTokenPresent);
            return slots.Count == 0 ? "Токены не найдены." : string.Join("\n\n", slots.Select(s => { var info = s.GetTokenInfo(); return $"Слот {s.SlotId} (0x{s.SlotId:X})\n{info.Label.Trim()}\nМодель: {info.Model.Trim()}\nСерийный номер: {info.SerialNumber.Trim()}"; }));
        }
    }
    private static List<IObjectHandle> Find(ISession session, CKO type, byte[]? id = null)
    {
        var factory = session.Factories.ObjectAttributeFactory;
        var attributes = new List<IObjectAttribute> { factory.Create(CKA.CKA_CLASS, type), factory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_RSA) };
        if (id is not null) attributes.Add(factory.Create(CKA.CKA_ID, id));
        try { return session.FindAllObjects(attributes); } finally { foreach (var a in attributes) a.Dispose(); }
    }
    private static byte[] Public(ISession session, IObjectHandle handle)
    {
        var attributes = session.GetAttributeValue(handle, new List<CKA> { CKA.CKA_MODULUS, CKA.CKA_PUBLIC_EXPONENT });
        try { return SshWire.RsaPublic(attributes[0].GetValueAsByteArray(), attributes[1].GetValueAsByteArray()); }
        finally { foreach (var a in attributes) a.Dispose(); }
    }
    public List<TokenKey> Keys(TokenDevice? device = null)
    {
        lock (gate)
        {
            var keys = new List<TokenKey>();
            foreach (var slot in device is null ? library.GetSlotList(SlotsType.WithTokenPresent) : new List<ISlot> { CheckedSlot(device) })
            {
                if (!slot.GetTokenInfo().TokenFlags.TokenInitialized) continue;
                using var session = slot.OpenSession(SessionType.ReadOnly);
                foreach (var handle in Find(session, CKO.CKO_PUBLIC_KEY))
                {
                    var attributes = session.GetAttributeValue(handle, new List<CKA> { CKA.CKA_ID, CKA.CKA_LABEL });
                    try { keys.Add(new(slot.SlotId, slot.GetTokenInfo().SerialNumber.Trim(), attributes[1].GetValueAsString().Trim(), attributes[0].GetValueAsByteArray(), Public(session, handle)) { ProviderPath = path, Vendor = device?.Vendor ?? "Токен" }); }
                    finally { foreach (var a in attributes) a.Dispose(); }
                }
            }
            return keys.DistinctBy(k => (k.Slot, k.Serial, Convert.ToHexString(k.PublicKey))).ToList();
        }
    }
    private static bool Login(ISession session, string pin)
    {
        try { session.Login(CKU.CKU_USER, pin); return true; }
        catch (Pkcs11Exception e) when (e.RV == CKR.CKR_USER_ALREADY_LOGGED_IN) { return false; }
    }
    private static byte[] ObjectId(ISession session, IObjectHandle handle)
    {
        var attributes = session.GetAttributeValue(handle, new List<CKA> { CKA.CKA_ID });
        try { return attributes[0].GetValueAsByteArray(); }
        finally { foreach (var a in attributes) a.Dispose(); }
    }
    private static IObjectHandle ResolvePrivate(ISession session, TokenKey key)
    {
        // Some providers expose duplicate public objects, or handle ID templates
        // differently after login. Match the actual RSA public material, not count/ID.
        var allPublic = Find(session, CKO.CKO_PUBLIC_KEY);
        var matching = allPublic.Where(h => Public(session, h).SequenceEqual(key.PublicKey)).ToArray();
        if (matching.Length == 0) throw new InvalidOperationException($"После ввода PIN выбранный публичный RSA-ключ не найден. ID {Convert.ToHexString(key.Id)}, доступных публичных объектов: {allPublic.Count}. Повторите выбор ключа. Это не подтверждает изменение ключа на устройстве.");
        var ids = matching.Select(h => ObjectId(session, h)).Append(key.Id).ToArray();
        var candidates = Find(session, CKO.CKO_PRIVATE_KEY).Where(h => ids.Any(id => id.SequenceEqual(ObjectId(session, h)))).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("Закрытый RSA-ключ для выбранной публичной части не найден после входа по PIN.");
        if (candidates.Length == 1) return candidates[0];
        foreach (var handle in candidates)
        {
            try { if (Public(session, handle).SequenceEqual(key.PublicKey)) return handle; }
            catch (Exception e) when (e is Pkcs11Exception or AttributeValueException) { /* Public attributes may be unavailable on private objects. */ }
        }
        throw new InvalidOperationException($"Найдено несколько закрытых RSA-ключей с одинаковым ID ({candidates.Length}), но библиотека не позволяет сопоставить их публичную часть. Выберите ключ с уникальным ID.");
    }
    private static bool VerifyTokenSignature(byte[] publicKey, string algorithm, byte[] data, byte[] signature)
    {
        using var input = new System.IO.MemoryStream(publicKey); _ = SshWire.ReadString(input);
        static byte[] Unsigned(byte[] value) => value.Length > 1 && value[0] == 0 ? value[1..] : value;
        var exponent = Unsigned(SshWire.ReadString(input)); var modulus = Unsigned(SshWire.ReadString(input));
        using var rsa = RSA.Create(); rsa.ImportParameters(new() { Exponent = exponent, Modulus = modulus });
        return rsa.VerifyData(data, signature, algorithm == "rsa-sha2-512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
    public IPrivateKeySource Source(TokenKey key, Func<string> getPin) => new SigningKeySource(key.PublicKey, (algorithm, data) =>
    {
        var pin = getPin();
        lock (gate)
        {
            var slot = library.GetSlotList(SlotsType.WithTokenPresent).SingleOrDefault(s => s.SlotId == key.Slot && s.GetTokenInfo().SerialNumber.Trim() == key.Serial) ?? throw new InvalidOperationException("Выбранный токен извлечён или заменён.");
            using var session = slot.OpenSession(SessionType.ReadOnly); bool loggedIn = Login(session, pin); pin = "";
            try
            {
                var privateKey = ResolvePrivate(session, key);
                // CKM_RSA_PKCS applies PKCS#1 v1.5 padding inside the token. Hashing is local;
                // no private key material ever leaves the device.
                byte[] digestInfo = algorithm switch
                {
                    "rsa-sha2-512" => [.. Convert.FromHexString("3051300D060960864801650304020305000440"), .. SHA512.HashData(data)],
                    "rsa-sha2-256" => [.. Convert.FromHexString("3031300D060960864801650304020105000420"), .. SHA256.HashData(data)],
                    _ => throw new NotSupportedException("Поддерживается аппаратная подпись RSA SHA-2.")
                };
                using var mechanism = session.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS);
                var signature = session.Sign(mechanism, privateKey, digestInfo);
                if (!VerifyTokenSignature(key.PublicKey, algorithm, data, signature)) throw new InvalidOperationException("Подпись токена не соответствует выбранному публичному ключу. Подпись не отправлена серверу; выберите правильную ключевую пару.");
                return [.. SshWire.String(algorithm), .. SshWire.String(signature)];
            }
            finally { if (loggedIn) session.Logout(); }
        }
    });
    public void Generate(TokenDevice device, string idText, string label, int bits, string pin)
    {
        lock (gate)
        {
            _ = CheckedSlot(device);
            var current = Devices(device.Vendor).Single(d => d.Identity == device.Identity);
            if (!current.RsaSizes.Contains(bits)) throw new InvalidOperationException("Токен не поддерживает генерацию RSA выбранной длины или недоступен для записи.");
            Generate(device.Slot.ToString(CultureInfo.InvariantCulture), idText, label, bits, pin, device.Serial);
        }
    }
    public void Generate(string slotText, string idText, string label, int bits, string pin, string? expectedSerial = null)
    {
        Rules.ValidateGeneration(slotText, idText, label, bits);
        ulong slotId = slotText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(slotText[2..], NumberStyles.HexNumber) : ulong.Parse(slotText);
        var id = Convert.FromHexString(idText);
        lock (gate)
        {
            var slot = library.GetSlotList(SlotsType.WithTokenPresent).SingleOrDefault(s => s.SlotId == slotId) ?? throw new InvalidOperationException("Токен в выбранном слоте не найден.");
            if (expectedSerial is not null && slot.GetTokenInfo().SerialNumber.Trim() != expectedSerial) throw new InvalidOperationException("Выбранный токен заменён. Обновите список устройств.");
            using var session = slot.OpenSession(SessionType.ReadWrite); bool loggedIn = Login(session, pin);
            try
            {
                if (Find(session, CKO.CKO_PUBLIC_KEY, id).Count != 0 || Find(session, CKO.CKO_PRIVATE_KEY, id).Count != 0) throw new InvalidOperationException("Этот ID уже занят. Выберите другой ID.");
                var f = session.Factories.ObjectAttributeFactory;
                List<IObjectAttribute> pub = [f.Create(CKA.CKA_TOKEN, true), f.Create(CKA.CKA_LABEL, label), f.Create(CKA.CKA_ID, id), f.Create(CKA.CKA_VERIFY, true), f.Create(CKA.CKA_MODULUS_BITS, (ulong)bits), f.Create(CKA.CKA_PUBLIC_EXPONENT, new byte[] { 1, 0, 1 })];
                List<IObjectAttribute> priv = [f.Create(CKA.CKA_TOKEN, true), f.Create(CKA.CKA_PRIVATE, true), f.Create(CKA.CKA_LABEL, label), f.Create(CKA.CKA_ID, id), f.Create(CKA.CKA_SENSITIVE, true), f.Create(CKA.CKA_EXTRACTABLE, false), f.Create(CKA.CKA_SIGN, true)];
                try { using var mechanism = session.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS_KEY_PAIR_GEN); session.GenerateKeyPair(mechanism, pub, priv, out _, out _); }
                finally { foreach (var a in pub.Concat(priv)) a.Dispose(); }
            }
            finally { if (loggedIn) session.Logout(); }
        }
    }
    public string Export(TokenDevice? device = null)
    {
        var keys = Keys(device); if (keys.Count == 0) throw new InvalidOperationException("На токене не найдены публичные RSA SSH-ключи.");
        return string.Concat(keys.Select(k => k.PublicLine));
    }
}
