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
    public Vector3 size = Vector3.one;
}
