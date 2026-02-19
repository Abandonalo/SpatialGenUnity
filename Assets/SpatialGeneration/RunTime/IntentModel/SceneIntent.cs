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
    public string proxy_id;        
    public Vector3 position;
    public Quaternion rotation;     
    public Vector3 size;
    public SpatialProxyRole role;
}
