using System.Collections.Generic;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using SpatialGeneration.Agent;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using SpatialGeneration;

public class RefinementPartSelectionTests
{
    private AgentAsset _asset;
    private AgentPart _left, _middle, _right;
    private AgentJob _previous;
    private GameObject _root;
    private bool _createdRoot;

    [SetUp]
    public void SetUp()
    {
        _previous = AgentJobState.instance.current;
        if (_previous?.running == true) Assert.Ignore("A scene generation job is running.");
        _root = GameObject.Find(RegionSelectionManager.GeneratedContentRootName);
        _createdRoot = _root == null;
        if (_createdRoot) _root = new GameObject(RegionSelectionManager.GeneratedContentRootName);
        _asset = new GameObject("Test chair").AddComponent<AgentAsset>();
        _asset.transform.SetParent(_root.transform);
        _asset.assetId = AgentSceneOps.ShortId();
        _asset.transform.SetPositionAndRotation(new Vector3(5,2,-4), Quaternion.Euler(10,37,0));
        _left = MakePart("left", -2);
        _middle = MakePart("middle", 0);
        _right = MakePart("right", 2);
    }

    private AgentPart MakePart(string id, float x)
    {
        var go = new GameObject(id);
        go.transform.SetParent(_asset.transform, false);
        go.transform.localPosition = Vector3.right * x;
        var part = go.AddComponent<AgentPart>();
        part.partId = id;
        return part;
    }

    [TearDown]
    public void TearDown()
    {
        if (_asset == null) return;
        Object.DestroyImmediate(_asset.gameObject);
        if (_createdRoot) Object.DestroyImmediate(_root);
        AgentJobState.instance.current = _previous;
        AgentJobState.instance.Persist();
    }

    private RegionSelection Box(float x, float width) => new() {
        center = _asset.transform.TransformPoint(new Vector3(x,0,0)), rotation = _asset.transform.rotation,
        size = new Vector3(width,1.2f,1.2f)
    };

    private void StartJob(RegionSelection selection) => AgentJobState.instance.current = new AgentJob {
        running = true, hasRegion = true, mode = AgentJobMode.Edit, condition = StudyCondition.SpatialControls,
        jobId = AgentSceneOps.ShortId(), request = "Change the selected geometry", selection = selection,
        regionCenter = selection.center, regionRotation = selection.rotation, regionSize = selection.size
    };

    [Test]
    public void PickedLegsExcludeMiddleAndCanReceiveAnAttachment()
    {
        var selection = RefinementPartSelection.Pick(null, _asset, _left, false);
        selection = RefinementPartSelection.Pick(selection, _asset, _right, true);
        CollectionAssert.AreEquivalent(new[]{"left","right"}, selection.wholeParts);
        Assert.That(selection.size.x, Is.EqualTo(5).Within(1e-5f));
        StartJob(selection);
        Assert.IsFalse(AgentSceneOps.RemoveParts(_asset.assetId, new List<string>{"middle"}).ok);
        Assert.IsTrue(AgentSceneOps.AddParts(_asset.assetId, new List<PartSpec> {
            new() {name="foot", shape="box", center=new[]{-2f,-0.6f,0}, size=new[]{1f,0.2f,1}}
        }).ok);
        Assert.IsFalse(RefinementPartSelection.Pick(selection, _asset, _left, true).wholeParts.Contains("left"));
    }

    [Test]
    public void DrawingPromotesMultiplePartsAndAdjustingDoesNotResnap()
    {
        var drawn = RefinementPartSelection.Draw(Box(0,4.8f), _asset, true);
        Assert.AreEqual(3, drawn.wholeParts.Count);
        RegionSelection exact = Box(0,4.9f);
        var adjusted = RefinementPartSelection.Adjust(drawn, exact.center, exact.rotation, exact.size, _asset);
        CollectionAssert.AreEquivalent(new[]{"left","right"}, adjusted.partialParts);
        CollectionAssert.AreEqual(new[]{"middle"}, adjusted.wholeParts);
    }

    [Test]
    public void MixedDrawRetainsOriginalCropForPartialRemoval()
    {
        // 90% of the left part and 25% of the middle part.
        RegionSelection selection = RefinementPartSelection.Draw(Box(-1.325f,2.15f), _asset, true);
        CollectionAssert.AreEqual(new[]{"left"}, selection.wholeParts);
        CollectionAssert.AreEqual(new[]{"middle"}, selection.partialParts);
        StartJob(selection);
        Assert.IsTrue(AgentSceneOps.RemoveParts(_asset.assetId, new List<string>{"middle"}).ok);
        Assert.That(_middle.fragments.Sum(c => c.Volume()), Is.EqualTo(.75).Within(1e-5));
        Assert.IsNotNull(_right);
        Assert.That(_middle.fragments.SelectMany(c => c.triangles).Min(p => p.x), Is.EqualTo(-.25f).Within(1e-5));
        var refreshed = RefinementPartSelection.AfterEdit(selection, _asset);
        Assert.Contains("middle", refreshed.outsideParts);
        Assert.IsEmpty(refreshed.partialParts);
    }

    [Test]
    public void PromptAndEmptyRegionAllowOnlyAdditionsOnTheChosenObject()
    {
        var empty = RefinementPartSelection.Draw(Box(4,1), _asset, true);
        StartJob(empty);
        var job = AgentJobState.instance.current;
        job.request = "Add a blue block here.";
        StringAssert.Contains(job.request, AgentPromptBuilder.BuildTaskJson(job,60));
        Assert.IsFalse(AgentSceneOps.RemoveParts(_asset.assetId,new List<string>{"middle"}).ok);
        Assert.IsTrue(AgentSceneOps.AddParts(_asset.assetId,new List<PartSpec>{
            new(){shape="box",center=new[]{4f,0,0},size=new[]{.5f,.5f,.5f},color="#0000FF"}
        }).ok);
        Assert.IsFalse(AgentSceneOps.UpdateParts(_asset.assetId,new List<PartSpec>{
            new(){id=job.addedPartIds[0],center=new[]{6f,0,0}}
        }).ok);
    }

    [Test]
    public void PartialColorChangePreservesTheUnselectedRemainder()
    {
        // Agent tool calls are not Undo steps: they run on the preview copy, and an applied idea is
        // undone as a whole (AgentStudyWorkflowTests.ConditionB_PartialUpdateMarksTheRemainder).
        RegionSelection selection = RefinementPartSelection.Draw(Box(-.25f,.5f), _asset, true);
        StartJob(selection);
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Assert.IsTrue(AgentSceneOps.UpdateParts(_asset.assetId,new List<PartSpec>{new(){id="middle",color="#FF0000"}}).ok);
        var pieces = AgentSceneOps.PartsOf(_asset);
        Assert.AreEqual(4,pieces.Count);
        var remainder = pieces.Single(p => p != _left && p != _middle && p != _right);
        Assert.AreEqual(Color.white,remainder.color);
        Assert.AreEqual(Color.red,_middle.color);
        Assert.That(_middle.transform.localScale.x,Is.EqualTo(.5f).Within(1e-5));
        Assert.That(remainder.fragments.Sum(c=>c.Volume()),Is.EqualTo(.5).Within(1e-5));
        Assert.IsFalse(AgentSceneOps.RemoveParts(_asset.assetId,new List<string>{remainder.partId}).ok);
        Undo.FlushUndoRecordObjects();
        Undo.RevertAllDownToGroup(group);
        Assert.AreEqual(4,AgentSceneOps.PartsOf(_asset).Count, "the tool call itself adds nothing to the Undo history");
        Assert.AreEqual(Color.red,_middle.color);
    }

    [UnityTest]
    public IEnumerator SceneViewClickShiftClickAndDrawUseOneGestureSurface()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            Assert.Ignore("Scene picking requires a graphics device.");
        var view = ScriptableObject.CreateInstance<SceneView>();
        var manager = _asset.gameObject.AddComponent<RegionSelectionManager>();
        var tool = new RefinementSceneInteraction();
        var assets = new List<AgentAsset>{_asset};
        Tool previousTool = Tools.current;
        Vector2 left = default, right = default, start = default, end = default;
        Vector2 repick = default, face = default, inward = default;
        int paints = 0;
        bool calibrating = false;
        Vector2 eventOffset = Vector2.zero;
        void OnScene(SceneView drawing)
        {
            if (drawing != view) return;
            if (calibrating && Event.current.type == EventType.MouseMove)
                eventOffset = HandleUtility.WorldToGUIPoint(_left.transform.position) - Event.current.mousePosition;
            tool.DrawSelection(drawing,manager,assets);
            if (Event.current.type != EventType.Repaint) return;
            Vector2 WindowPoint(Vector3 point) =>
                GUIUtility.GUIToScreenPoint(HandleUtility.WorldToGUIPoint(point)) - view.position.position;
            left = WindowPoint(_left.transform.position);
            right = WindowPoint(_right.transform.position);
            start = WindowPoint(_asset.transform.TransformPoint(new Vector3(-.4f,-.4f,0)));
            end = WindowPoint(_asset.transform.TransformPoint(new Vector3(.15f,.4f,0)));
            repick = WindowPoint(_asset.transform.TransformPoint(new Vector3(.35f,-.2f,0)));
            var box = manager.CurrentSelection;
            if (box != null)
            {
                Vector3 side = box.rotation * Vector3.right * (box.size.x * .5f);
                face = WindowPoint(box.center + side);
                inward = WindowPoint(box.center + side * .76f);
            }
            paints++;
        }
        void Send(EventType type, Vector2 at, bool shift = false) =>
            view.SendEvent(new Event { type = type, mousePosition = at + eventOffset, button = 0, shift = shift });
        try
        {
            Tools.current = Tool.Move;
            view.Show();
            view.position = new Rect(80,80,640,480);
            view.Focus();
            view.overlayCanvas.overlaysEnabled = false;
            view.LookAt(_asset.transform.position,_asset.transform.rotation,4,true,true);
            SceneView.duringSceneGui += OnScene;
            double deadline = EditorApplication.timeSinceStartup + 4;
            while (paints < 3 && EditorApplication.timeSinceStartup < deadline)
            {
                view.Repaint();
                yield return null;
            }
            Assert.Greater(paints,0, "Scene view did not paint");
            // SendEvent uses the host window origin; calibrate for dock and toolbar offsets.
            calibrating = true;
            Send(EventType.MouseMove,left);
            calibrating = false;
            Send(EventType.MouseMove,left);
            Send(EventType.MouseDown,left);
            Send(EventType.MouseUp,left);
            yield return null;
            Assert.IsNotNull(manager.CurrentSelection,"Clicking the left leg should start a selection.");
            Assert.That(manager.CurrentSelection.wholeParts,Is.EquivalentTo(new[]{"left"}));
            Send(EventType.MouseMove,right);
            Send(EventType.MouseDown,right,true);
            Send(EventType.MouseUp,right,true);
            Assert.That(manager.CurrentSelection.wholeParts,Is.EquivalentTo(new[]{"left","right"}));
            Send(EventType.MouseMove,start);
            Send(EventType.MouseDown,start);
            Send(EventType.MouseDrag,end);
            Send(EventType.MouseUp,end);
            Assert.That(manager.CurrentSelection.partialParts,Is.EquivalentTo(new[]{"middle"}));
            Assert.IsEmpty(manager.CurrentSelection.wholeParts);

            // A face drag on a freshly picked part must cut exactly, even above 85% coverage.
            Send(EventType.MouseMove,repick);
            Send(EventType.MouseDown,repick);
            Send(EventType.MouseUp,repick);
            Assert.That(manager.CurrentSelection.wholeParts,Is.EquivalentTo(new[]{"middle"}));
            int before = paints;
            view.Repaint();
            deadline = EditorApplication.timeSinceStartup + 4;
            while (paints == before && EditorApplication.timeSinceStartup < deadline) yield return null;
            Send(EventType.MouseMove,face);
            Send(EventType.MouseDown,face);
            view.SendEvent(new Event {type=EventType.MouseDrag,button=0,mousePosition=inward+eventOffset,delta=inward-face});
            Send(EventType.MouseUp,inward);
            Assert.That(manager.CurrentSelection.partialParts,Is.EquivalentTo(new[]{"middle"}));
            Assert.That(manager.CurrentSelection.size.x,Is.InRange(.85f,.99f));
        }
        finally
        {
            SceneView.duringSceneGui -= OnScene;
            tool.Cancel();
            view.Close();
            Tools.current = previousTool;
        }
    }

    [TestCase(AgentPartShape.Sphere)]
    [TestCase(AgentPartShape.Wedge)]
    [TestCase(AgentPartShape.Cone)]
    [TestCase(AgentPartShape.Box)]
    public void ObliqueCutConservesSolidVolume(AgentPartShape shape)
    {
        _middle.shape = shape;
        var box = Box(.15f,.4f);
        box.rotation *= Quaternion.Euler(14,25,30);
        var cut = PartGeometry.Cut(_middle,box);
        Assert.That(cut.coverage,Is.InRange(.01f,.99f));
        double total = PartGeometry.Cells(_middle).Sum(c=>c.Volume());
        Assert.That(cut.inside.Concat(cut.outside).Sum(c=>c.Volume()),Is.EqualTo(total).Within(1e-5));
    }
}
