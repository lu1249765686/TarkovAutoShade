$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$wpfProjectDir = Join-Path $projectDir "src-wpf"
$outputDir = Join-Path $projectDir "bin-wpf"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  TarkovAutoShade WPF Build Script" -ForegroundColor Cyan
Write-Host "  Using MSBuild" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$msbuildPaths = @(
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
)

$msbuild = $null
foreach ($path in $msbuildPaths) {
    if (Test-Path $path) {
        $msbuild = $path
        break
    }
}

if (-not $msbuild) {
    Write-Host "ERROR: MSBuild not found!" -ForegroundColor Red
    throw "请安装 Visual Studio Build Tools 或 .NET Framework Developer Pack。"
}

Write-Host "Using MSBuild: $msbuild" -ForegroundColor Green
Write-Host ""

if (Test-Path $outputDir) {
    Remove-Item -Path $outputDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Write-Host "Building WPF project..." -ForegroundColor Yellow
Push-Location $wpfProjectDir

try {
    & $msbuild "TarkovAutoShade.csproj" /p:Configuration=Release /p:OutputPath="$outputDir" /p:Platform=AnyCPU /v:minimal

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }

    Write-Host ""
    Write-Host "Build completed successfully!" -ForegroundColor Green

    $exePath = Join-Path $outputDir "TarkovAutoShade.exe"
    if (Test-Path $exePath) {
        Write-Host "  Output: $exePath" -ForegroundColor Cyan
        $fileInfo = Get-Item $exePath
        $fileSizeKB = [math]::Round($fileInfo.Length / 1KB, 2)
        Write-Host "  Size: $fileSizeKB KB" -ForegroundColor Cyan
    }
}
catch {
    Write-Host ""
    Write-Host "Build failed: $_" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Build Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
