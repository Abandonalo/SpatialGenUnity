using UnityEngine;

public static class SceneIntentBuilder
{
    public static SceneIntent Build()
    {
        SceneIntent intent = new SceneIntent();

        SpatialProxy[] proxies = Object.FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None);

        foreach (var proxy in proxies)
        {
            Vector3 worldSize = Vector3.Scale(proxy.size, proxy.transform.lossyScale);

            intent.spatialProxies.Add(new SpatialProxyIntent
{
    proxy_id = proxy.ProxyId,
    position = proxy.transform.position,
    rotation = proxy.transform.rotation,
    size = Vector3.Scale(proxy.size, proxy.transform.lossyScale),
    role = proxy.role
});


        }

        return intent;
    }
}
