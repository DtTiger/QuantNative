param(
    [string]$PreviewDirectory = 'C:\Users\Admin\WorkBuddy\2026-09-28-18-53-38\preview'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$testDir = Join-Path $root 'work\tests'
$exe = Join-Path $testDir 'UiPreview.exe'
$log = Join-Path $PreviewDirectory 'run.log'
& $exe $PreviewDirectory *>&1 | Out-File -Encoding utf8 $log
'RUN exit=' + $LASTEXITCODE | Out-File -Encoding utf8 -Append $log
