param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$project = "tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj"

function Invoke-TestStage {
    param(
        [string]$Name,
        [string]$Category
    )

    Write-Host "== $Name =="

    $args = @(
        "test",
        $project,
        "-c",
        $Configuration,
        "--filter",
        "Category=$Category",
        "--logger",
        "console;verbosity=minimal"
    )

    if ($NoBuild) {
        $args += "--no-build"
    }

    if ($NoRestore) {
        $args += "--no-restore"
    }

    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "Stage '$Name' failed with exit code $LASTEXITCODE."
    }
}

Invoke-TestStage -Name "Unit tests" -Category "Unit"
Invoke-TestStage -Name "Integration tests" -Category "Integration"
Invoke-TestStage -Name "Performance tests" -Category "Performance"
