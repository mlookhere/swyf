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

The original game assembly is backed up alongside it as `ScriptsAssDef.dll.before-32-player-patch.bak`. If Steam has replaced the game DLL while leaving an older backup, the patcher **archives that older backup with a unique name** and creates a fresh backup of the current unpatched DLL. It never overwrites the old backup. The patcher also refuses to patch unrecognized/already patched assembly metadata. After a Steam update, reassess the new game build before trying to reapply an old patcher; **do not restore an old backup over a newer Steam update**.

To build locally, install .NET 10 SDK and run:

```powershell
dotnet publish tools/LobbyPatcher/LobbyPatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Intermittent crashes: capture evidence before further patching

A passing IL rewrite/decompiler test does **not** establish runtime stability. In particular, the game may contain player allocation or voice/network assumptions for eight participants, or Steam/Unity may have independently changed since the packaged build.

1. Close the game. After a crash, avoid relaunching before capturing `Player.log` (Unity can rotate it).
2. Open Windows PowerShell in your game directory and download the audit script from this repository:

```powershell
Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/mlookhere/swyf/audit/32-player-lobby/tools/Collect-SWYFCrash.ps1' -OutFile '.\Collect-SWYFCrash.ps1'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\Collect-SWYFCrash.ps1' -GamePath 'E:\SteamLibrary\steamapps\common\Scam With Your Friends'
```

The script finds likely Unity Player logs under `%USERPROFILE%\AppData\LocalLow`, archives their last 12,000 lines, hashes the game assemblies and captures matching Windows Application crash events. It masks common Steam IDs, IP addresses, credentials and home-directory usernames, but cannot guarantee every secret is removed: **review the ZIP before sharing it**. It never uploads files, copies memory dumps or changes game files.

3. Record whether the crash occurred on the main menu, while creating a lobby, when the ninth person joined, after an in-game lobby-size change, during scene load, or during gameplay. Note exact selected lobby capacity and actual player count.
4. Reproduce with 8 or fewer players, then 9–12, then 16 and 32. If the game also crashes at 8, restore the original game assembly and repeat an unchanged scenario to distinguish patched-code issues from a base-game or system problem.
5. Before restoring, keep the game closed and verify the original backup is from the **same Steam build**. Steam's Verify Integrity can restore official files but will remove the patch.

Do not assume that rising the Steam lobby limit resolves memory, object spawning, proximity voice, network sync, or game-server restrictions.

## What remains unverified

Passing tests establish that the patched DLL can be written, read, decompiled, and restored. They do **not** prove that 32 actual players can play through the official backend. Test join codes, Steam invites, public lobby discovery, connect/reconnect at 9/16/32 players, late joins, player spawning/desks, peer voice chat, UI rosters, game progression, performance and joining player 33. All clients must use compatible game versions. The backend may still enforce an independent cap.

Steam game updates can replace modified assemblies. Test with friends in a private lobby before using public lobbies. This tool is not affiliated with the game developers.
