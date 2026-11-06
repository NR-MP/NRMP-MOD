using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NightRunnersMP;

internal sealed class SteamProfile
{
    public string Id;
    public string Name;
    public string AvatarFile;
}

// no steamworks in the game, so read what the steam client leaves around: account in the registry, name and pic in its files
internal static class Steam
{
    private const long AccountIdBase = 76561197960265728;

    // null unless steam's running and logged in
    public static SteamProfile Read()
    {
        using var active = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
        using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (active?.GetValue("ActiveUser") is not int account || account == 0)
            return null;
        if (active.GetValue("pid") is not int pid || !IsRunning(pid) || steam?.GetValue("SteamPath") is not string path)
            return null;

        var id = (AccountIdBase + (uint)account).ToString();
        var logins = Path.Combine(path, "config", "loginusers.vdf");
        var name = File.Exists(logins) ? FindName(File.ReadAllText(logins), id) : null;
        return name == null ? null : new SteamProfile { Id = id, Name = name, AvatarFile = Path.Combine(path, "config", "avatarcache", $"{id}.png") };
    }

    private static string FindName(string logins, string id)
    {
        var match = Regex.Match(logins, id + "\"\\s*\\{[^}]*?\"PersonaName\"\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
