using System.Threading.Tasks;
using UnityEngine;

public class MockGenerationBackend : IGenerationBackend
{
    public string Name => "Mock";

    public Task<GenerationResult> GenerateAsync(BackendRequest request)
    {
        var result = new GenerationResult();

        if (request?.constraints == null)
            return Task.FromResult(result);

        foreach (var c in request.constraints)
        {
            PrimitiveType type = (c.shape ?? "").ToLowerInvariant() switch
            {
                "sphere" => PrimitiveType.Sphere,
                "cylinder" => PrimitiveType.Cylinder,
                _ => PrimitiveType.Cube
            };

            result.objects.Add(new GeneratedObject
            {
                primitiveType = type,
                position = c.position,
                size = c.size
            });
        }

        return Task.FromResult(result);
    }
}
