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
        private static readonly FieldInfo _runStateField =
            typeof(RunSimulator).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private RunSimulator _sim;
        private float[] _obsBuffer;
        private object _syncCtx;
        private MethodInfo _pumpMethod;
        private Tokenizer tokenizer;

        private RunState? CurrentRunState => _runStateField.GetValue(_sim) as RunState;

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
            _obsBuffer = new float[RunSimulator.BufferLayout.TotalSize];

            var field = typeof(RunSimulator).GetField("_syncCtx", BindingFlags.NonPublic | BindingFlags.Static);
            _syncCtx = field.GetValue(null);
            _pumpMethod = _syncCtx.GetType().GetMethod("Pump");
            tokenizer = new Tokenizer();
        }

        private void PumpAll()
        {
            _pumpMethod.Invoke(_syncCtx, null);
        }

        // Reset/EnterCombat/Step all write into the same _obsBuffer via RunSimulator's buffer
        // writer (DetectDecisionPointToBuffer / ExecuteActionToBuffer) instead of building and
        // discarding a JSON dict every call -- ~1.8x faster per serialization, and ExecuteAction
        // avoids paying for a JSON dict a second time on top of that (see ExecuteActionToBuffer).

        public float[] Reset(string character = "Ironclad", int ascension = 0, string? seed = null)
        {
            _sim.StartRun(character, ascension, seed);
            return _sim.DetectDecisionPointToBuffer(_obsBuffer);
        }

        public float[] EnterCombat(string encounterId = "SHRINKER_BEETLE_WEAK")
        {
            _sim.EnterRoom("combat", encounterId, null);
            return _sim.DetectDecisionPointToBuffer(_obsBuffer);
        }

        public float[] ResetToCombat(string character = "Ironclad", int ascension = 0, string? seed = null, string encounterId = "SHRINKER_BEETLE_WEAK")
        {
            _sim.StartRunIntoCombat(character, ascension, seed, encounterId);
            return _sim.DetectDecisionPointToBuffer(_obsBuffer);
        }

        public float[] HealAndEnterCombat(string encounterId = "SHRINKER_BEETLE_WEAK")
        {
            _sim.HealAndEnterCombat(encounterId);
            return _sim.DetectDecisionPointToBuffer(_obsBuffer);
        }

        public Tuple<float[], float, bool> Step(int actionType, int arg1 = 0, int arg2 = 0)
        {
            var runState = CurrentRunState;
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
                    _sim.ExecuteActionToBuffer("play_card", args, _obsBuffer);
                    break;
                }
                case 1:
                    _sim.ExecuteActionToBuffer("end_turn", null, _obsBuffer);
                    break;
                default:
                    _sim.DetectDecisionPointToBuffer(_obsBuffer);
                    break;
            }

            float reward = 0f;
            bool done = false;

            // Check if game is over (player dead)
            if (player.Creature.IsDead)
            {
                done = true;
                reward = -1.0f;
            }
            // Check if combat is won
            else if (CombatManager.Instance.DebugOnlyGetState()?.Enemies.All(e => e == null || e.IsDead) == true)
            {
                done = true;
                reward = 1.0f;
            }

            return new Tuple<float[], float, bool>(_obsBuffer, reward, done);
        }
    }
}
