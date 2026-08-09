using UnityEngine;

namespace SpatialGeneration.Utils
{
    /// <summary>
    /// Component lookups that respect Unity's null semantics.
    ///
    /// <c>??</c>, <c>??=</c> and <c>?.</c> use the CLR's reference check, which bypasses the
    /// <c>==</c> overload <see cref="Object"/> uses to report destroyed components as null.
    /// So <c>GetComponent&lt;T&gt;() ?? AddComponent&lt;T&gt;()</c> happily returns a
    /// destroyed component, and the failure only appears on first use — as
    /// "There is no 'Camera' attached to the game object". Explicit <c>== null</c> tests
    /// invoke the overload and behave.
    /// </summary>
    public static class ComponentUtils
    {
        /// <summary>Existing component of type <typeparamref name="T"/>, or a newly added one.</summary>
        public static T GetOrAdd<T>(GameObject target) where T : Component
        {
            T existing = target.GetComponent<T>();
            return existing != null ? existing : target.AddComponent<T>();
        }

        /// <summary>Existing component, or null. Never a destroyed one.</summary>
        public static T GetAlive<T>(GameObject target) where T : Component
        {
            T existing = target.GetComponent<T>();
            return existing != null ? existing : null;
        }
    }
}
