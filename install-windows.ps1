param(
    [switch]$LaunchOnly,
    [switch]$NoLaunch,
    [switch]$BuildStandaloneExe
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoRoot

Write-Host ""
Write-Host "==============================================" -ForegroundColor Yellow
Write-Host "  ESSVB-sub v1.4.0 — Windows 11 Kurulum & Acilis" -ForegroundColor Cyan
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

function Find-StandaloneExe {
    $distDir = Join-Path $RepoRoot "dist"
    if (Test-Path $distDir) {
        $cand = Get-ChildItem -Path $distDir -Filter "ESSVB-sub-v*-win11-x64.exe" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($cand) { return $cand.FullName }
    }
    return $null
}

$ReleaseExePath = Join-Path $RepoRoot "src\EssvbSub.App\bin\Release\net10.0\ESSVB-sub.exe"
$StandaloneExePath = Find-StandaloneExe
$LaunchExe = if ($StandaloneExePath) { $StandaloneExePath } else { $ReleaseExePath }

$NeedBuild = $BuildStandaloneExe -or (-not $LaunchOnly) -or (-not (Test-Path $LaunchExe))

if ($NeedBuild) {
    if (-not (Test-DotNet10)) {
        Write-Host "[1/3] .NET 10 SDK bulunamadi. winget ile otomatik yukleniyor..." -ForegroundColor Yellow
        if (Get-Command winget -ErrorAction SilentlyContinue) {
            winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
            $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
        }
        if (-not (Test-DotNet10)) {
            Write-Host "HATA: .NET 10 SDK kurulamadi. Lutfen https://dotnet.microsoft.com/download/dotnet/10.0 adresinden indirip tekrar deneyin." -ForegroundColor Red
            Write-Host "Alternatif: GitHub Releases sayfasindan hazir tek dosya ESSVB-sub-win11-x64.exe indirebilirsiniz." -ForegroundColor Yellow
            exit 1
        }
    }

    if ($BuildStandaloneExe) {
        Write-Host "[2/3] Tek dosya Windows 11 .exe derleniyor..." -ForegroundColor Cyan
        & (Join-Path $RepoRoot "build-windows-exe.ps1")
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $StandaloneExePath = Find-StandaloneExe
        $LaunchExe = if ($StandaloneExePath) { $StandaloneExePath } else { $ReleaseExePath }
    }
    else {
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

        Write-Host "[2/3] Paketler yukleniyor ve ESSVB-sub derleniyor (Release)..." -ForegroundColor Cyan
        & dotnet restore (Join-Path $RepoRoot "ESSVB-sub.slnx")
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        & dotnet build (Join-Path $RepoRoot "ESSVB-sub.slnx") -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        $LaunchExe = if (Test-Path $ReleaseExePath) { $ReleaseExePath } else { $LaunchExe }
    }
}

# Create Desktop shortcut pointing directly to the GUI EXE (no console flash)
$Desktop = [Environment]::GetFolderPath("Desktop")
$ShortcutPath = Join-Path $Desktop "ESSVB-sub.lnk"
if ((Test-Path $LaunchExe) -and (-not (Test-Path $ShortcutPath) -or (-not $LaunchOnly))) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($ShortcutPath)
        $lnk.TargetPath = $LaunchExe
        $lnk.WorkingDirectory = Split-Path -Parent $LaunchExe
        $lnk.Description = "ESSVB-sub v1.4.0 Canli Altyazi & Ceviri Studyosu"
        $lnk.IconLocation = "$LaunchExe,0"
        $lnk.Save()
        Write-Host "[3/3] Masaustu kisayolu hazir: $ShortcutPath -> $LaunchExe" -ForegroundColor Green
    }
    catch {
        Write-Host "Masaustu kisayolu olusturulamadi (devam ediliyor): $($_.Exception.Message)" -ForegroundColor DarkGray
    }
}

if (-not $NoLaunch) {
    Write-Host "ESSVB-sub baslatiliyor: $LaunchExe" -ForegroundColor Green
    Start-Process -FilePath $LaunchExe -WorkingDirectory (Split-Path -Parent $LaunchExe)
}
