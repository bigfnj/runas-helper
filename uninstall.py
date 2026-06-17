#!/usr/bin/env python3
"""
RunAS Helper — uninstaller / cleanup tool.

Removes every artifact a RunAS Helper install can leave behind, in dependency
order. Use it for a clean uninstall or to scrub a partial/failed install that
the normal "Add or Remove Programs" entry can no longer remove.

What it removes (best-effort, continues past missing pieces):
  1. The tray client process (RunAsHelper.exe), so files aren't locked.
  2. The MSI product itself (msiexec /x), found by DisplayName in the
     Uninstall registry.
  3. The Windows service "RunASHelper".
  4. Registry keys HKLM\\Software\\RunAsHelper and HKCU\\Software\\RunAsHelper.
  5. The leftover %ProgramFiles%\\RunAsHelper folder.

Run from an elevated prompt, or just run it normally — it will relaunch itself
elevated (UAC prompt) when needed.

    python uninstall.py            # normal run
    python uninstall.py --dry-run  # show what it would do, change nothing
    python uninstall.py --yes      # skip the confirmation prompt
"""

from __future__ import annotations

import argparse
import ctypes
import os
import shutil
import subprocess
import sys

# ── Identifiers (must match RunAsHelper.Installer/Package.wxs) ───────────────
PRODUCT_NAME = "RunAS Helper"          # MSI Package Name / ARP DisplayName
SERVICE_NAME = "RunASHelper"           # ServiceInstall Name
PROCESS_NAME = "RunAsHelper.exe"       # tray client image name
INSTALL_DIRNAME = "RunAsHelper"        # folder under Program Files
REG_KEYS = (
    (r"HKLM", r"Software\RunAsHelper"),
    (r"HKCU", r"Software\RunAsHelper"),
)

DRY_RUN = False


# ── Console helpers ──────────────────────────────────────────────────────────
def info(msg: str) -> None:
    print(f"[*] {msg}")


def ok(msg: str) -> None:
    print(f"[+] {msg}")


def warn(msg: str) -> None:
    print(f"[!] {msg}")


def run(cmd: list[str], *, check: bool = False) -> subprocess.CompletedProcess:
    """Run a command, capturing output. Honours --dry-run."""
    printable = " ".join(cmd)
    if DRY_RUN:
        info(f"DRY-RUN would run: {printable}")
        return subprocess.CompletedProcess(cmd, 0, "", "")
    return subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        check=check,
    )


# ── Elevation ────────────────────────────────────────────────────────────────
def is_admin() -> bool:
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def relaunch_as_admin() -> None:
    """Re-launch this script elevated, preserving arguments."""
    params = " ".join(f'"{a}"' for a in sys.argv)
    info("Requesting administrator privileges (UAC)...")
    rc = ctypes.windll.shell32.ShellExecuteW(
        None, "runas", sys.executable, params, None, 1
    )
    # ShellExecuteW returns a value > 32 on success.
    if rc <= 32:
        warn("Elevation was declined or failed. Re-run from an elevated prompt.")
        sys.exit(1)
    sys.exit(0)


# ── Removal steps ────────────────────────────────────────────────────────────
def kill_tray_process() -> None:
    info(f"Stopping tray process {PROCESS_NAME} ...")
    r = run(["taskkill", "/F", "/IM", PROCESS_NAME, "/T"])
    if DRY_RUN:
        return
    if r.returncode == 0:
        ok(f"Killed {PROCESS_NAME}.")
    elif "not found" in (r.stdout + r.stderr).lower():
        info(f"{PROCESS_NAME} was not running.")
    else:
        warn(f"taskkill: {(r.stdout + r.stderr).strip()}")


def find_product_codes() -> list[str]:
    """Find MSI ProductCode GUID(s) by DisplayName in the Uninstall registry.

    Returns the subkey names (the ProductCode GUIDs) for use with msiexec /x.
    Searches both 64-bit and 32-bit (WOW6432Node) views.
    """
    import winreg

    codes: list[str] = []
    roots = [
        (winreg.HKEY_LOCAL_MACHINE,
         r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (winreg.HKEY_LOCAL_MACHINE,
         r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (winreg.HKEY_CURRENT_USER,
         r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    ]
    for hive, path in roots:
        try:
            base = winreg.OpenKey(hive, path)
        except FileNotFoundError:
            continue
        with base:
            for i in range(_subkey_count(base)):
                try:
                    sub_name = winreg.EnumKey(base, i)
                except OSError:
                    break
                try:
                    with winreg.OpenKey(base, sub_name) as sub:
                        name, _ = winreg.QueryValueEx(sub, "DisplayName")
                except (FileNotFoundError, OSError):
                    continue
                if name == PRODUCT_NAME and sub_name.startswith("{"):
                    if sub_name not in codes:
                        codes.append(sub_name)
    return codes


def _subkey_count(key) -> int:
    import winreg
    try:
        return winreg.QueryInfoKey(key)[0]
    except OSError:
        return 0


def uninstall_msi() -> None:
    info("Looking for the installed MSI product ...")
    if DRY_RUN:
        info("DRY-RUN would search the Uninstall registry and run msiexec /x.")
        return
    codes = find_product_codes()
    if not codes:
        info("No registered MSI product found (already removed or partial install).")
        return
    for code in codes:
        info(f"Uninstalling MSI product {code} ...")
        log = os.path.join(
            os.environ.get("TEMP", "."), "RunAsHelper-Uninstall.log"
        )
        r = run(
            ["msiexec", "/x", code, "/qn", "/norestart", "/l*v", log]
        )
        if r.returncode in (0, 1605, 3010):
            # 0 = ok, 1605 = not installed, 3010 = success/reboot required
            ok(f"MSI uninstall completed (exit {r.returncode}). Log: {log}")
        else:
            warn(
                f"msiexec returned {r.returncode}. See {log}. "
                "Continuing with manual cleanup."
            )


def delete_service() -> None:
    info(f'Removing Windows service "{SERVICE_NAME}" ...')
    # Stop first (ignore result), then delete.
    run(["sc", "stop", SERVICE_NAME])
    r = run(["sc", "delete", SERVICE_NAME])
    if DRY_RUN:
        return
    out = (r.stdout + r.stderr).strip()
    if r.returncode == 0:
        ok("Service removed.")
    elif "1060" in out or "does not exist" in out.lower():
        info("Service was not present.")
    elif "1072" in out:
        warn("Service marked for deletion; it will be gone after the next reboot.")
    else:
        warn(f"sc delete: {out}")


def delete_registry_keys() -> None:
    for root, path in REG_KEYS:
        info(rf"Removing registry key {root}\{path} ...")
        r = run(["reg", "delete", rf"{root}\{path}", "/f"])
        if DRY_RUN:
            continue
        out = (r.stdout + r.stderr).strip()
        if r.returncode == 0:
            ok(rf"Deleted {root}\{path}.")
        elif "unable to find" in out.lower() or "cannot find" in out.lower():
            info(rf"{root}\{path} not present.")
        else:
            warn(f"reg delete: {out}")


def remove_install_folder() -> None:
    program_files = os.environ.get("ProgramFiles", r"C:\Program Files")
    target = os.path.join(program_files, INSTALL_DIRNAME)
    info(f"Removing leftover folder {target} ...")
    if DRY_RUN:
        info(f"DRY-RUN would delete {target} if present.")
        return
    if not os.path.isdir(target):
        info("Install folder not present.")
        return
    try:
        shutil.rmtree(target)
        ok(f"Deleted {target}.")
    except Exception as exc:  # noqa: BLE001 - report and continue
        warn(f"Could not delete {target}: {exc}")


# ── Main ─────────────────────────────────────────────────────────────────────
def main() -> int:
    global DRY_RUN

    parser = argparse.ArgumentParser(
        description="Uninstall / clean up RunAS Helper."
    )
    parser.add_argument(
        "--dry-run", action="store_true",
        help="Show what would happen without changing anything.",
    )
    parser.add_argument(
        "--yes", "-y", action="store_true",
        help="Skip the confirmation prompt.",
    )
    args = parser.parse_args()
    DRY_RUN = args.dry_run

    if os.name != "nt":
        warn("This uninstaller targets Windows. Nothing to do on this OS.")
        return 1

    if not DRY_RUN and not is_admin():
        relaunch_as_admin()  # exits

    print("=" * 60)
    print(f"  RunAS Helper uninstaller{'  (DRY RUN)' if DRY_RUN else ''}")
    print("=" * 60)

    if not args.yes and not DRY_RUN:
        resp = input("This will remove RunAS Helper completely. Continue? [y/N] ")
        if resp.strip().lower() not in ("y", "yes"):
            info("Aborted by user.")
            return 0

    # Order matters: free file locks -> proper uninstall -> manual scrub.
    kill_tray_process()
    uninstall_msi()
    delete_service()
    delete_registry_keys()
    remove_install_folder()

    print("-" * 60)
    ok("Done." if not DRY_RUN else "Dry run complete — nothing was changed.")
    if not DRY_RUN:
        info("If a reboot was requested above, reboot to finish service removal.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\n[!] Interrupted.")
        sys.exit(130)
