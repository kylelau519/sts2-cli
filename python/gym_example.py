import time

from clr_bridge import load_clr, silence_stderr


def setup_env():
    return load_clr().GymEnv()

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
        # 1,000 steps measures mostly JIT warmup, not steady state (~1.3k steps/s vs. the ~3.4k
        # steps/s a 30,000-step run settles into once .NET's tiered JIT re-compiles the hot path)
        # -- large enough to report the throughput a real training loop would actually see.
        TOTAL_STEPS = 30000

        print(f"Benchmarking {TOTAL_STEPS} steps (play_card + end_turn), heal-and-reenter on death...")
        start = time.time()
        steps = 0
        while steps < TOTAL_STEPS:
            res = None
            for _ in range(CARDS_PER_TURN):
                res = env.Step(0, 0, 0)  # play_card: hand slot 0 targeting enemy 0
                steps += 1
                if res.Item3 or steps >= TOTAL_STEPS:
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
