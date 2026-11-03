#!/usr/bin/env python3
"""Build a tiny EFI loader and verify generated CD/DVD images with OVMF + QEMU."""

import argparse
import shutil
import subprocess
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fat-type", choices=("12", "16", "32"), default="12")
    parser.add_argument("--cli", type=Path, required=True, help="DiscImageStudio CLI executable")
    parser.add_argument("--ovmf-code", type=Path, default=Path("/usr/share/OVMF/OVMF_CODE_4M.fd"))
    parser.add_argument("--ovmf-vars", type=Path, default=Path("/usr/share/OVMF/OVMF_VARS_4M.fd"))
    parser.add_argument("--output-dir", type=Path, default=Path("artifacts/uefi-boot"))
    args = parser.parse_args()
    for tool in ("clang", "ld", "mkfs.fat", "mmd", "mcopy", "qemu-system-x86_64"):
        if not shutil.which(tool):
            parser.error(f"Required tool is missing: {tool}")
    for path in (args.cli, args.ovmf_code, args.ovmf_vars):
        if not path.is_file():
            parser.error(f"Required file is missing: {path}")
    root = Path(__file__).resolve().parents[2]
    work = args.output_dir.resolve()
    work.mkdir(parents=True, exist_ok=True)

    def run(*command: object) -> None:
        subprocess.run([str(item) for item in command], check=True)

    run("clang", "--target=x86_64-pc-win32-coff", "-ffreestanding", "-fno-stack-protector",
        "-mno-red-zone", "-O2", "-c", Path(__file__).with_name("boot.c"), "-o", work / "boot.obj")
    run("ld", "-mi386pep", "--subsystem", "10", "--entry", "efi_main", "--image-base", "0",
        "--enable-reloc-section", "-o", work / "BOOTX64.EFI", work / "boot.obj")
    fat = work / "efi.img"
    with fat.open("wb") as stream:
        stream.truncate({"12": 1440 * 1024, "16": 8 * 1024 * 1024, "32": 64 * 1024 * 1024}[args.fat_type])
    run("mkfs.fat", "-F", args.fat_type, fat)
    sectors = str(max(1024, (fat.stat().st_size // 2048 + 319) // 16 * 16))
    run("mmd", "-i", fat, "::/EFI", "::/EFI/BOOT")
    run("mcopy", "-i", fat, work / "BOOTX64.EFI", "::/EFI/BOOT/BOOTX64.EFI")
    data = work / "files"
    data.mkdir(exist_ok=True)
    (data / "hello.txt").write_text("Ordinary ISO file next to the recording artwork.\n", encoding="utf-8")
    image = root / "src/DiscImageStudio.App/Assets/DiscImageStudio-256.png"
    for media in ("cd", "dvd"):
        for placement in ("inner", "outer"):
            name = f"{media}-{placement}"
            iso = work / f"{name}.iso"
            if media == "cd":
                command = ["cd-generate", "--input", image, "--output", iso, "--sectors", sectors]
            else:
                command = ["solve", "--image", image, "--iso-output", iso,
                           "--total-sectors", sectors, "--fast-parallelism", "1"]
            run(args.cli.resolve(), *command, "--efi-boot-image", fat, "--data-dir", data,
                "--file-position", placement)
            variables = work / f"{name}-vars.fd"
            shutil.copyfile(args.ovmf_vars, variables)
            log = work / f"{name}-debug.log"
            with (work / f"{name}-qemu.log").open("w") as qemu_log:
                result = subprocess.run([
                    "qemu-system-x86_64", "-machine", "q35,accel=tcg", "-m", "128",
                    "-drive", f"if=pflash,format=raw,readonly=on,file={args.ovmf_code.resolve()}",
                    "-drive", f"if=pflash,format=raw,file={variables}",
                    "-drive", f"file={iso},format=raw,media=cdrom,readonly=on",
                    "-boot", "order=d", "-display", "none", "-serial", "none", "-monitor", "none",
                    "-net", "none", "-debugcon", f"file:{log}", "-global", "isa-debugcon.iobase=0xe9",
                    "-device", "isa-debug-exit,iobase=0xf4,iosize=0x04", "-no-reboot",
                ], stdout=qemu_log, stderr=subprocess.STDOUT, timeout=45)
            if result.returncode != 33 or b"DiscImageStudio UEFI boot OK" not in log.read_bytes():
                raise RuntimeError(f"{name}: EFI loader did not run; inspect {log} and {name}-qemu.log")
            print(f"{name}: UEFI loader executed successfully", flush=True)


if __name__ == "__main__":
    main()
