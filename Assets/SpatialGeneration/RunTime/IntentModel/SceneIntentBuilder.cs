using UnityEngine;

public static class SceneIntentBuilder
{
    public static SceneIntent Build()
    {
        SceneIntent intent = new SceneIntent();

        SpatialProxy[] proxies = Object.FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None);

        foreach (var proxy in proxies)
        {
            // Bake transform scale into size so proxy & generated match visually
            Vector3 worldSize = Vector3.Scale(proxy.size, proxy.transform.lossyScale);

            intent.spatialProxies.Add(new SpatialProxyIntent
            {
                position = proxy.transform.position,
                size = worldSize,
                role = proxy.role
            });
        }

        return intent;
    }
}
