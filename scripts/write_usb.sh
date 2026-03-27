#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ISO_PATH="${1:-$ROOT_DIR/dist/CigerTool-amd64.iso}"
TARGET_DEVICE="${2:-}"

if [[ ! -f "$ISO_PATH" ]]; then
  echo "ISO bulunamadi: $ISO_PATH" >&2
  exit 1
fi

if [[ -z "$TARGET_DEVICE" ]]; then
  echo "Mevcut diskler:"
  lsblk -dpno NAME,SIZE,MODEL,TRAN
  read -r -p "USB hedef aygitini girin (ornek /dev/sdb): " TARGET_DEVICE
fi

if [[ "$(lsblk -dn -o TYPE "$TARGET_DEVICE" 2>/dev/null || true)" != "disk" ]]; then
  echo "Gecersiz hedef aygit: $TARGET_DEVICE" >&2
  exit 1
fi

echo "UYARI: $TARGET_DEVICE uzerindeki tum veri silinecek."
read -r -p "Devam etmek icin EVET yazin: " CONFIRMATION
if [[ "$CONFIRMATION" != "EVET" ]]; then
  echo "Islem iptal edildi."
  exit 1
fi

wipefs -af "$TARGET_DEVICE"
dd if="$ISO_PATH" of="$TARGET_DEVICE" bs=16M status=progress conv=fsync oflag=direct
sync
echo "USB hazir: $TARGET_DEVICE"
