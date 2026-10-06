param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runtime = if ($Architecture -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$publishDirectory = Join-Path $projectRoot "build/windows-$Architecture/app"
$distDirectory = Join-Path $projectRoot 'build/windows-dist'
New-Item -ItemType Directory -Force -Path $distDirectory | Out-Null
dotnet publish (Join-Path $projectRoot 'windows/ClipHarbor.Windows/ClipHarbor.Windows.csproj') `
    -c $Configuration -r $runtime -p:Platform=$Architecture --self-contained true `
    -p:WindowsAppSDKSelfContained=true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }
if (!(Test-Path (Join-Path $publishDirectory 'ClipHarbor.exe'))) { throw 'Published executable is missing.' }
Copy-Item (Join-Path $projectRoot 'windows/PORTABLE-README.txt') (Join-Path $publishDirectory 'README.txt')
$archive = Join-Path $distDirectory "ClipHarbor-Windows-$Architecture.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
"$hash  $(Split-Path $archive -Leaf)" | Set-Content -Encoding ascii (Join-Path $distDirectory "SHA256SUMS-Windows-$Architecture.txt")
Write-Output "Created $archive"
