// Proof-run harness (scratch copy only). Drives the real StudyController / AgentWorkflow /
// AgentProcessRunner path with a stub CLI, and plays the agent by calling the same
// AgentSceneOps entry points the MCP tool wrappers call.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using SpatialGeneration.Agent;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class ProofRunWorkflowTests
{
    private static readonly string Scratch = Environment.GetEnvironmentVariable("PROOF_SCRATCH");
    private static string Go => Path.Combine(Scratch, "agent_go");
    private static readonly List<string> Lines = new();
    private HttpListener _http;

    private struct Obj { public string id, label; public float x, z, y0, d, h, w, dx, dz; }

    // analysis/targets/target_X.json (Market square), size = [depth along front, height, width].
    private static readonly Obj[] TargetX =
    {
        new() { id = "well", label = "well", x = 0f, z = 0f, y0 = 0f, d = 2f, h = 3f, w = 2f, dx = 1, dz = 0 },
        new() { id = "stall_a", label = "left stall", x = -3.38f, z = 0.91f, d = 1.5f, h = 2.6f, w = 2.4f, dx = 0.9656f, dz = -0.26f },
        new() { id = "stall_b", label = "right stall", x = 3.38f, z = 0.91f, d = 1.5f, h = 2.6f, w = 2.4f, dx = -0.9656f, dz = -0.26f },
        new() { id = "crates", label = "crates", x = -3.7217f, z = -0.5514f, d = 1.22f, h = 1.1f, w = 0.6f, dx = 0.9656f, dz = -0.26f },
        new() { id = "sack", label = "sack", x = -3.4223f, z = -0.632f, y0 = 0.6f, d = 0.4f, h = 0.47f, w = 0.4f, dx = 1, dz = 0 },
        new() { id = "cart", label = "cart", x = -5.0215f, z = 1.352f, d = 2.6f, h = 1.0f, w = 1.1f, dx = 0.26f, dz = 0.9656f },
        new() { id = "parasol", label = "parasol", x = -5.0215f, z = 1.352f, d = 1.5f, h = 3.2f, w = 1.5f, dx = 1, dz = 0 },
        new() { id = "barrel", label = "barrel", x = 3.38f, z = -0.95f, d = 0.44f, h = 0.5f, w = 0.44f, dx = 1, dz = 0 },
        new() { id = "bench", label = "bench", x = 0f, z = -2.5f, d = 0.48f, h = 0.9f, w = 1.5f, dx = 0, dz = 1 },
        new() { id = "gate", label = "gate", x = 0f, z = 6f, d = 0.64f, h = 3f, w = 3.1f, dx = 0, dz = -1 },
        new() { id = "signpost", label = "signpost", x = 2.5f, z = 5.45f, d = 0.2f, h = 2.4f, w = 0.2f, dx = 1, dz = 0 },
    };

    private static float Yaw(Obj o) => Mathf.Atan2(-o.dz, o.dx) * Mathf.Rad2Deg;

    private static void Note(string line)
    {
        Lines.Add(line);
        Debug.Log("[proof] " + line);
        File.WriteAllLines(Path.Combine(Scratch, "proof_report.txt"), Lines);
    }

    private static void Op(string what, OpResult r) =>
        Note($"  tool {what}: ok={r.ok} msg=\"{r.message}\"" +
             (r.data is Dictionary<string, object> d && d.TryGetValue("rejected", out object rej) && rej is List<object> l && l.Count > 0
                 ? " rejected=" + string.Join("|", l.Select(x => x is Dictionary<string, object> dd && dd.TryGetValue("reason", out object rr) ? rr : x))
                 : string.Empty));

    [OneTimeSetUp]
    public void Start()
    {
        Assert.IsFalse(string.IsNullOrEmpty(Scratch), "PROOF_SCRATCH not set");
        _http = new HttpListener();
        _http.Prefixes.Add("http://127.0.0.1:18765/");
        _http.Start();
        Task.Run(() =>
        {
            while (_http.IsListening)
            {
                try { var c = _http.GetContext(); c.Response.StatusCode = 200; c.Response.Close(); }
                catch { break; }
            }
        });

        StudyController.Confirm = (_, _, _, _) => true;
        BackendSettings s = BackendRegistry.Settings;
        s.backendKind = BackendKind.Agent;
        s.agentCli = AgentCli.ClaudeCode;
        s.claudeExecutable = Path.Combine(Scratch, "fake_claude.sh");
        s.mcpServerUrl = "http://127.0.0.1:18765/mcp";
        s.agentTimeoutSeconds = 120;
        s.maxGenerationRuns = 3;
        Note($"settings: backend={s.backendKind} cli={s.agentCli} model={s.agentModel} devMode={StudyController.DeveloperMode}");
    }

    [OneTimeTearDown]
    public void Stop() => _http?.Stop();

    private static IEnumerator WaitUntil(Func<bool> condition, double seconds, string what)
    {
        double start = EditorApplication.timeSinceStartup;
        while (!condition())
        {
            if (EditorApplication.timeSinceStartup - start > seconds)
                Assert.Fail("Timed out waiting for " + what);
            yield return null;
        }
    }

    /// <summary>Waits for the CLI to be running, plays the agent, then lets the stub exit.</summary>
    private static IEnumerator Agent(string label, Action<AgentJob> body)
    {
        yield return WaitUntil(() => AgentJobState.instance.HasRunningJob && AgentProcessRunner.IsRunning, 30, label + " start");
        AgentJob job = AgentJobState.instance.current;
        Note($"[{label}] job {job.jobId} mode={job.mode} cond={job.condition} hasRegion={job.hasRegion} partScope={job.HasPartScope}");
        try { body(job); }
        catch (Exception ex) { Note($"  !! agent body threw: {ex}"); }
        File.WriteAllText(Go, "go");
        yield return WaitUntil(() => !AgentWorkflow.IsBusy, 60, label + " finish");
        yield return null;
        yield return null;
        AgentRunReport r = AgentWorkflow.LastReport;
        Note($"  report: success={r.success} added={r.partsAdded} updated={r.partsUpdated} removed={r.partsRemoved} " +
             $"calls={r.toolCalls} rejected={r.rejectedCalls} exit={r.exitCode} msg=\"{r.message}\" | study msg=\"{StudyController.LastMessage}\"");
    }

    private static AgentAsset Asset(string label) =>
        AgentSceneOps.AllAssets().FirstOrDefault(a => a.label == label);

    private static List<PartSpec> Fill(Obj o, string color)
    {
        if (o.id == "crates")
            return new List<PartSpec>
            {
                new() { name = "crate_low", shape = "box", center = new[] { 0f, 0.3f, 0f }, size = new[] { 1.22f, 0.6f, 0.6f }, color = color },
                new() { name = "crate_top", shape = "box", center = new[] { -0.33f, 0.85f, 0f }, size = new[] { 0.5f, 0.5f, 0.5f }, color = color },
            };
        return new List<PartSpec>
        {
            new() { name = o.id + "_body", shape = "box", center = new[] { 0f, o.h / 2f, 0f }, size = new[] { o.d, o.h, o.w }, color = color }
        };
    }

    [UnityTest, Timeout(600000)]
    public IEnumerator ConditionB_FullStudyFlow()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        StudySession S = StudySession.instance;
        S.participantId = "P01";
        S.targetScene = "X";
        S.condition = StudyCondition.SpatialControls;
        S.ResetProgress();
        S.Persist();
        Note("=== CONDITION B ===");

        StudyController.StartCondition();
        Note($"phase after StartCondition: {S.phase}");

        // Designer authors Occupy proxies. stall_b is a duplicate of stall_a (Ctrl+D workflow).
        var proxies = new Dictionary<string, SpatialProxy>();
        foreach (Obj o in TargetX)
        {
            GameObject go;
            if (o.id == "stall_b")
            {
                go = UnityEngine.Object.Instantiate(proxies["stall_a"].gameObject);
                go.name = "Spatial Proxy (1)";
            }
            else
            {
                SpatialProxyFactory.CreateProxy();
                go = Selection.activeGameObject;
            }
            var p = go.GetComponent<SpatialProxy>();
            p.label = o.label;
            p.assetPrompt = "low poly " + o.label;
            p.size = new Vector3(o.d, o.h, o.w);
            go.transform.SetPositionAndRotation(new Vector3(o.x, o.y0 + o.h / 2f, o.z), Quaternion.Euler(0, Yaw(o), 0));
            proxies[o.id] = p;
        }
        var ids = proxies.Values.Select(p => p.ProxyId).ToList();
        Note($"proxies: {ids.Count}, distinct ids: {ids.Distinct().Count()}, empty ids: {ids.Count(string.IsNullOrEmpty)}; " +
             $"stall_a id={proxies["stall_a"].ProxyId} stall_b id={proxies["stall_b"].ProxyId}");

        // ---- Run 1: build every proxy asset.
        StudyController.RunGeneration();
        yield return Agent("run1", job =>
        {
            Op("get_task", AgentSceneOps.GetTask());
            Op("create_asset (expect reject in B)", AgentSceneOps.CreateAsset("extra", "", new[] { 0f, 0, 0 }, 0, new[] { 1f, 1, 1 }));
            foreach (Obj o in TargetX)
            {
                AgentAsset a = AgentSceneOps.AllAssets(job).FirstOrDefault(x => x.label == o.label);
                if (a == null) { Note($"  !! no prepared asset for {o.label}"); continue; }
                OpResult r = AgentSceneOps.AddParts(a.assetId, Fill(o, "#AA7744"));
                if (!r.ok) Op("add_parts " + o.label, r);
            }
            // A part poking out of the volume: soft warning only.
            AgentAsset well = AgentSceneOps.AllAssets(job).First(x => x.label == "well");
            OpResult warn = AgentSceneOps.AddParts(well.assetId, new List<PartSpec>
                { new() { name = "crank", shape = "cylinder", center = new[] { 0f, 3.4f, 0f }, size = new[] { 0.1f, 0.6f, 0.1f } } });
            Note($"  out-of-volume part: ok={warn.ok} warnings={((List<string>)((Dictionary<string, object>)warn.data)["warnings"]).Count}");
            Op("finish", AgentSceneOps.Finish("Built eleven market objects."));
        });
        Note($"after run1: runs={S.generationRuns} active={S.activeRun} assets={AgentSceneOps.AllAssets().Count}");

        // ---- Run 2: the agent fails part-way (only builds 3 assets).
        StudyController.RunGeneration();
        yield return Agent("run2", job =>
        {
            foreach (Obj o in TargetX.Take(3))
            {
                AgentAsset a = AgentSceneOps.AllAssets(job).First(x => x.label == o.label);
                AgentSceneOps.AddParts(a.assetId, Fill(o, "#4477AA"));
            }
        });
        Note($"after run2: runs={S.generationRuns} active={S.activeRun} archived=[{string.Join(",", S.archivedRuns)}]");

        StudyController.ShowVersion(1);
        Note($"ShowVersion(1): active={S.activeRun} archived=[{string.Join(",", S.archivedRuns)}] assets={AgentSceneOps.AllAssets().Count} " +
             $"partsInWell={AgentSceneOps.PartsOf(Asset("well")).Count}");

        StudyController.AcceptActiveVersion();
        Note($"accept: phase={S.phase} acceptedRun={S.acceptedRun}");
        RefinementController rig = StudyController.EnsureRefinementRig(false);

        // ---- Edit 1: click the well body (whole part), two ideas, apply the first.
        AgentAsset wellAsset = Asset("well");
        AgentPart wellBody = AgentSceneOps.PartsOf(wellAsset).First(p => p.name.StartsWith("well_body"));
        RefinementPartSelection.Commit(rig.selectionManager, RefinementPartSelection.Pick(null, wellAsset, wellBody, false), "pick");
        S.editRequest = "make the well taller";
        StudyController.SubmitEdit();
        Note($"edit1 submit: hasReview={S.HasReview} msg=\"{StudyController.LastMessage}\"");
        yield return Agent("edit1-idea1", job =>
        {
            Op("list_parts well", AgentSceneOps.ListParts(wellAsset.assetId));
            Op("update well body", AgentSceneOps.UpdateParts(wellAsset.assetId, new List<PartSpec>
                { new() { id = wellBody.partId, size = new[] { 2f, 3.6f, 2f }, center = new[] { 0f, 1.8f, 0f } } }));
            AgentAsset gate = AgentSceneOps.AllAssets(job).First(x => x.label == "gate");
            Op("update gate (expect reject)", AgentSceneOps.UpdateParts(gate.assetId, new List<PartSpec>
                { new() { id = AgentSceneOps.PartsOf(gate)[0].partId, color = "#FF0000" } }));
            Note($"  live well body during preview: scaleY={wellBody.transform.localScale.y:0.##} (expect 3, untouched)");
        });
        StudyController.GenerateIdea();
        yield return Agent("edit1-idea2", job =>
        {
            AgentAsset staged = AgentSceneOps.AllAssets(job).First(x => x.label == "well");
            AgentPart body = AgentSceneOps.PartsOf(staged).First(p => p.partId == wellBody.partId);
            Op("recolour well body", AgentSceneOps.UpdateParts(staged.assetId, new List<PartSpec> { new() { id = body.partId, color = "#3366CC" } }));
        });
        Note($"edit1 ideas={S.review?.ideas.Count}");
        StudyController.ShowIdea(0);
        StudyController.ApplyIdea();
        Note($"edit1 apply: msg=\"{StudyController.LastMessage}\" live well scaleY={wellBody.transform.localScale.y:0.##} kept={S.editsKept}");

        // ---- Edit 2: drag a box over the left half of the bench (partial part), remove it.
        AgentAsset bench = Asset("bench");
        AgentPart benchBody = AgentSceneOps.PartsOf(bench)[0];
        Obj b = TargetX.First(o => o.id == "bench");
        Transform bt = bench.transform;
        var drawn = new RegionSelection
        {
            center = bt.position + Vector3.up * (b.h / 2f) + bt.forward * (b.w / 4f),
            size = new Vector3(b.d + 0.2f, b.h + 0.2f, b.w / 2f),
            rotation = bt.rotation
        };
        RegionSelection sel = RefinementPartSelection.Draw(drawn, bench, false);
        Note($"edit2 selection: whole={sel.wholeParts.Count} partial={sel.partialParts.Count}");
        RefinementPartSelection.Commit(rig.selectionManager, sel, "draw");
        S.editRequest = "remove the left half of the bench";
        StudyController.SubmitEdit();
        yield return Agent("edit2", job =>
        {
            AgentAsset staged = AgentSceneOps.AllAssets(job).First(x => x.label == "bench");
            Op("list_parts bench", AgentSceneOps.ListParts(staged.assetId));
            Op("add part outside region (expect reject)", AgentSceneOps.AddParts(staged.assetId, new List<PartSpec>
                { new() { name = "far", shape = "box", center = new[] { 0f, 0.4f, -0.6f }, size = new[] { 0.3f, 0.3f, 0.3f } } }));
            Op("remove selected half", AgentSceneOps.RemoveParts(staged.assetId, new List<string> { benchBody.partId }));
        });
        StudyController.ApplyIdea();
        Note($"edit2 apply: msg=\"{StudyController.LastMessage}\" bench part scale={benchBody.transform.localScale} fragments={benchBody.fragments.Count}");

        // ---- Edit 3: preview then discard.
        RefinementPartSelection.Commit(rig.selectionManager,
            RefinementPartSelection.Pick(null, Asset("barrel"), AgentSceneOps.PartsOf(Asset("barrel"))[0], false), "pick");
        S.editRequest = "make the barrel red";
        StudyController.SubmitEdit();
        yield return Agent("edit3", job =>
        {
            AgentAsset staged = AgentSceneOps.AllAssets(job).First(x => x.label == "barrel");
            AgentSceneOps.UpdateParts(staged.assetId, new List<PartSpec> { new() { id = AgentSceneOps.PartsOf(staged)[0].partId, color = "#CC2222" } });
        });
        StudyController.DiscardIdeas();
        Note($"edit3 discard: submitted={S.editsSubmitted} kept={S.editsKept} undone={S.editsUndone} discarded={S.editsDiscarded} failed={S.editsFailed}");

        // ---- Participant presses Ctrl+Z after an applied edit (outside the window's control).
        int beforeUndo = AgentSceneOps.PartsOf(Asset("bench"))[0].fragments.Count;
        Undo.PerformUndo();
        yield return null;
        AgentAsset benchAfterUndo = Asset("bench");
        Note($"Ctrl+Z after edits: bench fragments {beforeUndo} -> {(benchAfterUndo != null ? AgentSceneOps.PartsOf(benchAfterUndo)[0].fragments.Count : -1)}; " +
             $"study counters unchanged: kept={S.editsKept} undone={S.editsUndone}");
        Undo.PerformRedo();
        yield return null;

        StudyController.SubmitScene();
        Note($"submit: phase={S.phase} reason={S.finishReason}");
        LogAssert.ignoreFailingMessages = true;
    }

    [UnityTest, Timeout(600000)]
    public IEnumerator ConditionA_FullStudyFlow()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        StudySession S = StudySession.instance;
        S.participantId = "P01";
        S.targetScene = "X";
        S.condition = StudyCondition.TextOnly;
        S.ResetProgress();
        S.Persist();
        Note("=== CONDITION A ===");
        StudyController.StartCondition();

        S.scenePrompt = "";
        StudyController.RunGeneration();
        yield return null;
        Note($"empty prompt: msg=\"{StudyController.LastMessage}\" runs={S.generationRuns}");

        S.scenePrompt = "A market square with a well in the middle, two stalls facing each other, ...";
        StudyController.RunGeneration();
        yield return Agent("A-run1", job =>
        {
            foreach (Obj o in TargetX)
            {
                OpResult c = AgentSceneOps.CreateAsset(o.label, "low poly " + o.label,
                    new[] { o.x, o.y0, o.z }, Yaw(o), new[] { o.d, o.h, o.w });
                string id = (string)((Dictionary<string, object>)c.data)["asset_id"];
                AgentSceneOps.AddParts(id, Fill(o, "#887766"));
            }
            AgentSceneOps.Finish("built");
        });
        StudyController.AcceptActiveVersion();

        // Text-only edit. The docs say condition A rebuilds one whole asset; try a part-level update.
        S.editRequest = "make the well taller";
        StudyController.SubmitEdit();
        yield return Agent("A-edit1", job =>
        {
            AgentAsset well = AgentSceneOps.AllAssets(job).First(x => x.label == "well");
            Op("A: part-level update without clear", AgentSceneOps.UpdateParts(well.assetId, new List<PartSpec>
                { new() { id = AgentSceneOps.PartsOf(well)[0].partId, size = new[] { 2f, 3.6f, 2f }, center = new[] { 0f, 1.8f, 0f } } }));
            AgentAsset gate = AgentSceneOps.AllAssets(job).First(x => x.label == "gate");
            Op("A: second asset (expect reject)", AgentSceneOps.ClearAsset(gate.assetId));
        });
        StudyController.ApplyIdea();
        Note($"A edit apply: msg=\"{StudyController.LastMessage}\"");
        StudyController.SubmitScene();
        Note($"A submit: phase={S.phase}");
        LogAssert.ignoreFailingMessages = true;
    }
}
