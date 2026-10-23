<#
.SYNOPSIS
  Publishes a new release of VPS Monitor.

.DESCRIPTION
  Sets the version, builds once locally as a sanity check, commits, tags vX.Y.Z and pushes.
  GitHub Actions then builds VPSMonitor.exe and attaches it to the GitHub release.
  With -Local the exe built on this PC is uploaded with the GitHub CLI (gh) right away.

.EXAMPLE
  .\release.ps1 1.1.0

.EXAMPLE
  .\release.ps1 1.1.0 -Local
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [switch]$Local
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$tag = "v$Version"

# git prints progress on stderr; don't let Windows PowerShell treat that as a failure.
function Invoke-Git {
    $ErrorActionPreference = "Continue"
    & git @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE) { throw "git $args failed (exit $LASTEXITCODE)" }
}

if (git status --porcelain) { throw "Working tree has uncommitted changes - commit or stash them first." }
if (git tag -l $tag) { throw "Tag $tag already exists." }
if ($Local -and -not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "-Local needs the GitHub CLI: winget install GitHub.cli" }

$proj = "src\VPSMonitor.csproj"
$text = [IO.File]::ReadAllText("$PSScriptRoot\$proj")
[IO.File]::WriteAllText("$PSScriptRoot\$proj", ($text -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"))

& "$PSScriptRoot\build.ps1"
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if (git status --porcelain) {
    Invoke-Git add $proj
    Invoke-Git commit -m "Release $tag"
}
Invoke-Git tag -a $tag -m "VPS Monitor $tag"
Invoke-Git push origin HEAD
Invoke-Git push origin $tag

if ($Local) {
    gh release create $tag dist\VPSMonitor.exe dist\VPSMonitor.exe.sha256 --title "VPS Monitor $tag" --generate-notes
} else {
    Write-Output "Pushed $tag. GitHub Actions is building the release - follow it with: gh run watch"
}
