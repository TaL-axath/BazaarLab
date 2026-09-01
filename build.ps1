$ErrorActionPreference = 'Stop'

$repositoryRoot = $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot 'src'

dotnet build (Join-Path $sourceRoot 'BazaarLab.Combat\BazaarLab.Combat.csproj') `
    -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'BazaarLab.Combat build failed' }

dotnet build (Join-Path $sourceRoot 'BazaarLab.PlacementSearch\BazaarLab.PlacementSearch.csproj') `
    -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'BazaarLab.PlacementSearch build failed' }

dotnet build (Join-Path $sourceRoot 'BazaarLab.BaselineMetrics\BazaarLab.BaselineMetrics.csproj') `
    -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'BazaarLab.BaselineMetrics build failed' }

& (Join-Path $sourceRoot 'BazaarLab.Plugin\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'BazaarLab plugin build failed' }

Write-Output 'BazaarLab build completed.'
