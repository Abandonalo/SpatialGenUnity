using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using SpatialGeneration.Generation.Intent;

/// <summary>
/// Contract tests for the authoring model: defaults, JSON stability across the Unity/Python
/// boundary, and role-to-constraint translation.
/// </summary>
public class IntentContractsTests
{
    [Test]
    public void Defaults_AreCorrect()
    {
        SceneIntent sceneIntent = new();
        ProxyIntent proxyIntent = new();
        ConstraintSet constraintSet = new();

        Assert.AreEqual(SceneStage.Creation, sceneIntent.Stage);
        Assert.AreEqual("meters", sceneIntent.Units);
        Assert.AreEqual("unity_world", sceneIntent.Frame);
        Assert.IsEmpty(sceneIntent.Proxies);

        Assert.AreEqual(ProxyRole.Occupy, proxyIntent.Role);
        Assert.AreEqual(ProxyShape.Box, proxyIntent.Shape);
        Assert.AreEqual(1f, proxyIntent.Strength);
        Assert.IsNotNull(proxyIntent.Pose);

        Assert.IsEmpty(constraintSet.Constraints);
        Assert.AreEqual("avoid_wins", constraintSet.ConflictPolicy);
    }

    [Test]
    public void SceneIntent_RoundTrip_IsStable()
    {
        SceneIntent sceneIntent = new() { Stage = SceneStage.Refinement };
        sceneIntent.Proxies.Add(new ProxyIntent
        {
            Id = "proxy_001",
            Role = ProxyRole.Attract,
            Shape = ProxyShape.Cylinder,
            Label = "vending_machine",
            AssetPrompt = "stylized vending machine with neon decals",
            Strength = 0.7f,
            Priority = 3
        });

        Assert.IsTrue(IntentJson.HasStableSceneIntentRoundTrip(sceneIntent));
    }

    [Test]
    public void ConstraintSet_RoundTrip_IsStable()
    {
        ConstraintSet constraintSet = new();
        constraintSet.Constraints.Add(new Constraint
        {
            Type = ConstraintType.OccupyVolume,
            ProxyId = "proxy_001",
            TargetLabel = "vending_machine",
            Weight = 0.8f,
            Priority = 2
        });

        Assert.IsTrue(IntentJson.HasStableConstraintSetRoundTrip(constraintSet));
    }

    [Test]
    public void ProxyFields_SurviveRoundTrip()
    {
        SceneIntent sceneIntent = new();
        sceneIntent.Proxies.Add(new ProxyIntent
        {
            Id = "proxy_critical",
            Role = ProxyRole.Avoid,
            Shape = ProxyShape.Sphere,
            AssetPrompt = "keep this space empty",
            Strength = 0.35f,
            Priority = 9
        });

        ProxyIntent proxy = IntentJson
            .DeserializeSceneIntent(IntentJson.SerializeSceneIntent(sceneIntent))
            .Proxies[0];

        Assert.AreEqual("proxy_critical", proxy.Id);
        Assert.AreEqual(ProxyRole.Avoid, proxy.Role);
        Assert.AreEqual(ProxyShape.Sphere, proxy.Shape);
        Assert.AreEqual("keep this space empty", proxy.AssetPrompt);
        Assert.AreEqual(0.35f, proxy.Strength);
        Assert.AreEqual(9, proxy.Priority);
    }

    [Test]
    public void Translator_MapsEachRole_ToItsConstraintType()
    {
        SceneIntent sceneIntent = new();
        sceneIntent.Proxies.Add(new ProxyIntent { Id = "box", Role = ProxyRole.Occupy, Shape = ProxyShape.Box, Strength = 0.8f });
        sceneIntent.Proxies.Add(new ProxyIntent { Id = "sphere", Role = ProxyRole.Avoid, Shape = ProxyShape.Sphere, Strength = 0.4f });
        sceneIntent.Proxies.Add(new ProxyIntent { Id = "cylinder", Role = ProxyRole.Attract, Shape = ProxyShape.Cylinder, Strength = 0.6f });

        ConstraintSet constraintSet = ConstraintTranslator.Translate(sceneIntent);

        Assert.AreEqual(3, constraintSet.Constraints.Count);
        Assert.AreEqual(ConstraintType.OccupyVolume, constraintSet.Constraints.Find(c => c.ProxyId == "box").Type);
        Assert.AreEqual(ConstraintType.KeepEmpty, constraintSet.Constraints.Find(c => c.ProxyId == "sphere").Type);
        Assert.AreEqual(ConstraintType.FocusRegion, constraintSet.Constraints.Find(c => c.ProxyId == "cylinder").Type);
    }

    [Test]
    public void Validate_ReportsUnknownProxyId()
    {
        SceneIntent sceneIntent = new();
        sceneIntent.Proxies.Add(new ProxyIntent { Id = "known_proxy" });

        ConstraintSet constraintSet = new();
        constraintSet.Constraints.Add(new Constraint
        {
            Type = ConstraintType.OccupyVolume,
            ProxyId = "missing_proxy",
            Weight = 0.5f
        });

        List<string> problems = constraintSet.Validate(sceneIntent);
        Assert.IsTrue(problems.Exists(p => p.Contains("unknown ProxyId")));
    }

    [Test]
    public void Validate_ReportsWeightOutOfRange()
    {
        SceneIntent sceneIntent = new();
        sceneIntent.Proxies.Add(new ProxyIntent { Id = "proxy" });

        ConstraintSet constraintSet = new();
        constraintSet.Constraints.Add(new Constraint { ProxyId = "proxy", Weight = 1.5f });

        Assert.IsTrue(constraintSet.Validate(sceneIntent).Exists(p => p.Contains("outside [0,1]")));
    }

    [Test]
    public void Validate_PassesForConsistentSet()
    {
        SceneIntent sceneIntent = new();
        sceneIntent.Proxies.Add(new ProxyIntent { Id = "proxy" });

        ConstraintSet constraintSet = new();
        constraintSet.Constraints.Add(new Constraint { ProxyId = "proxy", Weight = 1f });

        Assert.IsEmpty(constraintSet.Validate(sceneIntent));
    }
}
