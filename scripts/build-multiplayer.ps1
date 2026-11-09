param(
    [string]$GameRoot = 'D:\Epic Games\Flotsam',
    [switch]$Deploy
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vendor = Join-Path $root 'multiplayer\vendor'
$zip = Join-Path $vendor 'BepInEx_win_x64_5.4.23.6.zip'
New-Item -ItemType Directory -Path $vendor -Force | Out-Null
if (!(Test-Path -LiteralPath $zip)) {
    Invoke-WebRequest -Uri 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.6/BepInEx_win_x64_5.4.23.6.zip' -OutFile $zip
}
$loader = Join-Path $vendor 'BepInEx-5.4.23.6'
if (!(Test-Path -LiteralPath (Join-Path $loader 'BepInEx\core\BepInEx.dll'))) {
    Expand-Archive -LiteralPath $zip -DestinationPath $loader -Force
}

& dotnet build (Join-Path $root 'multiplayer\Flotsam.Multiplayer') -c Release "-p:GameRoot=$GameRoot" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Multiplayer plugin build failed' }

$dll = Join-Path $root 'multiplayer\Flotsam.Multiplayer\bin\Release\netstandard2.1\Flotsam.Multiplayer.dll'
$payload = Join-Path $root 'multiplayer\payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination $payload -Force
if (!(Test-Path -LiteralPath (Join-Path $payload 'BepInEx.zip'))) {
    Copy-Item -LiteralPath $zip -Destination (Join-Path $payload 'BepInEx.zip') -Force
}

if ($Deploy) {
    $targets = @(
        (Join-Path $GameRoot 'BepInEx\plugins\Flotsam.Multiplayer'),
        (Join-Path $root 'multiplayer\lab\host\BepInEx\plugins\Flotsam.Multiplayer'),
        (Join-Path $root 'multiplayer\lab\client\BepInEx\plugins\Flotsam.Multiplayer')
    )
    foreach ($dir in $targets) {
        if (Test-Path -LiteralPath (Split-Path $dir -Parent)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            try {
                Copy-Item -LiteralPath $dll -Destination $dir -Force
                Write-Host "Deployed -> $dir"
            } catch {
                Write-Warning "Skipped in-use target -> $dir"
            }
        }
    }
}
Write-Host "Plugin built -> $payload\Flotsam.Multiplayer.dll"
