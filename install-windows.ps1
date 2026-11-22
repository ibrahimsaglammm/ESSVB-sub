param(
    [switch]$LaunchOnly,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoRoot

Write-Host ""
Write-Host "==============================================" -ForegroundColor Yellow
Write-Host "   ESSVB-sub — Windows 11 Kurulum & Baslatici" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Yellow

function Test-DotNet10 {
    try {
        $sdks = & dotnet --list-sdks 2>$null
        return [bool]($sdks | Where-Object { $_ -match '^10\.' })
    }
    catch {
        return $false
    }
}

if (-not (Test-DotNet10)) {
    Write-Host "[1/3] .NET 10 SDK bulunamadi. winget ile otomatik yukleniyor..." -ForegroundColor Yellow
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
        $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
    }
    if (-not (Test-DotNet10)) {
        Write-Host "HATA: .NET 10 SDK kurulamadi. Lutfen https://dotnet.microsoft.com/download/dotnet/10.0 adresinden indirip tekrar deneyin." -ForegroundColor Red
        exit 1
    }
}

$ExePath = Join-Path $RepoRoot "src\EssvbSub.App\bin\Release\net10.0\ESSVB-sub.exe"
$NeedBuild = (-not $LaunchOnly) -or (-not (Test-Path $ExePath))

if ($NeedBuild) {
    $RustDir = Join-Path $RepoRoot "native\essvb_fast"
    $CargoCmd = Get-Command cargo -ErrorAction SilentlyContinue
    if (-not $CargoCmd -and (Test-Path "$env:USERPROFILE\.cargo\bin\cargo.exe")) {
        $env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
        $CargoCmd = Get-Command cargo -ErrorAction SilentlyContinue
    }
    if ($CargoCmd -and (Test-Path (Join-Path $RustDir "Cargo.toml"))) {
        Write-Host "[2a/3] Rust hizlandirma modulu (essvb_fast.dll) derleniyor..." -ForegroundColor Cyan
        Push-Location $RustDir
        & cargo build --release
        if ($LASTEXITCODE -eq 0 -and (Test-Path "target\release\essvb_fast.dll")) {
            New-Item -ItemType Directory -Force -Path "bin" | Out-Null
            Copy-Item "target\release\essvb_fast.dll" "bin\essvb_fast.dll" -Force
        }
        Pop-Location
    }

    Write-Host "[2/3] Paketler indiriliyor ve ESSVB-sub derleniyor (Release)..." -ForegroundColor Cyan
    & dotnet restore (Join-Path $RepoRoot "ESSVB-sub.slnx")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & dotnet build (Join-Path $RepoRoot "ESSVB-sub.slnx") -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Create Desktop shortcut pointing to Start-ESSVB-sub.bat
$Desktop = [Environment]::GetFolderPath("Desktop")
$ShortcutPath = Join-Path $Desktop "ESSVB-sub.lnk"
$BatPath = Join-Path $RepoRoot "Start-ESSVB-sub.bat"
if (-not (Test-Path $ShortcutPath) -or (-not $LaunchOnly)) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($ShortcutPath)
        $lnk.TargetPath = $BatPath
        $lnk.WorkingDirectory = $RepoRoot
        $lnk.Description = "ESSVB-sub Canli Altyazi & Ceviri Studyosu"
        if (Test-Path $ExePath) {
            $lnk.IconLocation = "$ExePath,0"
        }
        $lnk.Save()
        Write-Host "[3/3] Masaustu kisayolu hazir: $ShortcutPath" -ForegroundColor Green
    }
    catch {
        Write-Host "Masaustu kisayolu olusturulamadi (devam ediliyor): $($_.Exception.Message)" -ForegroundColor DarkGray
    }
}

if (-not $NoLaunch) {
    Write-Host "ESSVB-sub baslatiliyor..." -ForegroundColor Green
    Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath)
}
