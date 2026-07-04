[CmdletBinding()]
param(
    [string]$SptRoot = "C:\RealSPT"
)

$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionPath = Join-Path $ScriptRoot "YetAnotherTraderMod.sln"

Write-Host "Building YetAnotherTraderMod.sln [Debug]"
Write-Host "SPT install path: $SptRoot"

dotnet build $SolutionPath -c Debug "/p:SptInstallPath=$SptRoot"

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
