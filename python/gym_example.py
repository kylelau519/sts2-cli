import sys
import os
import time
import contextlib

from clr_loader import get_coreclr
from pythonnet import set_runtime


@contextlib.contextmanager
def silence_stderr():
    """Redirect OS-level fd 2 to devnull.

    GymEnv runs the CLR in-process (no subprocess to pipe stderr from, unlike
    env.py's Sts2Session), and its Console.Error.WriteLine [SIM] logging is
    unconditional. Terminal writes on every end_turn dominate the benchmark,
    so redirect the raw fd rather than touching Console.Error from .NET.

    Must be active before the CLR is loaded: .NET's Console.Error dups fd 2
    once on first access and keeps writing to that duplicate afterward, so
    redirecting fd 2 after setup_env()/Reset() has already run is a no-op.
    """
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

def setup_env():
    # Use a specific runtimeconfig to ensure pythonnet loads the .NET 9 CoreCLR environment properly
    # This prevents loading .NET 10 which breaks HarmonyLib.
    base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    runtime_config = os.path.join(base_dir, "src", "Sts2Headless", "bin", "Debug", "net9.0", "Sts2Headless.runtimeconfig.json")
    rt = get_coreclr(runtime_config=runtime_config)
    set_runtime(rt)

    import clr

    dll_path = os.path.join(base_dir, "src", "Sts2Headless", "bin", "Debug", "net9.0", "Sts2Headless.dll")
    sys.path.append(os.path.dirname(dll_path))

    clr.AddReference("Sts2Headless")
    import Sts2Headless

    env = Sts2Headless.GymEnv()
    return env

if __name__ == "__main__":
    print("Setting up Max-Speed Gym Env...")
    # Must wrap setup_env() itself: .NET's Console.Error dups fd 2 on first
    # access (inside the CLR init / first Log() call), so the redirect has to
    # be in place before that happens, not just around the benchmark loop.
    with silence_stderr():
        env = setup_env()

        print("Resetting env...")
        env.Reset("Ironclad", 0, None)

        print("Forcing direct entry into combat (SHRINKER_BEETLE_WEAK)...")
        env.EnterCombat("SHRINKER_BEETLE_WEAK")

        # 0 = play_card, 1 = end_turn, 2 = select_map_node, 3 = select_card_reward, 4 = skip_card_reward, 5 = choose_option

        # Reset() (StartRun -> RunManager.EnterAct -> GenerateMap) measured at ~75% of wall time
        # via dotnet-trace when a fresh run is generated after every single combat: it's
        # MapPathPruning.FindMatchingSegments inside sts2.dll itself (a string-keyed
        # SortedDictionary path-dedup algorithm), not anything in our headless layer. Calling
        # Reset() only on death (not every combat) amortizes that cost across the wins in between --
        # but even amortized, a single Reset() (~79ms, measured) is ~155x a single step (~0.5ms), so
        # with a policy that dies every ~8 fights, Reset() was STILL ~47% of total wall time (5000-
        # step measurement: 31 resets @ ~79ms = ~2.5s out of ~5.3s total).
        #
        # HealAndEnterCombat (RunSimulator.HealAndEnterCombat) is the fix: it heals the player to
        # full HP on the *same* run (Creature.HealInternal -- same public heal API relics/potions
        # use, so death->alive hooks fire correctly) and re-enters the encounter, without ever
        # calling StartRun/EnterAct again -- so it never touches MapPathPruning. Measured at
        # ~1ms/call (~77x cheaper than Reset()), dropping restart cost to <1% of total time.
        # Use plain Reset() instead when a genuinely fresh run (new map/seed/relics) is wanted.
        #
        # This in turn needs combats that can actually be *won*: an always-end_turn policy deals no
        # damage, so every combat ended in death and there'd be nothing to heal between. CARDS_PER_TURN
        # crudely plays hand slot 0 at enemy 0 a few times before ending turn -- not a real policy,
        # just enough offense for most fights to end in victory.
        CARDS_PER_TURN = 3

        print("Benchmarking 1,000 steps (play_card + end_turn), heal-and-reenter on death...")
        start = time.time()
        steps = 0
        while steps < 1000:
            res = None
            for _ in range(CARDS_PER_TURN):
                res = env.Step(0, 0, 0)  # play_card: hand slot 0 targeting enemy 0
                steps += 1
                if res.Item3 or steps >= 1000:
                    break
            else:
                res = env.Step(1, 0, 0)  # end_turn
                steps += 1
            # res.Item1 = observation float array
            # res.Item2 = reward float
            # res.Item3 = done bool

            if res.Item3: # If combat ended
                if res.Item2 < 0:  # died -- heal back up on the same run rather than a full Reset()
                    env.HealAndEnterCombat("SHRINKER_BEETLE_WEAK")
                else:
                    env.EnterCombat("SHRINKER_BEETLE_WEAK")

    elapsed = time.time() - start
    print(f"Ran {steps} steps in {elapsed:.4f}s ({steps/elapsed:.2f} steps/s)")
