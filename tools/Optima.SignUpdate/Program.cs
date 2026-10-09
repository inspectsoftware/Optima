using System.Security.Cryptography;
using Optima.Core.Updates;

// Release-side half of the update signature (the app-side half is UpdateSignature.Verify):
//
//   Optima.SignUpdate keygen <private-key.pem>
//       makes the key pair once. The private key goes to the file, which must stay outside every
//       repository and be backed up; the public key is printed, and goes into
//       UpdateSignature.PublicKey. An existing file is never overwritten: a new key means every
//       installed Optima refuses every later update.
//
//   Optima.SignUpdate sign <Optima-Setup-x.y.z.exe> <x.y.z> <private-key.pem>
//       writes <setup>.sig beside the setup, which is uploaded to the release with it.
//
//   Optima.SignUpdate verify <Optima-Setup-x.y.z.exe> <x.y.z>
//       checks a setup and its .sig the way the app will, against the key compiled into this build.
//
// installer.ps1 -Release runs sign and verify. Exit code 0 is success; anything else is not.

if (args.Length == 2 && args[0] == "keygen")
{
    var path = Path.GetFullPath(args[1]);
    if (File.Exists(path))
    {
        Console.Error.WriteLine($"{path} already exists. It is not overwritten.");
        return 2;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

if (args.Length == 4 && args[0] == "sign" && Version.TryParse(args[2], out var signVersion))
{
    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(args[3]));
    var signature = UpdateSignature.Sign(Hash(args[1]), signVersion, key);
    File.WriteAllText(args[1] + ".sig", signature);
    Console.WriteLine($"signed {Path.GetFileName(args[1])} as {UpdateSignature.Three(signVersion)}");
    return 0;
}

if (args.Length == 3 && args[0] == "verify" && Version.TryParse(args[2], out var verifyVersion))
{
    var ok = File.Exists(args[1] + ".sig")
        && UpdateSignature.Verify(Hash(args[1]), verifyVersion, File.ReadAllText(args[1] + ".sig"));
    Console.WriteLine(ok
        ? "the signature matches the key in this build"
        : "the signature does NOT match the key in this build");
    return ok ? 0 : 1;
}

Console.Error.WriteLine("usage: keygen <key.pem> | sign <setup.exe> <version> <key.pem> | verify <setup.exe> <version>");
return 64;

static byte[] Hash(string path)
{
    using var stream = File.OpenRead(path);
    return SHA256.HashData(stream);
}
