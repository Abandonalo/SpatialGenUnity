using System.Threading.Tasks;
using UnityEngine;
using NewBackendRequest = SpatialGeneration.Generation.Intent.BackendRequest;

public class MockGenerationBackend : IGenerationBackend
{
    public string Name => "Mock";

    public Task EnsureReadyAsync() => Task.CompletedTask;

    public Task<GenerationResult> GenerateAsync(NewBackendRequest request)
    {
        var result = new GenerationResult();

        if (request?.LegacyConstraints == null)
            return Task.FromResult(result);

        foreach (var c in request.LegacyConstraints)
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
