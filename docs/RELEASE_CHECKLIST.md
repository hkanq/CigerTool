# Release Checklist

## Build Oncesi

- [ ] `inputs/workspace/install.wim` mevcut
- [ ] `build-output/app/dist/CigerTool/CigerTool.exe` mevcut veya build sirasinda uretilebiliyor

## Build Sonrasi

- [ ] `artifacts/CigerTool.iso` olustu
- [ ] `artifacts/CigerTool.iso.sha256` olustu
- [ ] `artifacts/CigerTool-debug.zip` olustu
- [ ] `build-output/workspace/media-root/bootmgr` mevcut
- [ ] `build-output/workspace/media-root/boot/BCD` mevcut
- [ ] `build-output/workspace/media-root/boot/boot.sdi` mevcut
- [ ] `build-output/workspace/media-root/boot/etfsboot.com` mevcut
- [ ] `build-output/workspace/media-root/efi/boot/bootx64.efi` mevcut
- [ ] `build-output/workspace/media-root/efi/microsoft/boot/BCD` mevcut
- [ ] `build-output/workspace/media-root/efi/microsoft/boot/efisys.bin` mevcut
- [ ] `build-output/workspace/media-root/sources/boot.wim` mevcut
- [ ] `build-output/workspace/media-root/sources/install.wim` mevcut
- [ ] `build-output/workspace` altinda `.vhd` veya `.vhdx` yok

## Manuel Test

- [ ] ISO Rufus tarafinda desteklenen bootable imaj olarak algilaniyor
- [ ] BIOS modunda boot ediyor
- [ ] UEFI modunda boot ediyor
- [ ] `boot.wim` ortami otomatik basliyor
- [ ] Ayrica Setup UI gostermeden `install.wim` uygulanabiliyor
- [ ] OOBE gosterilmiyor
- [ ] Dogrudan masaustu aciliyor
- [ ] `CigerTool` otomatik basliyor
- [ ] Arayuz Turkce locale ile geliyor
