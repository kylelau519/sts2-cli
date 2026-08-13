"""Installation and setup utilities for sts2-cli."""

import os
import platform
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(ROOT, "src", "Sts2Headless", "Sts2Headless.csproj")
LIB_DIR = os.path.join(ROOT, "lib")


def _find_dotnet():
    """Find .NET SDK binary."""
    candidates = [
        os.path.expanduser("~/.dotnet-arm64/dotnet"),
        os.path.expanduser("~/.dotnet/dotnet"),
        "dotnet",
    ]
    for p in candidates:
        try:
            r = subprocess.run([p, "--version"], capture_output=True, text=True, timeout=5)
            if r.returncode == 0:
                return p
        except (FileNotFoundError, subprocess.TimeoutExpired):
            continue
    return None


DOTNET = _find_dotnet()


def _is_wsl():
    """Check if running inside WSL."""
    try:
        with open("/proc/version", "r") as f:
            return "microsoft" in f.read().lower()
    except OSError:
        return False


def _find_game_dir():
    """Auto-detect STS2 Steam install directory."""
    system = platform.system()
    candidates = []
    if system == "Darwin":
        base = os.path.expanduser("~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources")
        candidates = [
            os.path.join(base, "data_sts2_macos_arm64"),
            os.path.join(base, "data_sts2_macos_x86_64"),
        ]
    elif system == "Linux":
        if _is_wsl():
            # WSL: scan Windows drives for Steam install
            for drv in ["/mnt/c", "/mnt/d", "/mnt/e", "/mnt/f", "/mnt/g"]:
                for steam in [
                    f"{drv}/Program Files (x86)/Steam",
                    f"{drv}/Program Files/Steam",
                    f"{drv}/SteamLibrary",
                    f"{drv}/Games/Steam",
                    f"{drv}/Steam",
                ]:
                    d = f"{steam}/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64"
                    candidates.append(d)
        # Native Linux Steam
        for steam in ["~/.steam/steam", "~/.local/share/Steam"]:
            candidates.append(os.path.expanduser(f"{steam}/steamapps/common/Slay the Spire 2"))
    elif system == "Windows":
        candidates = [r"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"]

    for d in candidates:
        if os.path.isdir(d):
            return d
    return None


def _copy_dlls(game_dir):
    """Copy required DLLs from game directory to lib/."""
    os.makedirs(LIB_DIR, exist_ok=True)
    dlls = [
        "sts2.dll", "SmartFormat.dll", "SmartFormat.ZString.dll",
        "Sentry.dll", "Sentry.Godot.dll", "Steamworks.NET.dll", "MonoMod.Backports.dll",
        "MonoMod.ILHelpers.dll", "0Harmony.dll", "System.IO.Hashing.dll",
    ]
    for dll in dlls:
        src = os.path.join(game_dir, dll)
        dst = os.path.join(LIB_DIR, dll)
        if os.path.isfile(src):
            shutil.copy2(src, dst)
            print(f"  ✓ {dll}")
        else:
            # Search subdirectories
            for root_d, _, files in os.walk(game_dir):
                if dll in files:
                    shutil.copy2(os.path.join(root_d, dll), dst)
                    print(f"  ✓ {dll}")
                    break
            else:
                print(f"  ✗ {dll} not found")

    # Backup original sts2.dll
    sts2 = os.path.join(LIB_DIR, "sts2.dll")
    backup = os.path.join(LIB_DIR, "sts2.dll.original")
    if os.path.isfile(sts2) and not os.path.isfile(backup):
        shutil.copy2(sts2, backup)


def _patch_dll():
    """Apply IL patches to sts2.dll using setup.sh (requires Mono.Cecil via dotnet)."""
    setup_sh = os.path.join(ROOT, "setup.sh")
    if not os.path.isfile(setup_sh):
        print("  ⚠ setup.sh not found, skipping IL patch")
        return
    # Run just the patching part via setup.sh
    subprocess.run(["bash", setup_sh], cwd=ROOT)


def _build():
    """Build the C# project."""
    if not DOTNET:
        return False
    r = subprocess.run([DOTNET, "build", PROJECT], capture_output=True, text=True, timeout=60)
    return r.returncode == 0


def ensure_setup():
    """Check that everything is ready to run. Auto-setup if needed."""
    # Check .NET SDK
    if not DOTNET:
        print("❌ .NET SDK not found.")
        print("   Install .NET 9+ from https://dotnet.microsoft.com/download")
        sys.exit(1)

    # Check lib/sts2.dll exists
    sts2_dll = os.path.join(LIB_DIR, "sts2.dll")
    if not os.path.isfile(sts2_dll):
        print("📦 Game DLLs not found. Running first-time setup...")
        game_dir = _find_game_dir()
        if not game_dir:
            print("❌ Could not find Slay the Spire 2 installation.")
            print("   Install the game via Steam, then run again.")
            print("   Or run: ./setup.sh /path/to/game/data")
            sys.exit(1)
        print(f"  Found game at: {game_dir}")
        _copy_dlls(game_dir)
        if not os.path.isfile(sts2_dll):
            print("❌ Failed to copy sts2.dll")
            sys.exit(1)

    # Set STS2_GAME_DIR env var for runtime DLL resolution (point to lib/ where DLLs were copied)
    if "STS2_GAME_DIR" not in os.environ:
        os.environ["STS2_GAME_DIR"] = LIB_DIR

    # Check if built
    exe_dir = os.path.join(ROOT, "src", "Sts2Headless", "bin", "Debug", "net9.0")
    exe = os.path.join(exe_dir, "Sts2Headless.dll")
    if not os.path.isfile(exe) or os.path.getmtime(sts2_dll) > os.path.getmtime(exe):
        print("🏗️  Building...")
        if not _build():
            print("❌ Build failed. Try: ./setup.sh")
            sys.exit(1)
        print("  ✓ Build succeeded")
