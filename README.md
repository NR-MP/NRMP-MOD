<div align="center">
  
# Night Runners multiplayer MOD (NRMP)

Source of the NRMP mod. Made by MILLIYEN. BY MODDERS FOR MODDERS.

<a href="https://discord.gg/ERk9SxD4Ec"><img src="https://img.shields.io/badge/Discord-Join%20for%20support-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Join the Discord for support"></a>
<a href="../../releases"><img src="https://img.shields.io/badge/Download-Releases-E0002B?style=for-the-badge&logo=github&logoColor=white" alt="Download from Releases"></a>
<a href="docs/LAUNCHER.md"><img src="https://img.shields.io/badge/What%20it%20touches-LAUNCHER.md-30363D?style=for-the-badge&logo=readthedocs&logoColor=white" alt="What the launcher touches"></a>

<img src="https://img.shields.io/badge/platform-Windows-0078D6?style=flat-square&logo=windows&logoColor=white" alt="Windows">
<img src="https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET Framework 4.8">
<img src="https://img.shields.io/badge/admin-not%20needed-2EA043?style=flat-square" alt="No admin needed">
</div>

BepInEx 6 IL2CPP(Built with help from Claude (Anthropic), which assisted with the IL2CPP interop work) mod for Night Runners Private Alpha 3.2.7 and 3.2.8. Unofficial fan mod, not affiliated with PlanetJem.
This is just the mod. The server and the launcher aren't in here.

- `src/NightRunnersMP`: the mod
- `src/Shared`: wire format, interpolation and origin conversion, compiled into the mod

## Build

You need the .NET SDK and the game with my patched BepInEx for Unity 6.6 in it, interop included. No game files are in this repo, the build references them from your game folder.

```bash
dotnet build src/NightRunnersMP -c Release -p:GameDir="C:\path\to\Night Runners Private Alpha v3.27"
```

Building copies `NightRunnersMP.dll`, `LiteNetLib.dll` and the `ui` folder into the game's `BepInEx\plugins\NightRunnersMP`. Close the game first or the copy fails.

Build against the interop of the game version you play on, 3.2.7 and 3.2.8 each have their own.

## Settings

`BepInEx\config\com.nightrunners.multiplayer.cfg` gets created on the first run. Restart the game after changing it.

- `[Server]`: `Host`, `Port`, `JoinKey`

Join the discord to for the server info!

## Credits

BepInEx, Il2CppInterop and LiteNetLib, all rights stay with their authors. Big credit to PlanetJem for Night Runners.


## PS

i was never too keen on comments, so i did my best to explain everything that's going on in the code. any questions, feel free to find me in the NRMP discord

## License

Copyright (C) 2026 MILLIYEN. GPL-3.0, see `LICENSE`. Use it, change it, share it, but whatever you make from it has to stay open source under GPL-3.0 too.
