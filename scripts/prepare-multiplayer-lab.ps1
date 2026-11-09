param(
    [string]$Source = 'D:\Epic Games\Flotsam',
    [string]$Lab = 'D:\Epic Games\Flotsam Launcher\multiplayer\lab',
    [ValidateSet('shared', 'independent')][string]$Mode = 'independent',
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($Source)
$labRoot = [IO.Path]::GetFullPath($Lab)
if ($labRoot.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Lab must be outside the game installation'
}
$vendor = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\multiplayer\vendor\BepInEx-5.4.23.6'))
$plugin = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\multiplayer\Flotsam.Multiplayer\bin\Release\netstandard2.1\Flotsam.Multiplayer.dll'))
if (!(Test-Path -LiteralPath $plugin)) { throw 'Build the plugin first: npm run plugin:build' }

$secret = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
foreach ($instance in @('host', 'client')) {
    $target = Join-Path $labRoot $instance
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    & robocopy.exe $sourceRoot $target /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /XD mod-research BepInEx /XF '*.log' | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "Game copy failed: $LASTEXITCODE" }
    Copy-Item -Path (Join-Path $vendor '*') -Destination $target -Recurse -Force
    $plugins = Join-Path $target 'BepInEx\plugins\Flotsam.Multiplayer'
    New-Item -ItemType Directory -Path $plugins -Force | Out-Null
    Copy-Item -LiteralPath $plugin -Destination $plugins -Force
    $profile = Join-Path $target 'multiplayer-profile'
    $cfgPath = Join-Path $target 'network.json'
    @{
        role = $instance
        address = '127.0.0.1'
        port = 27027
        secret = $secret
        profile = $profile
        autoStart = ($instance -eq 'host')
        mode = $Mode
        nickname = if ($instance -eq 'host') { '房主' } else { '同行者' }
    } | ConvertTo-Json | Set-Content -LiteralPath $cfgPath -Encoding utf8NoBOM
    if ($Launch) {
        Start-Process -FilePath (Join-Path $target 'Flotsam.exe') -WorkingDirectory $target -Environment @{ FLOTSAM_MULTIPLAYER_CONFIG = $cfgPath }
    }
    Write-Output "$instance ready ($Mode) -> $target"
}
