param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "artifacts\publish"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputRoot = Join-Path $repoRoot $OutputDirectory

$desktopRidTargets = @("win-x64", "linux-x64", "osx-x64", "osx-arm64")

$commonProjects = @(
    "src\SimpleShadowsocks.Protocol\SimpleShadowsocks.Protocol.csproj",
    "src\SimpleShadowsocks.Client.Core\SimpleShadowsocks.Client.Core.csproj",
    "src\SimpleShadowsocks.Server.Core\SimpleShadowsocks.Server.Core.csproj"
)

$desktopProjects = @(
    @{
        Name = "server"
        Project = "src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj"
    },
    @{
        Name = "client"
        Project = "src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj"
    }
)

$androidProject = "src\SimpleShadowsocks.Client.Maui\SimpleShadowsocks.Client.Maui.csproj"
$androidFramework = "net9.0-android"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Invoke-DotNet {
    param(
        [string[]]$Arguments
    )

    Write-Host ("> dotnet " + ($Arguments -join " "))
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

function New-ZipFromDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$SourceDirectory,
        [Parameter(Mandatory)]
        [string]$DestinationArchive
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory)) {
        throw "Publish directory not found: $SourceDirectory"
    }

    if (Test-Path -LiteralPath $DestinationArchive) {
        Remove-Item -LiteralPath $DestinationArchive -Force
    }

    $sourcePath = (Resolve-Path -LiteralPath $SourceDirectory).Path
    $normalizedSourcePath = $sourcePath.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $sourceUri = [System.Uri]($normalizedSourcePath + [System.IO.Path]::DirectorySeparatorChar)
    $archive = [System.IO.Compression.ZipFile]::Open($DestinationArchive, [System.IO.Compression.ZipArchiveMode]::Create)

    try {
        $files = Get-ChildItem -LiteralPath $sourcePath -Recurse -File |
            Where-Object { $_.Extension -ne ".pdb" }

        foreach ($file in $files) {
            $entryName = $sourceUri.MakeRelativeUri([System.Uri]$file.FullName).ToString()
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-PublishDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,
        [Parameter(Mandatory)]
        [string]$Framework,
        [Parameter(Mandatory)]
        [string]$Rid
    )

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    return Join-Path $repoRoot "bin\$projectName\$Configuration\$Framework\$Rid\publish"
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

Write-Host "== Build shared libraries =="
foreach ($project in $commonProjects) {
    Invoke-DotNet -Arguments @(
        "build",
        $project,
        "-c",
        $Configuration
    )
}

Write-Host "== Publish desktop targets =="
foreach ($desktopProject in $desktopProjects) {
    foreach ($rid in $desktopRidTargets) {
        Invoke-DotNet -Arguments @(
            "publish",
            $desktopProject.Project,
            "-c",
            $Configuration,
            "-r",
            $rid,
            "--self-contained",
            "false"
        )

        $publishDirectory = Get-PublishDirectory -ProjectPath $desktopProject.Project -Framework "net9.0" -Rid $rid
        $archivePath = Join-Path $outputRoot "$($desktopProject.Name)-$rid.zip"
        New-ZipFromDirectory -SourceDirectory $publishDirectory -DestinationArchive $archivePath
    }
}

Write-Host "== Publish Android MAUI APK =="
Invoke-DotNet -Arguments @(
    "publish",
    $androidProject,
    "-c",
    $Configuration,
    "-f",
    $androidFramework
)

$androidOutputRoot = Join-Path $repoRoot "bin\SimpleShadowsocks.Client.Maui\$Configuration\$androidFramework"
$apk = Get-ChildItem -LiteralPath $androidOutputRoot -File -Filter *-Signed.apk |
    Sort-Object @{ Expression = { $_.LastWriteTimeUtc }; Descending = $true } |
    Select-Object -First 1

if ($null -eq $apk) {
    throw "Signed Android APK was not found directly under '$androidOutputRoot'. Publish must produce the same top-level '*-Signed.apk' artifact as the manual command."
}

Copy-Item -LiteralPath $apk.FullName -Destination (Join-Path $outputRoot $apk.Name) -Force

Write-Host ""
Write-Host "Artifacts created in '$outputRoot':"
Get-ChildItem -LiteralPath $outputRoot -File | Sort-Object Name | ForEach-Object {
    Write-Host " - $($_.Name)"
}
