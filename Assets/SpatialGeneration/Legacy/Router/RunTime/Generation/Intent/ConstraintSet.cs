using System;
using System.Collections.Generic;

namespace SpatialGeneration.Generation.Intent
{
    [Serializable]
    public class ConstraintSet
    {
        public List<Constraint> Constraints = new();

        public string ConflictPolicy = "avoid_wins";

        /// <summary>
        /// Checks the translated constraints against the intent they came from. Catches the
        /// failure modes that would otherwise surface as a silently wrong generation:
        /// constraints pointing at proxies that no longer exist, and weights outside [0,1].
        /// </summary>
        public List<string> Validate(SceneIntent sceneIntent)
        {
            var problems = new List<string>();

            var knownProxyIds = new HashSet<string>(StringComparer.Ordinal);
            if (sceneIntent?.Proxies != null)
            {
                foreach (ProxyIntent proxy in sceneIntent.Proxies)
                {
                    if (proxy != null && !string.IsNullOrWhiteSpace(proxy.Id))
                        knownProxyIds.Add(proxy.Id);
                }
            }

            for (int i = 0; i < Constraints.Count; i++)
            {
                Constraint constraint = Constraints[i];
                if (constraint == null)
                {
                    problems.Add($"Constraint[{i}] is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(constraint.ProxyId))
                    problems.Add($"Constraint[{i}] has an empty ProxyId.");
                else if (!knownProxyIds.Contains(constraint.ProxyId))
                    problems.Add($"Constraint[{i}] references unknown ProxyId '{constraint.ProxyId}'.");

                if (constraint.Weight < 0f || constraint.Weight > 1f)
                    problems.Add($"Constraint[{i}] weight {constraint.Weight:0.###} is outside [0,1].");
            }

            return problems;
        }
    }
}
