using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SpatialGeneration.Authoring;

namespace SpatialGeneration.Agent
{
    /// <summary>
    /// The study procedure for one condition: up to N generation runs, accepting a version,
    /// previewing and applying refinements, and submitting the scene.
    /// </summary>
    public static class StudyController
    {
        private static StudySession S => StudySession.instance;
        private static BackendSettings Settings => BackendRegistry.Settings;

        private const string DeveloperModeKey = "SpatialGeneration.DeveloperMode";

        /// <summary>
        /// Allows unlimited generation runs and an optional participant ID for testing.
        /// Stored per machine and logged with every study event.
        /// </summary>
        public static bool DeveloperMode
        {
            get => EditorPrefs.GetBool(DeveloperModeKey, false);
            set
            {
                if (value == DeveloperMode)
                    return;
                EditorPrefs.SetBool(DeveloperModeKey, value);
                Log("developer_mode", $"enabled={value}");
            }
        }

        public static int MaxRuns => DeveloperMode ? int.MaxValue : Mathf.Max(1, Settings.maxGenerationRuns);

        /// <summary>
        /// Confirmation dialog used by the study flow. Tests replace it, because batch mode cancels
        /// every modal dialog; the editor always uses <see cref="EditorUtility.DisplayDialog(string,string,string,string)"/>.
        /// </summary>
        internal static Func<string, string, string, string, bool> Confirm = EditorUtility.DisplayDialog;

        public static string LastMessage { get; private set; } = string.Empty;
        public static MessageType LastMessageType { get; private set; } = MessageType.None;

        // ------------------------------------------------------------------ setup

        public static void StartCondition()
        {
            if (IsBusy)
                return;

            bool hasContent = GameObject.Find(GeneratedContentRoot.Name) != null ||
                              GameObject.Find(GenerationVersions.ContainerName) != null;
            if (hasContent && !Confirm("Start condition",
                    "The scene already contains generated content. It will be hidden under " +
                    $"'{GenerationVersions.ContainerName}' so this condition starts from an empty scene.",
                    "Start", "Cancel"))
                return;

            GenerationVersions.ArchiveActive(0);

            if (DeveloperMode && string.IsNullOrWhiteSpace(S.participantId))
                S.participantId = "dev";
            S.ResetProgress();
            S.phase = StudyPhase.Generation;
            S.conditionStartUtc = StudySession.NowUtc();
            S.Persist();
            Note(string.Empty, MessageType.None);
            Log("condition_start", $"max_runs={(DeveloperMode ? "unlimited" : MaxRuns.ToString())}; " +
                                   $"agent={Settings.agentCli}; model={Settings.agentModel}; effort={Settings.agentEffort}");
        }

        public static void ResetSession()
        {
            if (IsBusy)
                return;
            Log("session_reset", string.Empty);
            S.ResetProgress();
            S.Persist();
            Note(string.Empty, MessageType.None);
        }

        // ------------------------------------------------------------------ generation

        // True from the Generate click until the agent run itself holds AgentWorkflow's busy flag:
        // the setup check awaits the MCP server, and nothing else may change the study meanwhile.
        private static bool _starting;

        /// <summary>An agent run is active or about to start. Every study action waits for it.</summary>
        public static bool IsBusy => AgentWorkflow.IsBusy || _starting;

        public static bool CanGenerate => S.phase == StudyPhase.Generation && S.generationRuns < MaxRuns && !IsBusy;

        /// <summary>Button entry point; the run continues in the background.</summary>
        public static void RunGeneration() => Forget(RunGenerationAsync(), "Generation");

        internal static async Task RunGenerationAsync()
        {
            if (!CanGenerate)
                return;

            _starting = true;
            string condition = S.conditionStartUtc;
            int previous = S.activeRun;
            int run;
            Task<AgentRunReport> running;
            try
            {
                string problem = AgentWorkflow.ValidateGenerationInputs(S.condition, S.scenePrompt)
                                 ?? await AgentWorkflow.CheckSetupAsync();
                if (problem != null)
                {
                    Note(problem, MessageType.Warning);
                    return;
                }

                // The setup check awaited the network; make sure the study is still where it was.
                if (S.phase != StudyPhase.Generation || S.conditionStartUtc != condition ||
                    S.activeRun != previous || S.generationRuns >= MaxRuns || AgentWorkflow.IsBusy)
                {
                    Note("The study changed while the setup was being checked; no run was started.", MessageType.Warning);
                    return;
                }

                run = S.generationRuns + 1;
                if (previous > 0)
                {
                    GenerationVersions.ArchiveActive(previous);
                    if (!S.archivedRuns.Contains(previous))
                        S.archivedRuns.Add(previous);
                }

                S.generationRuns = run;
                S.activeRun = run;
                S.Persist();
                Log("generation_run_start", $"run={run}; scene_prompt={S.scenePrompt}; style_prompt={S.stylePrompt}");
                Note($"Generation run {run} of {MaxRuns} started.", MessageType.None);

                // Takes AgentWorkflow's busy flag before its first await, so the guard hands over without a gap.
                running = AgentWorkflow.RunGenerationAsync(S.condition, S.scenePrompt, S.stylePrompt);
            }
            finally
            {
                _starting = false;
            }

            AgentRunReport report = await running;
            if (!report.hasContent)
            {
                RefundRun(run, previous, report);
                return;
            }

            S.runRecords.RemoveAll(r => r.run == run);
            S.runRecords.Add(new RunRecord
            {
                run = run, outcome = report.outcome, parts = report.partsAdded,
                emptyAssets = new System.Collections.Generic.List<string>(report.emptyAssets)
            });
            S.Persist();
            string snapshot = SceneSnapshot.Write($"run{run}");
            Log("generation_run_end", $"run={run}; outcome={report.outcome}; {ReportFields(report)}; snapshot={snapshot}");
            Note($"Run {run}: {report.message}", report.success ? MessageType.Info : MessageType.Warning);
        }

        /// <summary>
        /// A run that built nothing gives the participant nothing to judge, so it does not use up one of
        /// their runs: its empty version is removed and the previous one is shown again.
        /// </summary>
        private static void RefundRun(int run, int previous, AgentRunReport report)
        {
            GenerationVersions.DiscardActive();
            if (previous > 0 && GenerationVersions.Restore(previous))
                S.archivedRuns.Remove(previous);
            S.activeRun = previous > 0 && GameObject.Find(GeneratedContentRoot.Name) != null ? previous : 0;
            S.generationRuns = run - 1;
            S.Persist();
            Log("generation_run_refunded", $"run={run}; outcome={report.outcome}; restored_run={S.activeRun}; {ReportFields(report)}");
            Note($"Run {run} did not build anything ({report.message}) It does not count; generate again.",
                MessageType.Warning);
        }

        public static void ShowVersion(int run)
        {
            if (S.phase != StudyPhase.Generation || IsBusy || run == S.activeRun)
                return;

            if (!GenerationVersions.Activate(run, S.activeRun))
            {
                Note($"Run {run} is no longer in the scene.", MessageType.Warning);
                return;
            }

            S.archivedRuns.Remove(run);
            if (!S.archivedRuns.Contains(S.activeRun))
                S.archivedRuns.Add(S.activeRun);
            S.activeRun = run;
            S.Persist();
            Log("version_show", $"run={run}");
        }

        /// <summary>Only a version with geometry can be refined.</summary>
        public static bool CanAccept => S.phase == StudyPhase.Generation && S.activeRun > 0 && !IsBusy &&
                                        ActiveVersionPartCount() > 0;

        /// <summary>Parts in the active GeneratedContent.</summary>
        public static int ActiveVersionPartCount()
        {
            GameObject root = GameObject.Find(GeneratedContentRoot.Name);
            return root != null ? root.GetComponentsInChildren<AgentPart>(false).Length : 0;
        }

        /// <summary>
        /// Why accepting the active version needs an explicit choice (objects without parts, or a run
        /// that did not finish cleanly), or null. The window shows it and puts it in the confirmation.
        /// </summary>
        public static string ActiveVersionWarning()
        {
            var empty = AgentSceneOps.AllAssets().FindAll(a => AgentSceneOps.PartsOf(a).Count == 0).ConvertAll(a => a.label);
            RunRecord record = S.Record(S.activeRun);
            string text = empty.Count > 0 ? $"{empty.Count} object(s) have no geometry: {string.Join(", ", empty)}." : null;
            if (record != null && record.outcome != RunOutcome.Complete)
                text = $"Run {S.activeRun} ended as {record.outcome.ToString().ToLowerInvariant()}." + (text != null ? " " + text : string.Empty);
            return text;
        }

        public static void AcceptActiveVersion()
        {
            if (!CanAccept)
                return;

            S.acceptedRun = S.activeRun;
            S.acceptedUtc = StudySession.NowUtc();
            double generationSeconds = StudySession.SecondsSince(S.conditionStartUtc);
            string snapshot = SceneSnapshot.Write($"accepted_run{S.acceptedRun}");

            S.phase = StudyPhase.Refinement;
            S.refinementStartUtc = StudySession.NowUtc();
            S.editRequest = string.Empty;
            S.Persist();

            if (S.condition == StudyCondition.SpatialControls)
                Selection.activeGameObject = EnsureRefinementRig(resetSelection: true).gameObject;
            // Refinement starts with an empty history: Ctrl+Z then only reverts refinements (tracked by
            // StudyUndoTracker), never generation-phase edits behind the accepted version.
            Undo.ClearAll();

            Log("version_accept", $"run={S.acceptedRun}; outcome={S.Record(S.acceptedRun)?.outcome}; " +
                                  $"warning={ActiveVersionWarning()}; runs_used={S.generationRuns}; " +
                                  $"generation_seconds={generationSeconds:0.0}; snapshot={snapshot}");
            Note("Refinement started.", MessageType.Info);
        }

        // ------------------------------------------------------------------ refinement

        public static bool CanEdit => S.phase == StudyPhase.Refinement && !S.HasReview && !IsBusy;

        public static void SubmitEdit()
        {
            if (!CanEdit)
                return;

            RegionSelection region = null;
            if (S.condition == StudyCondition.SpatialControls)
            {
                RegionSelectionManager rig = EnsureRefinementRig(resetSelection: false);
                region = rig.CurrentSelection?.Clone();
                if (region == null || !region.partScoped)
                {
                    Note("Click a part or drag a region in the Scene view first.", MessageType.Warning);
                    return;
                }
            }

            string request = S.editRequest?.Trim() ?? string.Empty;
            if (request.Length == 0)
            {
                Note("Describe the change first.", MessageType.Warning);
                return;
            }

            try
            {
                S.review = RefinementReview.Begin(GameObject.Find(GeneratedContentRoot.Name), request,
                    S.stylePrompt, S.condition, region);
            }
            catch (System.Exception ex) { Note(ex.Message, MessageType.Warning); return; }
            S.editsSubmitted++;
            S.Persist();
            string before = SceneSnapshot.Write($"edit{S.editsSubmitted}_before");
            Log("edit_submit", $"edit={S.editsSubmitted}; request={request}; review={S.review.id}; snapshot={before}");
            GenerateIdea();
        }

        public static bool IsCurrentReview(RefinementReview review) => review != null && S.HasReview &&
            S.review.id == review.id && S.phase == StudyPhase.Refinement;

        /// <summary>Button entry point; the preview continues in the background.</summary>
        public static void GenerateIdea() => Forget(GenerateIdeaAsync(), "Preview");

        internal static async Task GenerateIdeaAsync()
        {
            var review = S.review;
            if (!IsCurrentReview(review) || IsBusy || AgentJobState.instance.HasRunningJob) return;
            Note("Generating an idea…", MessageType.None);
            AgentRunReport report = await AgentWorkflow.RunPreviewAsync(review);
            if (!IsCurrentReview(review)) return;
            Log("preview_result", $"review={review.id}; idea={review.ideas.Count + 1}; {ReportFields(report)}");
            if (report.hasContent)
            {
                review.ideas.Add(report.preview);
                review.selected = review.ideas.Count - 1;
            }
            S.Persist();
            Note(report.message, report.hasContent ? MessageType.None : MessageType.Warning);
        }

        // ------------------------------------------------------------------ undo of applied refinements

        /// <summary>Ctrl+Z on an applied idea: the edit is reverted in the scene, so record it as undone.</summary>
        internal static void OnApplyUndone()
        {
            if (S.phase != StudyPhase.Refinement || S.appliedEdits.Count == 0) return;
            int edit = S.appliedEdits[S.appliedEdits.Count - 1];
            S.appliedEdits.RemoveAt(S.appliedEdits.Count - 1);
            S.undoneEdits.Add(edit);
            S.editsKept--;
            S.editsUndone++;
            S.Persist();
            string snapshot = SceneSnapshot.Write($"edit{edit}_undone");
            Log("edit_undo", $"edit={edit}; snapshot={snapshot}");
            Note($"Refinement {edit} undone.", MessageType.None);
        }

        /// <summary>Ctrl+Y on an undone idea: the edit is back in the scene.</summary>
        internal static void OnApplyRedone()
        {
            if (S.phase != StudyPhase.Refinement || S.undoneEdits.Count == 0) return;
            int edit = S.undoneEdits[S.undoneEdits.Count - 1];
            S.undoneEdits.RemoveAt(S.undoneEdits.Count - 1);
            S.appliedEdits.Add(edit);
            S.editsKept++;
            S.editsUndone--;
            S.Persist();
            string snapshot = SceneSnapshot.Write($"edit{edit}_after");
            Log("edit_redo", $"edit={edit}; snapshot={snapshot}");
            Note($"Refinement {edit} restored.", MessageType.None);
        }

        /// <summary>Any other Undo or Redo during refinement, logged so the scene history stays explainable.</summary>
        internal static void OnOtherUndo(string name, bool redo)
        {
            if (S.phase != StudyPhase.Refinement) return;
            Log(redo ? "redo_other" : "undo_other", $"name={name}");
        }

        public static void ShowIdea(int index)
        {
            var review = S.review;
            if (!IsCurrentReview(review) || index < 0 || index >= review.ideas.Count) return;
            review.selected = index;
            S.Persist();
            Note(string.Empty, MessageType.None);
        }

        public static void ApplyIdea()
        {
            var review = S.review;
            if (!IsCurrentReview(review) || IsBusy || review.ideas.Count == 0) return;
            GameObject source = GameObject.Find(GeneratedContentRoot.Name);
            var manager = review.condition == StudyCondition.SpatialControls ? UnityEngine.Object.FindFirstObjectByType<RegionSelectionManager>() : null;
            try { review.Apply(source, manager); }
            catch (System.Exception ex) { Note(ex.Message, MessageType.Warning); return; }

            var jobId = review.ideas[review.selected].jobId;
            S.review = null;
            S.editsKept++;
            S.appliedEdits.Add(S.editsSubmitted);
            S.undoneEdits.Clear();      // a new Undo step drops Unity's redo history too
            S.editRequest = string.Empty;
            S.Persist();
            string after = SceneSnapshot.Write($"edit{S.editsSubmitted}_after");
            Log("edit_keep", $"review={review.id}; idea={review.selected + 1}; job={jobId}; snapshot={after}");
            Note("Refinement applied.", MessageType.Info);
        }

        public static void DiscardIdeas()
        {
            if (!S.HasReview || IsBusy) return;
            Log("preview_discard", $"review={S.review.id}; ideas={S.review.ideas.Count}");
            S.DiscardReview();
            S.Persist();
            Note("Previews discarded.", MessageType.None);
        }

        public static void SubmitScene()
        {
            if (S.phase != StudyPhase.Refinement)
                return;
            string message = S.HasReview ? "Submit the current scene and discard these refinement previews?" :
                "Finish this condition now? You cannot change the scene afterwards.";
            if (!Confirm("Submit scene", message,
                    "Submit", "Keep working"))
                return;

            if (S.HasReview)
            {
                Log("preview_discard", $"review={S.review.id}; reason=submitted");
                S.DiscardReview();
                if (AgentWorkflow.IsBusy) AgentWorkflow.Cancel();
            }
            S.phase = StudyPhase.Finished;
            S.finishReason = "submitted";
            S.finishedUtc = StudySession.NowUtc();
            S.Persist();

            string snapshot = SceneSnapshot.Write("final");
            Undo.ClearAll();            // the submitted scene can no longer be changed with Ctrl+Z
            Log("condition_finish", $"reason={S.finishReason}; refinement_seconds={StudySession.SecondsSince(S.refinementStartUtc):0.0}; " +
                                    $"edits={S.editsSubmitted}; kept={S.editsKept}; undone={S.editsUndone}; discarded={S.editsDiscarded}; " +
                                    $"failed={S.editsFailed}; " +
                                    $"snapshot={snapshot}");
            Note("Scene submitted. This condition is finished.", MessageType.Info);

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
                EditorSceneManager.MarkSceneDirty(scene);
        }

        // ------------------------------------------------------------------ going back a step

        /// <summary>
        /// The step "Back" returns to, or null. Setup has none. Generation can return to setup only
        /// before a run is used: starting the condition again would reset counted runs.
        /// </summary>
        public static StudyPhase? PreviousStep => S.phase switch
        {
            StudyPhase.Generation => S.generationRuns == 0 ? StudyPhase.Setup : (StudyPhase?)null,
            StudyPhase.Refinement => StudyPhase.Generation,
            StudyPhase.Finished => StudyPhase.Refinement,
            _ => (StudyPhase?)null
        };

        public static bool CanStepBack => PreviousStep != null && !IsBusy;

        /// <summary>Why Back is unavailable, for the button's tooltip; null when it is available.</summary>
        public static string StepBackBlockedReason =>
            IsBusy ? "Wait until the agent has finished." :
            S.phase == StudyPhase.Generation && S.generationRuns > 0
                ? "Generation runs have been used, so the condition cannot return to setup. " +
                  "A researcher can reset it in Researcher Tools."
                : PreviousStep == null ? "This is the first step." : null;

        /// <summary>
        /// Returns to the previous step after a confirmation. Refinement → generation un-accepts the
        /// version (applied refinements stay in it; open previews are discarded); finished →
        /// refinement reopens the submitted scene; generation → setup is only possible before any run.
        /// The analysis always reads the latest accepted and final snapshots, so accepting or
        /// submitting again supersedes the earlier ones. Logged and snapshotted outside developer mode.
        /// </summary>
        public static void StepBack()
        {
            if (!CanStepBack)
                return;

            StudyPhase from = S.phase, to = PreviousStep.Value;
            string question = to switch
            {
                StudyPhase.Setup => "Return to setup? No generation run has been used yet.",
                StudyPhase.Generation => $"Return to generation? Run {S.activeRun} keeps the refinements applied so far; " +
                                         "you can generate again, pick another version, or accept it again." +
                                         (S.HasReview ? " The open previews are discarded." : string.Empty),
                _ => "Reopen the submitted scene for refinement? Submit it again when you are done."
            };
            if (!Confirm("Go back a step", question, "Go back", "Stay"))
                return;

            switch (to)
            {
                case StudyPhase.Setup:
                    S.conditionStartUtc = string.Empty;
                    break;
                case StudyPhase.Generation:
                    if (S.HasReview)
                    {
                        Log("preview_discard", $"review={S.review.id}; ideas={S.review.ideas.Count}; reason=step_back");
                        S.DiscardReview();
                    }
                    S.acceptedRun = -1;
                    S.acceptedUtc = string.Empty;
                    S.refinementStartUtc = string.Empty;
                    S.editRequest = string.Empty;
                    S.appliedEdits.Clear();
                    S.undoneEdits.Clear();
                    // Ctrl+Z must not reach refinements from the abandoned acceptance.
                    Undo.ClearAll();
                    break;
                case StudyPhase.Refinement:
                    S.finishReason = string.Empty;
                    S.finishedUtc = string.Empty;
                    break;
            }

            S.phase = to;
            S.Persist();
            if (!DeveloperMode)
            {
                string snapshot = SceneSnapshot.Write($"back_to_{to.ToString().ToLowerInvariant()}");
                Log("step_back", $"from={from}; to={to}; runs={S.generationRuns}; active_run={S.activeRun}; " +
                                 $"edits={S.editsSubmitted}; kept={S.editsKept}; snapshot={snapshot}");
            }
            Note($"Back to {PhaseName(to)}.", MessageType.Info);
        }

        public static string PhaseName(StudyPhase phase) => phase switch
        {
            StudyPhase.Setup => "setup",
            StudyPhase.Generation => "generation",
            StudyPhase.Refinement => "refinement",
            _ => "finished"
        };

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// The scene's region-selection rig. The agent backend needs only the selection manager; a rig
        /// left by the legacy router pipeline (with its RefinementController) is reused as it is.
        /// </summary>
        public static RegionSelectionManager EnsureRefinementRig(bool resetSelection)
        {
            RegionSelectionManager manager = UnityEngine.Object.FindFirstObjectByType<RegionSelectionManager>();
            if (manager == null)
            {
                var root = new GameObject(RefinementRigName);
                Undo.RegisterCreatedObjectUndo(root, "Create Refinement Rig");
                manager = root.AddComponent<RegionSelectionManager>();
                resetSelection = true;
            }

            if (resetSelection)
            {
                if (Settings.backendKind == BackendKind.Agent) manager.SetSelection(null);
                else manager.ResetToDefault();
            }

            EditorUtility.SetDirty(manager.gameObject);
            return manager;
        }

        public const string RefinementRigName = "SpatialGenerationRefinement";

        // async void would lose exceptions thrown after the first await (e.g. a snapshot that cannot be written).
        private static async void Forget(Task task, string what)
        {
            try { await task; }
            catch (Exception ex)
            {
                Debug.LogError($"Spatial Generation: {what} failed.\n{ex}");
                Note($"{what} failed: {ex.Message}", MessageType.Error);
            }
        }

        private static string ReportFields(AgentRunReport r) =>
            $"job={r.jobId}; success={r.success}; outcome={r.outcome}; changed={r.changedScene}; seconds={r.seconds:0.0}; " +
            $"added={r.partsAdded}; updated={r.partsUpdated}; removed={r.partsRemoved}; assets_created={r.assetsCreated}; " +
            $"tool_calls={r.toolCalls}; rejected={r.rejectedCalls}; turns={r.turns}; exit={r.exitCode}; " +
            $"timed_out={r.timedOut}; cancelled={r.cancelled}; artifacts={r.artifactDir}; summary={r.summary}";

        private static void Log(string type, string detail)
        {
            InteractionLogger.Log(new InteractionEvent
            {
                type = "study_" + type,
                extra = $"{S.LogContext}; dev_mode={DeveloperMode}; {detail}"
            });
        }

        private static void Note(string message, MessageType type)
        {
            LastMessage = message ?? string.Empty;
            LastMessageType = type;
        }
    }
}
