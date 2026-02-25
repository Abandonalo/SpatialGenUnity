using UnityEngine;

public enum SpatialProxyRole
{
    Occupy,
    Avoid,
    Attract
}

public enum SpatialProxyShape
{
    Box,
    Sphere,
    Cylinder
}

public class SpatialProxy : MonoBehaviour
{
    public SpatialProxyRole role = SpatialProxyRole.Occupy;
    public string label = "";
    [Range(0f, 1f)] public float strength = 1f;
    public int priority = 0;
    public Vector3 size = Vector3.one;

    [SerializeField] private string proxyId;
    public string ProxyId => proxyId;
    public SpatialProxyShape Shape => InferShapeFromRole(role);

    public static SpatialProxyShape InferShapeFromRole(SpatialProxyRole proxyRole)
    {
        return proxyRole switch
        {
            SpatialProxyRole.Occupy => SpatialProxyShape.Box,
            SpatialProxyRole.Avoid => SpatialProxyShape.Sphere,
            SpatialProxyRole.Attract => SpatialProxyShape.Cylinder,
            _ => SpatialProxyShape.Box
        };
    }

    private void Awake()
    {
        EnsureProxyId();
        strength = Mathf.Clamp01(strength);
    }

    private void Reset()
    {
        EnsureProxyId();
        strength = Mathf.Clamp01(strength);
    }

    private void EnsureProxyId()
    {
        if (string.IsNullOrEmpty(proxyId))
            proxyId = System.Guid.NewGuid().ToString("N");
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        EnsureProxyId();
        strength = Mathf.Clamp01(strength);
    }
#endif
}