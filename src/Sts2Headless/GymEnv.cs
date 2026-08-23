using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Loader;
using System.Reflection;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using System.Threading;

namespace Sts2Headless
{
    public class GymEnv
    {
        private static bool _resolverAdded = false;
        private RunSimulator _sim;
        private float[] _obsBuffer;
        private object _syncCtx;
        private MethodInfo _pumpMethod;
        private Tokenizer tokenizer;

        static GymEnv()
        {
            if (!_resolverAdded)
            {
                var asm = typeof(GymEnv).Assembly;
                var dir = Path.GetDirectoryName(asm.Location);
                var libDir = Path.GetFullPath(Path.Combine(dir ?? "", "..", "..", "..", "..", "..", "lib"));
                var godotDir = Path.GetFullPath(Path.Combine(dir ?? "", "..", "..", "..", "..", "GodotStubs", "bin", "Debug", "net9.0"));
                
                AssemblyLoadContext.Default.Resolving += (ctx, name) =>
                {
                    if (name.Name == "GodotSharp")
                    {
                        var path = Path.Combine(godotDir, "GodotSharp.dll");
                        if (File.Exists(path)) return ctx.LoadFromAssemblyPath(path);
                    }
                    else
                    {
                        var path = Path.Combine(libDir, name.Name + ".dll");
                        if (File.Exists(path)) return ctx.LoadFromAssemblyPath(path);
                    }
                    return null;
                };
                _resolverAdded = true;
            }
        }

        public GymEnv()
        {
            _sim = new RunSimulator();
            _obsBuffer = new float[2048]; 
            
            var field = typeof(RunSimulator).GetField("_syncCtx", BindingFlags.NonPublic | BindingFlags.Static);
            _syncCtx = field.GetValue(null);
            _pumpMethod = _syncCtx.GetType().GetMethod("Pump");
            tokenizer = new Tokenizer();
        }

        private void PumpAll()
        {
            _pumpMethod.Invoke(_syncCtx, null);
        }

        public float[] Reset(string character = "Ironclad", int ascension = 0, string? seed = null)
        {
            _sim.StartRun(character, ascension, seed);
            var stateProp = typeof(RunSimulator).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);
            var runState = stateProp.GetValue(_sim) as RunState;
            var summary = _sim.PlayerSummaryJson(runState.Players[0]);
            return EncodeObservation(summary);
        }

        public float[] EnterCombat(string encounterId = "SHRINKER_BEETLE_WEAK")
        {
            _sim.EnterRoom("combat", encounterId, null);
            var stateProp = typeof(RunSimulator).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);
            var runState = stateProp.GetValue(_sim) as RunState;
            var summary = _sim.PlayerSummaryJson(runState.Players[0]);
            return EncodeObservation(summary);
        }

        public Tuple<float[], float, bool> Step(int actionType, int arg1 = 0, int arg2 = 0)
        {
            float reward = 0f;
            bool done = false;

            var stateProp = typeof(RunSimulator).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);
            var runState = stateProp.GetValue(_sim) as RunState;

            if (runState == null)
            {
                return new Tuple<float[], float, bool>(_obsBuffer, 0f, true);
            }

            var player = runState.Players[0];

            // 0 = play_card, 1 = end_turn -- reuses RunSimulator.ExecuteAction (same dispatch
            // the JSON protocol's "action" command hits) rather than reimplementing the
            // PlayCardAction/DoEndTurn plumbing here.
            switch (actionType)
            {
                case 0:
                {
                    var args = new Dictionary<string, object?> { ["card_index"] = arg1 };
                    if (arg2 >= 0) args["target_index"] = arg2;
                    _sim.ExecuteAction("play_card", args);
                    break;
                }
                case 1:
                    _sim.ExecuteAction("end_turn", null);
                    break;
            }

            // Directly get the summary dictionary using the internal method you exposed!
            var summary = _sim.PlayerSummaryJson(player);
            
            // Check if game is over (player dead)
            if (player.Creature.IsDead)
            {
                done = true;
                reward = -1.0f; 
            }
            // Check if combat is won
            else if (CombatManager.Instance.DebugOnlyGetState()?.Enemies.All(e => e == null || e.IsDead || e.IsDead) == true)
            {
                done = true;
                reward = 1.0f;
            }

            return new Tuple<float[], float, bool>(EncodeObservation(summary), reward, done);
        }

        private float[] EncodeObservation(Dictionary<string, object?>? playerSummary)
        {
            Array.Clear(_obsBuffer, 0, _obsBuffer.Length);
            int offset = 0;
            
            return _obsBuffer;
        }
    }
}
