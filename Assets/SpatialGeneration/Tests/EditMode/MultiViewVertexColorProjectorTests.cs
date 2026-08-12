using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

public class MultiViewVertexColorProjectorTests
{
    private readonly List<string> files = new();
    private readonly List<GameObject> objects = new();

    [TearDown]
    public void TearDown()
    {
        foreach (GameObject value in objects)
            if (value != null) UnityEngine.Object.DestroyImmediate(value);
        foreach (string file in files)
            if (File.Exists(file)) File.Delete(file);
    }

    [Test]
    public void ChoosesTheViewWhoseCameraFacesTheVertexNormal()
    {
        GameObject target = TargetTriangle(Vector3.right);
        Camera front = CameraAt(Vector3.right * 4f, Vector3.left);
        Camera back = CameraAt(Vector3.left * 4f, Vector3.right);
        Camera left = CameraAt(Vector3.back * 4f, Vector3.forward);
        Camera right = CameraAt(Vector3.forward * 4f, Vector3.back);
        var views = new List<RefinedViewProjection>
        {
            Projection("Front", front, SolidPng(Color.red)),
            Projection("Back", back, SolidPng(Color.green)),
            Projection("Left", left, SolidPng(Color.blue)),
            Projection("Right", right, SolidPng(Color.yellow)),
        };

        int coloured = MultiViewVertexColorProjector.Apply(target, views, string.Empty);
        Color[] colors = target.GetComponent<MeshFilter>().sharedMesh.colors;

        Assert.AreEqual(3, coloured);
        foreach (Color color in colors)
        {
            Assert.Greater(color.r, 0.99f);
            Assert.Less(color.g, 0.01f);
            Assert.Less(color.b, 0.01f);
        }
    }

    [Test]
    public void RejectsAProjectionOutsideThatViewsCrop()
    {
        GameObject target = TargetTriangle(Vector3.right);
        Camera front = CameraAt(Vector3.right * 4f, Vector3.left);
        RefinedViewProjection view = Projection("Front", front, SolidPng(Color.red));
        view.cropMinX = 0.8f;
        view.cropMinY = 0.8f;
        view.cropMaxX = 1f;
        view.cropMaxY = 1f;

        int coloured = MultiViewVertexColorProjector.Apply(target, new[] { view }, string.Empty);

        Assert.AreEqual(0, coloured);
        foreach (Color color in target.GetComponent<MeshFilter>().sharedMesh.colors)
            Assert.AreEqual(Color.white, color);
    }

    [Test]
    public void UsesCaptureTimeMatricesAfterTheCameraHasGone()
    {
        GameObject target = TargetTriangle(Vector3.right);
        Camera front = CameraAt(Vector3.right * 4f, Vector3.left);
        RefinedViewProjection view = Projection("Front", front, SolidPng(Color.red));
        view.worldToCameraMatrix = front.worldToCameraMatrix;
        view.projectionMatrix = front.projectionMatrix;
        view.cameraPosition = front.transform.position;
        view.hasStoredProjection = true;
        UnityEngine.Object.DestroyImmediate(front.gameObject);
        view.camera = null;

        int coloured = MultiViewVertexColorProjector.Apply(target, new[] { view }, string.Empty);

        Assert.AreEqual(3, coloured);
        foreach (Color color in target.GetComponent<MeshFilter>().sharedMesh.colors)
            Assert.Greater(color.r, 0.99f);
    }

    private GameObject TargetTriangle(Vector3 normal)
    {
        var target = new GameObject("Target");
        objects.Add(target);
        var mesh = new Mesh
        {
            vertices = new[]
            {
                new Vector3(0f, -0.25f, -0.25f),
                new Vector3(0f, 0.25f, -0.25f),
                new Vector3(0f, 0f, 0.25f),
            },
            normals = new[] { normal, normal, normal },
            triangles = new[] { 0, 1, 2 },
        };
        target.AddComponent<MeshFilter>().sharedMesh = mesh;
        target.AddComponent<MeshRenderer>();
        return target;
    }

    private Camera CameraAt(Vector3 position, Vector3 forward)
    {
        var go = new GameObject($"Camera_{objects.Count}");
        objects.Add(go);
        Camera camera = go.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = 1f;
        camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));
        return camera;
    }

    private RefinedViewProjection Projection(string type, Camera camera, string path) => new()
    {
        viewType = type,
        camera = camera,
        imageAbsolutePath = path,
        cropMinX = 0f,
        cropMinY = 0f,
        cropMaxX = 1f,
        cropMaxY = 1f,
    };

    private string SolidPng(Color colour)
    {
        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var colors = new Color[16];
        Array.Fill(colors, colour);
        texture.SetPixels(colors);
        texture.Apply();
        string path = Path.Combine(Path.GetTempPath(), $"spatialgen_colour_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
        files.Add(path);
        return path;
    }
}
