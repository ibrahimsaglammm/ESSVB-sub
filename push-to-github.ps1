$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$remoteUrl = "https://github.com/ibrahimsaglammm/ESSVB-sub.git"
if (-not ((git remote) -contains "origin")) {
    git remote add origin $remoteUrl
}

Write-Host "GitHub deposu kontrol ediliyor: https://github.com/ibrahimsaglammm/ESSVB-sub" -ForegroundColor Cyan
git push -u origin main
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Push basarisiz oldu. Eger 'Repository not found' hatasi aldiysaniz:" -ForegroundColor Yellow
    Write-Host "1) https://github.com/new adresinden 'ESSVB-sub' adinda bos bir depo olusturun." -ForegroundColor White
    Write-Host "2) Ardindan bu betigi yeniden calistirin: powershell -ExecutionPolicy Bypass -File .\push-to-github.ps1" -ForegroundColor White
    exit $LASTEXITCODE
}
Write-Host "Basariyla yuklendi: https://github.com/ibrahimsaglammm/ESSVB-sub" -ForegroundColor Green
