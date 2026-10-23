param(
    [string]$PreviewDirectory = 'C:\Users\Admin\WorkBuddy\2026-09-28-18-53-38\preview'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$testDir = Join-Path $root 'work\tests'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $PreviewDirectory | Out-Null
$exe = Join-Path $testDir 'UiPreview.exe'
# 编译失败必须立刻停：否则下面的 _run-preview 会拿上一次的旧 exe 跑，
# 截图看起来"成功"，其实代码根本没编译进去（2026-10-23 踩过）。
if (Test-Path $exe) { Remove-Item -LiteralPath $exe -Force }
& $compiler /nologo /target:exe "/out:$exe" "/r:$testDir\QuantNative.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $root 'tests\UiPreview.cs')
if ($LASTEXITCODE -ne 0) { throw "UiPreview 编译失败（exit=$LASTEXITCODE），截图不会更新。" }
'SETUP exit=' + $LASTEXITCODE | Out-File -Encoding utf8 (Join-Path $PreviewDirectory 'setup.log')
