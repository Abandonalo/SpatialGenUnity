using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SpatialGeneration.Agent
{
    [Serializable]
    public class AgentRunReport
    {
        public bool success;
        public bool changedScene;
        public string message = string.Empty;
        public string jobId = string.Empty;
        public string mode = string.Empty;
        public int undoGroup = -1;
        public double seconds;
        public int partsAdded;
        public int partsUpdated;
        public int partsRemoved;
        public int assetsCreated;
        public int toolCalls;
        public int rejectedCalls;
        public int turns = -1;
        public bool finishedByAgent;
        public bool cancelled;
        public bool timedOut;
        public bool backendError;
        [NonSerialized] public RefinementIdea preview;
        public int exitCode = -1;
        public string summary = string.Empty;
        public string artifactDir = string.Empty;
        public List<string> emptyAssets = new();
    }

    /// <summary>
    /// One agent run end to end: prepare the scene and the job, launch the CLI, wait while it
    /// works through MCP, then close the job and report what changed. Generation runs and
    /// refinement edits share this path, so both study conditions use the same backend and
    /// differ only in the spatial input they give it.
    /// </summary>
    public static class AgentWorkflow
    {
        private static bool _busy;

        public static bool IsBusy => _busy || AgentProcessRunner.IsRunning;
        public static string Status { get; private set; } = string.Empty;
        public static AgentRunReport LastReport { get; private set; }

        public static event Action Changed;

        private static BackendSettings Settings => BackendRegistry.Settings;

        // ------------------------------------------------------------------ generation

        public static async Task<AgentRunReport> RunGenerationAsync(
            StudyCondition condition, string scenePrompt, string stylePrompt)
        {
            var report = new AgentRunReport { mode = "generate" };
            if (!TryBegin(report, "Preparing generation…"))
                return report;

            try
            {
                List<SpatialProxy> proxies = null;
                if (condition == StudyCondition.SpatialControls)
                {
                    proxies = CollectOccupyProxies();
                    string problem = ValidateProxies(proxies);
                    if (problem != null)
                        return Fail(report, problem);
                }
                else if (string.IsNullOrWhiteSpace(scenePrompt))
                {
                    return Fail(report, "Describe the scene before generating.");
                }

                string setupProblem = await CheckSetupAsync();
                if (setupProblem != null)
                    return Fail(report, setupProblem);

                AgentJob job = NewJob(AgentJobMode.Generate, condition,
                    condition == StudyCondition.TextOnly ? scenePrompt.Trim() : string.Empty, stylePrompt);

                GameObject root = GenerationRunner.EnsureGeneratedRoot();
                if (proxies != null)
                {
                    foreach (SpatialProxy proxy in proxies)
                        job.assetIds.Add(PrepareProxyAsset(proxy, root.transform, job).assetId);
                }

                await RunJobAsync(job, report);

                if (proxies != null)
                {
                    foreach (AgentAsset asset in AgentSceneOps.AllAssets())
                    {
                        if (job.assetIds.Contains(asset.assetId) && AgentSceneOps.PartsOf(asset).Count == 0)
                            report.emptyAssets.Add(asset.label);
                    }
                }

                report.success = report.partsAdded > 0 && !report.cancelled;
                report.message = DescribeGeneration(report);
                return Finish(report);
            }
            catch (Exception ex)
            {
                return Fail(report, $"Generation failed: {ex.Message}", ex);
            }
        }

        // ------------------------------------------------------------------ edits

        public static async Task<AgentRunReport> RunPreviewAsync(RefinementReview review)
        {
            var report = new AgentRunReport { mode = "refinement_preview" };
            if (!TryBegin(report, "Generating refinement preview…")) return report;
            try
            {
                string setupProblem = await CheckSetupAsync();
                if (setupProblem != null) return Fail(report, setupProblem);
                // The review may have changed while setup was running.
                if (!StudyController.IsCurrentReview(review)) return Fail(report, "The preview has ended.");
                var job = NewJob(AgentJobMode.Edit, review.condition, review.request, review.style);
                job.previewToken = job.jobId;
                job.ideaNumber = review.ideas.Count + 1;
                job.previousIdeas = string.Join(" / ", review.ideas.Skip(Math.Max(0, review.ideas.Count - 3))
                    .Select(idea => idea.summary));
                if (review.condition == StudyCondition.SpatialControls)
                {
                    RegionSelection region = review.selection;
                    job.selection = region.Clone();
                    job.hasRegion = true;
                    job.regionCenter = region.center;
                    job.regionSize = region.size;
                    job.regionRotation = region.rotation;
                }
                GameObject source = GameObject.Find(GenerationRunner.GeneratedRootName);
                using (var stage = new RefinementPreviewStage(review, source, job.previewToken))
                {
                    await RunJobAsync(job, report);
                    if (report.cancelled || report.timedOut || report.backendError || report.exitCode != 0)
                    {
                        if (string.IsNullOrEmpty(report.message))
                            report.message = report.cancelled ? "Preview cancelled." : "The agent could not finish this idea. Try regenerating.";
                    }
                    else if (report.partsAdded + report.partsUpdated + report.partsRemoved + report.assetsCreated > 0)
                    {
                        var idea = review.CaptureIdea(stage.Root, job.jobId, report.summary);
                        if (idea.objects.Count > 0) report.preview = idea;
                    }
                }
                report.success = report.preview != null;
                if (report.success) report.message = $"Idea {job.ideaNumber} is ready.";
                else if (string.IsNullOrEmpty(report.message)) report.message = "The agent returned no change. Try another preview.";
                return Finish(report);
            }
            catch (Exception ex)
            {
                return Fail(report, $"Preview failed: {ex.Message}", ex);
            }
        }

        public static void Cancel()
        {
            if (!IsBusy)
                return;
            SetStatus("Stopping the agent…");
            AgentProcessRunner.Cancel();
        }

        /// <summary>Clears a job left marked as running by a domain reload or editor restart.</summary>
        public static void ClearStaleJob()
        {
            AgentJob job = AgentJobState.instance.current;
            if (job != null && job.running && !AgentProcessRunner.IsRunning)
            {
                job.running = false;
                AgentJobState.instance.Persist();
            }

            _busy = false;
            SetStatus(string.Empty);
        }

        public static async Task<string> CheckSetupAsync()
        {
            BackendSettings settings = Settings;
            if (settings.backendKind != BackendKind.Agent)
                return "Researcher settings: the backend is not set to Agent.";

            if (AgentProcessRunner.ResolveExecutable(settings, out string problem) == null)
                return problem;

            if (!AgentProcessRunner.IsValidMcpUrl(settings.mcpServerUrl))
            {
                return $"Researcher settings: the MCP server URL \"{settings.mcpServerUrl}\" is not a valid address. " +
                       $"It should look like {AgentProcessRunner.DefaultMcpUrl}.";
            }

            if (!await AgentProcessRunner.IsMcpServerReachableAsync(settings.mcpServerUrl))
            {
                return $"The MCP for Unity server is not answering at {settings.mcpServerUrl}. " +
                       "Open Window > MCP for Unity, start the server and check that Unity shows as connected.";
            }

            return null;
        }

        // ------------------------------------------------------------------ internals

        private static async Task RunJobAsync(AgentJob job, AgentRunReport report)
        {
            BackendSettings settings = Settings;
            job.artifactDir = CreateArtifactDirectory(job);
            report.jobId = job.jobId;
            report.artifactDir = job.artifactDir;

            AgentJobState.instance.current = job;
            AgentJobState.instance.Persist();

            string taskJson = AgentPromptBuilder.BuildTaskJson(job, settings.maxPartsPerAsset);
            string prompt = AgentPromptBuilder.BuildPrompt(job, taskJson);
            File.WriteAllText(Path.Combine(job.artifactDir, "task.json"), taskJson);
            File.WriteAllText(Path.Combine(job.artifactDir, "prompt.txt"), prompt);

            string cli = settings.agentCli == AgentCli.ClaudeCode ? "Claude Code" : "Codex";
            SetStatus($"{cli} is working…");

            AgentRunOutcome outcome = await AgentProcessRunner.RunAsync(settings, prompt, job.artifactDir);

            // The agent may have changed the job (counters, lock) through the tools, and a domain
            // reload would have replaced the instance; read it back from the store.
            AgentJob final = AgentJobState.instance.current ?? job;
            final.running = false;
            AgentJobState.instance.Persist();

            report.seconds = outcome.seconds;
            report.exitCode = outcome.exitCode;
            report.backendError = outcome.reportedError || !string.IsNullOrEmpty(outcome.error);
            report.timedOut = outcome.timedOut;
            report.cancelled = outcome.cancelled;
            report.turns = outcome.turns;
            report.partsAdded = final.partsAdded;
            report.partsUpdated = final.partsUpdated;
            report.partsRemoved = final.partsRemoved;
            report.assetsCreated = final.assetsCreated;
            report.toolCalls = final.toolCalls;
            report.rejectedCalls = final.rejectedCalls;
            report.finishedByAgent = final.finishedByAgent;
            report.summary = !string.IsNullOrWhiteSpace(final.summary) ? final.summary : outcome.resultText;
            report.changedScene = string.IsNullOrEmpty(job.previewToken) &&
                final.partsAdded + final.partsUpdated + final.partsRemoved + final.assetsCreated > 0;

            if (!string.IsNullOrEmpty(outcome.error))
                report.message = outcome.error;
            else if (final.toolCalls == 0 && outcome.reportedError && !string.IsNullOrWhiteSpace(outcome.resultText))
                report.message = $"{cli} stopped with an error: {outcome.resultText.Trim()}";
            else if (final.toolCalls == 0 && outcome.exitCode != 0 && !string.IsNullOrWhiteSpace(outcome.stderr))
                report.message = $"{cli} exited with code {outcome.exitCode}: {LastLine(outcome.stderr)}";
            else if (final.toolCalls == 0)
                report.message = "The agent never reached the Spatial Generation tools. Check that MCP for Unity is " +
                                 "connected and look at agent_stderr.txt in the run folder.";
        }

        private static AgentJob NewJob(AgentJobMode mode, StudyCondition condition, string request, string stylePrompt) => new()
        {
            jobId = $"{DateTime.UtcNow:HHmmss}_{AgentSceneOps.ShortId()}",
            mode = mode,
            condition = condition,
            request = request,
            stylePrompt = stylePrompt?.Trim() ?? string.Empty,
            running = true,
            startedUtc = StudySession.NowUtc()
        };

        /// <summary>Empty asset group seated on the proxy: bottom-centre origin, the proxy's rotation, its volume.</summary>
        private static AgentAsset PrepareProxyAsset(SpatialProxy proxy, Transform root, AgentJob job)
        {
            Transform t = proxy.transform;
            Vector3 worldSize = Vector3.Scale(proxy.size, t.lossyScale);
            Vector3 origin = t.position - t.up * (worldSize.y * 0.5f);

            string label = string.IsNullOrWhiteSpace(proxy.label) ? "object" : proxy.label.Trim();
            string assetId = AgentSceneOps.ShortId();
            var go = new GameObject($"Generated_Mesh_{AgentSceneOps.Sanitize(label)}_{assetId}");
            go.transform.SetParent(root, false);
            go.transform.SetPositionAndRotation(origin, t.rotation);

            AgentAsset asset = go.AddComponent<AgentAsset>();
            asset.assetId = assetId;
            asset.label = label;
            asset.prompt = string.IsNullOrWhiteSpace(proxy.assetPrompt) ? label : proxy.assetPrompt.Trim();
            asset.proxyId = proxy.ProxyId;
            asset.volumeSize = worldSize;
            asset.createdByJob = job.jobId;

            GeneratedMeshMetadata metadata = go.AddComponent<GeneratedMeshMetadata>();
            metadata.proxyId = proxy.ProxyId;
            metadata.proxyPosition = t.position;
            metadata.proxyRotation = t.rotation;
            metadata.proxySize = worldSize;

            Undo.RegisterCreatedObjectUndo(go, "Prepare proxy asset");
            return asset;
        }

        public static List<SpatialProxy> CollectOccupyProxies()
        {
            var proxies = new List<SpatialProxy>();
            foreach (SpatialProxy proxy in UnityEngine.Object.FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None))
            {
                if (proxy != null && proxy.isActiveAndEnabled && proxy.role == SpatialProxyRole.Occupy)
                    proxies.Add(proxy);
            }

            proxies.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return proxies;
        }

        /// <summary>Null when a generation run can start with these inputs, otherwise the problem.</summary>
        public static string ValidateGenerationInputs(StudyCondition condition, string scenePrompt) =>
            condition == StudyCondition.SpatialControls
                ? ValidateProxies(CollectOccupyProxies())
                : string.IsNullOrWhiteSpace(scenePrompt) ? "Describe the scene before generating." : null;

        private static string ValidateProxies(List<SpatialProxy> proxies)
        {
            if (proxies.Count == 0)
                return "Add at least one spatial proxy before generating.";

            foreach (SpatialProxy proxy in proxies)
            {
                if (string.IsNullOrWhiteSpace(proxy.assetPrompt) && string.IsNullOrWhiteSpace(proxy.label))
                    return $"Proxy '{proxy.name}' has no label or prompt. Describe what it should become.";
            }

            return null;
        }

        private static bool TryBegin(AgentRunReport report, string status)
        {
            if (IsBusy || AgentJobState.instance.HasRunningJob)
            {
                report.message = "The agent is already working. Wait for it to finish.";
                return false;
            }

            _busy = true;
            SetStatus(status);
            return true;
        }

        private static AgentRunReport Fail(AgentRunReport report, string message, Exception ex = null)
        {
            report.success = false;
            report.message = message;
            if (ex != null)
                Debug.LogError($"Spatial Generation: {message}\n{ex}");
            else
                Debug.LogWarning($"Spatial Generation: {message}");

            AgentJob job = AgentJobState.instance.current;
            if (job != null && job.running && job.jobId == report.jobId)
            {
                job.running = false;
                AgentJobState.instance.Persist();
            }

            return Finish(report);
        }

        private static AgentRunReport Finish(AgentRunReport report)
        {
            _busy = false;
            LastReport = report;
            SetStatus(string.Empty);

            if (!string.IsNullOrEmpty(report.artifactDir) && Directory.Exists(report.artifactDir))
                File.WriteAllText(Path.Combine(report.artifactDir, "report.json"), JsonUtility.ToJson(report, true));

            SceneView.RepaintAll();
            return report;
        }

        private static string DescribeGeneration(AgentRunReport r)
        {
            if (!string.IsNullOrEmpty(r.message) && r.partsAdded == 0)
                return r.message;
            if (r.cancelled)
                return "Generation was stopped.";
            if (r.timedOut)
                return $"The agent ran out of time after {r.seconds:0} s; {r.partsAdded} part(s) were built.";
            if (r.partsAdded == 0)
                return "The agent finished without building anything.";

            string text = $"Built {r.partsAdded} part(s) in {r.seconds:0} s.";
            if (r.emptyAssets.Count > 0)
                text += $" Still empty: {string.Join(", ", r.emptyAssets)}.";
            return text;
        }



        private static string CreateArtifactDirectory(AgentJob job)
        {
            string dir = Path.Combine(AgentProcessRunner.ProjectRoot, "Logs", "SpatialGeneration", "Agent",
                $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{(job.mode == AgentJobMode.Generate ? "generate" : "edit")}_{job.jobId}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string LastLine(string text)
        {
            string[] lines = text.Trim().Split('\n');
            return lines[lines.Length - 1].Trim();
        }

        private static void SetStatus(string status)
        {
            Status = status ?? string.Empty;
            Changed?.Invoke();
        }
    }
}
