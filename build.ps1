<#
.SYNOPSIS
    Builds RXShade as a self-contained single-file executable.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SelfTest        # build, then run the pipeline benchmark
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDir = "$PSScriptRoot\dist",
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\RXShade\RXShade.csproj'

Write-Host 'RXShade build - CortexDev' -ForegroundColor Cyan
Write-Host ('-' * 40)

# These are also set in the csproj, but they MUST be repeated here: since
# .NET 6, "dotnet publish" forces SelfContained=false unless -r or
# --self-contained appears on the command line, silently overriding the
# project property and producing a framework-dependent 9 MB exe instead.
dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $OutputDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

$exe = Join-Path $OutputDir 'RXShade.exe'
if (-not (Test-Path $exe)) { throw "Expected executable not found at $exe" }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

# A self-contained WPF single-file build is ~75 MB; anything near 10 MB means
# the runtime was not bundled and the exe will not run without .NET installed.
if ($sizeMb -lt 40) {
    throw "Published exe is only $sizeMb MB - the .NET runtime was not bundled. Publish is not self-contained."
}

Write-Host ''
Write-Host "Built: $exe  ($sizeMb MB, self-contained)" -ForegroundColor Green

if ($SelfTest) {
    Write-Host ''
    Write-Host 'Running pipeline self-test...' -ForegroundColor Cyan
    & $exe --selftest 3
}
