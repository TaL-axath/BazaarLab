$ErrorActionPreference = 'Stop'

$installerRoot = $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $installerRoot '..')).Path
$gameRoot = (Resolve-Path (Join-Path $repositoryRoot '..\..')).Path
$sourceRoot = Join-Path $repositoryRoot 'src'
$version = '1.0.7'
$packageName = 'BazaarLab-v' + $version + '-Windows-x64'
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$packageRoot = Join-Path $artifactRoot $packageName
$payloadRoot = Join-Path $packageRoot 'payload\BazaarLab'
$runtimeRoot = Join-Path $payloadRoot 'runtime'
$dataRoot = Join-Path $payloadRoot 'data'
$releaseRoot = Join-Path $gameRoot '.reverse\releases'
$archivePath = Join-Path $releaseRoot ($packageName + '.zip')

& (Join-Path $repositoryRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'BazaarLab build failed' }

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $sourceRoot 'BazaarLab.Plugin\bin\BazaarLab.dll') `
    -Destination (Join-Path $payloadRoot 'BazaarLab.dll')

$projects = @(
    @{ Name = 'BazaarLab.Combat'; Directory = 'BazaarLab.Combat' },
    @{ Name = 'BazaarLab.PlacementSearch'; Directory = 'BazaarLab.PlacementSearch' },
    @{ Name = 'BazaarLab.BaselineMetrics'; Directory = 'BazaarLab.BaselineMetrics' }
)
foreach ($project in $projects) {
    $buildRoot = Join-Path $sourceRoot ($project.Directory + '\bin\Release\net8.0')
    foreach ($extension in @('.dll', '.deps.json', '.runtimeconfig.json')) {
        $file = Join-Path $buildRoot ($project.Name + $extension)
        if (-not (Test-Path -LiteralPath $file)) { throw 'Missing runtime file: ' + $file }
        Copy-Item -LiteralPath $file -Destination $runtimeRoot
    }
}

$catalogCandidates = @(
    (Join-Path $gameRoot 'BepInEx\plugins\BazaarLab\data\official-cards.jsonl'),
    (Join-Path $gameRoot '.reverse\catalog\official-cards.jsonl')
)
$catalog = $catalogCandidates | Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $catalog) { throw 'official-cards.jsonl was not found' }
Copy-Item -LiteralPath $catalog -Destination (Join-Path $dataRoot 'official-cards.jsonl')

$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$sdkDirectory = (Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
$compiler = Join-Path $sdkDirectory 'Roslyn\bincore\csc.dll'
$installerOutput = Join-Path $packageRoot 'BazaarLab-Installer.exe'
$compilerArguments = @(
    $compiler,
    '/noconfig',
    '/nostdlib+',
    '/target:winexe',
    '/platform:x64',
    '/langversion:latest',
    '/optimize+',
    ('/out:' + $installerOutput),
    ('/reference:' + (Join-Path $frameworkRoot 'mscorlib.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Core.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Drawing.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Windows.Forms.dll')),
    (Join-Path $installerRoot 'BazaarLab.Installer\Program.cs'),
    (Join-Path $installerRoot 'BazaarLab.Installer\AssemblyInfo.cs')
)
& dotnet $compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }

Copy-Item -LiteralPath (Join-Path $installerRoot 'INSTALL.zh-CN.txt') -Destination $packageRoot

$manifestPath = Join-Path $packageRoot 'payload.manifest'
$manifestLines = Get-ChildItem -LiteralPath (Join-Path $packageRoot 'payload') -Recurse -File |
    Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring((Join-Path $packageRoot 'payload').Length + 1).Replace('\', '/')
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '|' + $relative
    }
[System.IO.File]::WriteAllLines($manifestPath, $manifestLines,
    [System.Text.UTF8Encoding]::new($false))

if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archivePath `
    -CompressionLevel Optimal

$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
Write-Output ('Package: ' + $archivePath)
Write-Output ('SHA256: ' + $archiveHash)
