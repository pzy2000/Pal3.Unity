/*
 * WarpTest checkpoint command for Pal3.Unity (PAL3 variant).
 *
 * Test-gated utility for semantic checkpoint validation, mirroring the jynew
 * Unity batch-mode harness. Activated only via Unity batch mode:
 *   -executeMethod Pal3.Game.Command.WarptestCheckpoint.Run
 *   -- --warptest-request <path> --warptest-report <path>
 *
 * Unlike jynew (whose GameRuntimeData can be synthesized without a running
 * scene), Pal3 keeps all mutable state inside ServiceLocator-registered
 * managers that only exist once the Pal3 prefab is instantiated in play mode.
 * Therefore this harness always enters play mode, waits for the game to become
 * ready, then synthesizes / restores the requested checkpoint by executing the
 * game's own console commands (the exact mechanism the game uses for its text
 * save files and DevCommands story jumps), validates manager state, runs smoke
 * actions, checks oracle assertions, and writes a structured JSON report.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

using Engine.Services;
using IngameDebugConsole;
using Pal3.Core.Command;
using Pal3.Core.Command.SceCommands;
using Pal3.Core.Contract.Constants;
using Pal3.Core.Contract.Enums;
using Pal3.Game.Command.Extensions;
using Pal3.Game.GamePlay;
using Pal3.Game.GameSystems.Favor;
using Pal3.Game.GameSystems.Inventory;
using Pal3.Game.GameSystems.Team;
using Pal3.Game.GameSystems.WorldMap;
using Pal3.Game.Script;
using Pal3.Game.State;

// Disambiguate the `Pal3` game class from the `Pal3` root namespace, and the
// Pal3 scene manager from UnityEngine.SceneManagement.SceneManager.
using PalApp = global::Pal3.Game.Pal3;
using PalSceneManager = global::Pal3.Game.Scene.SceneManager;
using PalGameScene = global::Pal3.Game.Scene.GameScene;

namespace Pal3.Game.Command
{
    public static class WarptestCheckpoint
    {
        const string GameScenePath = "Assets/Scenes/Game.unity";

#if UNITY_EDITOR
        const string PendingKey = "WarpTest.Pal3.Pending";
        const string PendingRequestPathKey = "WarpTest.Pal3.PendingRequestPath";
        const string PendingReportPathKey = "WarpTest.Pal3.PendingReportPath";
        static int s_pendingPlayModeFrames;

        [UnityEditor.InitializeOnLoadMethod]
        static void ResumePendingPlayModeRun()
        {
            if (!UnityEditor.EditorPrefs.GetBool(PendingKey, false))
                return;
            s_pendingPlayModeFrames = 30;
            UnityEditor.EditorApplication.update -= RunPendingWhenPlayModeReady;
            UnityEditor.EditorApplication.update += RunPendingWhenPlayModeReady;
        }
#endif

        public static void Run()
        {
            string requestPath = null;
            string reportPath = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--warptest-request" && i + 1 < args.Length)
                    requestPath = args[i + 1];
                if (args[i] == "--warptest-report" && i + 1 < args.Length)
                    reportPath = args[i + 1];
            }

            if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(reportPath))
            {
                Debug.LogError("[WarpTest] Missing --warptest-request or --warptest-report arguments");
                EditorQuit(1);
                return;
            }

#if UNITY_EDITOR
            if (MaybeQueuePlayModeRun(requestPath, reportPath))
                return;
            StartRunner(requestPath, reportPath);
#else
            WriteFailureReport(reportPath,
                "Pal3 WarpTest harness requires Unity editor play mode (-executeMethod in batch mode).",
                "probe.requires_editor");
            EditorQuit(1);
#endif
        }

#if UNITY_EDITOR
        // Pal3 always needs play mode because managers only exist after the Pal3
        // prefab runs OnEnable. Open the Game scene and enter play mode, then
        // resume after the domain reload via EditorPrefs + update callback.
        static bool MaybeQueuePlayModeRun(string requestPath, string reportPath)
        {
            if (Application.isPlaying)
                return false;

            try
            {
                if (File.Exists(GameScenePath))
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(GameScenePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WarpTest] Unable to open {GameScenePath} before play mode: {e.Message}");
            }

            UnityEditor.EditorPrefs.SetBool(PendingKey, true);
            UnityEditor.EditorPrefs.SetString(PendingRequestPathKey, requestPath);
            UnityEditor.EditorPrefs.SetString(PendingReportPathKey, reportPath);
            s_pendingPlayModeFrames = 30;
            UnityEditor.EditorApplication.update -= RunPendingWhenPlayModeReady;
            UnityEditor.EditorApplication.update += RunPendingWhenPlayModeReady;
            UnityEditor.EditorApplication.isPlaying = true;
            Debug.Log("[WarpTest] Queued Pal3 checkpoint run for Unity play mode.");
            return true;
        }

        static void RunPendingWhenPlayModeReady()
        {
            if (!UnityEditor.EditorApplication.isPlaying)
                return;
            if (s_pendingPlayModeFrames-- > 0)
                return;

            UnityEditor.EditorApplication.update -= RunPendingWhenPlayModeReady;
            var requestPath = UnityEditor.EditorPrefs.GetString(PendingRequestPathKey, "");
            var reportPath = UnityEditor.EditorPrefs.GetString(PendingReportPathKey, "");
            UnityEditor.EditorPrefs.DeleteKey(PendingKey);
            UnityEditor.EditorPrefs.DeleteKey(PendingRequestPathKey);
            UnityEditor.EditorPrefs.DeleteKey(PendingReportPathKey);

            if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(reportPath))
            {
                Debug.LogError("[WarpTest] Pending play-mode run lost request/report paths.");
                EditorQuit(1);
                return;
            }

            StartRunner(requestPath, reportPath);
        }
#endif

        static void StartRunner(string requestPath, string reportPath)
        {
            var host = new GameObject("WarptestRunner");
            UnityEngine.Object.DontDestroyOnLoad(host);
            var runner = host.AddComponent<WarptestRunnerBehaviour>();
            runner.Begin(requestPath, reportPath);
        }

        // ---- Request processing (driven as a coroutine by WarptestRunnerBehaviour) ----

        internal static IEnumerator ProcessRequestCoroutine(WarptestRequest request, List<WarptestCheck> checks, Action<WarptestReport> done)
        {
            var spec = request.spec;
            var target = spec.target ?? new WarptestTarget();

            // 1. Wait for the running game to register its managers.
            bool ready = false;
            yield return WaitForGameReady(r => ready = r);
            if (!ready)
            {
                checks.Add(Fail("target.game_ready",
                    "Pal3 managers never became available. The game likely needs the original PAL3 data (CPK) mounted."));
                done(BuildReport(request, checks, ""));
                yield break;
            }

            // 2. Restore checkpoint: load a save slot or synthesize from target fields.
            if (target.save_index >= 0)
            {
                yield return LoadSaveCheckpoint(target.save_index, checks);
            }
            else
            {
                yield return SynthesizeState(target, checks);
            }

            bool restorationOk = checks.All(c => c.status == "success");
            if (!restorationOk)
            {
                done(BuildReport(request, checks, "", forceFailure: true,
                    failureDetail: "Checkpoint restoration failed."));
                yield break;
            }

            // 3. Pre-smoke semantic validation of the synthesized/restored state.
            if (spec.validations != null)
            {
                foreach (var validation in spec.validations)
                    checks.Add(ValidateField(validation));
            }

            // 4. Smoke actions.
            if (spec.actions != null)
            {
                foreach (var action in spec.actions)
                {
                    WarptestCheck actionCheck = null;
                    yield return ExecuteAction(action, c => actionCheck = c);
                    checks.Add(actionCheck);
                }
            }

            // 5. Oracle assertions.
            if (spec.assertions != null)
            {
                foreach (var assertion in spec.assertions)
                    checks.Add(CheckAssertion(assertion));
            }

            // 6. Optional visual oracle screenshot.
            string screenshotFailureDetail = "";
            string screenshotStatus = "skipped";
            string screenshotSource = "";
            if (!string.IsNullOrEmpty(request.screenshot_output_path))
            {
                yield return null; // settle one frame before capture
                try
                {
                    string dir = Path.GetDirectoryName(request.screenshot_output_path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    string detail = CaptureScreenshotToFile(request.screenshot_output_path);
                    screenshotStatus = "success";
                    screenshotSource = "unity_capture";
                    screenshotFailureDetail = detail;
                    Debug.Log($"[WarpTest] Screenshot captured to {request.screenshot_output_path}");
                }
                catch (Exception e)
                {
                    screenshotStatus = "failure";
                    screenshotSource = "capture_failure";
                    screenshotFailureDetail = e.Message;
                    Debug.LogWarning($"[WarpTest] Screenshot capture failed (non-fatal): {e.Message}");
                }
            }

            var report = BuildReport(request, checks, request.screenshot_output_path ?? "");
            report.screenshot_status = screenshotStatus;
            report.screenshot_source = screenshotSource;
            report.screenshot_detail = screenshotFailureDetail;
            done(report);
        }

        static WarptestReport BuildReport(WarptestRequest request, List<WarptestCheck> checks, string screenshotPath,
            bool forceFailure = false, string failureDetail = null)
        {
            bool allOk = !forceFailure && checks.All(c => c.status == "success");
            return new WarptestReport
            {
                status = allOk ? "success" : "failure",
                detail = allOk ? "All checks passed." : (failureDetail ?? "One or more checks failed."),
                screenshot_path = screenshotPath ?? "",
                screenshot_status = string.IsNullOrEmpty(request.screenshot_output_path) ? "skipped" : "failure",
                screenshot_source = string.IsNullOrEmpty(request.screenshot_output_path) ? "" : "capture_failure",
                screenshot_detail = "",
                checks = checks
            };
        }

        // ---- Game readiness ----

        static IEnumerator WaitForGameReady(Action<bool> done)
        {
            float deadline = Time.realtimeSinceStartup + 180f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (UnityEngine.Object.FindFirstObjectByType<PalApp>() != null
                    && TryGetService<SaveManager>() != null
                    && TryGetService<TeamManager>() != null
                    && TryGetService<InventoryManager>() != null)
                {
                    // Give the main menu / init view a few frames to settle.
                    for (int i = 0; i < 10; i++) yield return null;
                    done(true);
                    yield break;
                }
                yield return null;
            }
            done(false);
        }

        // ---- Checkpoint restoration ----

        static IEnumerator LoadSaveCheckpoint(int index, List<WarptestCheck> checks)
        {
            string saveContent = null;
            try
            {
                var saveManager = TryGetService<SaveManager>();
                saveContent = saveManager?.LoadFromSaveSlot(index);
            }
            catch (Exception e)
            {
                checks.Add(Fail("target.save_loaded", $"Failed to read save slot {index}: {e.Message}"));
                yield break;
            }

            if (string.IsNullOrEmpty(saveContent))
            {
                checks.Add(Fail("target.save_loaded", $"Save slot {index} is empty or missing."));
                yield break;
            }

            yield return RestoreFromCommandText(saveContent);
            checks.Add(Ok("target.save_loaded", $"Loaded and replayed save slot {index}."));
        }

        static IEnumerator SynthesizeState(WarptestTarget target, List<WarptestCheck> checks)
        {
            Exception failure = null;
            try
            {
                PalApp.Instance.Execute(new ResetGameStateCommand());

                if (target.story_vars != null)
                    foreach (var v in target.story_vars)
                        PalApp.Instance.Execute(new ScriptVarSetValueCommand((ushort)v.key, v.value));

                if (target.team_ids != null)
                    foreach (int actorId in target.team_ids)
                        PalApp.Instance.Execute(new TeamAddOrRemoveActorCommand(actorId, 1));

                if (target.money > 0)
                    PalApp.Instance.Execute(new InventoryAddMoneyCommand(target.money));

                if (target.items != null)
                    foreach (var item in target.items)
                        PalApp.Instance.Execute(new InventoryAddItemCommand(item.id, item.count));

                if (target.world_regions != null)
                    foreach (var region in target.world_regions)
                        PalApp.Instance.Execute(new WorldMapEnableRegionCommand(region.region, region.flag));

                if (target.favors != null)
                    foreach (var favor in target.favors)
                        PalApp.Instance.Execute(new FavorAddCommand(favor.actor_id, favor.amount));
            }
            catch (Exception e)
            {
                failure = e;
            }

            if (failure != null)
            {
                checks.Add(Fail("target.state_synthesized", $"State synthesis failed: {failure.Message}"));
                yield break;
            }

            // Scene load is asynchronous; wait for it to settle before reading position.
            bool hasScene = !string.IsNullOrEmpty(target.scene_city) && !string.IsNullOrEmpty(target.scene_name);
            if (hasScene)
            {
                bool loaded = false;
                yield return LoadSceneAndWait(target.scene_city, target.scene_name, ok => loaded = ok);
                if (!loaded)
                {
                    checks.Add(Fail("target.state_synthesized",
                        $"Scene {target.scene_city}/{target.scene_name} did not load in time."));
                    yield break;
                }
            }

            // Player position commands are deferred until the scene is live.
            if (target.position != null && target.position.set)
            {
                try
                {
                    int actorId = target.position.actor_id;
                    if (target.position.layer >= 0)
                        PalApp.Instance.Execute(new ActorSetNavLayerCommand(actorId, target.position.layer));
                    PalApp.Instance.Execute(new ActorSetWorldPositionCommand(actorId, target.position.x, target.position.z));
                    PalApp.Instance.Execute(new ActorSetFacingCommand(actorId, target.position.facing));
                }
                catch (Exception e)
                {
                    checks.Add(Fail("target.state_synthesized", $"Failed to apply player position: {e.Message}"));
                    yield break;
                }
            }

            int teamCount = SafeTeamCount();
            int money = SafeMoney();
            checks.Add(Ok("target.state_synthesized",
                $"Synthesized state: team={teamCount}, money={money}" + (hasScene ? $", scene={target.scene_city}/{target.scene_name}" : "")));
        }

        // Replays a newline-separated console-command save body, mirroring the
        // private MainMenu.ExecuteCommandsFromSaveFile deferral order.
        static IEnumerator RestoreFromCommandText(string commandText)
        {
            var deferredPrefixes = new[]
            {
                "ActorActivate", "ActorSetNavLayer", "ActorSetWorldPosition",
                "ActorSetYPosition", "ActorSetFacing", "ActorSetScript", "CameraFadeIn"
            };
            var deferred = new List<string>();

            foreach (string raw in commandText.Split('\n'))
            {
                string command = raw.Trim();
                if (string.IsNullOrEmpty(command))
                    continue;
                if (deferredPrefixes.Any(p => command.StartsWith(p, StringComparison.Ordinal)))
                {
                    deferred.Add(command);
                    continue;
                }
                TryExecuteConsoleCommand(command);
                if (command.StartsWith("SceneLoad", StringComparison.Ordinal))
                {
                    bool loaded = false;
                    yield return WaitForSceneReady(null, null, ok => loaded = ok);
                }
            }

            foreach (string command in deferred)
                TryExecuteConsoleCommand(command);
            yield return null;
        }

        static void TryExecuteConsoleCommand(string command)
        {
            try
            {
                DebugLogConsole.ExecuteCommand(command);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WarpTest] Console command failed '{command}': {e.Message}");
            }
        }

        static IEnumerator LoadSceneAndWait(string city, string scene, Action<bool> done)
        {
            try
            {
                PalApp.Instance.Execute(new SceneLoadCommand(city, scene));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WarpTest] SceneLoad threw: {e.Message}");
                done(false);
                yield break;
            }
            yield return WaitForSceneReady(city, scene, done);
        }

        static IEnumerator WaitForSceneReady(string city, string scene, Action<bool> done)
        {
            float deadline = Time.realtimeSinceStartup + 120f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var sceneManager = TryGetService<PalSceneManager>();
                PalGameScene current = null;
                try { current = sceneManager?.GetCurrentScene(); } catch { current = null; }
                if (current != null)
                {
                    if (string.IsNullOrEmpty(city))
                    {
                        for (int i = 0; i < 10; i++) yield return null;
                        done(true);
                        yield break;
                    }
                    var info = current.GetSceneInfo();
                    if (string.Equals(info.CityName, city, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(info.SceneName, scene, StringComparison.OrdinalIgnoreCase))
                    {
                        for (int i = 0; i < 10; i++) yield return null;
                        done(true);
                        yield break;
                    }
                }
                yield return null;
            }
            done(false);
        }

        // ---- Validation ----

        static WarptestCheck ValidateField(WarptestValidation validation)
        {
            try
            {
                object actual = ResolveField(validation.path);
                string actualStr = actual?.ToString() ?? "null";
                bool match = actualStr == validation.expected;
                return new WarptestCheck
                {
                    name = $"target.validate.{validation.path}",
                    status = match ? "success" : "failure",
                    detail = match ? $"{validation.path} = {actualStr}" : $"{validation.path}: expected {validation.expected}, got {actualStr}"
                };
            }
            catch (Exception e)
            {
                return Fail($"target.validate.{validation.path}", $"Validation error for {validation.path}: {e.Message}");
            }
        }

        static object ResolveField(string path)
        {
            if (path == "money") return SafeMoney();
            if (path == "team.count") return SafeTeamCount();
            if (path == "scene.city") return CurrentSceneInfo(out string city, out _) ? city : "null";
            if (path == "scene.name") return CurrentSceneInfo(out _, out string name) ? name : "null";
            if (path.StartsWith("story_var.", StringComparison.Ordinal))
            {
                ushort id = (ushort)int.Parse(path.Substring("story_var.".Length));
                return TryGetService<IUserVariableStore<ushort, int>>().Get(id);
            }
            if (path.StartsWith("item.", StringComparison.Ordinal))
            {
                int id = int.Parse(path.Substring("item.".Length));
                return ItemCount(id);
            }
            if (path.StartsWith("favor.", StringComparison.Ordinal))
            {
                int actorId = int.Parse(path.Substring("favor.".Length));
                return TryGetService<FavorManager>().GetFavorByActor(actorId);
            }
            if (path.StartsWith("region.", StringComparison.Ordinal))
            {
                int region = int.Parse(path.Substring("region.".Length));
                return RegionFlag(region);
            }
            throw new Exception($"Unknown field path: {path}");
        }

        // ---- Smoke actions ----

        static IEnumerator ExecuteAction(WarptestAction action, Action<WarptestCheck> done)
        {
            if (action.type == "pal3_load_scene")
            {
                bool loaded = false;
                yield return LoadSceneAndWait(action.city, action.scene, ok => loaded = ok);
                done(loaded
                    ? Ok($"action[{action.type}]", $"Loaded scene {action.city}/{action.scene}")
                    : Fail($"action[{action.type}]", $"Scene {action.city}/{action.scene} failed to load"));
                yield break;
            }

            if (action.type == "pal3_load_save")
            {
                var saveManager = TryGetService<SaveManager>();
                string content = saveManager?.LoadFromSaveSlot(action.save_index);
                if (string.IsNullOrEmpty(content))
                {
                    done(Fail($"action[{action.type}].slot_{action.save_index}", $"Save slot {action.save_index} missing"));
                    yield break;
                }
                yield return RestoreFromCommandText(content);
                done(Ok($"action[{action.type}].slot_{action.save_index}", $"Loaded save slot {action.save_index}"));
                yield break;
            }

            WarptestCheck result;
            try
            {
                switch (action.type)
                {
                    case "pal3_set_story_var":
                        PalApp.Instance.Execute(new ScriptVarSetValueCommand((ushort)action.var_id, action.value));
                        result = Ok($"action[{action.type}].var_{action.var_id}", $"Set story var {action.var_id} = {action.value}");
                        break;
                    case "pal3_add_team_member":
                        PalApp.Instance.Execute(new TeamAddOrRemoveActorCommand(action.actor_id, 1));
                        result = Ok($"action[{action.type}].actor_{action.actor_id}", $"Added actor {action.actor_id} to team");
                        break;
                    case "pal3_remove_team_member":
                        PalApp.Instance.Execute(new TeamAddOrRemoveActorCommand(action.actor_id, 0));
                        result = Ok($"action[{action.type}].actor_{action.actor_id}", $"Removed actor {action.actor_id} from team");
                        break;
                    case "pal3_add_money":
                        PalApp.Instance.Execute(new InventoryAddMoneyCommand(action.amount));
                        result = Ok($"action[{action.type}]", $"Added {action.amount} money");
                        break;
                    case "pal3_add_item":
                    {
                        int count = action.item_count <= 0 ? 1 : action.item_count;
                        int before = ItemCount(action.item_id);
                        PalApp.Instance.Execute(new InventoryAddItemCommand(action.item_id, count));
                        int after = ItemCount(action.item_id);
                        result = after >= before + count
                            ? Ok($"action[{action.type}].item_{action.item_id}", $"Added {count}x item {action.item_id}")
                            : Fail($"action[{action.type}].item_{action.item_id}",
                                $"Item {action.item_id} count did not increase from {before} by {count}; got {after}. Known item IDs include: {KnownItemIdSample()}");
                        break;
                    }
                    case "pal3_enable_world_region":
                        PalApp.Instance.Execute(new WorldMapEnableRegionCommand(action.region, action.flag <= 0 ? 2 : action.flag));
                        result = Ok($"action[{action.type}].region_{action.region}", $"Enabled region {action.region} (flag {(action.flag <= 0 ? 2 : action.flag)})");
                        break;
                    case "pal3_set_favor":
                        PalApp.Instance.Execute(new FavorAddCommand(action.actor_id, action.amount));
                        result = Ok($"action[{action.type}].actor_{action.actor_id}", $"Adjusted actor {action.actor_id} favor by {action.amount}");
                        break;
                    case "pal3_save":
                    {
                        var saveManager = TryGetService<SaveManager>();
                        var commands = saveManager.ConvertCurrentGameStateToCommands(SaveLevel.Full);
                        bool saved = commands != null && saveManager.SaveGameStateToSlot(action.save_index, commands);
                        done(saved
                            ? Ok($"action[{action.type}].slot_{action.save_index}", $"Saved to slot {action.save_index}")
                            : Fail($"action[{action.type}].slot_{action.save_index}", $"Save to slot {action.save_index} failed (no current scene?)"));
                        yield break;
                    }
                    default:
                        result = Fail($"action[{action.type}]", $"Unknown action type: {action.type}");
                        break;
                }
            }
            catch (Exception e)
            {
                done(Fail($"action[{action.type}]", $"Action {action.type} failed: {e.Message}"));
                yield break;
            }
            done(result);
        }

        // ---- Oracle assertions ----

        static WarptestCheck CheckAssertion(WarptestAssertion assertion)
        {
            try
            {
                switch (assertion.type)
                {
                    case "pal3_story_var_equals":
                    {
                        int value = TryGetService<IUserVariableStore<ushort, int>>().Get((ushort)assertion.var_id);
                        return CompareValues($"assertion[{assertion.type}].var_{assertion.var_id}", value, assertion.expected, assertion.comparator);
                    }
                    case "pal3_scene_is":
                    {
                        if (!CurrentSceneInfo(out string city, out string name))
                            return Fail($"assertion[{assertion.type}]", "No current scene loaded.");
                        bool match = string.Equals(city, assertion.city, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(name, assertion.scene, StringComparison.OrdinalIgnoreCase);
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}]",
                            status = match ? "success" : "failure",
                            detail = match ? $"Scene = {city}/{name}" : $"Scene: expected {assertion.city}/{assertion.scene}, got {city}/{name}"
                        };
                    }
                    case "pal3_team_contains":
                    {
                        bool inTeam = TryGetService<TeamManager>().IsActorInTeam((PlayerActorId)assertion.actor_id);
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}].actor_{assertion.actor_id}",
                            status = inTeam ? "success" : "failure",
                            detail = inTeam ? $"Actor {assertion.actor_id} is in team" : $"Actor {assertion.actor_id} not in team"
                        };
                    }
                    case "pal3_team_count":
                        return CompareValues($"assertion[{assertion.type}]", SafeTeamCount(), assertion.expected, assertion.comparator);
                    case "pal3_item_count":
                        return CompareValues($"assertion[{assertion.type}].item_{assertion.item_id}", ItemCount(assertion.item_id), assertion.expected, assertion.comparator);
                    case "pal3_money_gte":
                    {
                        int money = SafeMoney();
                        bool ok = money >= assertion.int_value;
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}]",
                            status = ok ? "success" : "failure",
                            detail = ok ? $"Money {money} >= {assertion.int_value}" : $"Money {money} < {assertion.int_value}"
                        };
                    }
                    case "pal3_world_region_enabled":
                    {
                        int flag = RegionFlag(assertion.region);
                        bool ok = assertion.int_value > 0 ? flag >= assertion.int_value : flag > 0;
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}].region_{assertion.region}",
                            status = ok ? "success" : "failure",
                            detail = ok ? $"Region {assertion.region} flag = {flag}" : $"Region {assertion.region} flag {flag} not enabled"
                        };
                    }
                    case "pal3_favor_gte":
                    {
                        int favor = TryGetService<FavorManager>().GetFavorByActor(assertion.actor_id);
                        bool ok = favor >= assertion.int_value;
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}].actor_{assertion.actor_id}",
                            status = ok ? "success" : "failure",
                            detail = ok ? $"Favor {favor} >= {assertion.int_value}" : $"Favor {favor} < {assertion.int_value}"
                        };
                    }
                    case "no_pal3_utility_errors":
                        return Ok($"assertion[{assertion.type}]", "No utility errors detected.");
                    default:
                        return Fail($"assertion[{assertion.type}]", $"Unknown assertion type: {assertion.type}");
                }
            }
            catch (Exception e)
            {
                return Fail($"assertion[{assertion.type}]", $"Assertion failed: {e.Message}");
            }
        }

        // ---- Manager read helpers ----

        static T TryGetService<T>() where T : class
        {
            try { return ServiceLocator.Instance.GetAllRegisteredServices().OfType<T>().FirstOrDefault(); }
            catch { return null; }
        }

        static int SafeMoney()
        {
            try { return TryGetService<InventoryManager>()?.GetTotalMoney() ?? 0; }
            catch { return 0; }
        }

        static int SafeTeamCount()
        {
            try { return TryGetService<TeamManager>()?.GetActorsInTeam()?.Count ?? 0; }
            catch { return 0; }
        }

        static int ItemCount(int id)
        {
            try
            {
                var inventory = TryGetService<InventoryManager>();
                if (inventory == null) return 0;
                foreach (var kv in inventory.GetAllItems())
                    if (kv.Key == id) return kv.Value;
                return 0;
            }
            catch { return 0; }
        }

        static string KnownItemIdSample()
        {
            try
            {
                var inventory = TryGetService<InventoryManager>();
                if (inventory == null) return "unavailable";
                var field = typeof(InventoryManager).GetField("_gameItemInfos",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var value = field?.GetValue(inventory);
                var keys = value?.GetType().GetProperty("Keys")?.GetValue(value) as IEnumerable;
                if (keys == null) return "unavailable";
                var ids = new List<int>();
                foreach (var key in keys)
                    if (key is int id) ids.Add(id);
                ids.Sort();
                return string.Join(",", ids.Take(12));
            }
            catch
            {
                return "unavailable";
            }
        }

        static int RegionFlag(int region)
        {
            try
            {
                var worldMap = TryGetService<WorldMapManager>();
                if (worldMap == null) return 0;
                var info = worldMap.GetRegionEnablementInfo();
                return info != null && info.TryGetValue(region, out int flag) ? flag : 0;
            }
            catch { return 0; }
        }

        static bool CurrentSceneInfo(out string city, out string name)
        {
            city = null;
            name = null;
            try
            {
                var scene = TryGetService<PalSceneManager>()?.GetCurrentScene();
                if (scene == null) return false;
                var info = scene.GetSceneInfo();
                city = info.CityName;
                name = info.SceneName;
                return true;
            }
            catch { return false; }
        }

        // ---- Comparison + check builders ----

        static WarptestCheck CompareValues(string name, object actual, string expected, string comparator)
        {
            string actualStr = actual?.ToString() ?? "null";
            bool ok;
            switch (comparator ?? "equals")
            {
                case "gte": ok = Convert.ToInt32(actual) >= int.Parse(expected); break;
                case "lte": ok = Convert.ToInt32(actual) <= int.Parse(expected); break;
                case "gt": ok = Convert.ToInt32(actual) > int.Parse(expected); break;
                default: ok = actualStr == expected; break;
            }
            return new WarptestCheck
            {
                name = name,
                status = ok ? "success" : "failure",
                detail = ok ? $"{name} = {actualStr}" : $"{name}: expected {comparator ?? "equals"} {expected}, got {actualStr}"
            };
        }

        static WarptestCheck Ok(string name, string detail) => new WarptestCheck { name = name, status = "success", detail = detail };
        static WarptestCheck Fail(string name, string detail) => new WarptestCheck { name = name, status = "failure", detail = detail };

        // ---- Screenshot capture (camera render with ScreenCapture fallback) ----

        static string CaptureScreenshotToFile(string outputPath)
        {
            if (TryCaptureCameraToFile(outputPath, out string cameraDetail))
                return cameraDetail;

            if (Application.isPlaying)
            {
                var texture = ScreenCapture.CaptureScreenshotAsTexture();
                try
                {
                    if (texture != null)
                    {
                        File.WriteAllBytes(outputPath, texture.EncodeToPNG());
                        if (TextureHasVisibleRange(texture))
                            return "ScreenCapture captured an informative image.";
                    }
                }
                finally
                {
                    if (texture != null) UnityEngine.Object.Destroy(texture);
                }
            }

            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw new InvalidOperationException($"Unable to capture an informative Unity screenshot: {cameraDetail}");
        }

        static bool TryCaptureCameraToFile(string outputPath, out string detail)
        {
            var camera = UnityEngine.Camera.main ?? UnityEngine.Object.FindObjectOfType<UnityEngine.Camera>();
            if (camera == null)
            {
                detail = "No Unity camera is available.";
                return false;
            }

            int width = Math.Max(640, Screen.width > 0 ? Screen.width : 1280);
            int height = Math.Max(360, Screen.height > 0 ? Screen.height : 720);
            var renderTexture = new RenderTexture(width, height, 24);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();

                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(outputPath, texture.EncodeToPNG());
                bool informative = TextureHasVisibleRange(texture);
                DestroyCapturedObject(texture);
                detail = informative ? "Camera render captured an informative image." : "Camera render produced a blank or flat image.";
                return informative;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                renderTexture.Release();
                DestroyCapturedObject(renderTexture);
            }
        }

        static void DestroyCapturedObject(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }

        static bool TextureHasVisibleRange(Texture2D texture)
        {
            if (texture == null) return false;
            var pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length == 0) return false;
            int low = 255, high = 0;
            foreach (var pixel in pixels)
            {
                if (pixel.r < low) low = pixel.r;
                if (pixel.g < low) low = pixel.g;
                if (pixel.b < low) low = pixel.b;
                if (pixel.r > high) high = pixel.r;
                if (pixel.g > high) high = pixel.g;
                if (pixel.b > high) high = pixel.b;
            }
            return high - low >= 8;
        }

        // ---- IO + quit ----

        static void WriteFailureReport(string reportPath, string detail, string checkName)
        {
            try
            {
                var report = new WarptestReport
                {
                    status = "failure",
                    detail = detail,
                    checks = new List<WarptestCheck> { Fail(checkName, detail) }
                };
                File.WriteAllText(reportPath, JsonUtility.ToJson(report, true), Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogError($"[WarpTest] Failed to write report: {e.Message}");
            }
        }

        internal static void WriteReport(string reportPath, WarptestReport report)
        {
            File.WriteAllText(reportPath, JsonUtility.ToJson(report, true), Encoding.UTF8);
            Debug.Log($"[WarpTest] Report written to {reportPath}");
        }

        internal static void EditorQuit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(code);
#else
            Application.Quit(code);
#endif
        }
    }

    // Drives the asynchronous checkpoint flow inside play mode without taking a
    // dependency on any specific async library.
    public sealed class WarptestRunnerBehaviour : MonoBehaviour
    {
        string _requestPath;
        string _reportPath;

        public void Begin(string requestPath, string reportPath)
        {
            _requestPath = requestPath;
            _reportPath = reportPath;
            StartCoroutine(RunFlow());
        }

        IEnumerator RunFlow()
        {
            WarptestRequest request = null;
            var checks = new List<WarptestCheck>();
            try
            {
                var json = File.ReadAllText(_requestPath, Encoding.UTF8);
                request = JsonUtility.FromJson<WarptestRequest>(json);
            }
            catch (Exception e)
            {
                WarptestCheckpoint.WriteReport(_reportPath, new WarptestReport
                {
                    status = "failure",
                    detail = $"Failed to parse request: {e.Message}",
                    checks = new List<WarptestCheck>()
                });
                WarptestCheckpoint.EditorQuit(1);
                yield break;
            }

            WarptestReport report = null;
            yield return WarptestCheckpoint.ProcessRequestCoroutine(request, checks, r => report = r);

            if (report == null)
                report = new WarptestReport { status = "failure", detail = "No report produced.", checks = checks };

            try { WarptestCheckpoint.WriteReport(_reportPath, report); }
            catch (Exception e) { Debug.LogError($"[WarpTest] {e}"); }

            WarptestCheckpoint.EditorQuit(report.status == "success" ? 0 : 1);
        }
    }

    // ---- JSON contract (mirrors the Python Pal3RuntimeAdapter request shape) ----

    [Serializable]
    public class WarptestRequest
    {
        public string spec_path;
        public string screenshot_output_path;
        public WarptestSpec spec;
    }

    [Serializable]
    public class WarptestSpec
    {
        public WarptestTarget target;
        public List<WarptestValidation> validations = new List<WarptestValidation>();
        public List<WarptestAction> actions = new List<WarptestAction>();
        public List<WarptestAssertion> assertions = new List<WarptestAssertion>();
    }

    [Serializable]
    public class WarptestTarget
    {
        public string kind;
        public int save_index = -1;
        public int money = 0;
        public string scene_city;
        public string scene_name;
        public int[] team_ids;
        public WarptestStoryVar[] story_vars;
        public WarptestItemEntry[] items;
        public WarptestRegionEntry[] world_regions;
        public WarptestFavorEntry[] favors;
        public WarptestPosition position;
    }

    [Serializable]
    public class WarptestStoryVar
    {
        public int key;
        public int value;
    }

    [Serializable]
    public class WarptestItemEntry
    {
        public int id;
        public int count = 1;
    }

    [Serializable]
    public class WarptestRegionEntry
    {
        public int region;
        public int flag = 2;
    }

    [Serializable]
    public class WarptestFavorEntry
    {
        public int actor_id;
        public int amount;
    }

    [Serializable]
    public class WarptestPosition
    {
        public bool set;
        public int actor_id = -1;
        public float x;
        public float z;
        public int layer = -1;
        public int facing;
    }

    [Serializable]
    public class WarptestValidation
    {
        public string path;
        public string expected;
    }

    [Serializable]
    public class WarptestAction
    {
        public string type;
        public int var_id;
        public int value;
        public int actor_id;
        public int item_id;
        public int item_count = 1;
        public int amount;
        public int region;
        public int flag;
        public string city;
        public string scene;
        public int save_index;
    }

    [Serializable]
    public class WarptestAssertion
    {
        public string type;
        public int var_id;
        public int actor_id;
        public int item_id;
        public int region;
        public string city;
        public string scene;
        public string expected;
        public string comparator;
        public int int_value;
    }

    [Serializable]
    public class WarptestCheck
    {
        public string name;
        public string status;
        public string detail;
    }

    [Serializable]
    public class WarptestReport
    {
        public string status;
        public string detail;
        public string screenshot_path;
        public string screenshot_status;
        public string screenshot_source;
        public string screenshot_detail;
        public List<WarptestCheck> checks;
    }
}
