[CmdletBinding()]
param(
    [string]$SptRoot = "C:\RealSPT",
    [string]$OutputZipPath = "",
    [switch]$NoBuild,
    [switch]$IncludeSptFolder
)

$ErrorActionPreference = "Stop"

function Get-FirstXmlValue {
    param(
        [xml]$Xml,
        [string]$Name,
        [string]$DefaultValue
    )

    $node = $Xml.Project.PropertyGroup | ForEach-Object { $_.$Name } | Where-Object { $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($node)) {
        return $DefaultValue
    }

    return [string]$node
}

function Reset-Directory {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Resolve-PackageOutputDirectory {
    param(
        [string]$BaseOutput,
        [string]$RequiredFileName,
        [string]$TargetFramework,
        [string]$Label
    )

    if (!(Test-Path -LiteralPath $BaseOutput)) {
        throw "$Label package output folder was not found: $BaseOutput"
    }

    $directFile = Join-Path $BaseOutput $RequiredFileName
    if (Test-Path -LiteralPath $directFile -PathType Leaf) {
        return $BaseOutput
    }

    if (![string]::IsNullOrWhiteSpace($TargetFramework)) {
        $targetFrameworkOutput = Join-Path $BaseOutput $TargetFramework
        $targetFrameworkFile = Join-Path $targetFrameworkOutput $RequiredFileName
        if (Test-Path -LiteralPath $targetFrameworkFile -PathType Leaf) {
            return $targetFrameworkOutput
        }
    }

    $recursiveMatch = Get-ChildItem -LiteralPath $BaseOutput -Recurse -Force -File -Filter $RequiredFileName -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($null -ne $recursiveMatch) {
        return $recursiveMatch.Directory.FullName
    }

    throw "$Label package output did not contain $RequiredFileName under: $BaseOutput"
}

function Copy-ServerPackageFiles {
    param(
        [string]$Source,
        [string]$Destination,
        [string]$AssemblyName
    )

    if (!(Test-Path -LiteralPath $Source)) {
        throw "Server mod package output folder was not found: $Source"
    }

    $serverDll = Join-Path $Source "$AssemblyName.dll"
    if (!(Test-Path -LiteralPath $serverDll -PathType Leaf)) {
        throw "Server mod package output does not contain $AssemblyName.dll: $Source"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force

    Get-ChildItem -LiteralPath $Destination -Recurse -Force |
        Where-Object { !$_.PSIsContainer -and ($_.Extension -in @(".pdb", ".mdb", ".xml")) } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $toolsPath = Join-Path $Destination "tools"
    if (Test-Path -LiteralPath $toolsPath) {
        Remove-Item -LiteralPath $toolsPath -Recurse -Force
    }
}

function Copy-ClientPackageFiles {
    param(
        [string]$Source,
        [string]$Destination,
        [string]$AssemblyName,
        [string]$ProjectRoot
    )

    $clientDll = Join-Path $Source "$AssemblyName.dll"
    if (!(Test-Path -LiteralPath $clientDll -PathType Leaf)) {
        throw "Client plugin package output does not contain $AssemblyName.dll: $Source"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    # Only ship the YATM client plugin files. Do not copy SPT, Unity, BepInEx, Harmony,
    # or other referenced DLLs that MSBuild may place next to the plugin.
    Copy-Item -LiteralPath $clientDll -Destination $Destination -Force

    $settingsFromOutput = Join-Path $Source "settings.json"
    $settingsFromProject = Join-Path $ProjectRoot "settings.json"
    if (Test-Path -LiteralPath $settingsFromOutput -PathType Leaf) {
        Copy-Item -LiteralPath $settingsFromOutput -Destination $Destination -Force
    }
    elseif (Test-Path -LiteralPath $settingsFromProject -PathType Leaf) {
        Copy-Item -LiteralPath $settingsFromProject -Destination $Destination -Force
    }
}

function Assert-FileExists {
    param(
        [string]$Path,
        [string]$Label
    )

    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not staged where expected: $Path"
    }
}

function New-ZipFromDirectory {
    param(
        [string]$SourceDirectory,
        [string]$DestinationZip
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path -LiteralPath $DestinationZip) {
        Remove-Item -LiteralPath $DestinationZip -Force
    }

    $destinationParent = Split-Path -Parent $DestinationZip
    if (![string]::IsNullOrWhiteSpace($destinationParent)) {
        New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDirectory,
        $DestinationZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )
}

function Normalize-ZipPath {
    param([string]$Path)
    return $Path.Replace("\", "/").TrimStart("/").TrimEnd("/")
}

function Assert-ZipContainsFile {
    param(
        [string]$ZipPath,
        [string]$RequiredFile
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $normalizedFile = Normalize-ZipPath -Path $RequiredFile
        $match = $zip.Entries |
            Where-Object { (Normalize-ZipPath -Path $_.FullName) -eq $normalizedFile } |
            Select-Object -First 1

        if ($null -eq $match) {
            $sampleEntries = $zip.Entries |
                Select-Object -First 80 |
                ForEach-Object { $_.FullName } |
                Out-String

            throw "Package zip is missing expected file: $RequiredFile`nFirst zip entries:`n$sampleEntries"
        }
    }
    finally {
        $zip.Dispose()
    }
}

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionPath = Join-Path $ScriptRoot "YetAnotherTraderMod.sln"
$ServerProjectPath = Join-Path $ScriptRoot "YetAnotherTraderMod-ServerMod\YetAnotherTraderMod.csproj"
$ClientProjectRoot = Join-Path $ScriptRoot "YetAnotherTraderMod-ClientMod"
$ClientProjectPath = Join-Path $ClientProjectRoot "YetAnotherTraderMod.Client.csproj"

[xml]$ServerProject = Get-Content -LiteralPath $ServerProjectPath
[xml]$ClientProject = Get-Content -LiteralPath $ClientProjectPath

$ServerModName = Get-FirstXmlValue -Xml $ServerProject -Name "AssemblyName" -DefaultValue "YetAnotherTraderMod"
$Version = Get-FirstXmlValue -Xml $ServerProject -Name "Version" -DefaultValue "0.0.0"

$ClientPluginFolderName = "YetAnotherTraderMod.Client"
$ClientAssemblyName = "YetAnotherTraderMod.Client"
$ClientTargetFramework = Get-FirstXmlValue -Xml $ClientProject -Name "TargetFramework" -DefaultValue "netstandard2.1"

$DistRoot = Join-Path $ScriptRoot "dist"
$PackageRoot = Join-Path $DistRoot "package"
$ZipRoot = Join-Path $DistRoot "ziproot"

# Staged package layout is exactly what the install zip should contain:
#   SPT\user\mods\YetAnotherTraderMod\
#   BepInEx\plugins\YetAnotherTraderMod.Client\
#
# The server mod keeps the extra top-level SPT folder. The client/BepInEx plugin
# stays at the zip root under BepInEx.
$ServerPackageRoot = Join-Path $PackageRoot "SPT\user\mods\$ServerModName"
$ClientPackageRoot = Join-Path $PackageRoot "BepInEx\plugins\$ClientPluginFolderName"

if ([string]::IsNullOrWhiteSpace($OutputZipPath)) {
    $OutputZipPath = Join-Path $DistRoot "$ServerModName-v$Version.zip"
}
elseif (![System.IO.Path]::IsPathRooted($OutputZipPath)) {
    $OutputZipPath = Join-Path $ScriptRoot $OutputZipPath
}

Write-Host "Building YetAnotherTraderMod.sln [Package]"
Write-Host "SPT install path: $SptRoot"

if ($IncludeSptFolder) {
    Write-Warning "-IncludeSptFolder is no longer needed. The package already uses SPT/user/mods for the server mod and BepInEx/plugins for the client plugin."
}

if (!$NoBuild) {
    dotnet build $SolutionPath -c Package "/p:SptInstallPath=$SptRoot" "/p:SptPath=$SptRoot" "/p:TestSptServerPath=$SptRoot" "/p:CreateModZip=false" "/p:DeployToTestSpt=false" "/p:DeployClientToSpt=false"

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$ServerOutputBase = Join-Path $ScriptRoot "YetAnotherTraderMod-ServerMod\bin\Package"
$ClientOutputBase = Join-Path $ScriptRoot "YetAnotherTraderMod-ClientMod\bin\Package"

$ServerOutput = Resolve-PackageOutputDirectory -BaseOutput $ServerOutputBase -RequiredFileName "$ServerModName.dll" -TargetFramework "" -Label "Server mod"
$ClientOutput = Resolve-PackageOutputDirectory -BaseOutput $ClientOutputBase -RequiredFileName "$ClientAssemblyName.dll" -TargetFramework $ClientTargetFramework -Label "Client plugin"

Write-Host "Server package source: $ServerOutput"
Write-Host "Client package source: $ClientOutput"

Reset-Directory -Path $PackageRoot

Copy-ServerPackageFiles -Source $ServerOutput -Destination $ServerPackageRoot -AssemblyName $ServerModName
Copy-ClientPackageFiles -Source $ClientOutput -Destination $ClientPackageRoot -AssemblyName $ClientAssemblyName -ProjectRoot $ClientProjectRoot

Assert-FileExists -Path (Join-Path $ServerPackageRoot "$ServerModName.dll") -Label "Server mod DLL"
Assert-FileExists -Path (Join-Path $ClientPackageRoot "$ClientAssemblyName.dll") -Label "Client plugin DLL"

# The zip layout is intentionally hybrid:
#   SPT/user/mods/YetAnotherTraderMod/
#   BepInEx/plugins/YetAnotherTraderMod.Client/
New-ZipFromDirectory -SourceDirectory $PackageRoot -DestinationZip $OutputZipPath

Assert-ZipContainsFile -ZipPath $OutputZipPath -RequiredFile "SPT/user/mods/$ServerModName/$ServerModName.dll"
Assert-ZipContainsFile -ZipPath $OutputZipPath -RequiredFile "BepInEx/plugins/$ClientPluginFolderName/$ClientAssemblyName.dll"

Write-Host "Created combined SPT install zip: $OutputZipPath"
Write-Host "Zip root includes server mod: SPT\user\mods\$ServerModName"
Write-Host "Zip root includes client plugin: BepInEx\plugins\$ClientPluginFolderName"
