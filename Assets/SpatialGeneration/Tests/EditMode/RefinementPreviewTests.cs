using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialGeneration.Agent;
using UnityEditor;
using UnityEngine;

public sealed class RefinementPreviewTests
{
    private GameObject _root;
    private AgentAsset _chair, _other;
    private AgentPart _seat, _leg;
    private AgentJob _previous;
    private RefinementReview _review;

    [SetUp]
    public void SetUp()
    {
        _previous = AgentJobState.instance.current;
        if (_previous?.running == true) Assert.Ignore("An authoring job is active.");
        _root = new GameObject("Review test root");
        _root.transform.SetPositionAndRotation(new Vector3(7,0,-2), Quaternion.Euler(0,17,0));
        _chair = MakeAsset("chair");
        _other = MakeAsset("table");
        _chair.transform.localRotation = Quaternion.Euler(9,33,-11);
        _seat = MakePart(_chair,"seat",Vector3.up);
        _leg = MakePart(_chair,"leg",Vector3.zero);
        MakePart(_other,"top",Vector3.right * 3);
        var selection = RefinementPartSelection.Pick(null,_chair,_seat,false);
        _review = RefinementReview.Begin(_root, "Recolor the seat", "", StudyCondition.SpatialControls, selection);
    }

    private AgentAsset MakeAsset(string id)
    {
        var asset = new GameObject(id).AddComponent<AgentAsset>();
        asset.transform.SetParent(_root.transform,false);
        asset.assetId = id;
        asset.label = id;
        return asset;
    }

    private static AgentPart MakePart(AgentAsset asset, string id, Vector3 center)
    {
        var part = new GameObject(id).AddComponent<AgentPart>();
        part.partId = id;
        part.transform.SetParent(asset.transform,false);
        part.transform.localPosition = center;
        part.color = Color.white;
        part.Rebuild();
        return part;
    }

    [TearDown]
    public void TearDown()
    {
        if (_root == null) return;
        Object.DestroyImmediate(_root);
        AgentJobState.instance.current = _previous;
        AgentJobState.instance.Persist();
    }

    private AgentJob DraftJob() => new() {
        jobId = AgentSceneOps.ShortId(), previewToken = AgentSceneOps.ShortId(),
        running = true, mode = AgentJobMode.Edit, condition = _review.condition,
        request = _review.request, hasRegion = true, selection = _review.selection
    };

    private RefinementIdea GenerateColor(string color)
    {
        var job = DraftJob();
        using var stage = new RefinementPreviewStage(_review,_root,job.previewToken);
        AgentJobState.instance.current = job;
        var staged = AgentSceneOps.AllAssets(job).Single(a=>a.assetId == "chair");
        Assert.IsTrue(UnityEditor.SceneManagement.EditorSceneManager.IsPreviewSceneObject(staged));
        Assert.AreEqual(Color.white,AgentSceneOps.PartsOf(staged).Single(p=>p.partId=="seat").color,
            "Every regeneration starts with the original seat.");
        Assert.IsTrue(AgentSceneOps.UpdateParts("chair",new List<PartSpec>{new(){id="seat",color=color}}).ok);
        Assert.AreEqual(Color.white,_seat.color);
        var idea = _review.CaptureIdea(stage.Root,job.jobId,color);
        CollectionAssert.AreEqual(new[]{"chair"},idea.objects.Select(a=>a.id));
        CollectionAssert.AreEquivalent(new[]{"seat","leg"},idea.objects[0].parts.Select(p=>p.id));
        return idea;
    }

    [Test]
    public void RegenerateBrowseAndApplyEarlierIdeaAsOneUndo()
    {
        _review.ideas.Add(GenerateColor("#FF0000"));
        _review.ideas.Add(GenerateColor("#0000FF"));
        _review.selected = 0;
        var restored = JsonUtility.FromJson<RefinementReview>(JsonUtility.ToJson(_review));
        restored.Apply(_root);
        Assert.AreEqual(Color.red,_seat.color);
        Assert.AreSame(_leg,AgentSceneOps.PartsOf(_chair).Single(p=>p.partId=="leg"));
        Undo.PerformUndo();
        Assert.AreEqual(Color.white,_seat.color);
        Assert.AreSame(_seat,AgentSceneOps.PartsOf(_chair).Single(p=>p.partId=="seat"));
    }

    [Test]
    public void ClosedOrInterruptedDraftCannotWriteToTheLiveModel()
    {
        _review.ideas.Add(GenerateColor("#FF0000"));
        Assert.IsFalse(AgentSceneOps.RemoveParts("chair",new List<string>{"seat"}).ok);
        Assert.AreEqual(Color.white,_seat.color);
        var job = DraftJob();
        using (var attempt = new RefinementPreviewStage(_review,_root,job.previewToken))
        {
            AgentJobState.instance.current = job;
            Assert.IsTrue(AgentSceneOps.RemoveParts("chair",new List<string>{"seat"}).ok);
        }
        Assert.IsNotNull(_seat);
        Assert.AreEqual(1,_review.ideas.Count);
        _review.Apply(_root);
        Assert.AreEqual(Color.red,_seat.color);
    }

    [Test]
    public void ConflictingChangePreventsApplyAndKeepsTheDraft()
    {
        _review.ideas.Add(GenerateColor("#FF0000"));
        _seat.color = Color.green;
        Assert.Throws<System.InvalidOperationException>(()=>_review.Apply(_root));
        Assert.AreEqual(Color.green,_seat.color);
        Assert.AreEqual(1,_review.ideas.Count);
    }

    [Test]
    public void PartialRemovalIsOnlyVisibleInTheIdeaUntilApplied()
    {
        var bounds = new RegionSelection {
            center = _seat.transform.TransformPoint(Vector3.right*.25f), rotation=_seat.transform.rotation,
            size=new Vector3(.5f,.8f,1.1f)
        };
        _review.selection = RefinementPartSelection.Draw(bounds,_chair,true);
        var job = DraftJob();
        using (var stage = new RefinementPreviewStage(_review,_root,job.previewToken))
        {
            AgentJobState.instance.current = job;
            Assert.IsTrue(AgentSceneOps.RemoveParts("chair",new List<string>{"seat"}).ok);
            _review.ideas.Add(_review.CaptureIdea(stage.Root,job.jobId,"Cut seat"));
        }
        Assert.IsEmpty(_seat.fragments);
        _review.Apply(_root);
        Assert.That(_seat.fragments.Sum(cell=>cell.Volume()),Is.EqualTo(.6).Within(1e-4));
        Assert.IsEmpty(_leg.fragments);
    }

    [Test]
    public void RemovingPickedPartAndUpdatingSelectionUndoTogether()
    {
        var manager = _root.AddComponent<RegionSelectionManager>();
        manager.SetSelection(_review.selection);
        var job = DraftJob();
        using (var stage = new RefinementPreviewStage(_review, _root, job.previewToken))
        {
            AgentJobState.instance.current = job;
            Assert.IsTrue(AgentSceneOps.RemoveParts("chair", new List<string> { "seat" }).ok);
            _review.ideas.Add(_review.CaptureIdea(stage.Root, job.jobId, "Remove the seat"));
        }
        Assert.AreEqual("seat", manager.CurrentSelection.wholeParts.Single());
        _review.Apply(_root, manager);
        Assert.IsNull(manager.CurrentSelection);
        CollectionAssert.AreEqual(new[] { "leg" }, AgentSceneOps.PartsOf(_chair).Select(p => p.partId));
        Undo.PerformUndo();
        Assert.AreEqual("seat", manager.CurrentSelection.wholeParts.Single());
        CollectionAssert.AreEquivalent(new[] { "seat", "leg" }, AgentSceneOps.PartsOf(_chair).Select(p => p.partId));
    }

    [Test]
    public void NewObjectAndItsPartsStayInPreviewUntilApplied()
    {
        _review = RefinementReview.Begin(_root, "Add a stool", "", StudyCondition.TextOnly, null);
        var job = DraftJob();
        job.hasRegion = false;
        using (var stage = new RefinementPreviewStage(_review, _root, job.previewToken))
        {
            AgentJobState.instance.current = job;
            Assert.IsTrue(AgentSceneOps.CreateAsset("stool", "", new float[] { 0, 0, 0 }, 0, new float[] { 1, 1, 1 }).ok);
            var stool = AgentSceneOps.AllAssets(job).Single(a => a.label == "stool");
            var seat = new PartSpec { shape = "box", center = new[] { 0f, .5f, 0f }, size = new[] { 1f, .2f, 1f } };
            Assert.IsTrue(AgentSceneOps.AddParts(stool.assetId, new List<PartSpec> { seat }).ok);
            Assert.AreEqual(2, _root.GetComponentsInChildren<AgentAsset>().Length);
            _review.ideas.Add(_review.CaptureIdea(stage.Root, job.jobId, "Stool"));
        }
        _review.Apply(_root);
        var applied = _root.GetComponentsInChildren<AgentAsset>().Single(a => a.label == "stool");
        Assert.AreEqual(1, AgentSceneOps.PartsOf(applied).Count);
        Undo.PerformUndo();
        Assert.AreEqual(2, _root.GetComponentsInChildren<AgentAsset>().Length);
    }

    // Render the actual review camera and check that both edited and untouched geometry appear.
    [Test]
    public void ReviewCameraRendersWholeModelWithItsRefinedColor()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            Assert.Ignore("A graphics device is needed for a rendered preview.");
        _leg.color = Color.green;
        _leg.Rebuild();
        _review = RefinementReview.Begin(_root,_review.request,"",_review.condition,_review.selection);
        var idea = GenerateColor("#FF0000");
        using var view = new RefinementModelPreview();
        view.Show(_review,idea);
        var target = RenderTexture.GetTemporary(256,256);
        var previous = RenderTexture.active;
        var image = new Texture2D(256,256);
        try
        {
            Graphics.Blit(view.Render(new Rect(0,0,256,256)),target);
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0,0,256,256),0,0);
            Color32[] pixels = image.GetPixels32();
            Assert.Greater(pixels.Count(p=>p.r > p.g+25 && p.r > p.b+25),60, "refined seat");
            Assert.Greater(pixels.Count(p=>p.g > p.r+25 && p.g > p.b+25),60, "unchanged leg");
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Object.DestroyImmediate(image);
        }
    }
}
