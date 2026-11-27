# Builds a release package of Cuepoint: a self-contained build for 64-bit Windows (it carries the .NET runtime, so
# nothing else needs installing), with the README and licence, zipped as artifacts\Cuepoint-<version>-win-x64.zip.
# The version is the project's <Version>, in Cuepoint\Cuepoint.csproj.
#
#     powershell -File tools\package.ps1 [-SkipTests]
#
# The tests run first, and nothing's packaged if any fail. cue-framework must be checked out beside this repository.

param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Cuepoint\Cuepoint.csproj'
$runtime = 'win-x64'

$version = & dotnet msbuild $project -getProperty:Version
if ($LASTEXITCODE -ne 0 -or -not $version) { throw "Couldn't read the version from $project" }
$version = "$version".Trim()
$name = "Cuepoint-$version-$runtime"
$artifacts = Join-Path $root 'artifacts'
$folder = Join-Path $artifacts $name
$zip = Join-Path $artifacts "$name.zip"

if (-not $SkipTests) {
    & dotnet test (Join-Path $root 'Cuepoint.Tests\Cuepoint.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'The tests failed, so nothing was packaged' }
}

if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

& dotnet publish $project -c Release -r $runtime -o $folder -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
# DirectML's package brings its debug symbols along; they're 12 MB nobody running the app needs.
Get-ChildItem $folder -Filter *.pdb -Recurse | Remove-Item

Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $folder
# The README shows the screenshot from docs\.
New-Item -ItemType Directory (Join-Path $folder 'docs') -Force | Out-Null
Copy-Item (Join-Path $root 'docs\screenshot.png') (Join-Path $folder 'docs')

Compress-Archive -Path $folder -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Packaged $zip ($size MB)"
