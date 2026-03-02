using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using NewBackendRequest = SpatialGeneration.Generation.Intent.BackendRequest;

public class LocalFileGenerationBackend : IGenerationBackend
{
    public string Name => "LocalFile";
    private readonly BackendSettings _s;

    public LocalFileGenerationBackend(BackendSettings settings) => _s = settings;

    public async Task<GenerationResult> GenerateAsync(NewBackendRequest request)
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string dir = Path.Combine(projectRoot, _s.handoffFolder);
        Directory.CreateDirectory(dir);

        string reqPath = Path.Combine(dir, _s.requestFileName);
        string respPath = Path.Combine(dir, _s.responseFileName);

        // avoid stale responses
        if (File.Exists(respPath)) File.Delete(respPath);

        // write request
        File.WriteAllText(reqPath, JsonUtility.ToJson(request, true));

        // wait for response
        float waited = 0f;
        while (!File.Exists(respPath))
        {
            await Task.Delay((int)(_s.pollIntervalSeconds * 1000f));
            waited += _s.pollIntervalSeconds;
            if (waited > _s.maxWaitSeconds)
                throw new System.Exception($"Local backend timeout waiting for {respPath}");
        }

        string json = File.ReadAllText(respPath);
        var resp = JsonUtility.FromJson<BackendResponse>(json);

        // optional: delete so next run is clean
        File.Delete(respPath);

        return Convert(resp);
    }

    private GenerationResult Convert(BackendResponse resp)
    {
        var result = new GenerationResult();
        if (resp?.objects == null) return result;

        foreach (var o in resp.objects)
        {
            PrimitiveType type = (o.primitive ?? "").ToLowerInvariant() switch
            {
                "sphere" => PrimitiveType.Sphere,
                "cylinder" => PrimitiveType.Cylinder,
                _ => PrimitiveType.Cube
            };

            result.objects.Add(new GeneratedObject
            {
                primitiveType = type,
                position = o.position,
                size = o.size
            });
        }

        return result;
    }
}
