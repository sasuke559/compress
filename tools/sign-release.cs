// Signs release builds so Compress only installs updates that come from the developer.
//
//   dotnet run tools/sign-release.cs -- keygen          create the key pair (once; prints the public key for Core/Updater.cs)
//   dotnet run tools/sign-release.cs -- sign <exe>      write <exe>.sig next to the exe, upload both to the release
//
// The private key lives in %USERPROFILE%\.compress\update-signing-key.pem, never in the repo.
// Back it up: without it no update can be signed, and users would have to download the next version by hand.
using System.Security.Cryptography;

var keyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".compress");
var keyPath = Path.Combine(keyDir, "update-signing-key.pem");

switch (args.FirstOrDefault())
{
    case "keygen":
        if (File.Exists(keyPath)) { Console.Error.WriteLine($"Key already exists: {keyPath}"); return 1; }
        Directory.CreateDirectory(keyDir);
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
            Console.WriteLine($"Private key: {keyPath}  (back it up, never share it)");
            Console.WriteLine($"Public key:  {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}");
        }
        return 0;

    case "sign" when args.Length == 2:
        using (var key = ECDsa.Create())
        {
            key.ImportFromPem(File.ReadAllText(keyPath));
            var signature = key.SignData(File.ReadAllBytes(args[1]), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            File.WriteAllText(args[1] + ".sig", Convert.ToBase64String(signature));
            Console.WriteLine($"Signed: {args[1]}.sig");
        }
        return 0;

    default:
        Console.Error.WriteLine("usage: keygen | sign <file>");
        return 1;
}
