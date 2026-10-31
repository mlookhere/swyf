<#
.SYNOPSIS
Collects local Unity game-crash evidence without modifying the game.

.DESCRIPTION
Only logs from directories matching the game's known names are collected.
Crash dumps themselves are NOT copied (they may contain personal data).
The ZIP is local-only; review it before sharing.

.EXAMPLE
powershell -File .\tools\Collect-SWYFCrash.ps1 -GamePath "E:\SteamLibrary\steamapps\common\Scam With Your Friends"
#>
[CmdletBinding()]
param(
    [string] $GamePath = 'E:\SteamLibrary\steamapps\common\Scam With Your Friends',
    [string] $LogRoot = (Join-Path $env:USERPROFILE 'AppData\LocalLow'),
    [string] $OutputDirectory = (Get-Location).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$game = [IO.Path]::GetFullPath($GamePath)
$logBase = [IO.Path]::GetFullPath($LogRoot)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$working = Join-Path ([IO.Path]::GetTempPath()) ("swyf-crash-$stamp-" + [guid]::NewGuid().ToString('N'))
$zip = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "SWYF-crash-$stamp.zip"
New-Item -ItemType Directory -Path $working -Force | Out-Null

function Redact([string] $value) {
    $value = $value -replace '\b7656119\d{10}\b', '[STEAM_ID]'
    $value = $value -replace '(?i)(bearer\s+)[A-Za-z0-9._~+/-]+', '$1[REDACTED]'
    $value = $value -replace '(?i)(api[_-]?key|access[_-]?token|authorization|password|secret)(\s*[:=]\s*["'']?)[^\s"'']+', '$1$2[REDACTED]'
    $value = $value -replace '\b\d{1,3}(?:\.\d{1,3}){3}\b', '[IPV4]'
    $value = $value -replace '(?i)(?:[A-Za-z]:\\Users\\|/Users/|/home/)[^\\/\s]+', '[USER_HOME]'
    return $value
}

try {
    $meta = [ordered]@{
        collected_at = (Get-Date).ToUniversalTime().ToString('o')
        game_exists = (Test-Path -LiteralPath $game)
        log_root_exists = (Test-Path -LiteralPath $logBase)
        note = 'No memory dumps or full system information are included.'
        files = @()
    }

    foreach ($name in @(
        'Scam With Your Friends.exe',
        'Scam With Your Friends_Data\Managed\ScriptsAssDef.dll',
        'Scam With Your Friends_Data\Managed\ScriptsAssDef.dll.before-32-player-patch.bak',
        'Scam With Your Friends_Data\Managed\Assembly-CSharp.dll'
    )) {
        $file = Join-Path $game $name
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            $item = Get-Item -LiteralPath $file
            $meta.files += [ordered]@{
                name = $name
                bytes = $item.Length
                last_modified_utc = $item.LastWriteTimeUtc.ToString('o')
                sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
            }
        }
    }

    $matched = @()
    if (Test-Path -LiteralPath $logBase -PathType Container) {
        $matched = @(Get-ChildItem -LiteralPath $logBase -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -in @('Player.log', 'Player-prev.log', 'output_log.txt') -and
                $_.DirectoryName -match '(?i)scam\s*with\s*your\s*friends|metater|kolkata'
            } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 8)
    }

    $logIndex = 0
    foreach ($file in $matched) {
        $logIndex++
        $name = "unity-player-$logIndex.txt"
        $target = Join-Path $working $name
        $lines = Get-Content -LiteralPath $file.FullName -Tail 12000 -ErrorAction Stop
        Redact ($lines -join "`n") | Set-Content -LiteralPath $target -Encoding utf8
        $meta["log_$logIndex"] = [ordered]@{
            source = Redact $file.FullName
            modified_utc = $file.LastWriteTimeUtc.ToString('o')
            source_bytes = $file.Length
            included = $name
        }
    }

    if ($env:OS -eq 'Windows_NT') {
        try {
            $events = @(Get-WinEvent -FilterHashtable @{
                LogName = 'Application'
                Id = @(1000, 1001, 1026)
                StartTime = (Get-Date).AddDays(-7)
            } -MaxEvents 250 -ErrorAction Stop | Where-Object {
                $_.Message -match '(?i)Scam With Your Friends|UnityPlayer\.dll|ScriptsAssDef|MonoBleedingEdge'
            } | Select-Object -First 30)
            $eventText = foreach ($event in $events) {
                $msg = Redact ($event.Message -replace '\r?\n', ' ')
                "$($event.TimeCreated.ToUniversalTime().ToString('o')) [$($event.Id)] $($msg.Substring(0, [Math]::Min(1400, $msg.Length)))"
            }
            $eventText | Set-Content -LiteralPath (Join-Path $working 'windows-application-crashes.txt') -Encoding utf8
            $meta['windows_event_count'] = $events.Count
        } catch {
            $meta['windows_events_note'] = 'Application Event Log unavailable to this account.'
        }
    }

    $meta['unity_logs_found'] = $logIndex
    $meta | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $working 'manifest.json') -Encoding utf8

    if (Test-Path -LiteralPath $zip) {
        throw "Archive already exists, refusing to overwrite: $zip"
    }
    Compress-Archive -Path (Join-Path $working '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Crash report written: $zip"
    Write-Host "Unity logs found: $logIndex"
    if ($logIndex -eq 0) {
        Write-Warning "No matching Unity Player.log found. The game may use a different company/product folder. Search under: $logBase"
    }
    Write-Host "No crash dumps were copied. Review the ZIP before uploading (logs may contain personal data)."
}
finally {
    if (Test-Path -LiteralPath $working) {
        Remove-Item -LiteralPath $working -Recurse -Force
    }
}
