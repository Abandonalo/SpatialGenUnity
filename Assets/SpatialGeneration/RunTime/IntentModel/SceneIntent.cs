using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class SceneIntent
{
    public List<SpatialProxyIntent> spatialProxies = new();

    public string ToJson()
    {
        return JsonUtility.ToJson(this, true);
    }
}

[Serializable]
public class SpatialProxyIntent
{
    public Vector3 position;
    public Vector3 size;
    public SpatialProxyRole role;
}
