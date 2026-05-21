# DouyinTTS 一键构建 + 签名 + 安装
# 用法: powershell -ExecutionPolicy Bypass -File deploy.ps1

$ErrorActionPreference = "Stop"
$ProjectDir = Join-Path $PSScriptRoot "src\DouyinTTS.App"
$MsixPath = Join-Path $ProjectDir "AppPackages\DouyinTTS.App_1.0.0.0_x64_Test\DouyinTTS.App_1.0.0.0_x64.msix"
$DepPath = Join-Path $ProjectDir "AppPackages\DouyinTTS.App_1.0.0.0_x64_Test\Dependencies\x64\Microsoft.WindowsAppRuntime.2.msix"
$CertThumbprint = "986A68C4CE6D212F84C7C0A55D5CC91757A1630E"

# 查找 signtool.exe
$SignTool = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools\*\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $SignTool) {
    Write-Error "signtool.exe not found"
    exit 1
}

Write-Host "=== 1. Build ===" -ForegroundColor Cyan
dotnet publish "$ProjectDir\DouyinTTS.App.csproj" -c Release -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed"; exit 1 }

Write-Host "=== 2. Sign ===" -ForegroundColor Cyan
& $SignTool sign /fd SHA256 /sha1 $CertThumbprint $MsixPath
if ($LASTEXITCODE -ne 0) { Write-Error "Sign failed"; exit 1 }

Write-Host "=== 3. Uninstall ===" -ForegroundColor Cyan
$old = Get-AppxPackage -Name "DouyinTTS" -AllUsers -ErrorAction SilentlyContinue
if ($old) {
    $old | Remove-AppxPackage
    Write-Host "Uninstalled $($old.Version)"
} else {
    Write-Host "No previous version"
}

Write-Host "=== 4. Install ===" -ForegroundColor Cyan
Add-AppxPackage -Path $MsixPath -DependencyPath $DepPath

Write-Host "=== Done ===" -ForegroundColor Green
Start-Sleep -Seconds 2
powershell -NoProfile -Command "Get-AppxPackage -Name DouyinTTS | Select-Object Name, Version, Status | Format-Table"
