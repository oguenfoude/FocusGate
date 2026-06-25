using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length < 1)
{
    Console.WriteLine("Usage: LicenseGenerator <machineId> [expiryDays]");
    Console.WriteLine("  machineId:  The machine fingerprint (16 hex chars)");
    Console.WriteLine("  expiryDays: Days until license expires (0 = never, default: 0)");
    return;
}

var machineId = args[0];
var expiryDays = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 0;
var issuedAt = DateTime.UtcNow;
var expiresAt = expiryDays > 0 ? (DateTime?)issuedAt.AddDays(expiryDays) : null;

var privateKeyPath = Path.Combine(AppContext.BaseDirectory, "private_key.xml");
if (!File.Exists(privateKeyPath))
{
    privateKeyPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "private_key.xml"));
}
if (!File.Exists(privateKeyPath))
{
    Console.WriteLine($"Private key not found. Expected at: {Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "private_key.xml"))}");
    Console.WriteLine("Place private_key.xml next to the LicenseGenerator executable.");
    return;
}

var privateKeyXml = File.ReadAllText(privateKeyPath);

var expiresStr = expiresAt?.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "never";

var messageBytes = Encoding.UTF8.GetBytes($"{machineId}|{issuedAt:O}|{expiresAt?.ToString("O") ?? ""}");

using var rsa = RSA.Create();
rsa.FromXmlString(privateKeyXml);
var signatureBytes = rsa.SignData(messageBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

var license = new
{
    MachineId = machineId,
    IssuedAt = issuedAt,
    ExpiresAt = expiresAt,
    Signature = Convert.ToBase64String(signatureBytes)
};

var options = new JsonSerializerOptions { WriteIndented = true };
var json = JsonSerializer.Serialize(license, options);

var outputPath = args.Length > 2 ? args[2] : "license.json";
File.WriteAllText(outputPath, json);

Console.WriteLine($"License generated: {outputPath}");
Console.WriteLine($"  Machine: {machineId}");
Console.WriteLine($"  Issued:  {issuedAt:yyyy-MM-dd HH:mm:ss} UTC");
Console.WriteLine($"  Expires: {expiresStr}");
