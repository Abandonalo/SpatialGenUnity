using System;
using UnityEngine;

namespace SpatialGeneration.Generation.Intent
{
    [Serializable]
    public class CompiledConstraints
    {
        public Texture2D MaskOccupy;
        public Texture2D MaskAvoid;
        public Texture2D MaskFocus;
        public string ConstraintJson = string.Empty;
    }
}
