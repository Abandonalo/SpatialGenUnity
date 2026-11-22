using System;

namespace SpatialGeneration.Generation.Intent
{
    [Serializable]
    public class PoseData
    {
        public Vector3Data Position = Vector3Data.Zero;

        public QuaternionData Rotation = QuaternionData.Identity;

        public Vector3Data Scale = Vector3Data.One;
    }

    [Serializable]
    public class Vector3Data
    {
        public float X;

        public float Y;

        public float Z;

        public static Vector3Data Zero => new() { X = 0f, Y = 0f, Z = 0f };
        public static Vector3Data One => new() { X = 1f, Y = 1f, Z = 1f };
    }

    [Serializable]
    public class QuaternionData
    {
        public float X;

        public float Y;

        public float Z;

        public float W = 1f;

        public static QuaternionData Identity => new() { X = 0f, Y = 0f, Z = 0f, W = 1f };
    }
}
