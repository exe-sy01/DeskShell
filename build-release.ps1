# Builds DeskShell into dist\ and zips it as release\DeskShell-<version>.zip, the file to attach to a
# GitHub release (tag it v<version>) so installed copies can update to it.
# Bump <Version> in src\DeskShell\DeskShell.csproj first.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

[xml]$project = Get-Content "src\DeskShell\DeskShell.csproj"
$version = ($project.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version
if (-not $version) { throw "No <Version> in DeskShell.csproj" }

if (Test-Path dist) { Remove-Item dist -Recurse -Force }
dotnet publish src\DeskShell -c Release -r win-x64 --self-contained false -o dist -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

New-Item -ItemType Directory -Force release | Out-Null
$zip = "release\DeskShell-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "dist\*" -DestinationPath $zip
"Built $zip. Install it with dist\DeskShell.exe --install, or attach it to a GitHub release tagged v$version."
