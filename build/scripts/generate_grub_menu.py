from __future__ import annotations

import argparse
from pathlib import Path
import sys


def to_grub_path(path: Path, media_root: Path) -> str:
    relative = path.resolve().relative_to(media_root.resolve())
    return "/" + str(relative).replace("\\", "/")


def grub_label(value: str) -> str:
    return value.replace('"', "").strip()


def grub_literal(value: str) -> str:
    return value.replace("'", "")


def quoted_message(message: str) -> str:
    return message.replace('"', "").strip()


def render_windows_entry(name: str, iso_path: str, wimboot_path: str | None, failure_reason: str | None) -> list[str]:
    failure = quoted_message(failure_reason or "missing boot files")
    lines = [
        f'menuentry "Windows: {grub_label(name)}" {{',
        f"    set isofile='{grub_literal(iso_path)}'",
        '    loopback loop "$isofile"',
    ]
    if wimboot_path:
        lines.extend(
            [
                f"    if [ -f {wimboot_path} ] && [ -f (loop)/sources/boot.wim ] && [ -f (loop)/boot/boot.sdi ]; then",
                f"        linux {wimboot_path}",
                "        if [ -f (loop)/efi/microsoft/boot/bcd ]; then",
                "            initrd newc:bootmgfw.efi:(loop)/efi/microsoft/boot/bootmgfw.efi newc:bcd:(loop)/efi/microsoft/boot/bcd newc:boot.sdi:(loop)/boot/boot.sdi newc:boot.wim:(loop)/sources/boot.wim",
                "            boot",
                "        fi",
                "        if [ -f (loop)/bootmgr ] && [ -f (loop)/boot/bcd ]; then",
                "            initrd newc:bootmgr:(loop)/bootmgr newc:bcd:(loop)/boot/bcd newc:boot.sdi:(loop)/boot/boot.sdi newc:boot.wim:(loop)/sources/boot.wim",
                "            boot",
                "        fi",
                "    fi",
            ]
        )
    lines.extend(
        [
            "    if [ -f (loop)/efi/boot/bootx64.efi ]; then",
            "        chainloader (loop)/efi/boot/bootx64.efi",
            "        boot",
            "    fi",
            f'    echo "{failure}"',
            "    sleep 6",
            "}",
        ]
    )
    return lines


def render_linux_entry(name: str, iso_path: str, label: str, kernel: str | None, initrd: str | None, kernel_args: str) -> list[str]:
    missing_kernel = quoted_message("unsupported kernel")
    missing_initrd = quoted_message("missing boot files")
    safe_kernel = kernel or ""
    safe_initrd = initrd or ""
    return [
        f'menuentry "{grub_label(label)}: {grub_label(name)}" {{',
        f"    set isofile='{grub_literal(iso_path)}'",
        '    loopback loop "$isofile"',
        f"    if [ ! -f (loop){safe_kernel} ]; then",
        f'        echo "{missing_kernel}"',
        "        sleep 6",
        "        return",
        "    fi",
        f"    if [ ! -f (loop){safe_initrd} ]; then",
        f'        echo "{missing_initrd}"',
        "        sleep 6",
        "        return",
        "    fi",
        f"    linux (loop){safe_kernel} {kernel_args} ---",
        f"    initrd (loop){safe_initrd}",
        "}",
    ]


def render_ubuntu_debian_entry(name: str, iso_path: str, kernel: str | None, initrd: str | None) -> list[str]:
    is_debian = "debian" in name.lower()
    kernel_args = "boot=live findiso=$isofile" if is_debian else "boot=casper iso-scan/filename=$isofile noeject noprompt splash"
    return render_linux_entry(name, iso_path, "Linux", kernel, initrd, kernel_args)


def render_arch_entry(name: str, iso_path: str, kernel: str | None, initrd: str | None) -> list[str]:
    missing_kernel = quoted_message("unsupported kernel")
    missing_initrd = quoted_message("missing boot files")
    safe_kernel = kernel or ""
    safe_initrd = initrd or ""
    return [
        f'menuentry "Arch: {grub_label(name)}" {{',
        f"    set isofile='{grub_literal(iso_path)}'",
        '    search --set=root --file "$isofile"',
        "    probe -u $root --set=rootuuid",
        '    loopback loop "$isofile"',
        f"    if [ ! -f (loop){safe_kernel} ]; then",
        f'        echo "{missing_kernel}"',
        "        sleep 6",
        "        return",
        "    fi",
        f"    if [ ! -f (loop){safe_initrd} ]; then",
        f'        echo "{missing_initrd}"',
        "        sleep 6",
        "        return",
        "    fi",
        f"    linux (loop){safe_kernel} img_dev=/dev/disk/by-uuid/${{rootuuid}} img_loop=$isofile earlymodules=loop",
        f"    initrd (loop){safe_initrd}",
        "}",
    ]


def render_custom_entry(name: str, iso_path: str, config_path: str) -> list[str]:
    return [
        f'menuentry "Custom: {grub_label(name)}" {{',
        f"    set isofile='{grub_literal(iso_path)}'",
        f"    configfile {grub_literal(config_path)}",
        "}",
    ]


def render_chainload_entry(label: str, name: str, iso_path: str, failure_reason: str | None) -> list[str]:
    failure = quoted_message(failure_reason or "missing boot files")
    return [
        f'menuentry "{grub_label(label)}: {grub_label(name)}" {{',
        f"    set isofile='{grub_literal(iso_path)}'",
        '    loopback loop "$isofile"',
        "    if [ -f (loop)/efi/boot/bootx64.efi ]; then",
        "        chainloader (loop)/efi/boot/bootx64.efi",
        "        boot",
        "    fi",
        f'    echo "{failure}"',
        "    sleep 6",
        "}",
    ]


def render_fallback_entry(name: str, iso_path: str, failure_reason: str | None) -> list[str]:
    failure = quoted_message(failure_reason or "incompatible ISO type")
    return [
        f'menuentry "Unsupported: {grub_label(name)}" {{',
        f'    echo "{failure}"',
        f'    echo "{grub_literal(iso_path)}"',
        "    sleep 6",
        "}",
    ]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--media-root", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--wimboot-path", default="")
    args = parser.parse_args()

    project_root = Path(__file__).resolve().parents[2]
    sys.path.insert(0, str(project_root))

    from cigertool.models import BootStrategy, IsoProfile, IsoSupportStatus
    from cigertool.services.multiboot_service import MultibootService

    media_root = Path(args.media_root).resolve()
    output_path = Path(args.output).resolve()
    service = MultibootService()

    roots = [
        media_root / "isos" / "windows",
        media_root / "isos" / "linux",
        media_root / "isos" / "tools",
        media_root / "iso-library",
    ]
    entries = service.scan_isos([path for path in roots if path.exists()])
    wimboot_path = args.wimboot_path or ""

    lines = [
        "set default=0",
        "set timeout=12",
        "",
        "insmod part_gpt",
        "insmod part_msdos",
        "insmod fat",
        "insmod ntfs",
        "insmod exfat",
        "insmod iso9660",
        "insmod udf",
        "insmod loopback",
        "insmod chain",
        "insmod search",
        "insmod regexp",
        "insmod test",
        "insmod probe",
        "insmod normal",
        "",
        "terminal_output console",
        "",
        'menuentry "CigerTool (WinPE)" {',
        "    if [ -f /EFI/Microsoft/Boot/bootmgfw.efi ]; then",
        "        chainloader /EFI/Microsoft/Boot/bootmgfw.efi",
        "        boot",
        "    fi",
        "    if [ -f /EFI/Boot/bootx64.efi ]; then",
        "        chainloader /EFI/Boot/bootx64.efi",
        "        boot",
        "    fi",
        '    echo "missing boot files"',
        "    sleep 5",
        "}",
        "",
    ]

    if not entries:
        lines.extend(
            [
                'menuentry "ISO bulunamadi" {',
                '    echo "incompatible ISO type"',
                "    sleep 5",
                "}",
            ]
        )
    else:
        for entry in entries:
            iso_grub_path = to_grub_path(Path(entry.path), media_root)
            companion_cfg = (
                to_grub_path(Path(entry.companion_config), media_root)
                if entry.companion_config and Path(entry.companion_config).exists()
                else ""
            )
            reason = entry.failure_reason or ""
            print(f"[{entry.support_status.value.upper()}] {entry.name} :: {reason or 'ready'}")
            if entry.support_status is IsoSupportStatus.UNSUPPORTED:
                lines.extend(render_fallback_entry(entry.name, iso_grub_path, entry.failure_reason))
            elif entry.boot_strategy is BootStrategy.CUSTOM_CONFIG and companion_cfg:
                lines.extend(render_custom_entry(entry.name, iso_grub_path, companion_cfg))
            elif entry.profile is IsoProfile.WINDOWS:
                lines.extend(render_windows_entry(entry.name, iso_grub_path, wimboot_path or None, entry.failure_reason))
            elif entry.profile is IsoProfile.UBUNTU_DEBIAN:
                lines.extend(render_ubuntu_debian_entry(entry.name, iso_grub_path, entry.kernel_path, entry.initrd_path))
            elif entry.profile is IsoProfile.ARCH:
                lines.extend(render_arch_entry(entry.name, iso_grub_path, entry.kernel_path, entry.initrd_path))
            elif entry.boot_strategy is BootStrategy.EFI_CHAINLOAD:
                lines.extend(render_chainload_entry("Tool ISO", entry.name, iso_grub_path, entry.failure_reason))
            else:
                lines.extend(render_fallback_entry(entry.name, iso_grub_path, entry.failure_reason))
            lines.append("")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text("\n".join(lines).strip() + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
