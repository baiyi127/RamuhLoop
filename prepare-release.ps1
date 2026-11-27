param(
    [string]$DalamudPath = "$env:APPDATA\XIVLauncherCN\addon\Hooks\dev",
    [string]$Changelog = '0.2.5：新增副本外装备耐久严格低于10%时自动自行维修，默认开启。分别在白魔排本前、骑士开箱前检查实际配装，验证维修职业及暗物质，并确认耐久恢复后继续原流程。增加耐久显示和维修开关。已编译及隔离测试，维修窗口仍待游戏内验证。'
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -DalamudPath $DalamudPath
$ramuhProject = [xml](Get-Content (Join-Path $PSScriptRoot 'RamuhLoop/RamuhLoop.csproj') -Raw)
$ramuhVersion = [string]$ramuhProject.Project.PropertyGroup.Version
if ($ramuhVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$ramuhDist = Join-Path $PSScriptRoot 'dist'
$ramuhManifest = Get-Content (Join-Path $ramuhDist 'RamuhLoop.json') -Raw | ConvertFrom-Json -AsHashtable
if ($ramuhManifest.RepoUrl -ne 'https://github.com/baiyi127/RamuhLoop') { throw 'Unexpected release repository.' }
$ramuhDownload = "https://github.com/baiyi127/RamuhLoop/releases/download/v$ramuhVersion/RamuhLoop.zip"
$ramuhManifest['IsHide'] = $false
$ramuhManifest['IsTestingExclusive'] = $false
$ramuhManifest['DownloadLinkInstall'] = $ramuhDownload
$ramuhManifest['DownloadLinkUpdate'] = $ramuhDownload
$ramuhManifest['DownloadLinkTesting'] = $ramuhDownload
$ramuhManifest['IconUrl'] = 'https://raw.githubusercontent.com/baiyi127/RamuhLoop/main/icon.png'
$ramuhManifest['LastUpdate'] = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$ramuhManifest['Changelog'] = $Changelog
$ramuhJson = ConvertTo-Json -InputObject ([object[]]@($ramuhManifest)) -Depth 12
foreach ($ramuhFile in @('repo.json', 'pluginmaster.json')) {
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot $ramuhFile), $ramuhJson + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $ramuhFile) -Destination $ramuhDist -Force
}
Write-Output 'Repository URL: https://raw.githubusercontent.com/baiyi127/RamuhLoop/main/repo.json'
Write-Output "Release asset: $(Join-Path $ramuhDist 'RamuhLoop.zip')"
