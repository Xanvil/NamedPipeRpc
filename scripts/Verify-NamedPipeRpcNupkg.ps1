#Requires -Version 5.1
<#
.SYNOPSIS
  打包 NamedPipeRpc（含 Source Generator），并验证纯 PackageReference 消费方可编译运行。

.DESCRIPTION
  1. dotnet pack → artifacts/nuget（默认版本 1.1.0）
  2. 断言 nupkg 含 analyzers/dotnet/cs/NamedPipeRpc.Generators.dll 与 buildTransitive targets
  3. PackageSmoke.Diagnostics：期望编译失败并含 NPRC001-007
  4. restore / build / run NamedPipeRpc.PackageSmoke（成功路径 + 运行时错误）
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $PackageVersion = "1.1.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$packOut = Join-Path $repoRoot "artifacts\nuget"
$mainProj = Join-Path $repoRoot "NamedPipeRpc\NamedPipeRpc.csproj"
$smokeProj = Join-Path $repoRoot "NamedPipeRpc.PackageSmoke\NamedPipeRpc.PackageSmoke.csproj"
$diagProj = Join-Path $repoRoot "NamedPipeRpc.PackageSmoke.Diagnostics\NamedPipeRpc.PackageSmoke.Diagnostics.csproj"

Write-Host "==> Pack NamedPipeRpc $PackageVersion ($Configuration)" -ForegroundColor Cyan
if (Test-Path $packOut) {
    Remove-Item -Path (Join-Path $packOut "NamedPipeRpc.*.nupkg") -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $packOut -Force | Out-Null

dotnet pack $mainProj `
    -c $Configuration `
    -o $packOut `
    -p:Version=$PackageVersion `
    -p:PackageVersion=$PackageVersion
if ($LASTEXITCODE -ne 0) { throw "dotnet pack 失败 (exit $LASTEXITCODE)" }

$nupkg = Get-ChildItem -Path $packOut -Filter "NamedPipeRpc.$PackageVersion.nupkg" | Select-Object -First 1
if (-not $nupkg) {
    $nupkg = Get-ChildItem -Path $packOut -Filter "NamedPipeRpc.*.nupkg" | Select-Object -First 1
}
if (-not $nupkg) { throw "未找到 nupkg：$packOut" }

Write-Host "==> Inspect $($nupkg.Name)" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
try {
    $entries = $zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }
    $required = @(
        "analyzers/dotnet/cs/NamedPipeRpc.Generators.dll",
        "buildTransitive/NamedPipeRpc.targets"
    )
    foreach ($path in $required) {
        # nupkg 条目通常无前导斜杠，路径即相对包根
        if ($entries -notcontains $path) {
            Write-Host "包内条目：" -ForegroundColor Yellow
            $entries | Sort-Object | ForEach-Object { Write-Host "  $_" }
            throw "nupkg 缺少必需条目：$path"
        }
        Write-Host "  OK $path"
    }

    $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like "*.nuspec" } | Select-Object -First 1
    if (-not $nuspecEntry) { throw "nupkg 缺少 nuspec" }
    $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
    try { $nuspecXml = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    if ($nuspecXml -notmatch "<version>$([regex]::Escape($PackageVersion))</version>") {
        throw "nuspec 版本不是 $PackageVersion"
    }
    Write-Host "  OK nuspec version $PackageVersion"
    if ($nuspecXml -notmatch "<releaseNotes>\s*\S") {
        throw "nuspec 缺少 releaseNotes"
    }
    Write-Host "  OK releaseNotes"
}
finally {
    $zip.Dispose()
}

# 清除本地缓存中可能过期的同名包，强制使用刚 pack 的产物
$localCache = Join-Path $env:USERPROFILE ".nuget\packages\namedpiperpc\$PackageVersion"
if (Test-Path $localCache) {
    Remove-Item -Recurse -Force $localCache
}

Write-Host "==> Diagnostics smoke (expect NPRC compile errors)" -ForegroundColor Cyan
dotnet restore $diagProj --force
if ($LASTEXITCODE -ne 0) { throw "dotnet restore PackageSmoke.Diagnostics 失败 (exit $LASTEXITCODE)" }

$diagLog = Join-Path $repoRoot "artifacts\nuget\diagnostics-build.log"
dotnet build $diagProj -c $Configuration --no-restore 2>&1 | Tee-Object -FilePath $diagLog | Out-Host
if ($LASTEXITCODE -eq 0) {
    throw "PackageSmoke.Diagnostics 不应编译成功（期望包内 Generator 报 NPRC00x）"
}
$diagText = Get-Content -Raw $diagLog
$requiredCodes = @("NPRC001", "NPRC002", "NPRC003", "NPRC004", "NPRC005", "NPRC006", "NPRC007")
foreach ($code in $requiredCodes) {
    if ($diagText -notmatch [regex]::Escape($code)) {
        throw "Diagnostics 构建日志缺少期望诊断码：$code"
    }
    Write-Host "  OK diagnostic $code"
}

Write-Host "==> Restore / build / run PackageSmoke" -ForegroundColor Cyan
dotnet restore $smokeProj --force
if ($LASTEXITCODE -ne 0) { throw "dotnet restore PackageSmoke 失败 (exit $LASTEXITCODE)" }

dotnet build $smokeProj -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build PackageSmoke 失败 (exit $LASTEXITCODE)" }

dotnet run --project $smokeProj -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw "dotnet run PackageSmoke 失败 (exit $LASTEXITCODE)" }

Write-Host "==> NuGet package verification succeeded." -ForegroundColor Green
