using System.Threading.Tasks;
using UnityEngine;
using NewBackendRequest = SpatialGeneration.Generation.Intent.BackendRequest;
using ProxyShape = SpatialGeneration.Generation.Intent.ProxyShape;

public class MockGenerationBackend : IGenerationBackend
{
    public string Name => "Mock";

    public Task EnsureReadyAsync() => Task.CompletedTask;

    public Task<GenerationResult> GenerateAsync(NewBackendRequest request)
    {
        var result = new GenerationResult();

        if (request?.ProxyConstraints == null)
            return Task.FromResult(result);

        foreach (var c in request.ProxyConstraints)
        {
            if (c == null)
                continue;

            PrimitiveType type = c.Shape switch
            {
                ProxyShape.Sphere => PrimitiveType.Sphere,
                ProxyShape.Cylinder => PrimitiveType.Cylinder,
                _ => PrimitiveType.Cube
            };

            result.objects.Add(new GeneratedObject
            {
                primitiveType = type,
                position = c.Position == null ? Vector3.zero : new Vector3(c.Position.X, c.Position.Y, c.Position.Z),
                size = c.Size == null ? Vector3.one : new Vector3(c.Size.X, c.Size.Y, c.Size.Z)
            });
        }

        return Task.FromResult(result);
    }
}
