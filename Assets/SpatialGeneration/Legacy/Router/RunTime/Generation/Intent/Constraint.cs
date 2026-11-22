using System;

namespace SpatialGeneration.Generation.Intent
{
    public enum ConstraintType
    {
        OccupyVolume,
        KeepEmpty,
        FocusRegion
    }

    [Serializable]
    public class Constraint
    {
        public ConstraintType Type;

        public string ProxyId = string.Empty;

        public string TargetLabel = string.Empty;

        public string Mode = string.Empty;

        public Vector3Data AxisHint;

        public float Weight = 1f;

        public int Priority = 0;
    }
}
