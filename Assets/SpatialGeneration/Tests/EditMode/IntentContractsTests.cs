using NUnit.Framework;
using UnityEngine;
using BackendRequestBuilderModel = SpatialGeneration.Generation.Intent.BackendRequestBuilder;
using BackendRequestModel = SpatialGeneration.Generation.Intent.BackendRequest;
using ConstraintModel = SpatialGeneration.Generation.Intent.Constraint;
using ConstraintCompilerModel = SpatialGeneration.Generation.Intent.ConstraintCompiler;
using ConstraintDebugValidatorModel = SpatialGeneration.Generation.Intent.ConstraintDebugValidator;
using ConstraintSetModel = SpatialGeneration.Generation.Intent.ConstraintSet;
using ConstraintTranslatorModel = SpatialGeneration.Generation.Intent.ConstraintTranslator;
using ConstraintTypeModel = SpatialGeneration.Generation.Intent.ConstraintType;
using IntentJsonModel = SpatialGeneration.Generation.Intent.IntentJson;
using ProxyIntentModel = SpatialGeneration.Generation.Intent.ProxyIntent;
using ProxyRoleModel = SpatialGeneration.Generation.Intent.ProxyRole;
using ProxyShapeModel = SpatialGeneration.Generation.Intent.ProxyShape;
using SceneIntentModel = SpatialGeneration.Generation.Intent.SceneIntent;
using SceneStageModel = SpatialGeneration.Generation.Intent.SceneStage;

public class IntentContractsTests
{
    [Test]
    public void Defaults_AreCorrect()
    {
        SceneIntentModel sceneIntent = new();
        ProxyIntentModel proxyIntent = new();
        ConstraintSetModel constraintSet = new();

        Assert.AreEqual(SceneStageModel.Creation, sceneIntent.Stage);
        Assert.AreEqual("meters", sceneIntent.Units);
        Assert.AreEqual("unity_world", sceneIntent.Frame);
        Assert.IsNotNull(sceneIntent.Proxies);
        Assert.AreEqual(0, sceneIntent.Proxies.Count);

        Assert.AreEqual(string.Empty, proxyIntent.Id);
        Assert.AreEqual(ProxyRoleModel.Occupy, proxyIntent.Role);
        Assert.AreEqual(ProxyShapeModel.Box, proxyIntent.Shape);
        Assert.AreEqual(string.Empty, proxyIntent.Label);
        Assert.AreEqual(string.Empty, proxyIntent.AssetPrompt);
        Assert.AreEqual(1f, proxyIntent.Strength);
        Assert.AreEqual(0, proxyIntent.Priority);
        Assert.IsNotNull(proxyIntent.Pose);

        Assert.IsNotNull(constraintSet.Constraints);
        Assert.AreEqual(0, constraintSet.Constraints.Count);
        Assert.AreEqual("avoid_wins", constraintSet.ConflictPolicy);
    }

    [Test]
    public void SceneIntent_StableRoundTrip_IsByteEqual()
    {
        SceneIntentModel sceneIntent = new() { Stage = SceneStageModel.Refinement };
        sceneIntent.Proxies.Add(new ProxyIntentModel
        {
            Id = "proxy_001",
            Role = ProxyRoleModel.Attract,
            Shape = ProxyShapeModel.Cylinder,
            Label = "vending_machine",
            AssetPrompt = "stylized vending machine with neon decals",
            Strength = 0.7f,
            Priority = 3
        });

        Assert.IsTrue(IntentJsonModel.HasStableSceneIntentRoundTrip(sceneIntent));
    }

    [Test]
    public void ConstraintSet_StableRoundTrip_IsByteEqual()
    {
        ConstraintSetModel constraintSet = new();
        constraintSet.Constraints.Add(new ConstraintModel
        {
            Type = ConstraintTypeModel.OccupyVolume,
            ProxyId = "proxy_001",
            TargetLabel = "vending_machine",
            Weight = 0.8f,
            Priority = 2
        });

        Assert.IsTrue(IntentJsonModel.HasStableConstraintSetRoundTrip(constraintSet));
    }

    [Test]
    public void ProxyRequiredFields_SurviveRoundTrip()
    {
        SceneIntentModel sceneIntent = new();
        sceneIntent.Proxies.Add(new ProxyIntentModel
        {
            Id = "proxy_critical",
            Role = ProxyRoleModel.Avoid,
            Shape = ProxyShapeModel.Sphere,
            AssetPrompt = "keep this space empty",
            Strength = 0.35f,
            Priority = 9
        });

        string json = IntentJsonModel.SerializeSceneIntent(sceneIntent);
        SceneIntentModel deserialized = IntentJsonModel.DeserializeSceneIntent(json);
        ProxyIntentModel proxy = deserialized.Proxies[0];

        Assert.AreEqual("proxy_critical", proxy.Id);
        Assert.AreEqual(ProxyRoleModel.Avoid, proxy.Role);
        Assert.AreEqual(ProxyShapeModel.Sphere, proxy.Shape);
        Assert.AreEqual("keep this space empty", proxy.AssetPrompt);
        Assert.AreEqual(0.35f, proxy.Strength);
        Assert.AreEqual(9, proxy.Priority);
    }

    [Test]
    public void Translator_MapsThreeProxyRoles_ToExpectedConstraints()
    {
        SceneIntentModel sceneIntent = new();
        sceneIntent.Proxies.Add(new ProxyIntentModel { Id = "proxy_box", Role = ProxyRoleModel.Occupy, Shape = ProxyShapeModel.Box, Label = "vending_machine", Strength = 0.8f, Priority = 1 });
        sceneIntent.Proxies.Add(new ProxyIntentModel { Id = "proxy_sphere", Role = ProxyRoleModel.Avoid, Shape = ProxyShapeModel.Sphere, Strength = 0.4f, Priority = 2 });
        sceneIntent.Proxies.Add(new ProxyIntentModel { Id = "proxy_cylinder", Role = ProxyRoleModel.Attract, Shape = ProxyShapeModel.Cylinder, Strength = 0.6f, Priority = 3 });

        ConstraintSetModel constraintSet = ConstraintTranslatorModel.Translate(sceneIntent);
        Assert.AreEqual(3, constraintSet.Constraints.Count);
        Assert.AreEqual(ConstraintTypeModel.OccupyVolume, constraintSet.Constraints.Find(c => c.ProxyId == "proxy_box").Type);
        Assert.AreEqual(ConstraintTypeModel.KeepEmpty, constraintSet.Constraints.Find(c => c.ProxyId == "proxy_sphere").Type);
        Assert.AreEqual(ConstraintTypeModel.FocusRegion, constraintSet.Constraints.Find(c => c.ProxyId == "proxy_cylinder").Type);
    }

    [Test]
    public void AvoidWinsPolicy_RemovesOverlapFromOccupyMask()
    {
        Texture2D occupy = new(2, 1, TextureFormat.RGBA32, false);
        Texture2D avoid = new(2, 1, TextureFormat.RGBA32, false);
        occupy.SetPixel(0, 0, Color.white);
        occupy.SetPixel(1, 0, Color.white);
        occupy.Apply(false);
        avoid.SetPixel(0, 0, Color.white);
        avoid.SetPixel(1, 0, Color.black);
        avoid.Apply(false);

        ConstraintCompilerModel.ApplyAvoidWins(occupy, avoid);
        Assert.AreEqual(0f, occupy.GetPixel(0, 0).r, 1e-5f);
        Assert.AreEqual(1f, occupy.GetPixel(1, 0).r, 1e-5f);
        Object.DestroyImmediate(occupy);
        Object.DestroyImmediate(avoid);
    }

    [Test]
    public void BackendRequestBuilder_PackagesBase64MasksAndConstraintJson()
    {
        Texture2D depth = SolidTexture(4, 4, Color.gray);
        Texture2D occupy = SolidTexture(4, 4, Color.white);
        Texture2D avoid = SolidTexture(4, 4, Color.black);
        Texture2D focus = SolidTexture(4, 4, new Color(0.5f, 0.5f, 0.5f, 1f));

        ConstraintSetModel constraintSet = new();
        constraintSet.Constraints.Add(new ConstraintModel { Type = ConstraintTypeModel.OccupyVolume, ProxyId = "proxy_box", TargetLabel = "chair", Weight = 1f, Priority = 1 });

        var compiled = new SpatialGeneration.Generation.Intent.CompiledConstraints
        {
            MaskOccupy = occupy,
            MaskAvoid = avoid,
            MaskFocus = focus,
            ConstraintJson = IntentJsonModel.SerializeConstraintSet(constraintSet)
        };

        BackendRequestModel request = BackendRequestBuilderModel.Build(
            "modern living room", "low quality", depth, null, compiled, 1234, 28, 6.5f, "euler", constraintSet, "req_test");

        Assert.IsFalse(string.IsNullOrWhiteSpace(request.Payload.DepthBase64));
        Assert.IsFalse(string.IsNullOrWhiteSpace(request.Payload.MaskOccupyBase64));
        Assert.IsFalse(string.IsNullOrWhiteSpace(request.Payload.MaskAvoidBase64));
        Assert.IsFalse(string.IsNullOrWhiteSpace(request.Payload.MaskFocusBase64));
        Assert.IsFalse(string.IsNullOrWhiteSpace(request.ConstraintSetJson));
        Assert.AreEqual(4, request.Payload.Generation.Width);
        Assert.AreEqual(4, request.Payload.Generation.Height);
        Assert.IsNotNull(request.PerProxyAssetPrompts);
        Assert.IsNotNull(request.PerProxyAssetImages);

        Object.DestroyImmediate(depth);
        Object.DestroyImmediate(occupy);
        Object.DestroyImmediate(avoid);
        Object.DestroyImmediate(focus);
    }

    [Test]
    public void ConstraintDebugValidator_ReportsUnknownProxyId()
    {
        SceneIntentModel sceneIntent = new();
        sceneIntent.Proxies.Add(new ProxyIntentModel { Id = "known_proxy" });
        ConstraintSetModel constraintSet = new();
        constraintSet.Constraints.Add(new ConstraintModel { Type = ConstraintTypeModel.OccupyVolume, ProxyId = "missing_proxy", Weight = 0.5f });
        var compiled = new SpatialGeneration.Generation.Intent.CompiledConstraints
        {
            MaskOccupy = SolidTexture(4, 4, Color.white),
            MaskAvoid = SolidTexture(4, 4, Color.black),
            MaskFocus = SolidTexture(4, 4, Color.black)
        };

        var report = ConstraintDebugValidatorModel.Validate(sceneIntent, constraintSet, compiled, 4, 4);
        Assert.IsTrue(report.HasErrors);
        Assert.IsTrue(report.Errors.Exists(e => e.Contains("unknown ProxyId")));
        Object.DestroyImmediate(compiled.MaskOccupy);
        Object.DestroyImmediate(compiled.MaskAvoid);
        Object.DestroyImmediate(compiled.MaskFocus);
    }

    private static Texture2D SolidTexture(int width, int height, Color color)
    {
        Texture2D texture = new(width, height, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
        texture.SetPixels(pixels);
        texture.Apply(false);
        return texture;
    }
}
