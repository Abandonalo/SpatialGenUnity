using UnityEngine;

public enum SpatialProxyRole
{
    Occupy,
    Avoid,
    Attract
}

public class SpatialProxy : MonoBehaviour
{
    public SpatialProxyRole role = SpatialProxyRole.Occupy;

    // Interpreted differently per role:
    // Occupy: box size (x,y,z)
    // Avoid: sphere diameter uses max(x,y,z)
    // Attract: cylinder bounds (x = diameter, y = height, z = diameter)
    public Vector3 size = Vector3.one;
}
