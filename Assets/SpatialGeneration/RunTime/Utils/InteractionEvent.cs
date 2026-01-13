using System;
using UnityEngine;

[Serializable]
public class InteractionEvent
{
    public string type;            // "proxy_create", "proxy_resize", "proxy_role_change", "generate", "cleanup"
    public string session_id;      // set automatically by logger
    public double t;              // EditorApplication.timeSinceStartup (set by logger)
    public string proxy_id;        // stable per proxy
    public Vector3 position;
    public Vector3 size;
    public string role;
    public string extra;           // optional payload
    public string unity;           // session_start only
    public string project;         // session_start only
}
