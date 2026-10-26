#!/usr/bin/env python3
"""Audit decompiled Unity assemblies for lobby-size dependencies.

This script is read-only: it never patches the game or publishes decompiled sources.
Usage: python tools/audit_lobby.py <decompiled-root> <output.md>
"""
from __future__ import annotations

import re
import sys
from collections import Counter
from pathlib import Path

TERMS = (
    ("STEAM_LOBBY", re.compile(r"CreateLobby|SetLobbyMemberLimit|GetLobbyMemberLimit|GetNumLobbyMembers|SteamMatchmaking", re.I), 12),
    ("CAPACITY", re.compile(r"MaxLobbySize|LobbySize|MaxPlayers|PlayerLimit|MemberLimit|MaxConnections|maximumPlayers|maximumLobby", re.I), 12),
    ("JOIN_GATES", re.compile(r"CanJoin|TryJoin|JoinLobby|LobbyFull|IsLobbyFull|IsFull|FullLobby|CanAccept|Joinable|ConnectionApproval|OnServerConnect", re.I), 7),
    ("SPAWN_AND_SYNC", re.compile(r"SpawnPoint|SpawnPosition|PlayerSlot|PlayerIndex|AssignSeat|AssignDesk|AvailableDesk|SeatIndex|DeskIndex|NetworkServer|NetworkManager", re.I), 5),
    ("UI", re.compile(r"Max Lobby Size|Lobby Type|Host Settings|LobbyDropdown|LobbyOption|player.count|PlayerCount", re.I), 5),
)
FILE_HINT = re.compile(r"lobby|steam|network|multiplayer|matchmaking|host|session|player|spawn|desk|seat|gamemode", re.I)
CSharp = re.compile(r"\.cs$", re.I)
LIMIT = 400

def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: audit_lobby.py <decompiled-root> <output.md>", file=sys.stderr)
        return 2
    root, out = Path(sys.argv[1]), Path(sys.argv[2])
    if not root.is_dir():
        raise SystemExit(f"Decompiled folder missing: {root}")
    files = sorted(p for p in root.rglob("*.cs") if p.is_file())
    if not files:
        raise SystemExit("No C# files were produced. Verify ILSpy output.")
    hits = []
    candidates = []
    for file in files:
        lines = file.read_text(encoding="utf-8", errors="replace").splitlines()
        rel = file.relative_to(root).as_posix()
        good = bool(FILE_HINT.search(rel))
        file_hits = []
        for index, line in enumerate(lines):
            kinds = [name for name, pat, _ in TERMS if pat.search(line)]
            if not kinds:
                continue
            weight = sum(weight for name, _, weight in TERMS if name in kinds)
            if good:
                weight += 3
            if re.search(r"\b8\b|\b32\b|\b100\b", line):
                weight += 3
            snippet = line.strip().replace("|", "\\|")
            file_hits.append((weight, index + 1, "+".join(kinds), snippet[:260]))
        if file_hits:
            ranked = sorted(file_hits, key=lambda v: (-v[0], v[1]))
            candidates.append((sum(x[0] for x in ranked[:15]), rel, len(file_hits)))
            hits.extend((weight, rel, line, kind, snippet) for weight, line, kind, snippet in file_hits)

    hits.sort(key=lambda x: (-x[0], x[1], x[2]))
    candidates.sort(reverse=True)
    counter = Counter(h[3].split("+")[0] for h in hits)
    md = [
        "# SWYF 32-player lobby: compiled-assembly audit",
        "",
        "Generated from ILSpy decompilation for identification only; game binaries are unchanged.",
        "The report shows file/line names and single-line fragments, not full decompiled sources.",
        "",
        f"- Decompilation inputs: {root}",
        f"- C# files scanned: {len(files)}",
        f"- Relevant files: {len(candidates)}",
        f"- Relevant references: {len(hits)}",
        f"- First-category counts: {dict(counter)}",
        "",
        "## Leading classes/files to inspect",
        "",
        "| Relative class/file | Hits | Weighted score |",
        "| --- | ---: | ---: |",
    ]
    md.extend(f"| `{rel}` | {count} | {score} |" for score, rel, count in candidates[:100])
    md.extend(["", "## Top call sites and capacity-related references", "",
               "| File | Line | Category | Decompiled line (truncated) |",
               "| --- | ---: | --- | --- |"])
    md.extend(f"| `{rel}` | {line} | {kind} | `{snippet.replace(chr(96), chr(39))}` |"
              for _, rel, line, kind, snippet in hits[:LIMIT])
    md.extend(["", "## Testing gates", "",
               "- Confirm host dropdown offers 32 and persists 32.",
               "- Verify both Mirror server maxConnections and Steam's GetLobbyMemberLimit are >= 32.",
               "- Join sequentially with 9, 16 and 32 real Steam accounts; reject player 33.",
               "- Verify Steam invite/join code, public browsing, reconnect and late joins.",
               "- Verify networked player spawn/desk/seat allocation, UI rosters, voice and game sync.",
               "- Verify official backend accepts the limit; do not assume client patches override it.",
               "- Check CPU/bandwidth, disconnects, gameplay performance, and compatibility with unmodified clients.",
               ""])
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("\n".join(md), encoding="utf-8")
    print("\n".join(md[:min(len(md), 225)]))
    if len(md) > 225:
        print(f"\n(Truncated console preview; full report in {out})")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
