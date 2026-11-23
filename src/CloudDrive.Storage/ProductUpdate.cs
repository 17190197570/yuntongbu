using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CloudDrive.Storage;

public sealed record UpdateTrust(string ManifestPublicKeyPem, string PublisherCertificateSha256, string[] AllowedHosts);
public sealed record UpdateEnvelope(string Payload, string Signature);
public sealed record UpdateManifest(string Version, string Channel, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
    long Sequence, string PackageUrl, long PackageLength, string PackageSha256, string Notes);
public static class ProductUpdate
{
    private static HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromSeconds(30) };
    public static UpdateTrust LoadTrust()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "Resources", "update-trust.json");
        if (!File.Exists(file)) throw new IOException("尚未配置受信任发布公钥与发布者证书；更新不可用。");
        return JsonSerializer.Deserialize<UpdateTrust>(File.ReadAllText(file)) ?? throw new IOException("更新信任配置无效。");
    }
    public static UpdateManifest Verify(UpdateEnvelope envelope, UpdateTrust trust, string channel, DateTimeOffset now, long minimumSequence = 0)
    {
        if (trust.ManifestPublicKeyPem.Length < 100 || trust.PublisherCertificateSha256.Length != 64 || trust.AllowedHosts.Length == 0) throw new IOException("发布信任配置不完整。");
        byte[] payload = Convert.FromBase64String(envelope.Payload), signature = Convert.FromBase64String(envelope.Signature);
        if (payload.Length > 65536) throw new IOException("更新清单超出限制。");
        using var rsa = RSA.Create(); rsa.ImportFromPem(trust.ManifestPublicKeyPem);
        if (rsa.KeySize < 3072 || !rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException("更新清单签名错误，拒绝更新。");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(payload) ?? throw new IOException("更新清单无效。");
        if (!Version.TryParse(manifest.Version, out var version) || version.Build < 0 || version.Revision >= 0 || manifest.Channel != channel ||
            manifest.IssuedAt > now.AddMinutes(5) || manifest.ExpiresAt <= now || manifest.ExpiresAt > manifest.IssuedAt.AddDays(30) || manifest.Sequence < minimumSequence ||
            manifest.PackageLength is <= 0 or > (4L << 30) || manifest.PackageSha256.Length != 64 || !manifest.PackageSha256.All(Uri.IsHexDigit)) throw new IOException("更新版本/渠道/有效期/序号/长度不符合要求。");
        CheckUrl(manifest.PackageUrl, trust); return manifest;
    }
    private static Uri CheckUrl(string value, UpdateTrust trust)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || !trust.AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase)) throw new IOException("更新地址不属于受信任HTTPS发布域名。");
        return uri;
    }
    public static async Task<(UpdateManifest Manifest, UpdateEnvelope Envelope)> CheckAsync(ProductSettings settings, CancellationToken token)
    {
        var trust = LoadTrust(); var url = CheckUrl(settings.UpdateManifestUrl ?? throw new IOException("尚未设置更新清单地址。"), trust);
        using var client = Client(); using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 131072) throw new IOException("更新清单过大。");
        await using var source = await response.Content.ReadAsStreamAsync(token); using var memory = new MemoryStream(); byte[] buffer = new byte[4096]; int n;
        while ((n = await source.ReadAsync(buffer, token)) != 0) { if (memory.Length + n > 131072) throw new IOException("更新清单过大。"); await memory.WriteAsync(buffer.AsMemory(0, n), token); }
        var envelope = JsonSerializer.Deserialize<UpdateEnvelope>(memory.ToArray()) ?? throw new IOException("更新信封无效。");
        string state = Path.Combine(ProductEnvironment.DataRoot, "Config", "update-sequence.json"); Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        using var sequenceLock = new FileStream(state + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        long minimum = ReadSequence();
        var manifest = Verify(envelope, trust, settings.UpdateChannel, DateTimeOffset.UtcNow, minimum);
        ProductEnvironment.AtomicJson(state, manifest.Sequence); return (manifest, envelope);
    }
    public static async Task<string> DownloadAsync(UpdateEnvelope envelope, string channel, string directory, IProgress<(long Bytes, long Total)>? progress, CancellationToken token)
    {
        var trust = LoadTrust(); var manifest = Verify(envelope, trust, channel, DateTimeOffset.UtcNow, ReadSequence());
        Directory.CreateDirectory(directory); string package = Path.Combine(directory, manifest.PackageSha256.ToUpperInvariant() + ".msi"); string partial = package + ".partial";
        using var gate = new FileStream(package + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(package)) { await VerifyPackage(package, manifest, trust, token); return package; }
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var client = Client(); long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > manifest.PackageLength) throw new IOException("部分更新文件长度异常；保留文件供诊断。");
                if (offset != manifest.PackageLength)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, CheckUrl(manifest.PackageUrl, trust)); if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
                    if (offset > 0 && response.StatusCode == HttpStatusCode.OK) offset = 0;
                    else if (offset > 0 && (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != manifest.PackageLength)) throw new IOException("更新续传范围不匹配。");
                    await using var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, Wire.Chunk, true); output.SetLength(offset); output.Position = offset;
                    await using var input = await response.Content.ReadAsStreamAsync(token); byte[] buffer = new byte[Wire.Chunk];
                    while (true)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                        int count = await input.ReadAsync(buffer, deadline.Token); if (count == 0) break;
                        if (output.Position + count > manifest.PackageLength) throw new IOException("更新包超过签名清单长度。");
                        await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token); progress?.Report((output.Position, manifest.PackageLength));
                    }
                    output.Flush(true);
                }
                await VerifyPackage(partial, manifest, trust, token);
                File.Move(partial, package); ProductEnvironment.AtomicJson(package + ".manifest.json", envelope); return package;
            }
            catch (Exception e) when (attempt < 2 && !token.IsCancellationRequested && (e is HttpRequestException || e is TaskCanceledException)) { await Task.Delay(TimeSpan.FromSeconds(2 << attempt), token); }
        }
    }
    public static async Task VerifyPackage(string path, UpdateManifest manifest, UpdateTrust trust, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.Chunk, true);
        if (stream.Length != manifest.PackageLength || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("更新包SHA-256或长度不一致，拒绝安装。");
        Authenticode.Verify(path, trust.PublisherCertificateSha256);
    }
    public static async Task<int> InstallStagedAsync(string package, CancellationToken token)
    {
        var settings = ProductSettings.Load(); var trust = LoadTrust();
        var envelope = JsonSerializer.Deserialize<UpdateEnvelope>(await File.ReadAllTextAsync(package + ".manifest.json", token)) ?? throw new IOException("缺少已签名更新清单。");
        var manifest = Verify(envelope, trust, settings.UpdateChannel, DateTimeOffset.UtcNow, ReadSequence());
        string expected = Path.Combine(ProductEnvironment.DataRoot, "Cache", "Updates", manifest.PackageSha256.ToUpperInvariant() + ".msi");
        if (!Path.GetFullPath(package).Equals(expected, StringComparison.OrdinalIgnoreCase) || System.Version.Parse(manifest.Version) <= System.Version.Parse(ProductEnvironment.Version)) throw new IOException("更新包路径或版本不符合要求。");
        using var heldPackage = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        await VerifyPackage(package, manifest, trust, token);
        var index = new IndexStore(ProductEnvironment.DatabasePath);
        await ProductMaintenance.StopAsync(index, token);
        using (ProductMaintenance.AcquireOffline(index))
        {
            string backup = ProductBackup.Create(index, Path.Combine(ProductEnvironment.DataRoot, "Backups"));
            ProductEnvironment.AtomicJson(ProductMaintenance.Marker, new { At = DateTimeOffset.UtcNow, Backup = backup, PackageVersion = manifest.Version });
        }
        string logs = Path.Combine(ProductEnvironment.LogRoot, "Installer"); Directory.CreateDirectory(logs);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "/i", package, "/passive", "REBOOT=ReallySuppress", "/L*v", Path.Combine(logs, "upgrade-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log") }) start.ArgumentList.Add(arg);
        using var installer = System.Diagnostics.Process.Start(start) ?? throw new IOException("无法启动Windows Installer。"); await installer.WaitForExitAsync(CancellationToken.None);
        ProductEnvironment.AtomicJson(Path.Combine(logs, "last-upgrade.json"), new { At = DateTimeOffset.UtcNow, installer.ExitCode, manifest.Version, RestartRequired = installer.ExitCode == 3010 });
        if (installer.ExitCode == 0 && !File.Exists(ProductMaintenance.Marker)) using (System.Diagnostics.Process.Start(ProductEnvironment.StartInfo("CloudDrive.Bootstrap"))) { }
        // Failed installs preserve the backup and maintenance marker. Never automatically restore old task state.
        return installer.ExitCode;
    }
    private static long ReadSequence()
    { string path = Path.Combine(ProductEnvironment.DataRoot, "Config", "update-sequence.json"); return File.Exists(path) ? JsonSerializer.Deserialize<long>(File.ReadAllText(path)) : 0; }
}

public static class Authenticode
{
    public static void Verify(string path, string publisherSha256)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = Path.GetFullPath(path) }; nint data = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, data, false);
            var trust = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, Revocation = 1, UnionChoice = 1, File = data, StateAction = 1, Flags = 0x80 };
            Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            try
            {
                int status = WinVerifyTrust(new nint(-1), ref action, ref trust); if (status != 0) throw new CryptographicException("Authenticode信任校验失败：0x" + status.ToString("X8"));
                nint provider = WTHelperProvDataFromStateData(trust.StateData);
                nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                if (signer == 0) throw new CryptographicException("无法取得已验证发布者。");
                var chain = Marshal.PtrToStructure<ProviderSigner>(signer);
                if (chain.Count == 0 || chain.Certificates == 0) throw new CryptographicException("发布者证书链为空。");
                var certificate = Marshal.PtrToStructure<ProviderCertificate>(chain.Certificates);
                var context = Marshal.PtrToStructure<CertificateContext>(certificate.Certificate);
                if (context.Size is 0 or > 1048576) throw new CryptographicException("发布者证书长度无效。");
                byte[] encoded = new byte[context.Size]; Marshal.Copy(context.Encoded, encoded, 0, encoded.Length);
                if (!Convert.ToHexString(SHA256.HashData(encoded)).Equals(publisherSha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("安装包发布者与固定证书不一致。");
            }
            finally { trust.StateAction = 2; WinVerifyTrust(new nint(-1), ref action, ref trust); }
        }
        finally { Marshal.DestroyStructure<TrustFile>(data); Marshal.FreeHGlobal(data); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public nint FileHandle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData { public uint Size; public nint Policy, Sip; public uint UiChoice, Revocation, UnionChoice; public nint File; public uint StateAction; public nint StateData, Url; public uint Flags, Context; public nint SignatureSettings; }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderSigner { public uint Size; public System.Runtime.InteropServices.ComTypes.FILETIME At; public uint Count; public nint Certificates; }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificate { public uint Size; public nint Certificate; }
    [StructLayout(LayoutKind.Sequential)] private struct CertificateContext { public uint Encoding; public nint Encoded; public uint Size; public nint Info, Store; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern nint WTHelperProvDataFromStateData(nint state);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
}
