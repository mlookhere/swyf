# SWYF 32-player lobby patcher

This repo contains a **compiled Unity game**, not Unity editor sources. The tool patches only the local `Scam With Your Friends_Data/Managed/ScriptsAssDef.dll` using Mono.Cecil. Do not commit or redistribute patched proprietary game binaries.

## Intended change

- Offer lobby size selections **1–32** in the host UI (the game's saved selection maps directly to value minus one).
- Change the maximum lobby size constant and related saved-settings clamps to 32.
- Change Mirror's initial maxConnections default to 32 so the custom Steam transport receives a large enough value **when its server is started**.
- Change the Steam host lobby-member-limit clamp to 32.
- Preserve game settings, Steamworks.NET, `steam_api64.dll`, the original game's backend and other gameplay logic.

## Windows use

Download the `swyf-lobby-patcher-win-x64` artifact from the passing GitHub Actions run and extract it. Ensure the game is **closed**.

```powershell
.\LobbyPatcher.exe --inspect "F:\Projects\swyf"
.\LobbyPatcher.exe --apply "F:\Projects\swyf"
.\LobbyPatcher.exe --verify "F:\Projects\swyf"
```

Use the actual folder containing `Scam With Your Friends.exe` if your active Steam installation lives elsewhere. For restore:

```powershell
.\LobbyPatcher.exe --restore "F:\Projects\swyf"
```

The original game assembly is backed up alongside it as `ScriptsAssDef.dll.before-32-player-patch.bak`. The patcher refuses to overwrite an existing backup and refuses to patch unrecognized/already patched assembly metadata. After a Steam update, reassess the new game build before trying to reapply an old patcher; **do not restore an old backup over a newer Steam update**.

To build locally, install .NET 10 SDK and run:

```powershell
dotnet publish tools/LobbyPatcher/LobbyPatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## What remains unverified

Passing tests establish that the patched DLL can be written, read, decompiled, and restored. They do **not** prove that 32 actual players can play through the official backend. Test join codes, Steam invites, public lobby discovery, connect/reconnect at 9/16/32 players, late joins, player spawning/desks, peer voice chat, UI rosters, game progression, performance and joining player 33. All clients must use compatible game versions. The backend may still enforce an independent cap.

Steam game updates can replace modified assemblies. Test with friends in a private lobby before using public lobbies. This tool is not affiliated with the game developers.
