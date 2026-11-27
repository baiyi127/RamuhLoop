param([string]$DalamudPath = "$env:APPDATA\XIVLauncherCN\addon\Hooks\dev")
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath (Join-Path $DalamudPath 'Dalamud.dll'))) { throw 'Matching Dalamud.dll was not found.' }
& dotnet build (Join-Path $PSScriptRoot 'RamuhLoop/RamuhLoop.csproj') -c Release "-p:DALAMUD_HOME=$DalamudPath"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$ramuhOutput = Join-Path $PSScriptRoot 'RamuhLoop/bin/Release'
$ramuhDist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $ramuhDist -Force | Out-Null
foreach ($ramuhName in @('RamuhLoop.dll', 'RamuhLoop.json')) {
    Copy-Item -LiteralPath (Join-Path $ramuhOutput $ramuhName) -Destination $ramuhDist -Force
}
$ramuhPackageManifestPath = Join-Path $ramuhDist 'RamuhLoop.json'
$ramuhPackageManifest = Get-Content -LiteralPath $ramuhPackageManifestPath -Raw | ConvertFrom-Json -AsHashtable
$ramuhPackageManifest['IconUrl'] = 'https://raw.githubusercontent.com/baiyi127/RamuhLoop/main/icon.png'
[IO.File]::WriteAllText($ramuhPackageManifestPath, (ConvertTo-Json -InputObject $ramuhPackageManifest -Depth 12) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
foreach ($ramuhName in @('LICENSE.md', 'README.md', 'icon.png')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $ramuhName) -Destination $ramuhDist -Force
}
$ramuhFiles = @('RamuhLoop.dll', 'RamuhLoop.json', 'LICENSE.md', 'README.md', 'icon.png') | ForEach-Object { Join-Path $ramuhDist $_ }
Compress-Archive -LiteralPath $ramuhFiles -DestinationPath (Join-Path $ramuhDist 'RamuhLoop.zip') -Force
Write-Output "Plugin package: $ramuhDist"
