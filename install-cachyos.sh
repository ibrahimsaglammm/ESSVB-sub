#!/usr/bin/env bash
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_DIR"

echo "=============================================="
echo "  ESSVB-sub — CachyOS / Arch Kurulum Sihirbazi"
echo "=============================================="

if command -v pacman >/dev/null 2>&1; then
  echo "[1/3] CachyOS sistem paketleri kontrol ediliyor / kuruluyor..."
  sudo pacman -S --needed --noconfirm \
    dotnet-sdk \
    tesseract \
    tesseract-data-eng \
    pipewire-pulse \
    grim \
    ffmpeg
else
  echo "Uyari: pacman bulunamadi. dotnet-sdk (10), tesseract, pipewire-pulse, grim ve ffmpeg paketlerinin kurulu oldugundan emin olun."
fi

if command -v cargo >/dev/null 2>&1 && [ -f "$REPO_DIR/native/essvb_fast/Cargo.toml" ]; then
  echo "[2a/3] Rust hizlandirma kutuphanesi (libessvb_fast.so) derleniyor..."
  (cd "$REPO_DIR/native/essvb_fast" && cargo build --release && mkdir -p bin && cp -f target/release/libessvb_fast.so bin/libessvb_fast.so)
fi

echo "[2/3] ESSVB-sub derleniyor (Release)..."
dotnet restore ESSVB-sub.slnx
dotnet build ESSVB-sub.slnx -c Release --no-restore

DLL_PATH="$REPO_DIR/src/EssvbSub.App/bin/Release/net10.0/ESSVB-sub.dll"
ICON_PATH="$REPO_DIR/src/EssvbSub.App/Assets/essvb-logo.png"
DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$DESKTOP_DIR"

cat > "$DESKTOP_DIR/essvb-sub.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=ESSVB-sub
Comment=PS5, PS4 Pro, PC Oyunlari ve Film/Diziler icin Canli Turkce Altyazi
Exec=dotnet "$DLL_PATH"
Icon=$ICON_PATH
Terminal=false
Categories=Utility;AudioVideo;Game;
EOF

echo "[3/3] Masaustu uygulama menusu olusturuldu: $DESKTOP_DIR/essvb-sub.desktop"
echo "ESSVB-sub baslatiliyor..."
nohup dotnet "$DLL_PATH" >/dev/null 2>&1 &
