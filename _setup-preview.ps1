param(
    [string]$PreviewDirectory = 'C:\Users\Admin\WorkBuddy\2026-09-28-18-53-38\preview'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$testDir = Join-Path $root 'work\tests'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $PreviewDirectory | Out-Null
$exe = Join-Path $testDir 'UiPreview.exe'
& $compiler /nologo /target:exe "/out:$exe" "/r:$testDir\QuantNative.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $root 'tests\UiPreview.cs')
'SETUP exit=' + $LASTEXITCODE | Out-File -Encoding utf8 (Join-Path $PreviewDirectory 'setup.log')
