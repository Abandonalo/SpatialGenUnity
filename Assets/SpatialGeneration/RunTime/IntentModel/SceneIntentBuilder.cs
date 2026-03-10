using UnityEngine;

public static class SceneIntentBuilder
{
    public static SceneIntent Build()
    {
        SceneIntent intent = new SceneIntent();

        SpatialProxy[] proxies = Object.FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None);

        foreach (var proxy in proxies)
        {
            intent.spatialProxies.Add(new SpatialProxyIntent
            {
                proxy_id = proxy.ProxyId,
                position = proxy.transform.position,
                rotation = proxy.transform.rotation,
                size = Vector3.Scale(proxy.size, proxy.transform.lossyScale),
                role = proxy.role,
                shape = proxy.Shape,
                label = proxy.label,
                asset_prompt = proxy.assetPrompt,
                strength = proxy.strength,
                priority = proxy.priority
            });
        }

        return intent;
    }
}
