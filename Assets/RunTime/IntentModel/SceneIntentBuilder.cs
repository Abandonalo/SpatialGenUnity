using UnityEngine;

public static class SceneIntentBuilder
{
    public static SceneIntent Build()
    {
        SceneIntent intent = new SceneIntent();

        SpatialProxy[] proxies = Object.FindObjectsOfType<SpatialProxy>();
        foreach (var proxy in proxies)
        {
            intent.spatialProxies.Add(new SpatialProxyIntent
            {
                position = proxy.transform.position,
                size = proxy.size,
                role = proxy.role
            });
        }

        return intent;
    }
}
