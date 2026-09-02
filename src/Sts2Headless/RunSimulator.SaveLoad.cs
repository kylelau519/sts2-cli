// RunSimulator partial — Save/Load operations

using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2Headless;

public partial class RunSimulator
{
    public Dictionary<string, object?> LoadSave(string saveJson)
    {
        try
        {
            CleanUp();
            EnsureModelDbInitialized();

            Log("Loading save file...");

            if (!ValidateSaveSchemaVersion(saveJson, out var schemaError))
                return Error($"Save schema mismatch: {schemaError}");

            var readResult = SaveManager.FromJson<SerializableRun>(saveJson);
            if (!readResult.Success || readResult.SaveData == null)
                return Error($"Failed to parse save file: {readResult.Status} {readResult.ErrorMessage}");

            var save = readResult.SaveData;
            Log($"Save loaded: seed={save.SerializableRng?.Seed}, act={save.CurrentActIndex}, ascension={save.Ascension}");

            _runState = RunState.FromSerializable(save);
            if (_runState == null)
                return Error("Failed to create RunState from save");

            Log($"RunState created, players={_runState.Players?.Count}");

            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpSavedSingleplayer(_runState, save);
            LocalContext.NetId = netService.NetId;

            if (!_combatEventsRegistered)
            {
                CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
                CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();
                _combatEventsRegistered = true;
            }
            CardSelectCmd.PushSelector(_cardSelector);  // see RunSimulator.cs's StartRun() for why
            BundlePatches._bundleSimRef = this;

            var savedRoom = _runState.CurrentRoom;

            // Save visited coords before Launch (EnterAct will clear them)
            var savedVisitedCoords = _runState.VisitedMapCoords?.ToList() ?? new List<MapCoord>();
            var shouldResumeInitialNeow = IsInitialNeowSave(saveJson);
            Log($"Save has {savedVisitedCoords.Count} visited coords");

            // EnterAct -> SetActInternal unconditionally calls Odds.UnknownMapPoint.ResetToBase()
            // (RunManager.cs's SetActInternal) -- correct for an actual new-act transition, wrong
            // here: we call EnterAct below purely to reconstruct map-screen state for a mid-act
            // resume, and RunState.FromSerializable(save) (via RunOddsSet.FromSerializable) just
            // restored the real accumulated "?"-node pity-timer odds a few lines up. Without this,
            // every load_save resets that pity timer to base, which can silently change what an
            // Unknown ("?") map point resolves to on replay vs. what the original session actually
            // got (confirmed: Monster is the outcome most likely to flip, since it sits at the
            // smallest/first-checked cumulative-probability slot in UnknownMapPointOdds.Roll --
            // see Vakuu's whispering_earring_unknown_node_combat_checkpoint_bug memory for the
            // training-data bug this was traced from). Same snapshot-before/restore-after pattern
            // as savedVisitedCoords/ActFloor below, just for the odds instead of map position.
            var savedOdds = _runState.Odds?.UnknownMapPoint;
            var savedMonsterOdds = savedOdds?.MonsterOdds;
            var savedEliteOdds = savedOdds?.EliteOdds;
            var savedTreasureOdds = savedOdds?.TreasureOdds;
            var savedShopOdds = savedOdds?.ShopOdds;

            RunManager.Instance.Launch();
            Log("Run launched");

            if (savedRoom is MapRoom || savedRoom == null)
            {
                // Preserve Neow for saves created before the first blessing choice.
                // Once the run has visited at least one map node, re-entering Act 1
                // should not send the player back through the Ancient start node.
                if (_runState.CurrentActIndex == 0 && savedVisitedCoords.Count > 0)
                    _runState.ExtraFields.StartedWithNeow = false;
                RunManager.Instance.EnterAct(_runState.CurrentActIndex, doTransition: false).GetAwaiter().GetResult();
                _syncCtx.Pump();
                Log($"Entered Act {_runState.CurrentActIndex + 1}");

                if (shouldResumeInitialNeow && _runState.Map?.StartingMapPoint != null)
                {
                    Log("Restoring initial Neow event");
                    RunManager.Instance.EnterMapCoord(_runState.Map.StartingMapPoint.coord).GetAwaiter().GetResult();
                    _syncCtx.Pump();
                }

                // EnterAct clears visited coords and ActFloor — restore them from save
                if (savedVisitedCoords.Count > 0)
                {
                    if (_runState.VisitedMapCoords == null || _runState.VisitedMapCoords.Count == 0)
                    {
                        foreach (var coord in savedVisitedCoords)
                            _runState.AddVisitedMapCoord(coord);
                    }
                    _runState.ActFloor = savedVisitedCoords.Count;
                    var last = savedVisitedCoords[^1];
                    Log($"Restored map position: floor={_runState.ActFloor}, coord=({last.col},{last.row})");
                }

                // Restore the "?"-node pity-timer odds EnterAct just reset to base -- see the
                // comment above savedOdds for why.
                if (savedOdds != null && _runState.Odds?.UnknownMapPoint is { } restoredOdds)
                {
                    restoredOdds.MonsterOdds = savedMonsterOdds!.Value;
                    restoredOdds.EliteOdds = savedEliteOdds!.Value;
                    restoredOdds.TreasureOdds = savedTreasureOdds!.Value;
                    restoredOdds.ShopOdds = savedShopOdds!.Value;
                    Log($"Restored UnknownMapPoint odds: monster={restoredOdds.MonsterOdds}, "
                        + $"elite={restoredOdds.EliteOdds}, treasure={restoredOdds.TreasureOdds}, "
                        + $"shop={restoredOdds.ShopOdds}");
                }
            }
            else
            {
                Log($"Preserving saved room: {savedRoom.GetType().Name}");
            }

            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("LoadSave failed", ex);
        }
    }

    /// <summary>
    /// Expected run save <c>schema_version</c> (lazy: first load_save only, so StartRun never fails on reflection).
    /// Order: <c>STS2_SAVE_SCHEMA_VERSION</c> env → reflect sts2.dll → unknown, defer to SaveManager.
    /// </summary>
    private static int? GetExpectedSaveSchemaVersion()
    {
        if (_expectedSaveSchemaVersionReady)
            return _expectedSaveSchemaVersion;
        lock (_expectedSaveSchemaVersionLock)
        {
            if (_expectedSaveSchemaVersionReady)
                return _expectedSaveSchemaVersion;
            _expectedSaveSchemaVersion = ResolveExpectedSaveSchemaVersion();
            _expectedSaveSchemaVersionReady = true;
            return _expectedSaveSchemaVersion;
        }
    }

    private static int? ResolveExpectedSaveSchemaVersion()
    {
        var env = Environment.GetEnvironmentVariable("STS2_SAVE_SCHEMA_VERSION");
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out var envVer))
            return envVer;

        var reflected = TryReflectLatestSaveSchemaVersion();
        if (reflected.HasValue)
            return reflected.Value;

        Console.Error.WriteLine(
            "[Sts2Headless] Could not read save schema from sts2.dll; deferring schema compatibility " +
            "to SaveManager.FromJson. Set STS2_SAVE_SCHEMA_VERSION to enforce a specific version.");
        return null;
    }

    /// <summary>Find static parameterless GetLatestSchemaVersion (or close) on sts2; supports int/uint/long.</summary>
    private static int? TryReflectLatestSaveSchemaVersion()
    {
        var asm = typeof(SerializableRun).Assembly;
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }

        var candidates = new List<(int score, string typeName, int value)>();
        foreach (var t in types)
        {
            MethodInfo? m;
            try
            {
                foreach (var name in new[] { "GetLatestSchemaVersion", "GetLatestVersion" })
                {
                    m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
                    if (m == null) continue;
                    var tn = t.FullName ?? "";
                    // Avoid unrelated static GetLatestVersion() elsewhere in the assembly.
                    if (name == "GetLatestVersion" && !tn.Contains("Saves", StringComparison.Ordinal))
                        continue;

                    var conv = TryConvertSchemaNumber(m.Invoke(null, null));
                    if (!conv.HasValue) continue;

                    var score = name == "GetLatestSchemaVersion" ? 100 : 0;
                    if (tn.Contains("Saves", StringComparison.Ordinal)) score += 50;
                    if (tn.Contains("Schema", StringComparison.Ordinal) || tn.Contains("Migration", StringComparison.Ordinal))
                        score += 25;
                    candidates.Add((score, tn, conv.Value));
                }
            }
            catch
            {
                // type may not support full reflection on this runtime
            }
        }

        if (candidates.Count == 0)
            return null;

        var best = candidates.OrderByDescending(c => c.score).ThenBy(c => c.typeName).First();
        return best.value;
    }

    private static int? TryConvertSchemaNumber(object? value) => value switch
    {
        int i => i,
        uint u => u <= int.MaxValue ? (int)u : null,
        long l => l >= int.MinValue && l <= int.MaxValue ? (int)l : null,
        short s => s,
        ushort us => us,
        byte b => b,
        _ => null,
    };

    private static bool ValidateSaveSchemaVersion(string saveJson, out string error)
    {
        error = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("schema_version", out var versionElem))
            {
                error = "missing schema_version";
                return false;
            }

            if (versionElem.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !versionElem.TryGetInt32(out var schemaVersion))
            {
                error = "schema_version is not a valid integer";
                return false;
            }

            var expected = GetExpectedSaveSchemaVersion();
            if (expected.HasValue && schemaVersion != expected.Value)
            {
                error = $"expected v{expected.Value}, got v{schemaVersion}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"could not inspect save: {ex.Message}";
            return false;
        }
    }

    private static bool TrySetPropertyValue(object target, string propertyName, object? value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop?.CanWrite != true)
            return false;
        prop.SetValue(target, value);
        return true;
    }

    private static bool IsInitialNeowSave(string saveJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current_act_index", out var actIndexElem) || actIndexElem.GetInt32() != 0)
                return false;

            var hasVisitedCoords = root.TryGetProperty("visited_map_coords", out var visitedElem)
                                && visitedElem.ValueKind == System.Text.Json.JsonValueKind.Array
                                && visitedElem.GetArrayLength() > 0;
            if (hasVisitedCoords)
                return false;

            return root.TryGetProperty("extra_fields", out var extraFieldsElem)
                && extraFieldsElem.ValueKind == System.Text.Json.JsonValueKind.Object
                && extraFieldsElem.TryGetProperty("started_with_neow", out var startedElem)
                && startedElem.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRollbackSerializedSaveToPreRoom(SerializableRun serializableRun, out string error)
    {
        error = "";

        var saveType = serializableRun.GetType();
        var visitedProp = saveType.GetProperty("VisitedMapCoords");
        if (visitedProp == null)
        {
            error = "Save data is missing VisitedMapCoords";
            return false;
        }

        var visitedValue = visitedProp.GetValue(serializableRun);
        var visitedItems = new List<object?>();
        if (visitedValue is System.Collections.IEnumerable visitedEnumerable)
        {
            foreach (var item in visitedEnumerable)
                visitedItems.Add(item);
        }

        if (visitedItems.Count == 0)
        {
            error = "Cannot roll back save before the first room";
            return false;
        }

        visitedItems.RemoveAt(visitedItems.Count - 1);

        var visitedType = visitedProp.PropertyType;
        if (visitedType.IsArray)
        {
            var elementType = visitedType.GetElementType()!;
            var array = Array.CreateInstance(elementType, visitedItems.Count);
            for (int i = 0; i < visitedItems.Count; i++)
                array.SetValue(visitedItems[i], i);
            visitedProp.SetValue(serializableRun, array);
        }
        else if (visitedType.IsGenericType)
        {
            var elementType = visitedType.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            foreach (var item in visitedItems)
                list.Add(item);
            visitedProp.SetValue(serializableRun, list);
        }
        else
        {
            error = $"Unsupported VisitedMapCoords type: {visitedType.Name}";
            return false;
        }

        TrySetPropertyValue(serializableRun, "ActFloor", visitedItems.Count);
        TrySetPropertyValue(serializableRun, "CurrentMapCoord", visitedItems.Count > 0 ? visitedItems[^1] : null);
        TrySetPropertyValue(serializableRun, "PreFinishedRoom", null);
        TrySetPropertyValue(serializableRun, "CurrentRoom", null);
        return true;
    }

    public Dictionary<string, object?> SaveCheckpoint(string? outputPath)
    {
        try
        {
            if (_runState == null)
                return Error("No active run to save");

            if (string.IsNullOrEmpty(outputPath))
                return Error("No output path specified for quit save");

            var currentRoom = _runState.CurrentRoom;
            SerializableRun serializableRun;

            if (currentRoom is MapRoom || currentRoom == null)
            {
                Log($"Saving map checkpoint (room={currentRoom?.GetType().Name ?? "null"}, outputPath={outputPath})...");
                serializableRun = RunManager.Instance.ToSave(currentRoom);
            }
            else
            {
                Log($"Saving pre-room checkpoint from {currentRoom.GetType().Name} (outputPath={outputPath})...");
                serializableRun = RunManager.Instance.ToSave(new MapRoom());
                if (!TryRollbackSerializedSaveToPreRoom(serializableRun, out var rollbackError))
                    return Error($"Cannot save checkpoint: {rollbackError}");
            }

            var saveJson = SaveManager.ToJson(serializableRun);
            Log($"Serialized save: {saveJson.Length} chars");

            var dir = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(outputPath, saveJson);
            Log($"Save written to: {outputPath}");

            return new Dictionary<string, object?>
            {
                ["type"] = "save_result",
                ["success"] = true,
                ["path"] = outputPath,
                ["size"] = saveJson.Length,
                ["room_type"] = currentRoom?.GetType().Name,
            };
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("SaveCheckpoint failed", ex);
        }
    }
}
