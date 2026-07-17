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
 *
 * RunC1 is a separate headed/persistent path. Its restore_target operation is
 * setup-only and cannot execute actions or assertions; the agent must perform
 * Phase B through the public game UI before read-only semantic_goal probing.
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
        internal const string C1SessionVersion = "warptest-c1-unity-v3";
        const string StateEvidenceVersion = "warptest-unity-checkpoint-state-v1";

#if UNITY_EDITOR
        const string PendingKey = "WarpTest.Pal3.Pending";
        const string PendingRequestPathKey = "WarpTest.Pal3.PendingRequestPath";
        const string PendingReportPathKey = "WarpTest.Pal3.PendingReportPath";
        const string C1PendingKey = "WarpTest.Pal3.C1Pending";
        const string C1RequestPathKey = "WarpTest.Pal3.C1RequestPath";
        const string C1ReportPathKey = "WarpTest.Pal3.C1ReportPath";
        const string C1ReadyPathKey = "WarpTest.Pal3.C1ReadyPath";
        const string C1SessionIdKey = "WarpTest.Pal3.C1SessionId";
        static int s_pendingPlayModeFrames;
        static int s_pendingC1PlayModeFrames;
        static bool s_c1TransitionRequired;
        static int s_c1TransitionSlot = -1;
        static int s_c1TransitionArmedSequence = -1;
        static bool s_c1SaveObserved;
        static bool s_c1ResetObserved;
        static bool s_c1SceneLoadObserved;
        static bool s_c1LoadObserved;
        static int s_c1SaveFrame = -1;
        static int s_c1ResetFrame = -1;
        static int s_c1SceneLoadFrame = -1;
        static int s_c1LoadFrame = -1;
        static PalGameScene s_c1SceneAtSave;
        static string s_c1SavedSceneCity = "";
        static string s_c1SavedSceneName = "";
        static int s_c1PolicyFrameId = -1;
        static bool s_c1PolicyFrameConsumed = true;
        static readonly Dictionary<string, WarptestC1Report> s_c1InputReceipts = new Dictionary<string, WarptestC1Report>();
        static string s_c1SessionId = "";

        internal static void ConfigureC1BackgroundSession(string sessionId)
        {
            s_c1SessionId = sessionId ?? "";
            s_c1PolicyFrameId = -1;
            s_c1PolicyFrameConsumed = true;
            s_c1InputReceipts.Clear();
            Application.runInBackground = true;
        }

        internal static void ResetC1TransitionWitness()
        {
            s_c1TransitionRequired = false;
            s_c1TransitionSlot = -1;
            s_c1TransitionArmedSequence = -1;
            s_c1SaveObserved = false;
            s_c1ResetObserved = false;
            s_c1SceneLoadObserved = false;
            s_c1LoadObserved = false;
            s_c1SaveFrame = -1;
            s_c1ResetFrame = -1;
            s_c1SceneLoadFrame = -1;
            s_c1LoadFrame = -1;
            s_c1SceneAtSave = null;
            s_c1SavedSceneCity = "";
            s_c1SavedSceneName = "";
        }

        static bool HasC1TransitionExpectation(WarptestC1TransitionExpectation expectation)
        {
            return expectation != null
                && (!string.IsNullOrEmpty(expectation.kind) || expectation.slot >= 0);
        }

        static bool ArmC1TransitionWitness(WarptestC1TransitionExpectation expectation, int sequence)
        {
            ResetC1TransitionWitness();
            if (!HasC1TransitionExpectation(expectation))
                return true;
            if (expectation.kind != "save_then_load" || expectation.slot < 0)
                return false;
            s_c1TransitionRequired = true;
            s_c1TransitionSlot = expectation.slot;
            s_c1TransitionArmedSequence = sequence;
            return true;
        }

        internal static void ObserveC1Log(string condition, string stackTrace, LogType type)
        {
            if (!s_c1TransitionRequired || string.IsNullOrEmpty(condition)
                || condition.IndexOf("[SaveManager] Game state saved to:", StringComparison.Ordinal) < 0)
                return;
            string normalized = condition.Replace('\\', '/');
            if (!normalized.EndsWith($"slot_{s_c1TransitionSlot}_v1.txt", StringComparison.Ordinal))
                return;
            s_c1SaveObserved = true;
            s_c1SaveFrame = Time.frameCount;
            s_c1SceneAtSave = TryGetService<PalSceneManager>()?.GetCurrentScene();
            string city;
            string scene;
            if (CurrentSceneInfo(out city, out scene))
            {
                s_c1SavedSceneCity = city;
                s_c1SavedSceneName = scene;
            }
        }

        internal static void ObserveC1ResetCommand()
        {
            if (!s_c1TransitionRequired || !s_c1SaveObserved || s_c1LoadObserved)
                return;
            s_c1ResetObserved = true;
            s_c1ResetFrame = Time.frameCount;
        }

        internal static void ObserveC1SceneLoadCommand(SceneLoadCommand command)
        {
            if (!s_c1TransitionRequired || !s_c1SaveObserved || !s_c1ResetObserved
                || s_c1LoadObserved || command == null)
                return;
            if (!string.Equals(command.SceneCityName, s_c1SavedSceneCity, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(command.SceneName, s_c1SavedSceneName, StringComparison.OrdinalIgnoreCase))
                return;
            s_c1SceneLoadObserved = true;
            s_c1SceneLoadFrame = Time.frameCount;
        }

        internal static void ObserveC1Transition()
        {
            if (!s_c1TransitionRequired || !s_c1SaveObserved || !s_c1ResetObserved
                || !s_c1SceneLoadObserved || s_c1LoadObserved || s_c1SceneAtSave == null)
                return;
            PalGameScene current = TryGetService<PalSceneManager>()?.GetCurrentScene();
            if (current != null && !ReferenceEquals(current, s_c1SceneAtSave))
            {
                s_c1LoadObserved = true;
                s_c1LoadFrame = Time.frameCount;
            }
        }

        static bool C1ExpectationMatches(WarptestC1TransitionExpectation expectation)
        {
            return expectation != null
                && expectation.kind == "save_then_load"
                && expectation.slot == s_c1TransitionSlot;
        }

        static WarptestC1TransitionEvidence C1TransitionEvidence(int sequence)
        {
            return new WarptestC1TransitionEvidence
            {
                required = s_c1TransitionRequired,
                kind = s_c1TransitionRequired ? "save_then_load" : "",
                slot = s_c1TransitionSlot,
                source = "pal3_public_ui_save_load_witness_v1",
                armed_sequence = s_c1TransitionArmedSequence,
                observed_sequence = sequence,
                save_observed = s_c1SaveObserved,
                reset_observed = s_c1ResetObserved,
                scene_load_observed = s_c1SceneLoadObserved,
                load_observed = s_c1LoadObserved,
                save_frame = s_c1SaveFrame,
                reset_frame = s_c1ResetFrame,
                scene_load_frame = s_c1SceneLoadFrame,
                load_frame = s_c1LoadFrame,
                ordered = s_c1SaveObserved && s_c1ResetObserved
                    && s_c1SceneLoadObserved && s_c1LoadObserved
                    && s_c1ResetFrame >= s_c1SaveFrame
                    && s_c1SceneLoadFrame >= s_c1ResetFrame
                    && s_c1LoadFrame >= s_c1SceneLoadFrame,
            };
        }

        static void AddC1TransitionGoalChecks(
            WarptestC1TransitionExpectation expectation,
            List<WarptestCheck> checks)
        {
            if (!HasC1TransitionExpectation(expectation) && !s_c1TransitionRequired)
                return;
            bool matches = s_c1TransitionRequired && C1ExpectationMatches(expectation);
            checks.Add(matches
                ? Ok("c1.transition.expectation", $"Armed save/load witness for slot {s_c1TransitionSlot}.")
                : Fail("c1.transition.expectation", "Goal transition expectation was missing, malformed, or changed after semantic_start."));
            checks.Add(s_c1SaveObserved
                ? Ok("c1.transition.save", $"Observed successful public-UI save to slot {s_c1TransitionSlot} at frame {s_c1SaveFrame}.")
                : Fail("c1.transition.save", $"No successful public-UI save to slot {s_c1TransitionSlot} was observed after semantic_start."));
            bool ordered = s_c1SaveObserved && s_c1ResetObserved
                && s_c1SceneLoadObserved && s_c1LoadObserved
                && s_c1ResetFrame >= s_c1SaveFrame
                && s_c1SceneLoadFrame >= s_c1ResetFrame
                && s_c1LoadFrame >= s_c1SceneLoadFrame;
            checks.Add(ordered
                ? Ok("c1.transition.load", $"Observed ordered reset + saved-scene replay after the slot-{s_c1TransitionSlot} save.")
                : Fail("c1.transition.load", $"No ordered public-UI load replay of slot {s_c1TransitionSlot} was observed after its save."));
        }

        [UnityEditor.InitializeOnLoadMethod]
        static void ResumePendingPlayModeRun()
        {
            if (UnityEditor.EditorPrefs.GetBool(C1PendingKey, false))
            {
                s_pendingC1PlayModeFrames = 30;
                UnityEditor.EditorApplication.update -= RunPendingC1WhenPlayModeReady;
                UnityEditor.EditorApplication.update += RunPendingC1WhenPlayModeReady;
            }
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

        public static void RunC1()
        {
            string requestPath = null;
            string reportPath = null;
            string readyPath = null;
            string sessionId = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--warptest-c1-request" && i + 1 < args.Length)
                    requestPath = args[i + 1];
                if (args[i] == "--warptest-c1-report" && i + 1 < args.Length)
                    reportPath = args[i + 1];
                if (args[i] == "--warptest-c1-ready" && i + 1 < args.Length)
                    readyPath = args[i + 1];
                if (args[i] == "--warptest-c1-session" && i + 1 < args.Length)
                    sessionId = args[i + 1];
            }
            if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(reportPath) || string.IsNullOrEmpty(readyPath) || string.IsNullOrEmpty(sessionId))
            {
                Debug.LogError("[WarpTest C1] Missing request/report/ready arguments.");
                EditorQuit(1);
                return;
            }
#if UNITY_EDITOR
            if (MaybeQueueC1PlayModeRun(requestPath, reportPath, readyPath, sessionId))
                return;
            StartC1Runner(requestPath, reportPath, readyPath, sessionId);
#else
            Debug.LogError("[WarpTest C1] PAL3 C1 requires Unity editor play mode.");
            EditorQuit(1);
#endif
        }

#if UNITY_EDITOR
        static bool MaybeQueueC1PlayModeRun(string requestPath, string reportPath, string readyPath, string sessionId)
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
                Debug.LogWarning($"[WarpTest C1] Unable to open {GameScenePath}: {e.Message}");
            }
            UnityEditor.EditorPrefs.SetBool(C1PendingKey, true);
            UnityEditor.EditorPrefs.SetString(C1RequestPathKey, requestPath);
            UnityEditor.EditorPrefs.SetString(C1ReportPathKey, reportPath);
            UnityEditor.EditorPrefs.SetString(C1ReadyPathKey, readyPath);
            UnityEditor.EditorPrefs.SetString(C1SessionIdKey, sessionId);
            s_pendingC1PlayModeFrames = 30;
            UnityEditor.EditorApplication.update -= RunPendingC1WhenPlayModeReady;
            UnityEditor.EditorApplication.update += RunPendingC1WhenPlayModeReady;
            UnityEditor.EditorApplication.isPlaying = true;
            Debug.Log("[WarpTest C1] Queued persistent PAL3 play-mode session.");
            return true;
        }

        static void RunPendingC1WhenPlayModeReady()
        {
            if (!UnityEditor.EditorApplication.isPlaying)
                return;
            if (s_pendingC1PlayModeFrames-- > 0)
                return;
            UnityEditor.EditorApplication.update -= RunPendingC1WhenPlayModeReady;
            var requestPath = UnityEditor.EditorPrefs.GetString(C1RequestPathKey, "");
            var reportPath = UnityEditor.EditorPrefs.GetString(C1ReportPathKey, "");
            var readyPath = UnityEditor.EditorPrefs.GetString(C1ReadyPathKey, "");
            var sessionId = UnityEditor.EditorPrefs.GetString(C1SessionIdKey, "");
            UnityEditor.EditorPrefs.DeleteKey(C1PendingKey);
            UnityEditor.EditorPrefs.DeleteKey(C1RequestPathKey);
            UnityEditor.EditorPrefs.DeleteKey(C1ReportPathKey);
            UnityEditor.EditorPrefs.DeleteKey(C1ReadyPathKey);
            UnityEditor.EditorPrefs.DeleteKey(C1SessionIdKey);
            if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(reportPath) || string.IsNullOrEmpty(readyPath) || string.IsNullOrEmpty(sessionId))
            {
                Debug.LogError("[WarpTest C1] Pending session lost IPC paths.");
                EditorQuit(1);
                return;
            }
            StartC1Runner(requestPath, reportPath, readyPath, sessionId);
        }

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

#if UNITY_EDITOR
        static void StartC1Runner(string requestPath, string reportPath, string readyPath, string sessionId)
        {
            var existing = UnityEngine.Object.FindObjectOfType<WarptestC1RunnerBehaviour>();
            if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);
            var host = new GameObject("WarptestC1Runner");
            UnityEngine.Object.DontDestroyOnLoad(host);
            var runner = host.AddComponent<WarptestC1RunnerBehaviour>();
            runner.Begin(requestPath, reportPath, readyPath, sessionId);
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

        // ---- Final GameView screenshot capture (includes overlay UI) ----

        static string CaptureScreenshotToFile(string outputPath)
        {
            if (Screen.width != 1280 || Screen.height != 720)
                throw new InvalidOperationException($"GameView size drifted to {Screen.width}x{Screen.height}; expected 1280x720.");
            if (Application.isPlaying)
            {
                var texture = ScreenCapture.CaptureScreenshotAsTexture();
                try
                {
                    if (texture != null)
                    {
                        File.WriteAllBytes(outputPath, texture.EncodeToPNG());
                        if (TextureHasVisibleRange(texture))
                            return "ScreenCapture captured final GameView pixels including overlay UI.";
                    }
                }
                finally
                {
                    if (texture != null) UnityEngine.Object.Destroy(texture);
                }
            }

            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw new InvalidOperationException("Unable to capture informative final GameView pixels.");
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

        // ---- Persistent background-pixel C1 operations ----

#if UNITY_EDITOR
        static EventModifiers C1Modifiers(string[] names)
        {
            EventModifiers result = EventModifiers.None;
            foreach (var raw in names ?? Array.Empty<string>())
            {
                switch ((raw ?? "").ToLowerInvariant())
                {
                    case "shift": result |= EventModifiers.Shift; break;
                    case "ctrl": case "control": result |= EventModifiers.Control; break;
                    case "alt": case "option": result |= EventModifiers.Alt; break;
                    case "meta": case "command": case "cmd": result |= EventModifiers.Command; break;
                    default: throw new InvalidOperationException($"Unsupported modifier: {raw}");
                }
            }
            return result;
        }

        static KeyCode C1KeyCode(string raw)
        {
            switch ((raw ?? "").ToLowerInvariant())
            {
                case "enter": case "return": return KeyCode.Return;
                case "escape": case "esc": return KeyCode.Escape;
                case "backspace": return KeyCode.Backspace;
                case "delete": return KeyCode.Delete;
                case "tab": return KeyCode.Tab;
                case "space": return KeyCode.Space;
                case "left": return KeyCode.LeftArrow;
                case "right": return KeyCode.RightArrow;
                case "up": return KeyCode.UpArrow;
                case "down": return KeyCode.DownArrow;
                case "home": return KeyCode.Home;
                case "end": return KeyCode.End;
                case "pageup": return KeyCode.PageUp;
                case "pagedown": return KeyCode.PageDown;
            }
            KeyCode parsed;
            if (Enum.TryParse(raw, true, out parsed)) return parsed;
            throw new InvalidOperationException($"Unsupported key: {raw}");
        }

        static void QueueC1Event(Event value) => UnityEditor.EditorGUIUtility.QueueGameViewInputEvent(value);

        static int QueueC1Key(string key, string[] modifiers, char character = '\0')
        {
            var flags = C1Modifiers(modifiers);
            var code = character == '\0' ? C1KeyCode(key) : KeyCode.None;
            QueueC1Event(new Event { type = EventType.KeyDown, keyCode = code, character = character, modifiers = flags });
            QueueC1Event(new Event { type = EventType.KeyUp, keyCode = code, character = '\0', modifiers = flags });
            return 2;
        }

        static Vector2 C1Point(int x, int y)
        {
            if (x < 0 || x >= 1280 || y < 0 || y >= 720)
                throw new InvalidOperationException($"Input coordinate ({x}, {y}) is outside 1280x720.");
            return new Vector2(x, y);
        }

        static int QueueC1Action(WarptestC1InputAction action)
        {
            if (action == null) throw new InvalidOperationException("Input action is null.");
            switch (action.kind)
            {
                case "done": case "fail": case "wait":
                    if (action.seconds < 0 || action.seconds > 5) throw new InvalidOperationException("Wait duration is outside 0..5 seconds.");
                    return 0;
                case "click":
                {
                    int button = action.button == "right" ? 1 : action.button == "middle" ? 2 : 0;
                    int clicks = action.clicks == 0 ? 1 : action.clicks;
                    if (clicks < 1 || clicks > 3) throw new InvalidOperationException("Click count is outside 1..3.");
                    var point = C1Point(action.x, action.y);
                    var flags = C1Modifiers(action.modifiers);
                    for (int i = 0; i < clicks; i++)
                    {
                        QueueC1Event(new Event { type = EventType.MouseDown, mousePosition = point, button = button, clickCount = clicks, modifiers = flags });
                        QueueC1Event(new Event { type = EventType.MouseUp, mousePosition = point, button = button, clickCount = clicks, modifiers = flags });
                    }
                    return 2 * clicks;
                }
                case "key": return QueueC1Key(action.key, action.modifiers);
                case "type":
                {
                    int count = 0;
                    if (action.has_point)
                        count += QueueC1Action(new WarptestC1InputAction { kind = "click", x = action.x, y = action.y, clicks = 1, button = "left" });
                    if (action.overwrite)
                    {
                        count += QueueC1Key("a", new[] { "command" });
                        count += QueueC1Key("backspace", Array.Empty<string>());
                    }
                    string text = action.text ?? "";
                    if (text.Length > 4096) throw new InvalidOperationException("Input text exceeds 4096 characters.");
                    foreach (char character in text) count += QueueC1Key("", Array.Empty<string>(), character);
                    if (action.enter) count += QueueC1Key("enter", Array.Empty<string>());
                    return count;
                }
                case "scroll":
                    QueueC1Event(new Event { type = EventType.ScrollWheel, mousePosition = C1Point(action.x, action.y), delta = new Vector2(action.dx, action.dy) });
                    return 1;
                case "drag":
                {
                    if (action.duration < 0 || action.duration > 5) throw new InvalidOperationException("Drag duration is outside 0..5 seconds.");
                    int button = action.button == "right" ? 1 : action.button == "middle" ? 2 : 0;
                    var start = C1Point(action.x, action.y);
                    var end = C1Point(action.x2, action.y2);
                    var flags = C1Modifiers(action.modifiers);
                    QueueC1Event(new Event { type = EventType.MouseDown, mousePosition = start, button = button, modifiers = flags });
                    const int steps = 6;
                    for (int i = 1; i <= steps; i++)
                        QueueC1Event(new Event { type = EventType.MouseDrag, mousePosition = Vector2.Lerp(start, end, i / (float)steps), button = button, modifiers = flags });
                    QueueC1Event(new Event { type = EventType.MouseUp, mousePosition = end, button = button, modifiers = flags });
                    return steps + 2;
                }
                default: throw new InvalidOperationException($"Unsupported input action: {action.kind}");
            }
        }
#endif

        internal static IEnumerator ProcessC1RequestCoroutine(WarptestC1Request request, Action<WarptestC1Report> done)
        {
            var checks = new List<WarptestCheck>();
            var report = new WarptestC1Report
            {
                version = C1SessionVersion,
                sequence = request != null ? request.sequence : -1,
                operation = request != null ? request.operation : "",
                status = "failure",
                detail = "C1 request failed.",
                checks = checks,
                screenshot_path = request != null ? request.screenshot_output_path ?? "" : "",
                screenshot_status = "skipped",
                screenshot_source = "",
                screenshot_detail = "",
            };
            if (request == null || request.version != C1SessionVersion)
            {
                report.status = "rejected";
                report.detail = "Unexpected or missing C1 protocol version.";
                done(report);
                yield break;
            }
            if (string.IsNullOrEmpty(s_c1SessionId) || request.session_id != s_c1SessionId)
            {
                report.status = "rejected";
                report.detail = "C1 session nonce mismatch.";
                done(report);
                yield break;
            }
            if (request.spec == null)
                request.spec = new WarptestSpec { target = new WarptestTarget() };
            if (request.spec.target == null)
                request.spec.target = new WarptestTarget();

            switch (request.operation)
            {
                case "clean_entry":
                {
                    ResetC1TransitionWitness();
                    AsyncOperation load = null;
                    try { load = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync("Game"); }
                    catch (Exception e)
                    {
                        report.status = "engine_error";
                        report.detail = $"Unable to load PAL3 startup scene: {e.Message}";
                        done(report);
                        yield break;
                    }
                    if (load == null)
                    {
                        report.status = "engine_error";
                        report.detail = "Unable to load PAL3 startup scene.";
                        done(report);
                        yield break;
                    }
                    yield return load;
                    bool ready = false;
                    yield return WaitForGameReady(ok => ready = ok);
                    checks.Add(ready
                        ? Ok("c1.clean_entry", "Loaded the public PAL3 startup scene.")
                        : Fail("c1.clean_entry", "PAL3 managers did not become ready after startup reload."));
                    break;
                }
                case "restore_target":
                {
                    ResetC1TransitionWitness();
                    // SECURITY INVARIANT: this branch deliberately never reads or
                    // iterates the Phase B action or goal-assertion lists.
                    bool ready = false;
                    yield return WaitForGameReady(ok => ready = ok);
                    if (!ready)
                    {
                        checks.Add(Fail("c1.target.game_ready", "PAL3 managers never became available."));
                        break;
                    }
                    if (request.spec.target.save_index >= 0)
                        yield return LoadSaveCheckpoint(request.spec.target.save_index, checks);
                    else
                        yield return SynthesizeState(request.spec.target, checks);
                    if (checks.All(c => c.status == "success") && request.spec.validations != null)
                        foreach (var validation in request.spec.validations)
                            checks.Add(ValidateField(validation));
                    break;
                }
                case "semantic_start":
                {
                    bool transitionExpectationValid = ArmC1TransitionWitness(
                        request.transition_expectation, request.sequence);
                    if (HasC1TransitionExpectation(request.transition_expectation))
                        checks.Add(transitionExpectationValid
                            ? Ok("c1.transition.armed", $"Armed public-UI save/load witness for slot {request.transition_expectation.slot}.")
                            : Fail("c1.transition.armed", "Invalid save/load transition expectation."));
                    checks.AddRange(CheckC1Target(request.spec.target));
                    if (request.spec.validations != null)
                        foreach (var validation in request.spec.validations)
                            checks.Add(ValidateField(validation));
                    break;
                }
                case "semantic_goal":
                    if (request.spec.assertions == null || request.spec.assertions.Count == 0)
                        checks.Add(Fail("c1.semantic_goal", "No goal assertions were declared."));
                    else
                        foreach (var assertion in request.spec.assertions)
                            checks.Add(CheckAssertion(assertion));
                    AddC1TransitionGoalChecks(request.transition_expectation, checks);
                    break;
                case "capture":
                    if (string.IsNullOrEmpty(request.screenshot_output_path))
                    {
                        report.status = "rejected";
                        report.detail = "capture requires screenshot_output_path.";
                        done(report);
                        yield break;
                    }
                    for (int i = 0; i < 5; i++) yield return null;
                    try
                    {
                        string directory = Path.GetDirectoryName(request.screenshot_output_path);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                        string detail = CaptureScreenshotToFile(request.screenshot_output_path);
                        s_c1PolicyFrameId++;
                        s_c1PolicyFrameConsumed = false;
                        report.screenshot_status = "success";
                        report.screenshot_source = "unity_gameview_capture";
                        report.screenshot_detail = detail;
                        report.frame_id = s_c1PolicyFrameId;
                        report.frame_width = 1280;
                        report.frame_height = 720;
                        checks.Add(Ok("c1.live_capture", detail));
                    }
                    catch (Exception e)
                    {
                        if (File.Exists(request.screenshot_output_path)) File.Delete(request.screenshot_output_path);
                        report.status = "engine_error";
                        report.detail = e.Message;
                        report.screenshot_status = "failure";
                        report.screenshot_source = "capture_failure";
                        report.screenshot_detail = e.Message;
                        done(report);
                        yield break;
                    }
                    break;
                case "input_batch":
                    WarptestC1Report cached;
                    if (!string.IsNullOrEmpty(request.batch_id) && s_c1InputReceipts.TryGetValue(request.batch_id, out cached))
                    {
                        cached.sequence = request.sequence;
                        cached.operation = request.operation;
                        done(cached);
                        yield break;
                    }
                    if (string.IsNullOrEmpty(request.batch_id) || request.frame_id != s_c1PolicyFrameId || s_c1PolicyFrameConsumed)
                    {
                        report.status = "rejected";
                        report.detail = "Input batch references a stale or consumed frame.";
                        done(report);
                        yield break;
                    }
                    if (request.actions == null || request.actions.Count < 1 || request.actions.Count > 64)
                    {
                        report.status = "rejected";
                        report.detail = "Input batch action count is outside 1..64.";
                        done(report);
                        yield break;
                    }
                    try
                    {
                        int eventCount = 0;
                        foreach (var action in request.actions) eventCount += QueueC1Action(action);
                        s_c1PolicyFrameConsumed = true;
                        report.accepted = true;
                        report.batch_id = request.batch_id;
                        report.event_count = eventCount;
                        report.resulting_frame_id = request.frame_id + 1;
                        report.input_backend = "unity_editor_queue_gameview_input_v1";
                        report.error = "";
                        checks.Add(Ok("c1.input_batch", $"Queued {eventCount} GameView events."));
                        s_c1InputReceipts[request.batch_id] = report;
                    }
                    catch (Exception e)
                    {
                        report.status = "engine_error";
                        report.detail = e.Message;
                        report.error = e.Message;
                        done(report);
                        yield break;
                    }
                    break;
                case "close":
                    checks.Add(Ok("c1.close", "Close acknowledged."));
                    break;
                default:
                    report.status = "rejected";
                    report.detail = $"Unsupported C1 operation: {request.operation ?? "<missing>"}";
                    done(report);
                    yield break;
            }

            report.transition_evidence = C1TransitionEvidence(request.sequence);
            bool allOk = checks.Count > 0 && checks.All(c => c.status == "success");
            report.status = allOk ? "success" : "failure";
            report.detail = allOk ? "C1 live operation succeeded." : "One or more C1 live checks failed.";
            done(report);
        }

        static List<WarptestCheck> CheckC1Target(WarptestTarget target)
        {
            var checks = new List<WarptestCheck>();
            if (UnityEngine.Object.FindFirstObjectByType<PalApp>() == null)
            {
                checks.Add(Fail("c1.target.runtime", "PAL3 runtime is not live."));
                return checks;
            }
            if (!string.IsNullOrEmpty(target.scene_city) || !string.IsNullOrEmpty(target.scene_name))
            {
                bool hasScene = CurrentSceneInfo(out string city, out string name);
                bool match = hasScene
                    && string.Equals(city, target.scene_city, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(name, target.scene_name, StringComparison.OrdinalIgnoreCase);
                checks.Add(match
                    ? Ok("c1.target.scene", $"Scene = {city}/{name}")
                    : Fail("c1.target.scene", $"Expected {target.scene_city}/{target.scene_name}, got {city ?? "null"}/{name ?? "null"}."));
            }
            if (target.money > 0)
                checks.Add(CompareValues("c1.target.money", SafeMoney(), target.money.ToString(), "gte"));
            if (target.team_ids != null)
                foreach (int actorId in target.team_ids)
                {
                    bool present = false;
                    try { present = TryGetService<TeamManager>().IsActorInTeam((PlayerActorId)actorId); }
                    catch { present = false; }
                    checks.Add(present
                        ? Ok($"c1.target.team.actor_{actorId}", $"Actor {actorId} is in the live team.")
                        : Fail($"c1.target.team.actor_{actorId}", $"Actor {actorId} is missing from the live team."));
                }
            if (target.story_vars != null)
                foreach (var value in target.story_vars)
                {
                    int actual = TryGetService<IUserVariableStore<ushort, int>>().Get((ushort)value.key);
                    checks.Add(CompareValues($"c1.target.story_var_{value.key}", actual, value.value.ToString(), "equals"));
                }
            if (target.items != null)
                foreach (var item in target.items)
                    checks.Add(CompareValues($"c1.target.item_{item.id}", ItemCount(item.id), item.count.ToString(), "gte"));
            if (target.world_regions != null)
                foreach (var region in target.world_regions)
                    checks.Add(CompareValues($"c1.target.region_{region.region}", RegionFlag(region.region), region.flag.ToString(), "gte"));
            if (target.favors != null)
                foreach (var favor in target.favors)
                {
                    int actual = TryGetService<FavorManager>().GetFavorByActor(favor.actor_id);
                    checks.Add(CompareValues($"c1.target.favor_{favor.actor_id}", actual, favor.amount.ToString(), "gte"));
                }
            if (checks.Count == 0)
                checks.Add(Ok("c1.target.runtime", "PAL3 runtime is live."));
            return checks;
        }

        internal static void WriteC1Json(string path, object payload)
        {
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonUtility.ToJson(payload, true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tempPath, path);
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

        internal static WarptestReport AttachStateEvidence(
            WarptestRequest request,
            WarptestReport report)
        {
            if (report == null)
                report = new WarptestReport
                {
                    status = "failure",
                    detail = "No utility report produced.",
                    checks = new List<WarptestCheck>()
                };
            report.evidence_version = StateEvidenceVersion;
            report.evidence_task_id = request?.evidence_task_id ?? "";
            report.evidence_seed = request != null ? request.evidence_seed : 0;
            report.evidence_stage = request?.evidence_stage ?? "";
            report.evidence_benchmark = request?.evidence_benchmark ?? "";
            report.process_id = System.Diagnostics.Process.GetCurrentProcess().Id;
            report.process_alive_at_observation = true;
            return report;
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

    // Persistent headed C1 transport. It only dispatches to the setup/read-only
    // C1 coroutine above; it never invokes ExecuteAction.
#if UNITY_EDITOR
    public sealed class WarptestC1RunnerBehaviour : MonoBehaviour,
        ICommandExecutor<ResetGameStateCommand>,
        ICommandExecutor<SceneLoadCommand>
    {
        string _requestPath;
        string _reportPath;
        int _lastSequence;
        bool _busy;

        public void Begin(string requestPath, string reportPath, string readyPath, string sessionId)
        {
            _requestPath = requestPath;
            _reportPath = reportPath;
            _lastSequence = 0;
            Application.logMessageReceived -= WarptestCheckpoint.ObserveC1Log;
            Application.logMessageReceived += WarptestCheckpoint.ObserveC1Log;
            CommandExecutorRegistry<ICommand>.Instance.Register(this);
            WarptestCheckpoint.ResetC1TransitionWitness();
            WarptestCheckpoint.ConfigureC1BackgroundSession(sessionId);
            WarptestCheckpoint.WriteC1Json(readyPath, new WarptestC1Ready
            {
                version = WarptestCheckpoint.C1SessionVersion,
                sequence = 0,
                status = "ready",
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                session_id = sessionId,
            });
            Debug.Log("[WarpTest C1] PAL3 persistent session ready.");
        }

        void Update()
        {
            WarptestCheckpoint.ObserveC1Transition();
            if (_busy || !File.Exists(_requestPath)) return;
            WarptestC1Request request;
            try
            {
                string json = File.ReadAllText(_requestPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;
                request = JsonUtility.FromJson<WarptestC1Request>(json);
            }
            catch
            {
                return;
            }
            if (request == null || request.sequence <= _lastSequence) return;
            _busy = true;
            StartCoroutine(Process(request));
        }

        public void Execute(ResetGameStateCommand command)
        {
            WarptestCheckpoint.ObserveC1ResetCommand();
        }

        public void Execute(SceneLoadCommand command)
        {
            WarptestCheckpoint.ObserveC1SceneLoadCommand(command);
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= WarptestCheckpoint.ObserveC1Log;
            CommandExecutorRegistry<ICommand>.Instance.UnRegister(this);
            WarptestCheckpoint.ResetC1TransitionWitness();
        }

        IEnumerator Process(WarptestC1Request request)
        {
            WarptestC1Report report = null;
            if (request.sequence != _lastSequence + 1)
            {
                report = new WarptestC1Report
                {
                    version = WarptestCheckpoint.C1SessionVersion,
                    sequence = request.sequence,
                    operation = request.operation,
                    status = "rejected",
                    detail = $"Expected sequence {_lastSequence + 1}, got {request.sequence}.",
                    checks = new List<WarptestCheck>(),
                };
            }
            else
            {
                yield return WarptestCheckpoint.ProcessC1RequestCoroutine(request, value => report = value);
            }
            if (report == null)
            {
                report = new WarptestC1Report
                {
                    version = WarptestCheckpoint.C1SessionVersion,
                    sequence = request.sequence,
                    operation = request.operation,
                    status = "engine_error",
                    detail = "C1 coroutine produced no report.",
                    checks = new List<WarptestCheck>(),
                };
            }
            WarptestCheckpoint.WriteC1Json(_reportPath, report);
            _lastSequence = request.sequence;
            _busy = false;
            if (request.operation == "close" && report.status == "success")
            {
                yield return null;
                WarptestCheckpoint.EditorQuit(0);
            }
        }
    }
#endif

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

            report = WarptestCheckpoint.AttachStateEvidence(request, report);

            try { WarptestCheckpoint.WriteReport(_reportPath, report); }
            catch (Exception e) { Debug.LogError($"[WarpTest] {e}"); }

            WarptestCheckpoint.EditorQuit(report.status == "success" ? 0 : 1);
        }
    }

    // ---- JSON contract (mirrors the Python Pal3RuntimeAdapter request shape) ----

    [Serializable]
    public class WarptestC1Request
    {
        public string version;
        public int sequence;
        public string operation;
        public string session_id;
        public string spec_path;
        public string screenshot_output_path;
        public int frame_id = -1;
        public string batch_id;
        public List<WarptestC1InputAction> actions = new List<WarptestC1InputAction>();
        public WarptestSpec spec;
        public WarptestC1TransitionExpectation transition_expectation;
    }

    [Serializable]
    public class WarptestC1Report
    {
        public string version;
        public int sequence;
        public string operation;
        public string status;
        public string detail;
        public string screenshot_path;
        public string screenshot_status;
        public string screenshot_source;
        public string screenshot_detail;
        public int frame_id = -1;
        public int frame_width;
        public int frame_height;
        public string batch_id;
        public bool accepted;
        public int event_count;
        public int resulting_frame_id = -1;
        public string error;
        public string input_backend;
        public WarptestC1TransitionEvidence transition_evidence;
        public List<WarptestCheck> checks = new List<WarptestCheck>();
    }

    [Serializable]
    public class WarptestC1TransitionExpectation
    {
        public string kind;
        public int slot = -1;
    }

    [Serializable]
    public class WarptestC1TransitionEvidence
    {
        public bool required;
        public string kind;
        public int slot = -1;
        public string source;
        public int armed_sequence = -1;
        public int observed_sequence = -1;
        public bool save_observed;
        public bool reset_observed;
        public bool scene_load_observed;
        public bool load_observed;
        public int save_frame = -1;
        public int reset_frame = -1;
        public int scene_load_frame = -1;
        public int load_frame = -1;
        public bool ordered;
    }

    [Serializable]
    public class WarptestC1Ready
    {
        public string version;
        public int sequence;
        public string status;
        public int pid;
        public string session_id;
    }

    [Serializable]
    public class WarptestC1InputAction
    {
        public string kind;
        public int x;
        public int y;
        public int x2;
        public int y2;
        public bool has_point;
        public string key;
        public string[] modifiers = Array.Empty<string>();
        public string text;
        public int dx;
        public int dy;
        public float seconds;
        public float duration;
        public string button;
        public int clicks;
        public bool overwrite;
        public bool enter;
    }

    [Serializable]
    public class WarptestRequest
    {
        public string spec_path;
        public string screenshot_output_path;
        public string evidence_task_id;
        public int evidence_seed;
        public string evidence_stage;
        public string evidence_benchmark;
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
        public string evidence_version;
        public string evidence_task_id;
        public int evidence_seed;
        public string evidence_stage;
        public string evidence_benchmark;
        public int process_id;
        public bool process_alive_at_observation;
        public string screenshot_path;
        public string screenshot_status;
        public string screenshot_source;
        public string screenshot_detail;
        public List<WarptestCheck> checks;
    }
}
