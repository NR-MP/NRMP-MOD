using System;
using System.IO;
using System.Security.Cryptography;
using NightRunnersMP.Shared;

namespace NightRunnersMP;

// this pc's key for a steam id, made the first time it joins. the server ties the id to it, so moving pcs means copying
// the identity folder along or asking the server owner to forget the old key.
// not in the game folder, people pass those around. not next to the launcher either, removing that shouldn't lose it
internal static class PcKey
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NightRunnersMP", "identity");

    // public key and signature for the server's nonce, null when there's no key to be had
    public static (byte[] Key, byte[] Signature)? Sign(string steamId, byte[] nonce)
    {
        if (!Identity.IsSteamId(steamId) || nonce == null)
            return null;
        var file = Path.Combine(Folder, steamId + ".key");
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (File.Exists(file))
            {
                key.ImportPkcs8PrivateKey(File.ReadAllBytes(file), out _);
            }
            else
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(file, key.ExportPkcs8PrivateKey());
                NetBehaviour.Log.LogInfo($"Made this pc's key for {steamId}, it's in {Folder}");
            }
            return (key.ExportSubjectPublicKeyInfo(), Identity.Sign(key, steamId, nonce));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is CryptographicException)
        {
            NetBehaviour.Log.LogWarning($"No key for {steamId}, crews stay off: {e.Message}");
            return null;
        }
    }
}
