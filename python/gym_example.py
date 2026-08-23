import sys
import os
import time

from clr_loader import get_coreclr
from pythonnet import set_runtime

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
    env = setup_env()
    
    print("Resetting env...")
    env.Reset("Ironclad", 0, None)
    
    print("Forcing direct entry into combat (SHRINKER_BEETLE_WEAK)...")
    env.EnterCombat("SHRINKER_BEETLE_WEAK")

    # 0 = play_card, 1 = end_turn, 2 = select_map_node, 3 = select_card_reward, 4 = skip_card_reward, 5 = choose_option
    
    print("Benchmarking 10,000 real combat end_turn steps...")
    start = time.time()
    steps = 0
    while steps < 10000:
        res = env.Step(1, 0, 0) # 1 is end_turn
        # res.Item1 = observation float array
        # res.Item2 = reward float
        # res.Item3 = done bool
        
        steps += 1
        
        if res.Item3: # If combat ended
            env.Reset("Ironclad", 0, None)
            env.EnterCombat("SHRINKER_BEETLE_WEAK")

    elapsed = time.time() - start
    print(f"Ran {steps} steps in {elapsed:.4f}s ({steps/elapsed:.2f} steps/s)")
