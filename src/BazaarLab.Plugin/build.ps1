$ErrorActionPreference = 'Stop'

$projectDirectory = $PSScriptRoot
$gameDirectory = (Resolve-Path (Join-Path $projectDirectory '..\..\..\..')).Path
$managedDirectory = Join-Path $gameDirectory 'TheBazaar_Data\Managed'
$bepInExDirectory = Join-Path $gameDirectory 'BepInEx'
$outputDirectory = Join-Path $projectDirectory 'bin'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$sdkDirectory = (Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1).FullName
$compiler = Join-Path $sdkDirectory 'Roslyn\bincore\csc.dll'
$references = @(
    'mscorlib.dll',
    'netstandard.dll',
    'System.dll',
    'System.Core.dll',
    'System.Data.dll',
    'System.Runtime.dll',
    'System.Memory.dll',
    'System.IO.Compression.dll',
    'System.Text.Json.dll',
    'Gilzoide.SqliteNet.dll',
    'MessagePack.dll',
    'MessagePack.Annotations.dll',
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.IMGUIModule.dll'
    'UnityEngine.UIModule.dll'
    'UnityEngine.UI.dll'
    'Unity.TextMeshPro.dll'
) | ForEach-Object { Join-Path $managedDirectory $_ }
$references += @(
    (Join-Path $bepInExDirectory 'core\BepInEx.dll'),
    (Join-Path $bepInExDirectory 'core\0Harmony.dll'),
    (Join-Path $bepInExDirectory 'plugins\BazaarPlusPlus.dll'),
    (Join-Path $managedDirectory 'BazaarGameShared.dll'),
    (Join-Path $managedDirectory 'BazaarGameClient.dll'),
    (Join-Path $managedDirectory 'TheBazaarRuntime.dll')
)

$arguments = @(
    $compiler,
    '/noconfig',
    '/nostdlib+',
    '/target:library',
    '/langversion:latest',
    '/nullable:enable',
    '/warnaserror+',
    ('/out:' + (Join-Path $outputDirectory 'BazaarLab.dll'))
)
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$arguments += @(
    (Join-Path $projectDirectory 'Plugin.cs'),
    (Join-Path $projectDirectory 'CatalogManager.cs'),
    (Join-Path $projectDirectory 'PlacementControls.cs'),
    (Join-Path $projectDirectory 'MonsterCombatControls.cs'),
    (Join-Path $projectDirectory 'BaselineCurveControls.cs'),
    (Join-Path $projectDirectory 'EncounterPreviewControls.cs'),
    (Join-Path $projectDirectory 'DecisionTrace.cs'),
    (Join-Path $projectDirectory 'LineupDuelControls.cs'),
    (Join-Path $projectDirectory 'NativeReplayPlayback.cs')
    (Join-Path $projectDirectory 'FloatingWindowControls.cs')
    (Join-Path $projectDirectory 'BazaarLabUiComponents.cs')
)
& dotnet $arguments
if ($LASTEXITCODE -ne 0) {
    throw "capture bridge compilation failed with exit code $LASTEXITCODE"
}
Write-Output (Join-Path $outputDirectory 'BazaarLab.dll')
