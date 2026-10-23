param([switch]$Network)
$ErrorActionPreference = 'Stop'
$testDir = Join-Path $PSScriptRoot 'work\tests'
& (Join-Path $PSScriptRoot 'build-local.ps1') -OutputDirectory $testDir
New-Item -ItemType File -Force -Path (Join-Path $testDir 'portable.dat') | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testNames = if ($Network) { @('NetworkTests', 'BoardNetworkTests', 'AutoAssociationNetworkTests') } else { @('Regression', 'FeatureRegression') }
foreach ($testName in $testNames) {
    $testExe = Join-Path $testDir ($testName + '.exe')
    $source = Join-Path $PSScriptRoot ('tests\' + $testName + '.cs')
    & $compiler /nologo "/out:$testExe" "/r:$testDir\QuantNative.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll $source
    if ($LASTEXITCODE -ne 0) { throw "$testName compilation failed." }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "$testName failed." }
}
