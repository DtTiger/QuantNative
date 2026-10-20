# QuantNative 一键构建脚本
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# 依次尝试 dotnet / msbuild；成功后把 exe 复制到 dist\ 并打印路径。
# 不需要 Docker、不需要 Visual Studio（有 .NET SDK 即可；net48 的引用程序集
# 由 NuGet 包 Microsoft.NETFramework.ReferenceAssemblies 提供）。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$project = Join-Path $root 'src\QuantNative\QuantNative.csproj'
$outDir = Join-Path $root 'dist'

Write-Host '=== QuantNative 构建 ===' -ForegroundColor Cyan
Write-Host "工程: $project"

if (-not (Test-Path $project)) {
    throw "找不到工程文件: $project"
}

function Invoke-Build {
    param([string]$Exe, [string[]]$Args)

    Write-Host "使用编译器: $Exe" -ForegroundColor Green
    & $Exe @Args
    if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）" }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$msbuild = Get-Command msbuild -ErrorAction SilentlyContinue

if ($dotnet) {
    Invoke-Build -Exe $dotnet.Source -Args @('build', $project, '-c', 'Release', '--nologo')
}
elseif ($msbuild) {
    Invoke-Build -Exe $msbuild.Source -Args @($project, '/p:Configuration=Release', '/nologo', '/v:minimal')
}
else {
    Write-Host @'
没有找到 dotnet 或 msbuild。三条路可选：
  1) 安装 .NET SDK（任意版本，无需安装 .NET Framework 开发包）：https://dotnet.microsoft.com/download
  2) 安装 Visual Studio 生成工具（勾选 .NET 桌面开发）
  3) 什么都不装：把本仓库推到 GitHub，用 .github/workflows/build.yml 在云端出 exe
'@ -ForegroundColor Yellow
    exit 1
}

$built = Join-Path $root 'src\QuantNative\bin\Release\QuantNative.exe'
if (-not (Test-Path $built)) { throw "构建完成但找不到产物: $built" }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$target = Join-Path $outDir 'QuantNative.exe'
Copy-Item $built $target -Force

$size = [math]::Round((Get-Item $target).Length / 1KB, 1)
Write-Host ''
Write-Host "构建成功 -> $target ($size KB)" -ForegroundColor Green
Write-Host '提示：把 portable.dat（空文件）放到 exe 同目录即为便携模式，数据写入 data\ 子目录。'
