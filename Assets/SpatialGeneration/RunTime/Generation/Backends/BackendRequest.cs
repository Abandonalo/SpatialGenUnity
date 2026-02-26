using System;
using UnityEngine;

[Serializable]
public class BackendRequest
{
    public string request_id;
    public Constraint[] constraints;
    public string prompt;
    public string negativePrompt;
    public string constraintSetJson;
    public string depthImagePath;
    public string cannyImagePath;
    public string[] maskImagePaths;
    public string maskOccupyImagePath;
    public string maskAvoidImagePath;
    public string maskFocusImagePath;
    public float maskOccupyWeight = 1f;
    public float maskAvoidWeight = 1f;
    public float maskFocusWeight = 1f;
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
