param(
    [switch]$NoRestore,
    [switch]$SkipRust,
    [switch]$SkipSelfCheck
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoRoot

$PropsPath = Join-Path $RepoRoot "Directory.Build.props"
[xml]$PropsXml = Get-Content -Path $PropsPath -Encoding UTF8
$Version = ($PropsXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "1.3.0" }

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Yellow
Write-Host "  ESSVB-sub v$Version — Windows 11 Tek Dosya .exe Derleyici" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Yellow

# 1. Build native Rust engine (essvb_fast.dll) unless skipped
if (-not $SkipRust) {
    $CargoCmd = Get-Command cargo -ErrorAction SilentlyContinue
    if (-not $CargoCmd -and (Test-Path "$env:USERPROFILE\.cargo\bin\cargo.exe")) {
        $env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
        $CargoCmd = Get-Command cargo -ErrorAction SilentlyContinue
    }

    $RustDir = Join-Path $RepoRoot "native\essvb_fast"
    if ($CargoCmd -and (Test-Path (Join-Path $RustDir "Cargo.toml"))) {
        Write-Host "[1/4] Rust hizlandirma motoru (essvb_fast.dll) derleniyor..." -ForegroundColor Cyan
        Push-Location $RustDir
        try {
            & cargo build --release
            if ($LASTEXITCODE -ne 0) { throw "cargo build --release basarisiz oldu ($LASTEXITCODE)" }
            $BuiltDll = Join-Path $RustDir "target\release\essvb_fast.dll"
            if (Test-Path $BuiltDll) {
                $BinDir = Join-Path $RustDir "bin"
                New-Item -ItemType Directory -Force -Path $BinDir | Out-Null
                Copy-Item $BuiltDll (Join-Path $BinDir "essvb_fast.dll") -Force
                Write-Host "      -> $BinDir\essvb_fast.dll guncellendi." -ForegroundColor Green
            }
        }
        finally {
            Pop-Location
        }
    }
    else {
        Write-Host "[1/4] Rust (cargo) bulunamadi; mevcut C# yedek veya hazir DLL kullanilacak." -ForegroundColor DarkYellow
    }
}
else {
    Write-Host "[1/4] Rust derlemesi atlandi (-SkipRust)." -ForegroundColor DarkGray
}

# 2. Publish self-contained single-file win-x64 executable
$DistDir = Join-Path $RepoRoot "dist"
$StageDir = Join-Path $DistDir "single-win11-x64"
if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

$AppProj = Join-Path $RepoRoot "src\EssvbSub.App\EssvbSub.App.csproj"
$PublishArgs = @(
    "publish", $AppProj,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:IncludeAllContentForSelfExtract=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "--disable-build-servers",
    "-m:1",
    "-o", $StageDir
)
if ($NoRestore) {
    $PublishArgs += "--no-restore"
}

Write-Host "[2/4] Tek dosya Windows 11 .exe derleniyor (Self-Contained win-x64)..." -ForegroundColor Cyan
& dotnet @PublishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$PublishedExe = Join-Path $StageDir "ESSVB-sub.exe"
if (-not (Test-Path $PublishedExe)) {
    throw "Yayinlanan EXE bulunamadi: $PublishedExe"
}

$OutFileName = "ESSVB-sub-v$Version-win11-x64.exe"
$OutExe = Join-Path $DistDir $OutFileName
$OutSha = Join-Path $DistDir "$OutFileName.sha256"

if (Test-Path $OutExe) { Remove-Item $OutExe -Force }
Copy-Item $PublishedExe $OutExe -Force
Remove-Item $StageDir -Recurse -Force

# 3. Compute SHA-256 checksum
Write-Host "[3/4] SHA-256 dogrulama dosyasi olusturuluyor..." -ForegroundColor Cyan
$Hash = (Get-FileHash -Path $OutExe -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($OutSha, "$Hash  $OutFileName`n", [System.Text.UTF8Encoding]::new($false))

$ExeInfo = Get-Item $OutExe
$SizeMb = [Math]::Round($ExeInfo.Length / 1MB, 2)
Write-Host "      EXE    : $OutExe ($SizeMb MB)" -ForegroundColor Green
Write-Host "      SHA256 : $Hash" -ForegroundColor Green

# 4. Run zero-UI self-check on standalone EXE
if (-not $SkipSelfCheck) {
    Write-Host "[4/4] Bagimsiz .exe icerik ve motor kontrolu (--self-check)..." -ForegroundColor Cyan
    $CheckOutput = & $OutExe --self-check
    $CheckExit = $LASTEXITCODE
    foreach ($line in $CheckOutput) {
        Write-Host "      $line" -ForegroundColor White
    }
    if ($CheckExit -ne 0) {
        throw "EXE --self-check basarisiz oldu (ExitCode=$CheckExit)"
    }
    Write-Host "      Sonuc  : BASARILI (Tum gomulu bilesenler hazir)" -ForegroundColor Green
}
else {
    Write-Host "[4/4] --self-check atlandi (-SkipSelfCheck)." -ForegroundColor DarkGray
}
