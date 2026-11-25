# Removes the Sea Power Multiplayer co-op campaign fork.
# Run via "Uninstall (double-click me).bat" next to this file.
$ErrorActionPreference = 'Stop'

# Find Sea Power through Steam's library list (same as the installer).
$steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
$libs = @()
if ($steam) {
  $libs += $steam
  $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
  if (Test-Path $vdf) {
    foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += $m.Groups[1].Value.Replace('\\','\') }
  }
}
$game = $null
foreach ($l in ($libs | Select-Object -Unique)) {
  $p = Join-Path $l 'steamapps\common\Sea Power'
  if (Test-Path (Join-Path $p 'Sea Power_Data')) { $game = $p; break }
}
if (-not $game) {
  Write-Host "Couldn't find Sea Power automatically." -ForegroundColor Yellow
  $game = Read-Host "Paste the Sea Power folder (the one containing 'Sea Power_Data')"
  if (-not (Test-Path (Join-Path $game 'Sea Power_Data'))) { Write-Host "That isn't the Sea Power folder." -ForegroundColor Red; exit 1 }
}
Write-Host "Sea Power: $game"

# Sea Power locks the DLL while it runs.
if (Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue) {
  Write-Host "Sea Power is running - quit it completely, then run this again." -ForegroundColor Red; exit 1
}

$removed = 0
function Remove-IfThere($path) {
  if (Test-Path $path) {
    Remove-Item $path -Recurse -Force
    Write-Host "  removed $path"
    $script:removed++
  }
}

Write-Host ""
Write-Host "Removing the mod:"
Remove-IfThere (Join-Path $game 'Sea Power_Data\StreamingAssets\SeapowerMultiplayer')
Remove-IfThere (Join-Path $game 'BepInEx\plugins\SeapowerMultiplayer.dll')

# Temporary files the mod writes while you play (battle copies sent between players).
$data = Join-Path $env:USERPROFILE 'AppData\LocalLow\Triassic Games\Sea Power'
if (Test-Path $data) {
  Write-Host "Removing its temporary files:"
  Remove-IfThere (Join-Path $data 'MPMission')
  Remove-IfThere (Join-Path $data 'MPTrace')
  Get-ChildItem (Join-Path $data 'Saves') -Filter 'MPSession*' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-IfThere $_.FullName }
}

# Settings (username, hotkeys, options): ask, since a reinstall would want them.
$cfg = Join-Path $game 'BepInEx\config\com.seapowermultiplayer.plugin.cfg'
if (Test-Path $cfg) {
  Write-Host ""
  $a = Read-Host "Also delete the mod's settings (username, hotkeys, options)? [y/N]"
  if ($a -match '^(y|yes)$') { Remove-IfThere $cfg } else { Write-Host "  kept $cfg" }
}

Write-Host ""
if ($removed -eq 0) {
  Write-Host "Nothing to remove - the mod wasn't installed in $game." -ForegroundColor Yellow
} else {
  Write-Host "Done. The co-op campaign mod is uninstalled." -ForegroundColor Green
}
Write-Host "Your other mods, campaigns and saves are untouched."
Write-Host "Note: Workshop mods the guest's game subscribed to or ticked to match a host stay as they are -"
Write-Host "unsubscribe or untick them in Steam / the game's mod menu if you don't want them."
