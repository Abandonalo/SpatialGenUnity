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

    [SerializeField] private string proxyId;
    public string ProxyId => proxyId;

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (string.IsNullOrEmpty(proxyId))
            proxyId = System.Guid.NewGuid().ToString("N");
    }
#endif
}