using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SpatialGeneration.Agent;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

public class ReviewWorkflowTests
{
    GameObject root;
    AgentAsset asset;
    AgentPart seat, leg;
    StudySession session;
    [SetUp] public void Setup()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Undo.ClearAll();
        AgentJobState.instance.current = null;
        session = StudySession.instance;
        session.ResetProgress();
        session.participantId = "review-fixture";
        session.targetScene = "X";
        session.condition = StudyCondition.TextOnly;
        root = new GameObject(GenerationRunner.GeneratedRootName);
        asset = new GameObject("chair").AddComponent<AgentAsset>();
        asset.transform.SetParent(root.transform);
        asset.assetId = "chair"; asset.label = "chair";
        seat = Part("seat", Vector3.up); leg = Part("leg", Vector3.zero);
    }
    AgentPart Part(string id, Vector3 position)
    {
        var p = new GameObject(id).AddComponent<AgentPart>();
        p.transform.SetParent(asset.transform); p.transform.localPosition = position;
        p.partId = id; p.color = Color.white; p.Rebuild(); return p;
    }
    [TearDown] public void Cleanup()
    {
        AgentJobState.instance.current = null;
        session.ResetProgress();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Undo.ClearAll();
    }
    [Test] public void EmptyFailedRunMustNotBeAcceptable()
    {
        Object.DestroyImmediate(root);
        session.phase = StudyPhase.Generation; session.activeRun = 1; session.generationRuns = 1;
        Assert.IsFalse(StudyController.CanAccept, "A run with no generated root must not enter refinement.");
    }
    [Test] public void TextOnlyEditMustRejectLocalUpdateBeforeWholeRebuild()
    {
        AgentJobState.instance.current = new AgentJob {jobId="review", running=true, mode=AgentJobMode.Edit, condition=StudyCondition.TextOnly};
        var result = AgentSceneOps.UpdateParts("chair", new List<PartSpec>{new PartSpec{id="seat",color="#FF0000"}});
        Assert.IsFalse(result.ok, "Condition A must enforce whole-object regeneration, not permit a local recolor.");
    }
    [Test] public void FailedPreflightMustNotConsumeRunOrArchiveScene()
    {
        session.phase = StudyPhase.Generation; session.scenePrompt = ""; session.activeRun=1; session.generationRuns=1;
        StudyController.RunGeneration();
        Assert.AreEqual(1, session.generationRuns);
        Assert.AreSame(root, GameObject.Find(GenerationRunner.GeneratedRootName));
    }
    [Test] public void VersionsRoundTripRetainsSceneGeometry()
    {
        GenerationVersions.ArchiveActive(1);
        Assert.IsNull(GameObject.Find(GenerationRunner.GeneratedRootName));
        Assert.IsTrue(GenerationVersions.Activate(1,2));
        Assert.AreSame(root,GameObject.Find(GenerationRunner.GeneratedRootName));
        Assert.AreEqual(2, AgentSceneOps.PartsOf(asset).Count);
    }
    void ApplyPreview()
    {
        session.phase = StudyPhase.Refinement; session.editsSubmitted=1;
        var selection = RefinementPartSelection.Pick(null,asset,seat,false);
        var review = RefinementReview.Begin(root,"Recolor seat","",StudyCondition.SpatialControls,selection);
        session.review=review;
        var job = new AgentJob {jobId="review", previewToken="review",running=true,mode=AgentJobMode.Edit,condition=StudyCondition.SpatialControls,selection=selection,hasRegion=true};
        using (var stage=new RefinementPreviewStage(review,root,job.previewToken))
        {
            AgentJobState.instance.current=job;
            Assert.IsTrue(AgentSceneOps.UpdateParts("chair",new List<PartSpec>{new PartSpec{id="seat",color="#FF0000"}}).ok);
            review.ideas.Add(review.CaptureIdea(stage.Root,job.jobId,"Red seat"));
        }
        job.running=false;
        StudyController.ApplyIdea();
        Assert.AreEqual(Color.red,seat.color);
    }
    [Test] public void ApplyUndoRedoRestoresAuthoringData()
    {
        ApplyPreview(); Undo.PerformUndo(); Assert.AreEqual(Color.white,seat.color);
        Undo.PerformRedo(); Assert.AreEqual(Color.red,seat.color);
        Assert.AreEqual(Color.white,leg.color);
    }
    [Test] public void UndoAppliedIdeaMustUpdateStudyCounters()
    {
        ApplyPreview(); Assert.AreEqual(1,session.editsKept);
        Undo.PerformUndo(); Assert.AreEqual(Color.white,seat.color);
        Assert.AreEqual(1,session.editsUndone,"Standard Unity Undo must be reflected in the study record.");
        Assert.AreEqual(0,session.editsKept);
    }
    [Test] public void DiscardedIdeaMustWriteAnEditOutcomeSnapshot()
    {
        session.phase=StudyPhase.Refinement;session.editsSubmitted=27;
        var review=RefinementReview.Begin(root,"Recolor","",StudyCondition.TextOnly,null);
        review.ideas.Add(new RefinementIdea {jobId="discard-review",summary="unused"});session.review=review;
        string before=SceneSnapshot.Write("edit27_before");
        StudyController.DiscardIdeas();
        Assert.IsFalse(session.HasReview);
        var files=Directory.GetFiles(Path.GetDirectoryName(before));
        Assert.IsTrue(files.Any(f=>f.EndsWith("_edit27_undone.json")||f.EndsWith("_edit27_after.json")),
            "The edit scorer only discovers edits with a before and outcome snapshot.");
    }
    [Test] public void ExportRealPartialRemovalForScoringAudit()
    {
        SceneSnapshot.Write("partial_before");
        var region = new RegionSelection {center=seat.transform.TransformPoint(Vector3.right*.25f),rotation=seat.transform.rotation,size=new Vector3(.5f,.8f,1.1f)};
        var selection = RefinementPartSelection.Draw(region,asset,true);
        var review = RefinementReview.Begin(root,"Cut seat","",StudyCondition.SpatialControls,selection);
        var job=new AgentJob {jobId="partial",previewToken="partial",running=true,mode=AgentJobMode.Edit,condition=StudyCondition.SpatialControls,hasRegion=true,selection=selection};
        using(var stage=new RefinementPreviewStage(review,root,job.previewToken))
        {
            AgentJobState.instance.current=job;
            Assert.IsTrue(AgentSceneOps.RemoveParts("chair",new List<string>{"seat"}).ok);
            review.ideas.Add(review.CaptureIdea(stage.Root,job.jobId,"Cut"));
        }
        review.Apply(root);
        Assert.Greater(seat.fragments.Count,0);
        Assert.That(seat.fragments.Sum(c=>c.Volume()),Is.EqualTo(.6f).Within(.001f));
        SceneSnapshot.Write("partial_after");
    }
}
