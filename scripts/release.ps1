<#
.SYNOPSIS
  Builds the installer for a new version and publishes it as a GitHub release.
  Installed copies of BearMinVLC pick the update up on their next launch.

.EXAMPLE
  ./scripts/release.ps1 -Version 0.3.0 -Notes "Playlist support"

  Needs: the .NET 10 SDK, `dotnet tool restore` (for vpk), and the GitHub CLI signed in (`gh auth login`).
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Notes = ""
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$repo = 'https://github.com/bearminimumdev/BearMinVLC'
$publish = "artifacts/publish/$Version"
$releases = 'artifacts/releases'

if (git status --porcelain) { throw 'Commit your changes first, so the release matches the code on GitHub.' }

dotnet tool restore | Out-Null

# Self-contained: the installed app doesn't need .NET on the PC.
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src/BearMinVLC/BearMinVLC.csproj -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $publish
if ($LASTEXITCODE) { throw 'publish failed' }

# Fetch the previous release so Velopack can build a small delta update instead of a full download.
New-Item -ItemType Directory -Force $releases | Out-Null
dotnet vpk download github --repoUrl $repo -o $releases 2>$null
if ($LASTEXITCODE) { Write-Host 'No previous release found; this one ships full packages only.' }

dotnet vpk pack `
    --packId BearMinVLC `
    --packVersion $Version `
    --packDir $publish `
    --mainExe BearMinVLC.exe `
    --packTitle BearMinVLC `
    --packAuthors 'Lucas Berndt' `
    --icon src/BearMinVLC/Assets/BearMinVLC.ico `
    --splashImage installer/splash.png `
    --outputDir $releases
if ($LASTEXITCODE) { throw 'vpk pack failed' }

$token = gh auth token
if (-not $token) { throw 'Sign in to GitHub first: gh auth login' }
dotnet vpk upload github `
    --repoUrl $repo `
    --outputDir $releases `
    --token $token `
    --tag "v$Version" `
    --releaseName "BearMinVLC $Version" `
    --publish `
    --merge
if ($LASTEXITCODE) { throw 'upload failed' }

if ($Notes) { gh release edit "v$Version" --repo $repo --notes $Notes | Out-Null }
Write-Host "Released v$Version. Installer: $repo/releases/latest/download/BearMinVLC-win-Setup.exe"
