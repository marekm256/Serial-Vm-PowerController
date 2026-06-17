$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-RepoRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StartDirectory
    )

    try {
        $gitRoot = & git -C $StartDirectory rev-parse --show-toplevel 2>$null
        if ($LASTEXITCODE -eq 0 -and ![string]::IsNullOrWhiteSpace($gitRoot)) {
            return [System.IO.Path]::GetFullPath($gitRoot.Trim())
        }
    } catch {
        # Fall back to the script directory when git is not available.
    }

    return (Resolve-Path -LiteralPath $StartDirectory).Path
}

$repoRoot = Get-RepoRoot -StartDirectory $scriptRoot
$solutionPath = Join-Path $repoRoot "SerialVmPowerController.sln"
$releaseDir = Join-Path $repoRoot "SerialVmPowerController\bin\Release"
$serviceDir = Join-Path $repoRoot "Service"
$appName = "SerialVmPowerController"

function Get-MSBuildPath {
    $knownPaths = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($path in $knownPaths) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if (![string]::IsNullOrWhiteSpace($found)) {
            return $found
        }
    }

    throw "MSBuild.exe was not found. Install Visual Studio Build Tools or open the solution in Visual Studio."
}

function Assert-InRepo {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullRepo = (Resolve-Path -LiteralPath $repoRoot).Path
    $fullPath = [System.IO.Path]::GetFullPath($Path)

    if ($fullPath -eq $fullRepo -or -not $fullPath.StartsWith($fullRepo + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository: $fullPath"
    }
}

$msbuild = Get-MSBuildPath
Write-Host "Building Release..."
& $msbuild $solutionPath /p:Configuration=Release /m

if (!(Test-Path -LiteralPath $releaseDir)) {
    throw "Release output was not found: $releaseDir"
}

Assert-InRepo -Path $serviceDir
if (Test-Path -LiteralPath $serviceDir) {
    Remove-Item -LiteralPath $serviceDir -Recurse -Force
}

New-Item -ItemType Directory -Path $serviceDir | Out-Null

$runtimeFiles = @(
    "$appName.exe",
    "$appName.exe.config"
)

foreach ($fileName in $runtimeFiles) {
    $sourcePath = Join-Path $releaseDir $fileName
    if (Test-Path -LiteralPath $sourcePath) {
        Copy-Item -LiteralPath $sourcePath -Destination $serviceDir -Force
    }
}

Get-ChildItem -LiteralPath $releaseDir -Filter "*.dll" -File -ErrorAction SilentlyContinue |
    Copy-Item -Destination $serviceDir -Force

$copiedFiles = Get-ChildItem -LiteralPath $serviceDir -File
foreach ($file in $copiedFiles) {
    if ($file.Name -like "*.exe" -and $file.Name -ne "$appName.exe") {
        Remove-Item -LiteralPath $file.FullName -Force
    }
}

$readmePath = Join-Path $repoRoot "README.md"
if (Test-Path -LiteralPath $readmePath) {
    Copy-Item -LiteralPath $readmePath -Destination $serviceDir -Force
}

$requiredExe = Join-Path $serviceDir "$appName.exe"
if (!(Test-Path -LiteralPath $requiredExe)) {
    throw "$appName.exe was not copied to the Service folder."
}

Write-Host "Created portable Service folder:"
Write-Host $serviceDir
Get-ChildItem -LiteralPath $serviceDir -File | Select-Object Name, Length

