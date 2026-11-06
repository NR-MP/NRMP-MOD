using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BepInEx.Logging;
using LiteNetLib.Utils;
using NightRunnersMP.Shared;
using PlanetJem.Customization;
using UnityEngine;

namespace NightRunnersMP;

// liveries are image files on the owner's pc, so the build only names them by hash and the bytes go through the server once.
// kept in a folder of my own by hash, same image never downloads twice. a file only counts once its hash checks out
internal sealed class LiverySync
{
    private const float PartSeconds = 0.1f;
    private const float AskSeconds = 5f;
    // real liveries are 2048 or 4096, a small file can still unpack into gigabytes past this
    private const int MaxSide = 4096;

    private static readonly HashSet<string> Mine = new();

    private readonly ManualLogSource _log;
    private readonly Action<Action<NetDataWriter>> _send;
    private readonly RemoteBuilds _builds;
    private readonly Dictionary<string, byte[]> _partial = new();
    private readonly Dictionary<string, int> _received = new();
    private readonly Dictionary<string, float> _asked = new();
    private readonly Dictionary<string, Texture2D> _textures = new();
    private readonly HashSet<string> _have = new();
    private readonly HashSet<string> _uploaded = new();
    private string _uploading;
    private byte[] _uploadData;
    private int _uploadOffset;
    private float _nextPart;

    public LiverySync(ManualLogSource log, Action<Action<NetDataWriter>> send, RemoteBuilds builds)
    {
        _log = log;
        _send = send;
        _builds = builds;
    }

    // two-players.bat runs two games on one pc, a shared folder would skip the download i'm testing
    private static string Folder
    {
        get
        {
            var test = Environment.GetEnvironmentVariable("NRMP_TEST") == "2" ? "-2" : "";
            var root = LiveryStore.RootDirectory.TrimEnd('\\', '/');
            var folder = Path.Combine(Path.GetDirectoryName(root), "NRMP-liveries" + test);
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static string PathOf(string hash) => Path.Combine(Folder, hash + ".png");

    // copy of my own livery in the folder, gives back its hash. null = nothing to send
    public static string Keep(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(LiveryStore.RootDirectory, path);
        if (!File.Exists(path))
            return null;
        var info = new FileInfo(path);
        if (info.Length > Protocol.MaxLiveryBytes)
        {
            NetBehaviour.Log.LogWarning($"Livery of {info.Length} bytes is too big to send");
            return null;
        }
        var data = File.ReadAllBytes(path);
        if (!IsPicture(data, out var why))
        {
            NetBehaviour.Log.LogWarning($"Livery not sent, {why}");
            return null;
        }
        var hash = Hash(data);
        var kept = PathOf(hash);
        if (!File.Exists(kept))
            File.WriteAllBytes(kept, data);
        Mine.Add(hash);
        return hash;
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data), 0, Protocol.LiveryHashBytes);

    // new connection, the server may have restarted
    public void Reset()
    {
        _uploaded.Clear();
        _partial.Clear();
        _received.Clear();
        _asked.Clear();
        _uploading = null;
    }

    public void Tick(IEnumerable<Remote> remotes, bool hold)
    {
        Upload();
        if (hold)
            return;
        foreach (var remote in remotes)
        {
            try
            {
                Follow(remote);
            }
            catch (Exception e)
            {
                _log.LogWarning($"Livery of {remote.Name} failed: {e.Message}");
                remote.LiveryCar = remote.Car;
            }
        }
    }

    private void Upload()
    {
        if (_uploading == null)
        {
            foreach (var hash in Mine)
            {
                if (!_uploaded.Add(hash))
                    continue;
                _uploading = hash;
                _uploadData = File.ReadAllBytes(PathOf(hash));
                _uploadOffset = 0;
                _log.LogInfo($"Sending livery {hash}, {_uploadData.Length} bytes");
                break;
            }
        }
        if (_uploading == null || Time.unscaledTime < _nextPart)
            return;
        _nextPart = Time.unscaledTime + PartSeconds;
        var count = Math.Min(Protocol.LiveryPartBytes, _uploadData.Length - _uploadOffset);
        var hashBytes = Convert.FromHexString(_uploading);
        var offset = _uploadOffset;
        var data = _uploadData;
        _send(w =>
        {
            w.Put((byte)MsgType.LiveryPart);
            w.Put(hashBytes);
            w.Put(data.Length);
            w.Put(offset);
            w.Put(data, offset, count);
        });
        _uploadOffset += count;
        if (_uploadOffset >= _uploadData.Length)
            _uploading = null;
    }

    private void Follow(Remote remote)
    {
        if (!ReferenceEquals(remote.LiveryFrom, remote.Build))
        {
            remote.LiveryFrom = remote.Build;
            remote.Livery = remote.Build != null ? CarBuild.Parse(remote.Build) : null;
            remote.LiveryCar = null;
        }
        var job = remote.Livery;
        if (job?.LiveryMain == null || (remote.Car != null && remote.LiveryCar == remote.Car))
            return;
        var ready = Fetch(job.LiveryMain) & (job.LiveryMask == null || Fetch(job.LiveryMask));
        if (!ready || remote.Car == null || remote.NeedsSpawn || _builds.IsFitting(remote))
            return;
        var car = remote.Car.GetComponentInChildren<CarLocalCustom>(true);
        if (car == null)
            return;
        remote.LiveryCar = remote.Car;
        var main = Load(job.LiveryMain, false);
        if (main == null)
            return;
        car.SetLiveryDecal(main, job.LiveryFinish, job.LiveryMask != null ? Load(job.LiveryMask, true) : null);
        _log.LogInfo($"Livery on {remote.Name}'s car");
    }

    // true once it's here, otherwise asks the server every so often
    private bool Fetch(string hash)
    {
        if (_have.Contains(hash))
            return true;
        if (File.Exists(PathOf(hash)))
        {
            _have.Add(hash);
            return true;
        }
        if (!_partial.ContainsKey(hash) && (!_asked.TryGetValue(hash, out var at) || Time.unscaledTime >= at))
        {
            _asked[hash] = Time.unscaledTime + AskSeconds;
            var bytes = Convert.FromHexString(hash);
            _send(w =>
            {
                w.Put((byte)MsgType.LiveryWant);
                w.Put(bytes);
            });
        }
        return false;
    }

    private Texture2D Load(string hash, bool linear)
    {
        if (_textures.TryGetValue(hash, out var known) && known != null)
            return known;
        // checked again here, the folder could have files from before the check existed
        if (!File.Exists(PathOf(hash)))
            return null;
        if (!IsPicture(File.ReadAllBytes(PathOf(hash)), out var why))
        {
            _log.LogWarning($"Livery {hash} won't load, {why}");
            return null;
        }
        var texture = LiveryStore.LoadTexture(PathOf(hash), out var status, linear);
        if (texture == null)
            _log.LogWarning($"Livery {hash} won't load: {status}");
        else
            _textures[hash] = texture;
        return texture;
    }

    public void OnPart(NetDataReader reader)
    {
        if (reader.AvailableBytes < Protocol.LiveryHashBytes + 2 * sizeof(int))
            return;
        var bytes = new byte[Protocol.LiveryHashBytes];
        reader.GetBytes(bytes, bytes.Length);
        var hash = Convert.ToHexString(bytes);
        var total = reader.GetInt();
        var offset = reader.GetInt();
        var count = reader.AvailableBytes;
        // only ones i asked for, nobody gets to fill my memory or folder
        if (!_asked.ContainsKey(hash) || _have.Contains(hash))
            return;
        if (total <= 0 || total > Protocol.MaxLiveryBytes || offset < 0 || count > total - offset)
            return;
        // offset 0 = a fresh send, whatever came before it got cut off
        if (offset == 0 || !_partial.TryGetValue(hash, out var data) || data.Length != total)
        {
            data = new byte[total];
            _partial[hash] = data;
            _received[hash] = 0;
        }
        reader.GetBytes(data, offset, count);
        _received[hash] += count;
        if (_received[hash] < total)
            return;

        _partial.Remove(hash);
        _received.Remove(hash);
        if (Hash(data) != hash)
        {
            _log.LogWarning($"Livery {hash} came through damaged");
            return;
        }
        if (!IsPicture(data, out var why))
        {
            _have.Add(hash);
            _log.LogWarning($"Livery {hash} thrown away, {why}");
            return;
        }
        var clean = Clean(data);
        if (clean == null)
        {
            _have.Add(hash);
            _log.LogWarning($"Livery {hash} thrown away, it isn't a picture unity can read");
            return;
        }
        File.WriteAllBytes(PathOf(hash), clean);
        _log.LogInfo($"Livery {hash} downloaded, {total} bytes, kept as a clean {clean.Length} byte png");
    }

    // pixels only, into a brand new png. whatever else was tucked in the file stays behind. a hitch on a big one, once per livery
    private static byte[] Clean(byte[] data)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            if (!ImageConversion.LoadImage(texture, data))
                return null;
            byte[] png = ImageConversion.EncodeToPNG(texture);
            return png != null && png.Length > 0 ? png : null;
        }
        finally
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    // only a png or jpg, and only up to MaxSide. sizes come from the header, nothing gets unpacked to find out.
    // the bytes only ever go to unity's image decoder and get saved as .png in my folder under their hash, nothing runs them
    public static bool IsPicture(byte[] data, out string why)
    {
        int width, height;
        if (data.Length > 24 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G'
            && data[12] == 'I' && data[13] == 'H' && data[14] == 'D' && data[15] == 'R')
        {
            width = BigEndian(data, 16);
            height = BigEndian(data, 20);
        }
        else if (data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            if (!JpegSize(data, out width, out height))
            {
                why = "a jpg with no size in it";
                return false;
            }
        }
        else
        {
            why = "not a png or jpg";
            return false;
        }
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide)
        {
            why = $"{width}x{height} is over {MaxSide}x{MaxSide}";
            return false;
        }
        why = null;
        return true;
    }

    private static int BigEndian(byte[] data, int at) => (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];

    // walks the markers to the frame header
    private static bool JpegSize(byte[] data, out int width, out int height)
    {
        width = height = 0;
        var at = 2;
        while (at + 9 < data.Length)
        {
            if (data[at] != 0xFF)
                return false;
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            var length = (data[at + 2] << 8) | data[at + 3];
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                height = (data[at + 5] << 8) | data[at + 6];
                width = (data[at + 7] << 8) | data[at + 8];
                return true;
            }
            if (length < 2)
                return false;
            at += 2 + length;
        }
        return false;
    }
}
