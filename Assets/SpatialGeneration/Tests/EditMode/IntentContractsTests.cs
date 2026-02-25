using NUnit.Framework;
using ConstraintModel = SpatialGeneration.Generation.Intent.Constraint;
using ConstraintSetModel = SpatialGeneration.Generation.Intent.ConstraintSet;
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
        SceneIntentModel sceneIntent = new()
        {
            Stage = SceneStageModel.Refinement
        };

        sceneIntent.Proxies.Add(new ProxyIntentModel
        {
            Id = "proxy_001",
            Role = ProxyRoleModel.Attract,
            Shape = ProxyShapeModel.Cylinder,
            Label = "vending_machine",
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
            Strength = 0.35f,
            Priority = 9
        });

        string json = IntentJsonModel.SerializeSceneIntent(sceneIntent);
        SceneIntentModel deserialized = IntentJsonModel.DeserializeSceneIntent(json);

        Assert.AreEqual(1, deserialized.Proxies.Count);
        ProxyIntentModel proxy = deserialized.Proxies[0];

        Assert.AreEqual("proxy_critical", proxy.Id);
        Assert.AreEqual(ProxyRoleModel.Avoid, proxy.Role);
        Assert.AreEqual(ProxyShapeModel.Sphere, proxy.Shape);
        Assert.AreEqual(0.35f, proxy.Strength);
        Assert.AreEqual(9, proxy.Priority);
    }
}
