using System.Security.Cryptography;

namespace PeerDM7.Core;

/// <summary>
/// A signing key that lives only as long as the app is open. The peer ID is derived from its
/// public half, so during a session a name can be copied but an ID cannot. Nothing is written
/// to disk: close the app and this identity is gone.
/// </summary>
public sealed class Identity : IDisposable
{
    public ECDsa Key { get; }
    public byte[] PublicKey { get; }
    public string Id { get; }

    public Identity()
    {
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = Key.ExportSubjectPublicKeyInfo();
        Id = IdFromPublicKey(PublicKey);
    }

    public static string IdFromPublicKey(byte[] spki) =>
        Convert.ToHexString(SHA256.HashData(spki), 0, 10).ToLowerInvariant();

    public void Dispose() => Key.Dispose();
}
