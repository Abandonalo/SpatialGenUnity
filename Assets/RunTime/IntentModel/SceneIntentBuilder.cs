using UnityEngine;

public static class SceneIntentBuilder
{
    public static SceneIntent Build()
    {
        SceneIntent intent = new SceneIntent();

        SpatialProxy[] proxies = Object.FindObjectsByType<SpatialProxy>(
            FindObjectsSortMode.None
        );

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
