using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NightRunnersMP;

// other players' liveries used to sit in the folder as pngs anyone could copy. the key ships in the dll, so this only stops the easy way
internal static class LiveryVault
{
    public const string Extension = ".nrl";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly byte[] Key = SHA256.HashData(Encoding.ASCII.GetBytes("NightRunnersMP livery cache v1"));

    public static void Write(string path, string hash, byte[] data)
    {
        var packed = new byte[NonceBytes + TagBytes + data.Length];
        RandomNumberGenerator.Fill(packed.AsSpan(0, NonceBytes));
        using (var aes = new AesGcm(Key))
            aes.Encrypt(packed.AsSpan(0, NonceBytes), data, packed.AsSpan(NonceBytes + TagBytes), packed.AsSpan(NonceBytes, TagBytes), Convert.FromHexString(hash));
        var temp = path + ".part";
        File.WriteAllBytes(temp, packed);
        File.Move(temp, path, true);
    }

    // null = not there. a file that doesn't open under its own hash goes away, so it downloads again
    public static byte[] Read(string path, string hash)
    {
        if (!File.Exists(path))
            return null;
        var packed = File.ReadAllBytes(path);
        try
        {
            if (packed.Length <= NonceBytes + TagBytes)
                throw new CryptographicException();
            var data = new byte[packed.Length - NonceBytes - TagBytes];
            using (var aes = new AesGcm(Key))
                aes.Decrypt(packed.AsSpan(0, NonceBytes), packed.AsSpan(NonceBytes + TagBytes), packed.AsSpan(NonceBytes, TagBytes), data, Convert.FromHexString(hash));
            return data;
        }
        catch (CryptographicException)
        {
            File.Delete(path);
            return null;
        }
    }
}
