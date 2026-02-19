using System;
using UnityEngine;

[Serializable]
public class BackendResponse
{
    public GeneratedObjectSpec[] objects;
}

[Serializable]
public class GeneratedObjectSpec
{
    public string primitive; // "cube"|"sphere"|"cylinder"
    public Vector3 position;
    public Vector3 size;
}
