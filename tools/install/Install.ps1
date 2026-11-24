# Installs the Sea Power Multiplayer co-op campaign fork.
# Run via "Install (double-click me).bat" next to this file.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll  = Join-Path $here 'SeapowerMultiplayer\SeapowerMultiplayer.dll'
if (-not (Test-Path $dll)) { Write-Host "SeapowerMultiplayer.dll is missing - unzip the whole download first." -ForegroundColor Red; exit 1 }

# Find Sea Power through Steam's library list.
$steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
$libs = @()
if ($steam) {
  $libs += $steam
  $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
  if (Test-Path $vdf) {
    foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += $m.Groups[1].Value.Replace('\\','\') }
  }
}
$game = $null; $lib = $null
foreach ($l in ($libs | Select-Object -Unique)) {
  $p = Join-Path $l 'steamapps\common\Sea Power'
  if (Test-Path (Join-Path $p 'Sea Power_Data')) { $game = $p; $lib = $l; break }
}
if (-not $game) {
  Write-Host "Couldn't find Sea Power automatically." -ForegroundColor Yellow
  $game = Read-Host "Paste the Sea Power folder (the one containing 'Sea Power_Data')"
  if (-not (Test-Path (Join-Path $game 'Sea Power_Data'))) { Write-Host "That isn't the Sea Power folder." -ForegroundColor Red; exit 1 }
}
Write-Host "Sea Power: $game"

if (-not (Test-Path (Join-Path $game 'BepInEx\core\BepInEx.dll'))) {
  Write-Host "BepInEx isn't installed in that folder. Install BepInEx 5 (and R.A.T.S. Keel / Anchor Chain as you normally do) first." -ForegroundColor Red; exit 1
}

# 1. Anchor Chain / Keel copy (mod menu)
$sa = Join-Path $game 'Sea Power_Data\StreamingAssets\SeapowerMultiplayer'
New-Item -ItemType Directory -Force -Path $sa | Out-Null
Copy-Item (Join-Path $here 'SeapowerMultiplayer\*') $sa -Force
# 2. BepInEx copy (what actually boots on game 0.9)
$plugins = Join-Path $game 'BepInEx\plugins'
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
Copy-Item $dll $plugins -Force
Write-Host "Installed to:" -ForegroundColor Green
Write-Host "  $sa"
Write-Host "  $plugins\SeapowerMultiplayer.dll"

# The Workshop copy of the multiplayer mod conflicts (same mod id).
if ($lib) {
  $ws = Join-Path $lib 'steamapps\workshop\content\1286220'
  if (Test-Path $ws) {
    $hit = Get-ChildItem $ws -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'SeapowerMultiplayer.dll') }
    foreach ($h in $hit) {
      Write-Host ""
      Write-Host "WARNING: the Workshop copy of Seapower Multiplayer is still subscribed (Workshop item $($h.Name))." -ForegroundColor Yellow
      Write-Host "Unsubscribe from it in Steam, or untick it in the game's mod menu - two copies conflict." -ForegroundColor Yellow
    }
  }
}
Write-Host ""
Write-Host "Done. Start Sea Power. Your partner installs the same zip." -ForegroundColor Green
