param(
    [string]$PreviewDirectory = 'C:\Users\Admin\WorkBuddy\2026-09-28-18-53-38\preview'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$log = Join-Path $PreviewDirectory 'preview.log'
New-Item -ItemType Directory -Force -Path $PreviewDirectory | Out-Null
Start-Transcript -Path $log -Force | Out-Null

$testDir = Join-Path $root 'work\tests'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Requires .NET Framework 4.8 and its C# compiler.' }

New-Item -ItemType Directory -Force -Path $PreviewDirectory | Out-Null
$PreviewDirectory = (Resolve-Path -LiteralPath $PreviewDirectory).Path

# 复用回归测试编译出的 QuantNative.exe（run-tests.ps1 已经产出）
if (-not (Test-Path -LiteralPath (Join-Path $testDir 'QuantNative.exe'))) {
    & (Join-Path $root 'build-local.ps1') -OutputDirectory $testDir
}

$exe = Join-Path $testDir 'UiPreview.exe'
& $compiler /nologo /target:exe "/out:$exe" "/r:$testDir\QuantNative.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $root 'tests\UiPreview.cs')
if ($LASTEXITCODE -ne 0) { throw 'UiPreview compilation failed.' }

& $exe $PreviewDirectory
if ($LASTEXITCODE -ne 0) { throw 'UiPreview run failed.' }
Write-Output "Preview screenshots written to $PreviewDirectory"
Stop-Transcript | Out-Null
