using System;
using System.Collections.Generic;

namespace SpatialGeneration.Generation.Intent
{
    public enum SceneStage
    {
        Creation,
        Refinement
    }

    [Serializable]
    public class SceneIntent
    {
        public SceneStage Stage = SceneStage.Creation;

        public List<ProxyIntent> Proxies = new();

        public CameraIntent Camera;

        public string Units = "meters";

        public string Frame = "unity_world";
    }

    [Serializable]
    public class CameraIntent
    {
        public PoseData Pose = new();

        public float FieldOfViewDeg = 60f;
    }
}
