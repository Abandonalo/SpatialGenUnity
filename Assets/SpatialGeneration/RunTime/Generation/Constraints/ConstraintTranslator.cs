using System;
using System.Collections.Generic;
using UnityEngine;

public static class ConstraintTranslator
{
    public static BackendRequest Build(SceneIntent intent)
    {
        var constraints = new List<Constraint>();

        foreach (var p in intent.spatialProxies)
        {
            string type = p.role.ToString().ToLowerInvariant();
            string shape = p.role switch
            {
                SpatialProxyRole.Occupy => "box",
                SpatialProxyRole.Avoid => "sphere",
                SpatialProxyRole.Attract => "cylinder",
                _ => "box"
            };

            constraints.Add(new Constraint
            {
                id = Guid.NewGuid().ToString("N"),
                proxy_id = p.proxy_id,
                type = type,
                shape = shape,
                position = p.position,
                rotation = p.rotation,
                size = p.size
            });
        }

        return new BackendRequest
        {
            request_id = Guid.NewGuid().ToString("N"),
            constraints = constraints.ToArray()
        };
    }
}
