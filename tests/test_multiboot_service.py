from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from cigertool.models import BootStrategy, IsoCategory, IsoProfile, IsoSupportStatus
from cigertool.services.multiboot_service import MultibootService


class MultibootServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.service = MultibootService()

    def test_profiles_windows_iso_from_folder(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "isos" / "windows"
            root.mkdir(parents=True)
            iso = root / "Windows11.iso"
            iso.write_bytes(b"x")

            entry = self.service.profile_iso(iso, root)

            self.assertEqual(entry.category, IsoCategory.WINDOWS)
            self.assertEqual(entry.profile, IsoProfile.WINDOWS)
            self.assertEqual(entry.boot_strategy, BootStrategy.WIMBOOT)
            self.assertEqual(entry.support_status, IsoSupportStatus.UNTESTED)
            self.assertIsNone(entry.failure_reason)

    def test_uses_custom_companion_config(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "isos" / "tools"
            root.mkdir(parents=True)
            iso = root / "rescue.iso"
            iso.write_bytes(b"x")
            companion = root / "rescue.grub.cfg"
            companion.write_text("menuentry 'custom' {}", encoding="utf-8")

            entry = self.service.profile_iso(iso, root)

            self.assertEqual(entry.boot_strategy, BootStrategy.CUSTOM_CONFIG)
            self.assertEqual(Path(entry.companion_config), companion)
            self.assertEqual(entry.support_status, IsoSupportStatus.SUPPORTED)
            self.assertEqual(entry.status_label, "✔ çalışır")

    def test_sidecar_overrides_profile(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "isos" / "linux"
            root.mkdir(parents=True)
            iso = root / "mystery.iso"
            iso.write_bytes(b"x")
            sidecar = root / "mystery.cigertool.json"
            sidecar.write_text(
                json.dumps(
                    {
                        "profile": "arch",
                        "boot_strategy": "linux-loopback",
                        "kernel_path": "/arch/boot/x86_64/vmlinuz-linux",
                        "initrd_path": "/arch/boot/x86_64/initramfs-linux.img",
                    }
                ),
                encoding="utf-8",
            )

            entry = self.service.profile_iso(iso, root)

            self.assertEqual(entry.profile, IsoProfile.ARCH)
            self.assertEqual(entry.boot_strategy, BootStrategy.LINUX_LOOPBACK)
            self.assertEqual(entry.kernel_path, "/arch/boot/x86_64/vmlinuz-linux")
            self.assertEqual(entry.support_status, IsoSupportStatus.UNTESTED)

    def test_marks_unknown_linux_as_unsupported_kernel(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "isos" / "linux"
            root.mkdir(parents=True)
            iso = root / "mystery-linux.iso"
            iso.write_bytes(b"x")

            entry = self.service.profile_iso(iso, root)

            self.assertEqual(entry.category, IsoCategory.LINUX)
            self.assertEqual(entry.profile, IsoProfile.OTHER)
            self.assertEqual(entry.support_status, IsoSupportStatus.UNSUPPORTED)
            self.assertEqual(entry.failure_reason, "unsupported kernel")
            self.assertEqual(entry.status_label, "❌ desteklenmiyor")

    def test_marks_unknown_other_iso_as_incompatible(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "iso-library"
            root.mkdir(parents=True)
            iso = root / "mystery.iso"
            iso.write_bytes(b"x")

            entry = self.service.profile_iso(iso, root)

            self.assertEqual(entry.category, IsoCategory.OTHER)
            self.assertEqual(entry.support_status, IsoSupportStatus.UNSUPPORTED)
            self.assertEqual(entry.failure_reason, "incompatible ISO type")


if __name__ == "__main__":
    unittest.main()
