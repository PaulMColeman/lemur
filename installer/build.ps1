<#
  Builds Lemur-<version>.msi: publishes the tray app and llmmr self-contained (the .NET runtime included, so PCs need
  nothing else and no admin rights), then runs WiX 6.

  Needs: the .NET 10 SDK and the WiX tool with its UI extension:
    dotnet tool install --global wix --version 6.0.1
    wix extension add -g WixToolset.UI.wixext/6.0.1

  .\installer\build.ps1              -> installer\out\Lemur-1.2.0.msi
#>
param([string]$Version = "1.2.0")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $PSScriptRoot "build\app"
$out = Join-Path $PSScriptRoot "out"

if (Test-Path $app) { Remove-Item -Recurse -Force $app }
New-Item -ItemType Directory -Force $app, $out | Out-Null

# Both programs share one folder and one copy of the runtime.
foreach ($project in "src\Lemur", "src\Lemur.Cli") {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -p:Version=$Version -p:DebugType=none -o $app -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}
foreach ($required in "Lemur.exe", "llmmr.exe", "help\manual.html") {
    if (-not (Test-Path (Join-Path $app $required))) { throw "missing from the publish: $required" }
}

$msi = Join-Path $out "Lemur-$Version.msi"
$assets = Join-Path $root "assets"
wix build (Join-Path $PSScriptRoot "Lemur.wxs") -loc (Join-Path $PSScriptRoot "Lemur.wxl") `
    -ext WixToolset.UI.wixext -arch x64 `
    -d "Version=$Version" -d "AppDir=$app" -d "AssetsDir=$assets" `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

# Windows Installer's checks. ICE38/64/91 assume a per-machine install (files in the user profile should have registry
# key paths, roam, and so on); this package is per-user by design. ICE61 is the same-version upgrade allowed on purpose.
wix msi validate -sice ICE38 -sice ICE64 -sice ICE91 -sice ICE61 $msi
if ($LASTEXITCODE -ne 0) { throw "MSI validation failed" }

$size = (Get-Item $msi).Length / 1MB
Write-Output ("{0} ({1:N1} MB)" -f $msi, $size)
