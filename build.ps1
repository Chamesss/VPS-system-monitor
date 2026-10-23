# Builds dist\VPSMonitor.exe (+ .sha256). Needs the .NET SDK on the build machine only;
# the exe itself runs on any Windows 10/11 with the built-in .NET Framework 4.8.
#   .\build.ps1                 # version from src\VPSMonitor.csproj
#   .\build.ps1 -Version 1.2.0  # override the version
param([string]$Version)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not (Test-Path "$root\src\app.ico")) { & "$root\tools\make-icon.ps1" }

$props = @("-c", "Release", "-nologo", "-v:minimal")
if ($Version) { $props += "-p:Version=$Version" }
dotnet build "$root\src\VPSMonitor.csproj" @props
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$exe = "$root\dist\VPSMonitor.exe"
Copy-Item "$root\src\bin\Release\net48\VPSMonitor.exe" $exe -Force
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$exe.sha256", "$hash  VPSMonitor.exe`n")
$size = [Math]::Round((Get-Item $exe).Length / 1KB)
Write-Output "Built dist\VPSMonitor.exe ($size KB)  sha256 $hash"
