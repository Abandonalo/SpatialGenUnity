using System.Collections.Generic;
using UnityEngine;

public class GenerationResult
{
    public List<GeneratedObject> objects = new();
    public List<string> outputFiles = new();
}

public class GeneratedObject
{
    public Vector3 position;
    public Vector3 size;
    public PrimitiveType primitiveType;
}
