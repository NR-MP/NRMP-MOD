# Night Runners multiplayer (NRMP)

Source of the NRMP mod. Made by MILLIYEN. BY MODDERS FOR MODDERS.

BepInEx 6 IL2CPP mod for Night Runners Private Alpha 3.2.7 and 3.2.8. Unofficial fan mod, not affiliated with PlanetJem.
This is just the mod. The server and the launcher aren't in here.

- `src/NightRunnersMP`: the mod
- `src/Shared`: wire format, interpolation and origin conversion, compiled into the mod

## A note from me

I was never too keen on comments, so i did my best to explain everything that's going on in the code. Any questions, feel free to find me in the NRMP Discord.

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

## Credits

BepInEx, Il2CppInterop and LiteNetLib, all rights stay with their authors. Big credit to PlanetJem for Night Runners.


## PS

i was never too keen on comments, so i did my best to explain everything that's going on in the code. any questions, feel free to find me in the NRMP discord

## License

Copyright (C) 2026 MILLIYEN. GPL-3.0, see `LICENSE`. Use it, change it, share it, but whatever you make from it has to stay open source under GPL-3.0 too.
