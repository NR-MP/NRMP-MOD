using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
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
    private const float PruneSeconds = 10f;
    private const float UnusedSeconds = 300f;
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
    private readonly Dictionary<Remote, (GameObject Car, string Main, string Mask)> _worn = new();
    private readonly HashSet<string> _inUse = new();
    private readonly List<string> _unused = new();
    private readonly Dictionary<string, float> _unusedSince = new();
    private readonly List<Remote> _gone = new();
    private readonly HashSet<string> _cleaning = new();
    private readonly ConcurrentQueue<(string Hash, int Total, int Length, string Error)> _cleaned = new();
    private float _pruneAt;
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
        Cleaned();
        if (!hold)
        {
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
        Prune(remotes);
    }

    // a car keeps wearing its livery after the build drops it, so a texture stays until that car is gone
    private void Prune(IEnumerable<Remote> remotes)
    {
        if (Time.unscaledTime < _pruneAt)
            return;
        _pruneAt = Time.unscaledTime + PruneSeconds;
        _inUse.Clear();
        foreach (var remote in remotes)
        {
            if (!_worn.TryGetValue(remote, out var worn) || worn.Car == null || worn.Car != remote.Car)
                continue;
            _inUse.Add(worn.Main);
            if (worn.Mask != null)
                _inUse.Add(worn.Mask);
        }
        foreach (var pair in _worn)
            if (pair.Value.Car == null || pair.Value.Car != pair.Key.Car)
                _gone.Add(pair.Key);
        foreach (var remote in _gone)
            _worn.Remove(remote);
        _gone.Clear();
        var now = Time.unscaledTime;
        foreach (var key in _textures.Keys)
        {
            if (_inUse.Contains(key))
                _unusedSince.Remove(key);
            else if (!_unusedSince.TryGetValue(key, out var since))
                _unusedSince[key] = now;
            else if (now - since >= UnusedSeconds)
                _unused.Add(key);
        }
        foreach (var key in _unused)
        {
            var texture = _textures[key];
            _textures.Remove(key);
            _unusedSince.Remove(key);
            if (texture != null)
                UnityEngine.Object.Destroy(texture);
        }
        _unused.Clear();
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
        var mask = job.LiveryMask != null ? Load(job.LiveryMask, true) : null;
        car.SetLiveryDecal(main, job.LiveryFinish, mask);
        _worn[remote] = (remote.Car, Key(job.LiveryMain, false), mask != null ? Key(job.LiveryMask, true) : null);
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
        if (!_partial.ContainsKey(hash) && !_cleaning.Contains(hash) && (!_asked.TryGetValue(hash, out var at) || Time.unscaledTime >= at))
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

    private static string Key(string hash, bool linear) => linear ? hash + "/linear" : hash;

    private Texture2D Load(string hash, bool linear)
    {
        if (_textures.TryGetValue(Key(hash, linear), out var known) && known != null)
            return known;
        // checked again here, the folder could have files from before the check existed
        if (!File.Exists(PathOf(hash)))
            return null;
        if (!IsPicture(File.ReadAllBytes(PathOf(hash)), out var why))
        {
            _log.LogWarning($"Livery {hash} won't load, {why}");
            return null;
        }
        var start = SlowStep.Start();
        var texture = LiveryStore.LoadTexture(PathOf(hash), out var status, linear);
        SlowStep.Stop(start, $"loading livery {hash}");
        if (texture == null)
            _log.LogWarning($"Livery {hash} won't load: {status}");
        else
            _textures[Key(hash, linear)] = texture;
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
        if (!_asked.ContainsKey(hash) || _have.Contains(hash) || _cleaning.Contains(hash))
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
        var start = SlowStep.Start();
        var pixels = Pixels(data, out var width, out var height, out var channels);
        SlowStep.Stop(start, $"reading livery {hash}");
        if (pixels == null)
        {
            _have.Add(hash);
            _log.LogWarning($"Livery {hash} thrown away, it isn't a picture unity can read");
            return;
        }
        _cleaning.Add(hash);
        var path = PathOf(hash);
        Task.Run(() =>
        {
            try
            {
                var clean = Png(pixels, width, height, channels);
                var temp = path + ".part";
                File.WriteAllBytes(temp, clean);
                File.Move(temp, path, true);
                _cleaned.Enqueue((hash, total, clean.Length, null));
            }
            catch (Exception e)
            {
                _cleaned.Enqueue((hash, total, 0, e.Message));
            }
        });
    }

    private void Cleaned()
    {
        while (_cleaned.TryDequeue(out var done))
        {
            _cleaning.Remove(done.Hash);
            if (done.Error != null)
            {
                _have.Add(done.Hash);
                _log.LogWarning($"Livery {done.Hash} thrown away, saving it failed: {done.Error}");
            }
            else
            {
                _log.LogInfo($"Livery {done.Hash} downloaded, {done.Total} bytes, kept as a clean {done.Length} byte png");
            }
        }
    }

    // pixels only, into a brand new png. whatever else was tucked in the file stays behind
    private static byte[] Pixels(byte[] data, out int width, out int height, out int channels)
    {
        width = height = channels = 0;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            if (!ImageConversion.LoadImage(texture, data))
                return null;
            width = texture.width;
            height = texture.height;
            var format = texture.format;
            channels = format == TextureFormat.RGB24 ? 3 : 4;
            if (format is TextureFormat.RGBA32 or TextureFormat.RGB24)
            {
                var raw = texture.GetRawTextureData();
                if (raw != null && raw.Length >= width * height * channels)
                    return Copy(raw.Pointer, width * height * channels);
            }
            channels = 4;
            var colors = texture.GetPixels32();
            return colors != null && colors.Length == width * height ? Copy(colors.Pointer, width * height * 4) : null;
        }
        finally
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    // past the il2cpp array header
    private static byte[] Copy(IntPtr array, int size)
    {
        var bytes = new byte[size];
        Marshal.Copy(array + 4 * IntPtr.Size, bytes, 0, size);
        return bytes;
    }

    // unity's rows go bottom up
    private static byte[] Png(byte[] pixels, int width, int height, int channels)
    {
        var stride = width * channels;
        var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, System.IO.Compression.CompressionLevel.Fastest, true))
        {
            var row = new byte[stride + 1];
            row[0] = 1;
            for (var y = height - 1; y >= 0; y--)
            {
                var at = y * stride;
                for (var x = 0; x < stride; x++)
                    row[x + 1] = (byte)(pixels[at + x] - (x >= channels ? pixels[at + x - channels] : 0));
                zlib.Write(row, 0, row.Length);
            }
        }
        var png = new MemoryStream();
        png.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        PutBigEndian(header, 0, width);
        PutBigEndian(header, 4, height);
        header[8] = 8;
        header[9] = (byte)(channels == 4 ? 6 : 2);
        Chunk(png, "IHDR", header, header.Length);
        Chunk(png, "IDAT", packed.GetBuffer(), (int)packed.Length);
        Chunk(png, "IEND", Array.Empty<byte>(), 0);
        return png.ToArray();
    }

    private static void Chunk(Stream to, string type, byte[] data, int length)
    {
        var head = new byte[8];
        PutBigEndian(head, 0, length);
        for (var i = 0; i < 4; i++)
            head[4 + i] = (byte)type[i];
        to.Write(head, 0, 8);
        to.Write(data, 0, length);
        var tail = new byte[4];
        PutBigEndian(tail, 0, (int)(Crc(Crc(0xFFFFFFFFu, head, 4, 4), data, 0, length) ^ 0xFFFFFFFFu));
        to.Write(tail, 0, 4);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(uint crc, byte[] data, int offset, int length)
    {
        for (var i = offset; i < offset + length; i++)
            crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void PutBigEndian(byte[] data, int at, int value)
    {
        data[at] = (byte)(value >> 24);
        data[at + 1] = (byte)(value >> 16);
        data[at + 2] = (byte)(value >> 8);
        data[at + 3] = (byte)value;
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
