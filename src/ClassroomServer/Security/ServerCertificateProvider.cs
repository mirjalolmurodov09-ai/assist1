using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClassroomControl.ClassroomServer.Data;

namespace ClassroomControl.ClassroomServer.Security;

/// <summary>Creates (once) and loads the Teacher's self-signed TLS certificate. Students pin its fingerprint on first use, so it must
/// survive restarts. The PFX password is random and stored encrypted (DPAPI) in the settings table.</summary>
public static class ServerCertificateProvider
{
    private const string PasswordSetting = "Tls.PfxPassword";
    private static readonly TimeSpan Validity = TimeSpan.FromDays(3650);

    public static X509Certificate2 LoadOrCreate(string pfxPath, ClassroomStore store, ISecretProtector protector)
    {
        var protectedPassword = store.GetSetting(PasswordSetting);
        var password = protectedPassword is null ? null : protector.Unprotect(protectedPassword);

        if (File.Exists(pfxPath) && password is not null)
        {
            try
            {
                return new X509Certificate2(pfxPath, password, X509KeyStorageFlags.Exportable);
            }
            catch (CryptographicException)
            {
                // Damaged or unreadable: fall through and create a new identity (students will have to re-trust it).
            }
        }

        password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=ClassroomControl Teacher ({Environment.MachineName})", key, HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow + Validity);
        var pfx = generated.Export(X509ContentType.Pfx, password);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pfxPath))!);
        File.WriteAllBytes(pfxPath, pfx);
        store.SetSetting(PasswordSetting, protector.Protect(password));
        return new X509Certificate2(pfx, password, X509KeyStorageFlags.Exportable);
    }
}
