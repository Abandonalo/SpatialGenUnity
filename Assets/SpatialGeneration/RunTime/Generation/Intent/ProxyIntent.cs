using System;

namespace SpatialGeneration.Generation.Intent
{
    public enum ProxyRole
    {
        Occupy,
        Avoid,
        Attract
    }

    public enum ProxyShape
    {
        Box,
        Sphere,
        Cylinder
    }

    [Serializable]
    public class ProxyIntent
    {
        public string Id = string.Empty;

        public ProxyRole Role = ProxyRole.Occupy;

        public ProxyShape Shape = ProxyShape.Box;

        public string Label = string.Empty;

        public PoseData Pose = new();

        public float Strength = 1f;

        public int Priority = 0;
    }
}
