using System;
using System.Collections.Generic;

namespace SpatialGeneration.Generation.Intent
{
    [Serializable]
    public class ConstraintSet
    {
        public List<Constraint> Constraints = new();

        public string ConflictPolicy = "avoid_wins";
    }
}
