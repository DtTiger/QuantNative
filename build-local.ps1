param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Requires .NET Framework 4.8 and its C# compiler.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$metadata = Join-Path $OutputDirectory 'ReviewAssemblyInfo.cs'
@'
using System.Reflection;
[assembly:AssemblyVersion("2.6.0.0")]
[assembly:AssemblyFileVersion("2.6.0.0")]
[assembly:AssemblyInformationalVersion("2.6.0-review")]
[assembly:AssemblyTitle("QuantNative Review")]
[assembly:AssemblyProduct("QuantNative")]
'@ | Set-Content -LiteralPath $metadata -Encoding UTF8
$sourceRoot = Join-Path $PSScriptRoot 'src\QuantNative'
$sources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object FullName)
$target = Join-Path $OutputDirectory 'QuantNative.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu "/out:$target" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll "/win32manifest:$sourceRoot\app.manifest" @sources $metadata
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash + '  QuantNative.exe' | Set-Content -LiteralPath ($target + '.sha256') -Encoding ASCII
Write-Output "Built unsigned review executable: $target"
