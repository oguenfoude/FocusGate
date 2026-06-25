using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FocusGate.Core.Services;

public static class LicenseService
{
    private const string LicenseFileName = "license.json";
    private const string PrivateKeyResource = "FocusGate.Core.Keys.private_key.xml";

    public static LicenseData GenerateForMachine(string machineId)
    {
        var issuedAt = DateTime.UtcNow;
        var expiresAt = (DateTime?)null;

        var messageBytes = Encoding.UTF8.GetBytes($"{machineId}|{issuedAt:O}|");

        var privateKeyXml = LoadEmbeddedPrivateKey();
        using var rsa = RSA.Create();
        rsa.FromXmlString(privateKeyXml);
        var signatureBytes = rsa.SignData(messageBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return new LicenseData
        {
            MachineId = machineId,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            Signature = Convert.ToBase64String(signatureBytes)
        };
    }

    public static bool VerifyLicense(string dataDirectory, string machineId, out string? error)
    {
        error = null;
        var licensePath = Path.Combine(dataDirectory, LicenseFileName);

        if (!File.Exists(licensePath))
        {
            error = "License file not found. Please contact your administrator.";
            return false;
        }

        try
        {
            var json = File.ReadAllText(licensePath);
            var license = JsonSerializer.Deserialize<LicenseData>(json);
            if (license == null)
            {
                error = "Invalid license file format.";
                return false;
            }

            if (string.IsNullOrEmpty(license.MachineId) || license.MachineId != machineId)
            {
                error = $"License is bound to machine {license.MachineId}, but this machine is {machineId}.";
                return false;
            }

            if (license.ExpiresAt.HasValue && license.ExpiresAt.Value < DateTime.UtcNow)
            {
                error = $"License expired on {license.ExpiresAt.Value:yyyy-MM-dd}.";
                return false;
            }

            var messageBytes = Encoding.UTF8.GetBytes($"{license.MachineId}|{license.IssuedAt:O}|{license.ExpiresAt?.ToString("O") ?? ""}");
            var signatureBytes = Convert.FromBase64String(license.Signature);

            var publicKeyXml = LoadEmbeddedPublicKey();
            using var rsa = RSA.Create();
            rsa.FromXmlString(publicKeyXml);

            if (!rsa.VerifyData(messageBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                error = "License signature verification failed.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"License verification error: {ex.Message}";
            return false;
        }
    }

    private static string LoadEmbeddedPublicKey()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("public_key.xml", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            throw new InvalidOperationException("Public key resource not found in assembly.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string LoadEmbeddedPrivateKey()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("private_key.xml", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            throw new InvalidOperationException("Private key resource not found in assembly.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public class LicenseData
    {
        public string MachineId { get; set; } = "";
        public DateTime IssuedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string Signature { get; set; } = "";
    }
}
