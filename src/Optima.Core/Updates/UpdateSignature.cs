using System.Security.Cryptography;
using System.Text;

namespace Optima.Core.Updates;

/// <summary>
/// What makes a downloaded setup Optima's own. The setup is not code-signed and is run as
/// administrator, so where it came from is not enough: whoever can replace a file on the releases
/// page could replace it. Every release setup therefore has a signature made with a private key
/// that is on the release machine and nowhere else, and this build carries the matching public key.
///
/// The signature covers the setup's SHA-256 and the version it was released as. The version is in
/// there so that an old, genuinely signed setup cannot be offered again under a higher number.
/// </summary>
public static class UpdateSignature
{
    /// <summary>ECDSA P-256, SubjectPublicKeyInfo, base64. Its private half signs the releases (tools\Optima.SignUpdate).</summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOv8ug2dGAaOzAnv/rTe561J2n4sYcobtoKA+e0QWLg6STl/qdtKWCRjvEJtjMJ3jN1sMzztXGDkAB71DDuavbw==";

    /// <summary>The exact bytes that are signed.</summary>
    public static byte[] Payload(byte[] setupSha256, Version version)
        => Encoding.ASCII.GetBytes("optima-update\n" + Three(version) + "\n" + Convert.ToHexString(setupSha256));

    public static string Sign(byte[] setupSha256, Version version, ECDsa privateKey)
        => Convert.ToBase64String(privateKey.SignData(Payload(setupSha256, version), HashAlgorithmName.SHA256));

    /// <summary>False for anything that is not a valid signature by <paramref name="publicKey"/>; never throws.</summary>
    public static bool Verify(byte[] setupSha256, Version version, string signature, string publicKey = PublicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(
                Payload(setupSha256, version), Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>"0.8.0" for 0.8.0.0, for 0.8.0 and for 0.8: one spelling, whichever way the version was built.</summary>
    public static string Three(Version version)
        => $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
}
