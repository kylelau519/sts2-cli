"""In-process CLR bootstrap shared by every buffer-mode Python entry point (gym_example.py's
benchmark harness, buffer_env.py's Sts2BufferSession). Loads sts2.dll's CoreCLR directly into this
Python process via pythonnet -- no subprocess, no JSON stdin/stdout -- so a caller can construct
C# objects (GymEnv, BufferSession, ...) and call their methods like any Python object.

Split out of gym_example.py (which used to inline this) so buffer_env.py doesn't have to import a
`if __name__ == "__main__"`-shaped benchmark script just to reuse its setup.
"""
from __future__ import annotations

import contextlib
import os
import sys


@contextlib.contextmanager
def silence_stderr():
    """Redirect OS-level fd 2 to devnull. The CLR's Console.Error dups fd 2 once on first access
    and keeps writing to that duplicate afterward, so this must be active before the CLR loads
    (see load_clr()) -- redirecting after the fact is a no-op. Sts2Headless's [SIM] logging is
    unconditional and floods the terminal on every step otherwise."""
    stderr_fd = sys.stderr.fileno()
    saved_fd = os.dup(stderr_fd)
    devnull_fd = os.open(os.devnull, os.O_WRONLY)
    try:
        os.dup2(devnull_fd, stderr_fd)
        yield
    finally:
        os.dup2(saved_fd, stderr_fd)
        os.close(devnull_fd)
        os.close(saved_fd)


_sts2headless = None  # cached: clr.AddReference + import only needs to happen once per process


def load_clr():
    """Loads the Sts2Headless assembly into this process's CoreCLR and returns the imported
    `Sts2Headless` CLR module (its public classes -- GymEnv, BufferSession -- are attributes of
    this module). Wrapped in silence_stderr() since the CLR's first Console.Error touch happens
    during this call."""
    global _sts2headless
    if _sts2headless is not None:
        return _sts2headless

    with silence_stderr():
        from clr_loader import get_coreclr
        from pythonnet import set_runtime

        # A specific runtimeconfig ensures pythonnet loads .NET 9's CoreCLR -- loading .NET 10
        # instead breaks HarmonyLib (see CLAUDE.md's Build section).
        base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        runtime_config = os.path.join(
            base_dir, "src", "Sts2Headless", "bin", "Debug", "net9.0",
            "Sts2Headless.runtimeconfig.json",
        )
        rt = get_coreclr(runtime_config=runtime_config)
        set_runtime(rt)

        import clr

        dll_path = os.path.join(base_dir, "src", "Sts2Headless", "bin", "Debug", "net9.0", "Sts2Headless.dll")
        sys.path.append(os.path.dirname(dll_path))

        clr.AddReference("Sts2Headless")
        import Sts2Headless

        _sts2headless = Sts2Headless

    return _sts2headless
