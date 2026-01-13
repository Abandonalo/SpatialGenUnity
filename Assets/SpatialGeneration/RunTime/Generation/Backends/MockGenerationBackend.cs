using UnityEngine;

public class MockGenerationBackend : IGenerationBackend
{
    public GenerationResult Generate(SceneIntent intent)
    {
        GenerationResult result = new GenerationResult();

        foreach (var proxy in intent.spatialProxies)
        {
            result.objects.Add(new GeneratedObject
            {
                position = proxy.position,
                size = proxy.size,
                primitiveType = MapRoleToPrimitive(proxy.role)
            });
        }

        return result;
    }

    private PrimitiveType MapRoleToPrimitive(SpatialProxyRole role)
    {
        return role switch
        {
            SpatialProxyRole.Occupy => PrimitiveType.Cube,
            SpatialProxyRole.Avoid => PrimitiveType.Sphere,
            SpatialProxyRole.Attract => PrimitiveType.Cylinder,
            _ => PrimitiveType.Cube
        };
    }
}

