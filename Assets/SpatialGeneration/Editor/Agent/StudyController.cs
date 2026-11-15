using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

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

        public static string LastMessage { get; private set; } = string.Empty;
        public static MessageType LastMessageType { get; private set; } = MessageType.None;

        // ------------------------------------------------------------------ setup

        public static void StartCondition()
        {
            if (AgentWorkflow.IsBusy)
                return;

            bool hasContent = GameObject.Find(GenerationRunner.GeneratedRootName) != null ||
                              GameObject.Find(GenerationVersions.ContainerName) != null;
            if (hasContent && !EditorUtility.DisplayDialog("Start condition",
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
            if (AgentWorkflow.IsBusy)
                return;
            Log("session_reset", string.Empty);
            S.ResetProgress();
            S.Persist();
            Note(string.Empty, MessageType.None);
        }

        // ------------------------------------------------------------------ generation

        private static bool _starting;

        public static bool CanGenerate => S.phase == StudyPhase.Generation && S.generationRuns < MaxRuns &&
                                          !AgentWorkflow.IsBusy && !_starting;

        public static async void RunGeneration()
        {
            if (!CanGenerate)
                return;

            _starting = true;
            string problem;
            try
            {
                problem = AgentWorkflow.ValidateGenerationInputs(S.condition, S.scenePrompt)
                          ?? await AgentWorkflow.CheckSetupAsync();
            }
            finally
            {
                _starting = false;
            }

            if (problem != null)
            {
                Note(problem, MessageType.Warning);
                return;
            }

            int run = S.generationRuns + 1;
            if (S.activeRun > 0)
            {
                GenerationVersions.ArchiveActive(S.activeRun);
                if (!S.archivedRuns.Contains(S.activeRun))
                    S.archivedRuns.Add(S.activeRun);
            }

            S.generationRuns = run;
            S.activeRun = run;
            S.Persist();
            Log("generation_run_start", $"run={run}; scene_prompt={S.scenePrompt}; style_prompt={S.stylePrompt}");
            Note($"Generation run {run} of {MaxRuns} started.", MessageType.None);

            AgentRunReport report = await AgentWorkflow.RunGenerationAsync(S.condition, S.scenePrompt, S.stylePrompt);

            string snapshot = SceneSnapshot.Write($"run{run}");
            Log("generation_run_end", $"run={run}; {ReportFields(report)}; snapshot={snapshot}");
            Note($"Run {run}: {report.message}", report.success ? MessageType.Info : MessageType.Warning);
        }

        public static void ShowVersion(int run)
        {
            if (S.phase != StudyPhase.Generation || AgentWorkflow.IsBusy || run == S.activeRun)
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

        public static bool CanAccept => S.phase == StudyPhase.Generation && S.activeRun > 0 && !AgentWorkflow.IsBusy;

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

            Log("version_accept", $"run={S.acceptedRun}; runs_used={S.generationRuns}; " +
                                  $"generation_seconds={generationSeconds:0.0}; snapshot={snapshot}");
            Note("Refinement started.", MessageType.Info);
        }

        // ------------------------------------------------------------------ refinement

        public static bool CanEdit => S.phase == StudyPhase.Refinement && !S.awaitingDecision && !S.HasReview && !AgentWorkflow.IsBusy;

        public static void SubmitEdit()
        {
            if (!CanEdit)
                return;

            RegionSelection region = null;
            if (S.condition == StudyCondition.SpatialControls)
            {
                RefinementController rig = EnsureRefinementRig(resetSelection: false);
                region = rig.selectionManager != null ? rig.selectionManager.CurrentSelection?.Clone() : null;
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
                S.review = RefinementReview.Begin(GameObject.Find(GenerationRunner.GeneratedRootName), request,
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

        public static async void GenerateIdea()
        {
            var review = S.review;
            if (!IsCurrentReview(review) || AgentWorkflow.IsBusy || AgentJobState.instance.HasRunningJob) return;
            Note("Generating an idea…", MessageType.None);
            AgentRunReport report = await AgentWorkflow.RunPreviewAsync(review);
            if (!IsCurrentReview(review)) return;
            Log("preview_result", $"review={review.id}; idea={review.ideas.Count + 1}; {ReportFields(report)}");
            if (report.success)
            {
                review.ideas.Add(report.preview);
                review.selected = review.ideas.Count - 1;
            }
            S.Persist();
            Note(report.message, report.success ? MessageType.None : MessageType.Warning);
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
            if (!IsCurrentReview(review) || AgentWorkflow.IsBusy || review.ideas.Count == 0) return;
            GameObject source = GameObject.Find(GenerationRunner.GeneratedRootName);
            var manager = review.condition == StudyCondition.SpatialControls ? Object.FindFirstObjectByType<RegionSelectionManager>() : null;
            try { review.Apply(source, manager); }
            catch (System.Exception ex) { Note(ex.Message, MessageType.Warning); return; }

            var jobId = review.ideas[review.selected].jobId;
            S.review = null;
            S.editsKept++;
            S.editRequest = string.Empty;
            S.Persist();
            string after = SceneSnapshot.Write($"edit{S.editsSubmitted}_after");
            Log("edit_keep", $"review={review.id}; idea={review.selected + 1}; job={jobId}; snapshot={after}");
            Note("Refinement applied.", MessageType.Info);
        }

        public static void DiscardIdeas()
        {
            if (!S.HasReview || AgentWorkflow.IsBusy) return;
            Log("preview_discard", $"review={S.review.id}; ideas={S.review.ideas.Count}");
            S.DiscardReview();
            S.Persist();
            Note("Previews discarded.", MessageType.None);
        }

        public static void KeepPending()
        {
            if (!S.awaitingDecision)
                return;
            S.awaitingDecision = false;
            S.editsKept++;
            S.Persist();
            RefreshSelection();
            Log("edit_keep", $"job={S.pendingJobId}");
            Note("Change kept.", MessageType.None);
        }

        private static void RefreshSelection()
        {
            if (S.condition == StudyCondition.SpatialControls)
            {
                var manager = Object.FindFirstObjectByType<RegionSelectionManager>();
                var before = manager != null ? manager.CurrentSelection : null;
                if (before?.partScoped == true)
                {
                    AgentAsset owner = AgentSceneOps.AllAssets().Find(a => a.assetId == before.assetId);
                    manager.SetSelection(RefinementPartSelection.AfterEdit(before, owner));
                    EditorUtility.SetDirty(manager);
                    SceneView.RepaintAll();
                }
            }
        }

        public static void UndoPending()
        {
            if (!S.awaitingDecision)
                return;
            if (S.pendingUndoGroup >= 0)
                Undo.RevertAllDownToGroup(S.pendingUndoGroup);
            S.awaitingDecision = false;
            S.editsUndone++;
            S.Persist();
            string snapshot = SceneSnapshot.Write($"edit{S.editsSubmitted}_undone");
            Log("edit_undo", $"job={S.pendingJobId}; snapshot={snapshot}");
            Note("Change undone.", MessageType.None);
        }

        public static void SubmitScene()
        {
            if (S.phase != StudyPhase.Refinement)
                return;
            string message = S.HasReview ? "Submit the current scene and discard these refinement previews?" :
                "Finish this condition now? You cannot change the scene afterwards.";
            if (!EditorUtility.DisplayDialog("Submit scene", message,
                    "Submit", "Keep working"))
                return;

            if (S.HasReview)
            {
                Log("preview_discard", $"review={S.review.id}; reason=submitted");
                S.DiscardReview();
                if (AgentWorkflow.IsBusy) AgentWorkflow.Cancel();
            }
            // Before previews, pending edits were already in the live scene.
            if (S.awaitingDecision) KeepPending();

            S.phase = StudyPhase.Finished;
            S.finishReason = "submitted";
            S.finishedUtc = StudySession.NowUtc();
            S.Persist();

            string snapshot = SceneSnapshot.Write("final");
            Log("condition_finish", $"reason={S.finishReason}; refinement_seconds={StudySession.SecondsSince(S.refinementStartUtc):0.0}; " +
                                    $"edits={S.editsSubmitted}; kept={S.editsKept}; undone={S.editsUndone}; failed={S.editsFailed}; " +
                                    $"snapshot={snapshot}");
            Note("Scene submitted. This condition is finished.", MessageType.Info);

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
                EditorSceneManager.MarkSceneDirty(scene);
        }

        // ------------------------------------------------------------------ helpers

        public static RefinementController EnsureRefinementRig(bool resetSelection)
        {
            RefinementController controller = Object.FindFirstObjectByType<RefinementController>();
            if (controller == null)
            {
                var root = new GameObject("SpatialGenerationRefinement");
                Undo.RegisterCreatedObjectUndo(root, "Create Refinement Rig");
                RegionSelectionManager manager = root.AddComponent<RegionSelectionManager>();
                controller = root.AddComponent<RefinementController>();
                controller.selectionManager = manager;
                resetSelection = true;
            }

            if (controller.selectionManager == null)
            {
                // Explicit null tests: in the editor GetComponent returns a "fake null" that ?? misses.
                RegionSelectionManager manager = controller.GetComponent<RegionSelectionManager>();
                if (manager == null)
                    manager = controller.gameObject.AddComponent<RegionSelectionManager>();
                controller.selectionManager = manager;
                resetSelection = true;
            }

            if (resetSelection)
            {
                if (Settings.backendKind == BackendKind.Agent) controller.selectionManager.SetSelection(null);
                else controller.selectionManager.ResetToDefault();
            }

            EditorUtility.SetDirty(controller.gameObject);
            return controller;
        }

        private static string ReportFields(AgentRunReport r) =>
            $"job={r.jobId}; success={r.success}; changed={r.changedScene}; seconds={r.seconds:0.0}; " +
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
