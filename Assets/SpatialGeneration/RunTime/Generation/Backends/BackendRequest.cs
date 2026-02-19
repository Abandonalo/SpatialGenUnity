using System;
using UnityEngine;

[Serializable]
public class BackendRequest
{
    public string request_id;
    public Constraint[] constraints;
}

[Serializable]
public class Constraint
{
    public string id;
    public string proxy_id;
    public string type;     // "occupy"|"avoid"|"attract"
    public string shape;    // "box"|"sphere"|"cylinder"
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 size;    // box bounds, sphere diameter, cylinder (d,h,d)
}
