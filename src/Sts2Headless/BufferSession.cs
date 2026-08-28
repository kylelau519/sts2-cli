using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Sts2Headless
{
    /// <summary>
    /// General-purpose in-process session over RunSimulator's buffer protocol -- the Python-facing
    /// counterpart to env.py's Sts2Session, but with no subprocess and no JSON on the hot path: every
    /// call writes into one reused float[] (see RunSimulator.States.Buffer.cs's BufferLayout) instead
    /// of building/parsing a JSON dict. Unlike GymEnv (a narrow play_card/end_turn benchmark harness),
    /// this exposes the same generic action(name, args) surface the JSON protocol does, by reusing
    /// RunSimulator.ExecuteActionToBuffer's dispatch -- nothing here reimplements game logic.
    ///
    /// One BufferSession wraps one RunSimulator for its whole lifetime. StartRun is safe to call
    /// more than once on the same instance (RunSimulator.StartRun fully reinitializes via CleanUp()
    /// first -- the JSON protocol already relies on this for its own start_run-on-a-live-subprocess
    /// path), so a caller may either construct a fresh BufferSession per trial/branch (cheap: no
    /// subprocess spawn, just a C# object) or keep one around and re-Start it, whichever the calling
    /// pattern needs.
    /// </summary>
    public class BufferSession
    {
        private static bool _resolverAdded = false;

        private readonly RunSimulator _sim;
        private readonly float[] _buffer;

        static BufferSession()
        {
            if (!_resolverAdded)
            {
                var asm = typeof(BufferSession).Assembly;
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

        public BufferSession()
        {
            _sim = new RunSimulator();
            _buffer = new float[RunSimulator.BufferLayout.TotalSize];
        }

        public static int BufferSize => RunSimulator.BufferLayout.TotalSize;

        public static string ExportTokenMap() => RunSimulator.ExportTokenMap();

        public float[] StartRun(string character, string? seed, int ascension)
        {
            _sim.StartRun(character, ascension, seed);
            return _sim.DetectDecisionPointToBuffer(_buffer);
        }

        /// <summary>Generic action dispatch -- argsJson is a JSON object string (or null/empty for
        /// no-arg actions like "end_turn"), parsed the same way Program.cs's "action" command
        /// parses its JsonElement args, then handed to the exact same ExecuteAction/
        /// ExecuteActionToBuffer dispatch the JSON protocol uses. No action-specific logic here.
        /// The perofrmance overhead is claimed to be trivial.</summary>
        public float[] Action(string action, string? argsJson)
        {
            Dictionary<string, object?>? args = null;
            if (!string.IsNullOrEmpty(argsJson))
            {
                args = new Dictionary<string, object?>();
                using var doc = JsonDocument.Parse(argsJson);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    args[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Number => prop.Value.GetInt32(),
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.ToString(),
                    };
                }
            }
            return _sim.ExecuteActionToBuffer(action, args, _buffer);
        }

        public void RandomizeDrawPile(int seed) => _sim.RandomizeDrawPile(seed);

        /// <summary>Set by the most recent Action() call iff it returned DecisionType.Error --
        /// see RunSimulator.LastActionError. None (not stale) after a call that succeeded.</summary>
        public string? LastError => _sim.LastActionError;
    }
}
