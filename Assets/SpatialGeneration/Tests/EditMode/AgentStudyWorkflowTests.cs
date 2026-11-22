using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;
using SpatialGeneration.Agent;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using SpatialGeneration;
using SpatialGeneration.Authoring;
using Debug = UnityEngine.Debug;

/// <summary>
/// The study workflow end to end on the real StudyController → AgentWorkflow → AgentProcessRunner
/// path. The CLI is a stub script and the MCP endpoint a stub HTTP listener; while the stub runs,
/// the test plays the agent by calling the AgentSceneOps entry points the MCP tools call.
/// Records go to a temporary folder, and the session, job, settings and dialog seam are restored.
/// </summary>
public sealed class AgentStudyWorkflowTests
{
    private string _temp, _goFile, _sessionJson, _jobJson, _settingsJson;
    private Func<string, string, string, string, bool> _confirm;
    private HttpListener _http;
    /// <summary>Delay of the stub MCP endpoint, to hold a run in its setup check.</summary>
    private static volatile int _mcpDelayMs;

    // ------------------------------------------------------------------ fixture

    [SetUp]
    public void SetUp()
    {
        if (Application.platform == RuntimePlatform.WindowsEditor)
            Assert.Ignore("The stub agent CLI is a shell script.");
        if (AgentWorkflow.IsBusy || AgentJobState.instance.HasRunningJob)
            Assert.Ignore("An agent run is active in this editor.");

        _sessionJson = EditorJsonUtility.ToJson(StudySession.instance);
        _jobJson = EditorJsonUtility.ToJson(AgentJobState.instance);
        _settingsJson = EditorJsonUtility.ToJson(BackendRegistry.Settings);
        _confirm = StudyController.Confirm;
        StudyController.Confirm = (_, _, _, _) => true;

        _temp = Path.GetFullPath(FileUtil.GetUniqueTempPathInProject());
        Directory.CreateDirectory(_temp);
        StudyPaths.Redirect(Path.Combine(_temp, "Logs"));

        _goFile = Path.Combine(_temp, "agent_go");
        string cli = Path.Combine(_temp, "stub_agent.sh");
        File.WriteAllText(cli,
            "#!/bin/bash\n" +
            $"cat > \"{_temp}/last_prompt.txt\"\n" +
            $"pwd > \"{_temp}/last_cwd.txt\"; ls -A > \"{_temp}/last_cwd_listing.txt\"\n" +
            $"for i in $(seq 1 600); do [ -f \"{_goFile}\" ] && break; sleep 0.05; done\n" +
            $"code=$(cat \"{_goFile}\" 2>/dev/null); rm -f \"{_goFile}\"\n" +
            "echo '{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"stub\",\"num_turns\":1}'\n" +
            "exit \"${code:-0}\"\n");
        Process.Start(new ProcessStartInfo("/bin/chmod", $"+x \"{cli}\"") { UseShellExecute = false })?.WaitForExit();

        int port = FreePort();
        _http = new HttpListener();
        _http.Prefixes.Add($"http://127.0.0.1:{port}/");
        _http.Start();
        HttpListener listener = _http;
        Task.Run(() =>
        {
            while (listener.IsListening)
            {
                try
                {
                    HttpListenerContext c = listener.GetContext();
                    if (_mcpDelayMs > 0) System.Threading.Thread.Sleep(_mcpDelayMs);
                    c.Response.StatusCode = 200;
                    c.Response.Close();
                }
                catch (Exception) { return; }
            }
        });

        BackendSettings s = BackendRegistry.Settings;
        s.backendKind = BackendKind.Agent;
        s.agentCli = AgentCli.ClaudeCode;
        s.claudeExecutable = cli;
        s.mcpServerUrl = $"http://127.0.0.1:{port}/mcp";
        s.agentTimeoutSeconds = 60;
        s.maxGenerationRuns = 3;

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        StudySession.instance.ResetProgress();
        StudySession.instance.participantId = "workflow_test";
        StudySession.instance.targetScene = "X";
    }

    [TearDown]
    public void TearDown()
    {
        _mcpDelayMs = 0;
        AgentProcessRunner.Cancel();
        _http?.Stop();
        _http = null;
        StudyController.Confirm = _confirm;
        if (_settingsJson != null) EditorJsonUtility.FromJsonOverwrite(_settingsJson, BackendRegistry.Settings);
        if (_sessionJson != null) { EditorJsonUtility.FromJsonOverwrite(_sessionJson, StudySession.instance); StudySession.instance.Persist(); }
        if (_jobJson != null) { EditorJsonUtility.FromJsonOverwrite(_jobJson, AgentJobState.instance); AgentJobState.instance.Persist(); }
        StudyPaths.Redirect(null);
        AgentWorkflow.ClearStaleJob();
        Undo.ClearAll();
        try { if (_temp != null && Directory.Exists(_temp)) Directory.Delete(_temp, true); }
        catch (IOException) { }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static IEnumerator WaitUntil(Func<bool> condition, string what, double seconds = 30)
    {
        double start = EditorApplication.timeSinceStartup;
        while (!condition())
        {
            if (EditorApplication.timeSinceStartup - start > seconds) Assert.Fail("Timed out waiting for " + what);
            yield return null;
        }
    }

    /// <summary>Waits for the stub CLI, acts as the agent, then lets the CLI exit and the run finish.</summary>
    private IEnumerator Agent(Action<AgentJob> body, int exitCode = 0)
    {
        yield return WaitUntil(() => AgentJobState.instance.HasRunningJob && AgentProcessRunner.IsRunning, "the agent to start");
        try { body(AgentJobState.instance.current); }
        finally { File.WriteAllText(_goFile, exitCode.ToString()); }
        yield return WaitUntil(() => !AgentWorkflow.IsBusy, "the agent run to finish");
        yield return null;
    }

    // ------------------------------------------------------------------ scene helpers

    private static SpatialProxy Proxy(string label, Vector3 footprint, float yaw, Vector3 size)
    {
        SpatialProxyFactory.CreateProxy();
        SpatialProxy p = Selection.activeGameObject.GetComponent<SpatialProxy>();
        p.label = label;
        p.assetPrompt = "low poly " + label;
        p.size = size;
        p.transform.SetPositionAndRotation(footprint + Vector3.up * (size.y / 2f), Quaternion.Euler(0, yaw, 0));
        return p;
    }

    private static List<PartSpec> Body(Vector3 size, string color = "#AA7744") => new()
    {
        new PartSpec { name = "body", shape = "box", center = new[] { 0f, size.y / 2f, 0f },
            size = new[] { size.x, size.y, size.z }, color = color }
    };

    private static AgentAsset Asset(string label, AgentJob job = null) =>
        AgentSceneOps.AllAssets(job).FirstOrDefault(a => a.label == label);

    private string[] Snapshots(string suffix) =>
        Directory.GetFiles(StudyPaths.Snapshots, "*.json", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(suffix + ".json")).OrderBy(f => f).ToArray();

    /// <summary>Condition B up to an accepted run: a well and a bench built from their proxies.</summary>
    private IEnumerator GenerateAndAcceptB()
    {
        StudySession.instance.condition = StudyCondition.SpatialControls;
        StudyController.StartCondition();
        Proxy("well", Vector3.zero, 0, new Vector3(2, 3, 2));
        Proxy("bench", new Vector3(0, 0, -2.5f), -90, new Vector3(0.5f, 0.9f, 1.5f));

        StudyController.RunGeneration();
        yield return Agent(job =>
        {
            foreach (AgentAsset a in AgentSceneOps.AllAssets(job))
                Assert.IsTrue(AgentSceneOps.AddParts(a.assetId, Body(a.volumeSize)).ok, a.label);
            AgentSceneOps.Finish("built");
        });
        Assert.IsTrue(AgentWorkflow.LastReport.success, AgentWorkflow.LastReport.message);
        StudyController.AcceptActiveVersion();
        Assert.AreEqual(StudyPhase.Refinement, StudySession.instance.phase);
    }

    private static void SelectWholePart(string label)
    {
        RegionSelectionManager rig = StudyController.EnsureRefinementRig(false);
        AgentAsset asset = Asset(label);
        RefinementPartSelection.Commit(rig,
            RefinementPartSelection.Pick(null, asset, AgentSceneOps.PartsOf(asset)[0], false), "Select part");
    }

    // ------------------------------------------------------------------ tests

    [UnityTest]
    public IEnumerator ConditionB_GenerationFillsOnlyTheProxyAssets()
    {
        StudySession.instance.condition = StudyCondition.SpatialControls;
        StudyController.StartCondition();
        Proxy("well", Vector3.zero, 0, new Vector3(2, 3, 2));
        Proxy("gate", new Vector3(0, 0, 6), 90, new Vector3(0.6f, 3, 3));

        StudyController.RunGeneration();
        OpResult extra = null;
        yield return Agent(job =>
        {
            Assert.AreEqual(2, job.assetIds.Count);
            extra = AgentSceneOps.CreateAsset("extra", "", new[] { 0f, 0, 0 }, 0, new[] { 1f, 1, 1 });
            AgentAsset well = Asset("well", job);
            AgentSceneOps.AddParts(well.assetId, Body(well.volumeSize));
        });

        Assert.IsFalse(extra.ok, "condition B must not create assets outside the proxies");
        AgentRunReport report = AgentWorkflow.LastReport;
        Assert.AreEqual(1, report.partsAdded);
        CollectionAssert.AreEqual(new[] { "gate" }, report.emptyAssets);
        Assert.AreEqual(1, Snapshots("_run1").Length);
        Assert.AreEqual(1, StudySession.instance.generationRuns);
    }

    [UnityTest]
    public IEnumerator ConditionB_PreviewLeavesLiveSceneUntilApplied()
    {
        yield return GenerateAndAcceptB();
        AgentPart liveBody = AgentSceneOps.PartsOf(Asset("well"))[0];
        SelectWholePart("well");
        StudySession.instance.editRequest = "make the well taller";
        StudyController.SubmitEdit();

        OpResult outside = null;
        yield return Agent(job =>
        {
            AgentAsset staged = Asset("well", job);
            Assert.AreNotSame(Asset("well"), staged, "edits work on a private copy");
            AgentSceneOps.UpdateParts(staged.assetId, new List<PartSpec>
                { new() { id = liveBody.partId, size = new[] { 2f, 3.6f, 2f }, center = new[] { 0f, 1.8f, 0f } } });
            outside = AgentSceneOps.UpdateParts(Asset("bench", job).assetId, new List<PartSpec>
                { new() { id = AgentSceneOps.PartsOf(Asset("bench", job))[0].partId, color = "#FF0000" } });
        });

        Assert.IsFalse(outside.ok, "an unselected object must stay unchanged");
        Assert.AreEqual(3f, liveBody.transform.localScale.y, 1e-4f, "the live scene changes only on apply");
        Assert.AreEqual(1, StudySession.instance.review.ideas.Count);

        Vector3 volume = Asset("well").volumeSize;
        StudyController.ApplyIdea();
        Assert.AreEqual(3.6f, liveBody.transform.localScale.y, 1e-4f);
        Assert.AreEqual(volume, Asset("well").volumeSize, "condition B volumes are the designer's proxies");
        Assert.AreEqual(1, StudySession.instance.editsKept);
        Assert.AreEqual(1, Snapshots("_edit1_after").Length);
    }

    [UnityTest]
    public IEnumerator ConditionB_DiscardIsNotCountedAsUndo()
    {
        yield return GenerateAndAcceptB();
        SelectWholePart("bench");
        StudySession.instance.editRequest = "make the bench red";
        StudyController.SubmitEdit();
        yield return Agent(job =>
        {
            AgentAsset staged = Asset("bench", job);
            AgentSceneOps.UpdateParts(staged.assetId, new List<PartSpec>
                { new() { id = AgentSceneOps.PartsOf(staged)[0].partId, color = "#CC2222" } });
        });
        StudyController.DiscardIdeas();

        Assert.AreEqual(1, StudySession.instance.editsDiscarded);
        Assert.AreEqual(0, StudySession.instance.editsUndone);
        Assert.IsFalse(StudySession.instance.HasReview);
    }

    [UnityTest]
    public IEnumerator ConditionA_EditChangesOnlyOneAsset()
    {
        StudySession.instance.condition = StudyCondition.TextOnly;
        StudyController.StartCondition();
        StudySession.instance.scenePrompt = "A well with a gate behind it.";
        StudyController.RunGeneration();
        yield return Agent(job =>
        {
            foreach ((string label, float z) in new[] { ("well", 0f), ("gate", 6f) })
            {
                OpResult created = AgentSceneOps.CreateAsset(label, label, new[] { 0f, 0f, z }, 0, new[] { 1f, 2f, 1f });
                var id = (string)((Dictionary<string, object>)created.data)["asset_id"];
                AgentSceneOps.AddParts(id, Body(new Vector3(1, 2, 1)));
            }
        });
        StudyController.AcceptActiveVersion();

        StudySession.instance.editRequest = "make the well taller";
        StudyController.SubmitEdit();
        OpResult first = null, second = null;
        yield return Agent(job =>
        {
            first = AgentSceneOps.ClearAsset(Asset("well", job).assetId);
            second = AgentSceneOps.ClearAsset(Asset("gate", job).assetId);
        });

        Assert.IsTrue(first.ok, first.message);
        Assert.IsFalse(second.ok, "a text-only edit may change one asset");
    }

    // Snapshot fields the analysis reads for clipped parts (C1).
    [Serializable] private class SnapPiece { public float[] center_world, size_world; }
    [Serializable] private class SnapPart { public string part_id, source_part_id; public float[] size_world, geometry_size_world; public SnapPiece[] pieces; }
    [Serializable] private class SnapAsset { public string label; public SnapPart[] parts; }
    [Serializable] private class SnapDoc { public SnapAsset[] assets; }

    private SnapAsset SnapshotAsset(string suffix, string label) =>
        JsonUtility.FromJson<SnapDoc>(File.ReadAllText(Snapshots(suffix).Last())).assets.First(a => a.label == label);

    /// <summary>Drags a box over the half of the bench on its +Z (local width) side.</summary>
    private static void SelectHalfOfBench()
    {
        AgentAsset bench = Asset("bench");
        Transform t = bench.transform;
        Vector3 v = bench.volumeSize;
        var drawn = new RegionSelection
        {
            center = t.position + t.up * (v.y / 2f) + t.forward * (v.z / 4f),
            size = new Vector3(v.x + 0.2f, v.y + 0.2f, v.z / 2f),
            rotation = t.rotation
        };
        RegionSelection selection = RefinementPartSelection.Draw(drawn, bench, false);
        Assert.AreEqual(1, selection.partialParts.Count, "the box covers part of the bench");
        RefinementPartSelection.Commit(StudyController.EnsureRefinementRig(false), selection, "Draw box");
    }

    [UnityTest]
    public IEnumerator ConditionB_PartialRemovalIsWrittenAsClippedGeometry()
    {
        yield return GenerateAndAcceptB();
        string partId = AgentSceneOps.PartsOf(Asset("bench"))[0].partId;
        SelectHalfOfBench();
        StudySession.instance.editRequest = "remove the left half of the bench";
        StudyController.SubmitEdit();
        yield return Agent(job => Assert.IsTrue(
            AgentSceneOps.RemoveParts(Asset("bench", job).assetId, new List<string> { partId }).ok));
        StudyController.ApplyIdea();

        SnapPart part = SnapshotAsset("_edit1_after", "bench").parts.Single();
        Assert.AreEqual(partId, part.part_id);
        Assert.AreEqual(1.5f, part.size_world[2], 1e-3f, "the transform keeps the full primitive");
        Assert.AreEqual(0.75f, part.geometry_size_world[2], 1e-3f, "the geometry is the remaining half");
        Assert.AreEqual(1, part.pieces.Length);
    }

    [UnityTest]
    public IEnumerator ConditionB_PartialUpdateMarksTheRemainder()
    {
        yield return GenerateAndAcceptB();
        string partId = AgentSceneOps.PartsOf(Asset("bench"))[0].partId;
        SelectHalfOfBench();
        StudySession.instance.editRequest = "paint the left half of the bench red";
        StudyController.SubmitEdit();
        yield return Agent(job => Assert.IsTrue(AgentSceneOps.UpdateParts(Asset("bench", job).assetId,
            new List<PartSpec> { new() { id = partId, color = "#CC2222" } }).ok));
        StudyController.ApplyIdea();

        SnapPart[] parts = SnapshotAsset("_edit1_after", "bench").parts;
        Assert.AreEqual(2, parts.Length);
        SnapPart edited = parts.Single(p => p.part_id == partId);
        SnapPart remainder = parts.Single(p => p.part_id != partId);
        Assert.AreEqual(partId, remainder.source_part_id);
        Assert.AreEqual(string.Empty, edited.source_part_id);
        Assert.AreEqual(0.75f, edited.geometry_size_world[2], 1e-3f);
        Assert.AreEqual(0.75f, remainder.geometry_size_world[2], 1e-3f);

        // Ctrl+Z takes the whole applied idea back: one part, whole and uncoloured again.
        Undo.PerformUndo();
        yield return null;
        AgentPart restored = AgentSceneOps.PartsOf(Asset("bench")).Single();
        Assert.AreEqual(partId, restored.partId);
        Assert.AreEqual(0, restored.fragments.Count);
        Assert.AreNotEqual("CC2222", ColorUtility.ToHtmlStringRGB(restored.color));
        Assert.AreEqual(1, StudySession.instance.editsUndone);
    }

    // ------------------------------------------------------------------ proxy IDs (C2)

    [UnityTest]
    public IEnumerator DuplicatingAProxyGivesTheCopyANewId()
    {
        SpatialProxy original = Proxy("left stall", new Vector3(-3, 0, 1), 0, new Vector3(1.5f, 2.6f, 2.4f));
        string id = original.ProxyId;
        for (int i = 0; i < 3; i++) yield return null;      // the proxy exists before it is duplicated
        Selection.activeGameObject = original.gameObject;
        Unsupported.DuplicateGameObjectsUsingPasteboard();   // what Ctrl+D runs
        for (int i = 0; i < 5; i++) yield return null;      // change events are published on the next update

        SpatialProxy[] proxies = UnityEngine.Object.FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None);
        Assert.AreEqual(2, proxies.Length);
        Assert.AreEqual(id, original.ProxyId, "the original keeps its ID");
        Assert.AreEqual(2, proxies.Select(p => p.ProxyId).Distinct().Count());
    }

    [UnityTest]
    public IEnumerator ProxyCreatedAndDuplicatedInOneUpdateKeepsTheOriginalId()
    {
        SpatialProxy original = Proxy("left stall", new Vector3(-3, 0, 1), 0, new Vector3(1.5f, 2.6f, 2.4f));
        string id = original.ProxyId;
        Selection.activeGameObject = original.gameObject;
        Unsupported.DuplicateGameObjectsUsingPasteboard();
        for (int i = 0; i < 5; i++) yield return null;

        Assert.AreEqual(id, original.ProxyId, "the copy sits after the original in the hierarchy");
        Assert.AreEqual(2, UnityEngine.Object.FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None)
            .Select(p => p.ProxyId).Distinct().Count());
    }

    [UnityTest]
    public IEnumerator ScriptedProxyCopiesAreRepairedBeforeGeneration()
    {
        StudySession.instance.condition = StudyCondition.SpatialControls;
        StudyController.StartCondition();
        SpatialProxy left = Proxy("left stall", new Vector3(-3, 0, 1), 0, new Vector3(1.5f, 2.6f, 2.4f));
        string id = left.ProxyId;
        SpatialProxy right = UnityEngine.Object.Instantiate(left.gameObject).GetComponent<SpatialProxy>();
        right.label = "right stall";
        right.transform.position = new Vector3(3, 1.3f, 1);
        Assert.AreEqual(id, right.ProxyId, "Instantiate copies the ID without editor change events");

        StudyController.RunGeneration();
        yield return Agent(job =>
        {
            foreach (AgentAsset a in AgentSceneOps.AllAssets(job))
                AgentSceneOps.AddParts(a.assetId, Body(a.volumeSize));
        });

        Assert.AreEqual(id, left.ProxyId);
        Assert.AreNotEqual(id, right.ProxyId);
        CollectionAssert.AreEquivalent(new[] { left.ProxyId, right.ProxyId },
            AgentSceneOps.AllAssets().Select(a => a.proxyId).ToArray());
        Assert.AreEqual("right stall", AgentSceneOps.AllAssets().Single(a => a.proxyId == right.ProxyId).label);
    }

    [UnityTest]
    public IEnumerator RepairKeepsTheIdOnTheProxyThatOwnsTheAsset()
    {
        StudySession.instance.condition = StudyCondition.SpatialControls;
        StudyController.StartCondition();
        SpatialProxy owner = Proxy("well", new Vector3(5, 0, 5), 0, new Vector3(2, 3, 2));
        StudyController.RunGeneration();
        yield return Agent(job =>
        {
            foreach (AgentAsset a in AgentSceneOps.AllAssets(job))
                AgentSceneOps.AddParts(a.assetId, Body(a.volumeSize));
        });
        string id = owner.ProxyId;

        SpatialProxy copy = UnityEngine.Object.Instantiate(owner.gameObject).GetComponent<SpatialProxy>();
        copy.transform.SetAsFirstSibling();                  // first in hierarchy, but far from the asset
        copy.transform.position = new Vector3(-5, 1.5f, -5);

        Assert.AreEqual(1, SpatialProxyIds.RepairDuplicates(new[] { copy, owner }));
        Assert.AreEqual(id, owner.ProxyId);
        Assert.AreNotEqual(id, copy.ProxyId);
    }

    // ------------------------------------------------------------------ undo (C4)

    [UnityTest]
    public IEnumerator GenerationRunIsNotOnTheUndoStack()
    {
        StudySession.instance.condition = StudyCondition.SpatialControls;
        StudyController.StartCondition();
        Proxy("well", Vector3.zero, 0, new Vector3(2, 3, 2));
        Undo.ClearAll();
        StudyController.RunGeneration();
        yield return Agent(job =>
        {
            AgentAsset well = Asset("well", job);
            AgentSceneOps.AddParts(well.assetId, Body(well.volumeSize));
            AgentSceneOps.AddParts(well.assetId, Body(well.volumeSize * 0.5f));
        });

        Undo.PerformUndo();
        yield return null;
        AgentAsset asset = Asset("well");
        Assert.IsNotNull(asset, "Ctrl+Z must not remove the generated asset");
        Assert.AreEqual(2, AgentSceneOps.PartsOf(asset).Count, "Ctrl+Z must not remove generated parts");
    }

    [UnityTest]
    public IEnumerator UndoAndRedoOfAnAppliedRefinementAreRecorded()
    {
        yield return GenerateAndAcceptB();
        AgentPart body = AgentSceneOps.PartsOf(Asset("well"))[0];
        SelectWholePart("well");
        StudySession.instance.editRequest = "make the well taller";
        StudyController.SubmitEdit();
        yield return Agent(job => AgentSceneOps.UpdateParts(Asset("well", job).assetId, new List<PartSpec>
            { new() { id = body.partId, size = new[] { 2f, 3.6f, 2f }, center = new[] { 0f, 1.8f, 0f } } }));
        StudyController.ApplyIdea();
        Assert.AreEqual(3.6f, body.transform.localScale.y, 1e-4f);

        Undo.PerformUndo();
        yield return null;
        StudySession s = StudySession.instance;
        Assert.AreEqual(3f, body.transform.localScale.y, 1e-4f, "Ctrl+Z reverts the applied idea");
        Assert.AreEqual(0, s.editsKept);
        Assert.AreEqual(1, s.editsUndone);
        Assert.AreEqual(1, Snapshots("_edit1_undone").Length);

        Undo.PerformRedo();
        yield return null;
        Assert.AreEqual(3.6f, body.transform.localScale.y, 1e-4f);
        Assert.AreEqual(1, s.editsKept);
        Assert.AreEqual(0, s.editsUndone);
        Assert.AreEqual(2, Snapshots("_edit1_after").Length, "the redo writes a newer after snapshot");
    }

    [UnityTest]
    public IEnumerator SubmittedSceneCannotBeUndone()
    {
        yield return GenerateAndAcceptB();
        AgentPart body = AgentSceneOps.PartsOf(Asset("well"))[0];
        SelectWholePart("well");
        StudySession.instance.editRequest = "make the well taller";
        StudyController.SubmitEdit();
        yield return Agent(job => AgentSceneOps.UpdateParts(Asset("well", job).assetId, new List<PartSpec>
            { new() { id = body.partId, size = new[] { 2f, 3.6f, 2f }, center = new[] { 0f, 1.8f, 0f } } }));
        StudyController.ApplyIdea();
        StudyController.SubmitScene();
        Assert.AreEqual(StudyPhase.Finished, StudySession.instance.phase);
        Assert.AreEqual(1, Snapshots("_final").Length);

        Undo.PerformUndo();
        yield return null;
        Assert.AreEqual(3.6f, body.transform.localScale.y, 1e-4f, "the final snapshot stays the scene");
    }

    // ------------------------------------------------------------------ run lifecycle (Codex 7b, 3, 6)

    /// <summary>Condition B with a well and a gate; run 1 fills both.</summary>
    private IEnumerator GenerateB()
    {
        StudySession.instance.condition = StudyCondition.SpatialControls;
        StudyController.StartCondition();
        Proxy("well", Vector3.zero, 0, new Vector3(2, 3, 2));
        Proxy("gate", new Vector3(0, 0, 6), 90, new Vector3(0.6f, 3, 3));
        StudyController.RunGeneration();
        yield return Agent(job =>
        {
            foreach (AgentAsset a in AgentSceneOps.AllAssets(job))
                AgentSceneOps.AddParts(a.assetId, Body(a.volumeSize));
        });
        Assert.AreEqual(RunOutcome.Complete, AgentWorkflow.LastReport.outcome, AgentWorkflow.LastReport.message);
    }

    [UnityTest]
    public IEnumerator StudyActionsWaitWhileARunIsStarting()
    {
        yield return GenerateB();
        StudySession s = StudySession.instance;
        _mcpDelayMs = 1500;                                   // the setup check is waiting on the MCP server
        StudyController.RunGeneration();
        yield return null;

        Assert.IsTrue(StudyController.IsBusy, "a starting run counts as busy");
        Assert.IsFalse(StudyController.CanAccept);
        StudyController.AcceptActiveVersion();
        StudyController.ResetSession();
        Assert.AreEqual(StudyPhase.Generation, s.phase, "Accept must not slip in while the run starts");
        Assert.AreEqual(1, s.activeRun);

        _mcpDelayMs = 0;
        yield return Agent(job =>
        {
            foreach (AgentAsset a in AgentSceneOps.AllAssets(job))
                AgentSceneOps.AddParts(a.assetId, Body(a.volumeSize));
        });
        Assert.AreEqual(2, s.activeRun);
        CollectionAssert.Contains(s.archivedRuns, 1);
        Assert.AreEqual(StudyPhase.Generation, s.phase);
    }

    [UnityTest]
    public IEnumerator RunThatBuildsNothingDoesNotCount()
    {
        yield return GenerateB();
        StudySession s = StudySession.instance;
        StudyController.RunGeneration();
        yield return Agent(_ => { });                        // e.g. the agent never reached the tools

        Assert.AreEqual(RunOutcome.Failed, AgentWorkflow.LastReport.outcome);
        Assert.AreEqual(1, s.generationRuns, "an empty run is refunded");
        Assert.AreEqual(1, s.activeRun, "the previous version is shown again");
        CollectionAssert.IsEmpty(s.archivedRuns);
        Assert.AreEqual(2, AgentSceneOps.AllAssets().Count);
        Assert.AreEqual(1, UnityEngine.Object.FindObjectsByType<AgentAsset>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Count(a => a.label == "well"), "the empty run's prepared assets are gone");
        Assert.IsTrue(StudyController.CanAccept);
    }

    [UnityTest]
    public IEnumerator FirstEmptyRunLeavesNothingToAccept()
    {
        StudySession.instance.condition = StudyCondition.SpatialControls;
        StudyController.StartCondition();
        Proxy("well", Vector3.zero, 0, new Vector3(2, 3, 2));
        StudyController.RunGeneration();
        yield return Agent(_ => { });

        Assert.AreEqual(0, StudySession.instance.generationRuns);
        Assert.AreEqual(0, StudySession.instance.activeRun);
        Assert.IsNull(GeneratedContentRoot.Find());
        Assert.IsFalse(StudyController.CanAccept);
    }

    [UnityTest]
    public IEnumerator VersionWithoutGeometryCannotBeAccepted()
    {
        yield return GenerateB();
        foreach (AgentAsset a in AgentSceneOps.AllAssets())
            foreach (AgentPart p in AgentSceneOps.PartsOf(a))
                UnityEngine.Object.DestroyImmediate(p.gameObject);
        Assert.IsFalse(StudyController.CanAccept);
    }

    [UnityTest]
    public IEnumerator CrashAfterBuildingIsPartialAndNeedsAnExplicitAccept()
    {
        StudySession.instance.condition = StudyCondition.SpatialControls;
        StudyController.StartCondition();
        Proxy("well", Vector3.zero, 0, new Vector3(2, 3, 2));
        Proxy("gate", new Vector3(0, 0, 6), 90, new Vector3(0.6f, 3, 3));
        StudyController.RunGeneration();
        yield return Agent(job =>
        {
            AgentAsset well = Asset("well", job);
            AgentSceneOps.AddParts(well.assetId, Body(well.volumeSize));
        }, exitCode: 1);                                     // built something, then the CLI crashed

        AgentRunReport report = AgentWorkflow.LastReport;
        Assert.AreEqual(RunOutcome.Partial, report.outcome);
        Assert.IsFalse(report.success, "a crashed run is not a success");
        Assert.AreEqual(1, StudySession.instance.generationRuns, "a run with content counts");
        Assert.AreEqual(RunOutcome.Partial, StudySession.instance.Record(1).outcome);
        Assert.IsTrue(StudyController.CanAccept, "the partial version can still be refined");
        string warning = StudyController.ActiveVersionWarning();
        StringAssert.Contains("partial", warning);
        StringAssert.Contains("gate", warning, "the warning names the empty object");
    }

    [Test]
    public void OutcomeClassification()
    {
        RunOutcome Of(bool content, bool cancelled = false, bool timedOut = false, int exit = 0, bool empty = false)
        {
            var r = new AgentRunReport { hasContent = content, cancelled = cancelled, timedOut = timedOut, exitCode = exit };
            if (empty) r.emptyAssets.Add("gate");
            AgentWorkflow.Classify(r);
            Assert.AreEqual(r.outcome == RunOutcome.Complete, r.success);
            return r.outcome;
        }

        Assert.AreEqual(RunOutcome.Complete, Of(true));
        Assert.AreEqual(RunOutcome.Partial, Of(true, exit: 1));
        Assert.AreEqual(RunOutcome.Partial, Of(true, empty: true));
        Assert.AreEqual(RunOutcome.TimedOut, Of(true, timedOut: true));
        Assert.AreEqual(RunOutcome.Cancelled, Of(true, cancelled: true));
        Assert.AreEqual(RunOutcome.Failed, Of(false));
        Assert.AreEqual(RunOutcome.Failed, Of(false, exit: -1));
        Assert.AreEqual(RunOutcome.TimedOut, Of(false, timedOut: true));
        Assert.AreEqual(RunOutcome.Cancelled, Of(false, cancelled: true));
    }

    // ------------------------------------------------------------------ condition A baseline (Codex 2)

    [UnityTest]
    public IEnumerator ConditionA_AnExistingObjectIsRebuiltNotPatched()
    {
        StudySession.instance.condition = StudyCondition.TextOnly;
        StudyController.StartCondition();
        StudySession.instance.scenePrompt = "A well with a gate behind it.";
        StudyController.RunGeneration();
        yield return Agent(job =>
        {
            foreach ((string label, float z) in new[] { ("well", 0f), ("gate", 6f) })
            {
                OpResult created = AgentSceneOps.CreateAsset(label, label, new[] { 0f, 0f, z }, 0, new[] { 1f, 2f, 1f });
                AgentSceneOps.AddParts((string)((Dictionary<string, object>)created.data)["asset_id"], Body(new Vector3(1, 2, 1)));
            }
        });
        StudyController.AcceptActiveVersion();

        StudySession.instance.editRequest = "make the well red";
        StudyController.SubmitEdit();
        OpResult update = null, add = null, remove = null, clear = null, rebuild = null;
        bool editableBeforeClear = true;
        yield return Agent(job =>
        {
            AgentAsset well = Asset("well", job);
            string partId = AgentSceneOps.PartsOf(well)[0].partId;
            var listing = (Dictionary<string, object>)AgentSceneOps.ListParts(well.assetId).data;
            editableBeforeClear = (bool)((Dictionary<string, object>)((List<object>)listing["parts"])[0])["editable"];
            update = AgentSceneOps.UpdateParts(well.assetId, new List<PartSpec> { new() { id = partId, color = "#CC2222" } });
            remove = AgentSceneOps.RemoveParts(well.assetId, new List<string> { partId });
            add = AgentSceneOps.AddParts(well.assetId, Body(new Vector3(0.5f, 0.5f, 0.5f)));
            clear = AgentSceneOps.ClearAsset(well.assetId);
            rebuild = AgentSceneOps.AddParts(well.assetId, Body(new Vector3(1, 2, 1), "#CC2222"));
        });

        Assert.IsFalse(editableBeforeClear, "parts are listed as not editable until the object is cleared");
        Assert.IsFalse(update.ok, "a local recolour is the condition B edit");
        Assert.IsFalse(remove.ok);
        Assert.IsFalse(add.ok);
        StringAssert.Contains("spatialgen_clear_asset", update.message);
        Assert.IsTrue(clear.ok, clear.message);
        Assert.IsTrue(rebuild.ok, rebuild.message);
        StudyController.ApplyIdea();
        AgentPart rebuilt = AgentSceneOps.PartsOf(Asset("well")).Single();
        Assert.AreEqual("CC2222", ColorUtility.ToHtmlStringRGB(rebuilt.color));

        // The documented exception: an edit that adds a new object builds it directly.
        StudySession.instance.editRequest = "add a bucket next to the well";
        StudyController.SubmitEdit();
        OpResult bucketParts = null;
        yield return Agent(job =>
        {
            OpResult created = AgentSceneOps.CreateAsset("bucket", "bucket", new[] { 1.5f, 0f, 0f }, 0, new[] { 0.4f, 0.4f, 0.4f });
            bucketParts = AgentSceneOps.AddParts((string)((Dictionary<string, object>)created.data)["asset_id"],
                Body(new Vector3(0.4f, 0.4f, 0.4f)));
        });
        Assert.IsTrue(bucketParts.ok, bucketParts.message);
    }

    // ------------------------------------------------------------------ condition A volumes

    [UnityTest]
    public IEnumerator ConditionA_RebuiltObjectGetsItsNewVolume()
    {
        StudySession.instance.condition = StudyCondition.TextOnly;
        StudyController.StartCondition();
        StudySession.instance.scenePrompt = "A well with a gate behind it.";
        StudyController.RunGeneration();
        yield return Agent(job =>
        {
            foreach ((string label, float z) in new[] { ("well", 0f), ("gate", 6f) })
            {
                OpResult created = AgentSceneOps.CreateAsset(label, label, new[] { 0f, 0f, z }, 0, new[] { 1f, 2f, 1f });
                AgentSceneOps.AddParts((string)((Dictionary<string, object>)created.data)["asset_id"], Body(new Vector3(1, 2, 1)));
            }
        });
        StudyController.AcceptActiveVersion();
        Assert.AreEqual(new Vector3(1, 2, 1), Asset("well").volumeSize);

        StudySession.instance.editRequest = "make the well 3.5 m tall";
        StudyController.SubmitEdit();
        OpResult rebuild = null;
        yield return Agent(job =>
        {
            AgentAsset well = Asset("well", job);
            AgentSceneOps.ClearAsset(well.assetId);
            rebuild = AgentSceneOps.AddParts(well.assetId, Body(new Vector3(1, 3.5f, 1)));
        });

        Assert.IsTrue(rebuild.ok, rebuild.message);
        CollectionAssert.IsEmpty((List<string>)((Dictionary<string, object>)rebuild.data)["warnings"],
            "a condition A rebuild is not held to its old volume");
        StudyController.ApplyIdea();
        Vector3 v = Asset("well").volumeSize;
        Assert.AreEqual(3.5f, v.y, 1e-3f, "the volume follows the rebuilt object");
        Assert.AreEqual(1f, v.x, 1e-3f);
        Assert.AreEqual(new Vector3(1, 2, 1), Asset("gate").volumeSize, "untouched objects keep their volume");
        StringAssert.Contains("3.5", File.ReadAllText(Snapshots("_edit1_after").Single()), "the snapshot records the new volume");

        Undo.PerformUndo();
        yield return null;
        Assert.AreEqual(new Vector3(1, 2, 1), Asset("well").volumeSize, "Ctrl+Z restores the old volume with the old parts");
    }

    [Test]
    public void FittedVolumeIsCentredOnTheFootprint()
    {
        var root = new GameObject("fit test");
        try
        {
            AgentAsset asset = root.AddComponent<AgentAsset>();
            root.transform.rotation = Quaternion.Euler(0, 30, 0);   // the fit is in the asset's own frame
            var go = new GameObject("part", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(0.2f, 1f, 0f);
            go.transform.localScale = new Vector3(1f, 2f, 1f);
            go.AddComponent<AgentPart>().Rebuild();

            Assert.IsTrue(AgentSceneOps.TryFitVolume(asset, out Vector3 volume));
            Assert.AreEqual(1.4f, volume.x, 1e-4f, "x spans ±0.7 around the origin");
            Assert.AreEqual(2f, volume.y, 1e-4f);
            Assert.AreEqual(1f, volume.z, 1e-4f);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    // ------------------------------------------------------------------ agent isolation (C3)

    [UnityTest]
    public IEnumerator AgentRunsInAnEmptyFolderOutsideTheProject()
    {
        StudySession.instance.condition = StudyCondition.SpatialControls;
        StudyController.StartCondition();
        Proxy("well", Vector3.zero, 0, new Vector3(2, 3, 2));
        StudyController.RunGeneration();
        yield return Agent(_ => { });

        string cwd = Path.GetFullPath(File.ReadAllText(Path.Combine(_temp, "last_cwd.txt")).Trim());
        string project = Path.GetFullPath(StudyPaths.ProjectRoot);
        Assert.IsFalse(cwd.StartsWith(project, StringComparison.Ordinal), $"agent started in {cwd}");
        Assert.AreEqual(string.Empty, File.ReadAllText(Path.Combine(_temp, "last_cwd_listing.txt")).Trim(),
            "the working folder is empty");
    }

    [Test]
    public void ClaudeRunsWithoutFileShellOrWebTools()
    {
        BackendSettings s = BackendRegistry.Settings;
        s.agentCli = AgentCli.ClaudeCode;
        string args = AgentProcessRunner.BuildClaudeArguments(s, _temp);
        StringAssert.Contains("--tools \"ToolSearch,ListMcpResourcesTool,ReadMcpResourceTool\"", args);
        StringAssert.Contains("--strict-mcp-config", args);
        foreach (string tool in new[] { "Read", "Glob", "Grep", "Bash", "Agent", "WebFetch" })
            StringAssert.Contains(tool, AgentProcessRunner.ClaudeDeniedTools);
        foreach (string tool in AgentProcessRunner.ToolNames)
            StringAssert.Contains($"mcp__{AgentProcessRunner.ClaudeServerName}__{tool}", args);
        StringAssert.DoesNotContain("mcp__UnityMCP__manage", args);
    }

    [Test]
    public void CodexIsLimitedToTheSpatialGenerationTools()
    {
        BackendSettings s = BackendRegistry.Settings;
        s.agentCli = AgentCli.Codex;
        string args = AgentProcessRunner.BuildCodexArguments(s, _temp);
        StringAssert.Contains("--ignore-user-config", args);
        StringAssert.Contains("--sandbox read-only", args);
        StringAssert.Contains($"-C \"{_temp}\"", args);
        StringAssert.Contains("-c features.shell_tool=false", args);
        StringAssert.Contains("-c features.unified_exec=false", args);
        StringAssert.Contains($"mcp_servers.{AgentProcessRunner.CodexServerName}.enabled_tools=[", args);
        foreach (string tool in AgentProcessRunner.ToolNames)
            StringAssert.Contains($"\\\"{tool}\\\"", args);
        StringAssert.Contains("read files", AgentProcessRunner.IsolationNote(s), "the setup check warns about Codex");
    }

    [Test]
    public void ShapeParserRejectsNumbers()
    {
        Assert.IsTrue(AgentSceneOps.TryParseShape("cylinder", out AgentPartShape shape));
        Assert.AreEqual(AgentPartShape.Cylinder, shape);
        Assert.IsFalse(AgentSceneOps.TryParseShape("7", out _));
        Assert.IsFalse(AgentSceneOps.TryParseShape("-1", out _));
    }
}
