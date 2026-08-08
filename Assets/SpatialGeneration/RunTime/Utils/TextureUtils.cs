using System;
using UnityEngine;

namespace SpatialGeneration.Utils
{
    /// <summary>
    /// Texture helpers shared by the generation and refinement capture paths.
    /// Every backend payload is a base64 PNG, so encoding lives here rather than in
    /// each renderer.
    /// </summary>
    public static class TextureUtils
    {
        /// <summary>Base64 PNG, or empty string when <paramref name="texture"/> is null.</summary>
        public static string EncodePngBase64(Texture2D texture)
        {
            if (texture == null)
                return string.Empty;

            byte[] bytes = texture.EncodeToPNG();
            return bytes == null || bytes.Length == 0 ? string.Empty : Convert.ToBase64String(bytes);
        }

        public static void WritePng(Texture2D texture, string path)
        {
            if (texture == null || string.IsNullOrWhiteSpace(path))
                return;

            byte[] bytes = texture.EncodeToPNG();
            if (bytes != null && bytes.Length > 0)
                System.IO.File.WriteAllBytes(path, bytes);
        }

        /// <summary>Reads the active contents of <paramref name="target"/> into a CPU texture.</summary>
        public static Texture2D ReadPixels(RenderTexture target, bool flipVertically = false)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            try
            {
                var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                texture.Apply(false, false);
                if (flipVertically)
                    FlipVertical(texture);
                return texture;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        /// <summary>Sobel magnitude of the red channel. Used as the Canny-style edge conditioning.</summary>
        public static Texture2D BuildEdgeMap(Texture2D source)
        {
            if (source == null)
                return null;

            int width = source.width;
            int height = source.height;
            Color[] src = source.GetPixels();
            var dst = new Color[src.Length];

            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    float gx =
                        -src[(y - 1) * width + (x - 1)].r + src[(y - 1) * width + (x + 1)].r +
                        -2f * src[y * width + (x - 1)].r + 2f * src[y * width + (x + 1)].r +
                        -src[(y + 1) * width + (x - 1)].r + src[(y + 1) * width + (x + 1)].r;
                    float gy =
                        src[(y - 1) * width + (x - 1)].r + 2f * src[(y - 1) * width + x].r + src[(y - 1) * width + (x + 1)].r -
                        src[(y + 1) * width + (x - 1)].r - 2f * src[(y + 1) * width + x].r - src[(y + 1) * width + (x + 1)].r;

                    float edge = Mathf.Clamp01(Mathf.Sqrt(gx * gx + gy * gy));
                    dst[y * width + x] = new Color(edge, edge, edge, 1f);
                }
            }

            // Borders stay black: the 3x3 kernel has no valid neighbourhood there.
            for (int x = 0; x < width; x++)
            {
                dst[x] = Color.black;
                dst[(height - 1) * width + x] = Color.black;
            }
            for (int y = 0; y < height; y++)
            {
                dst[y * width] = Color.black;
                dst[y * width + (width - 1)] = Color.black;
            }

            var edges = new Texture2D(width, height, TextureFormat.RGBA32, false);
            edges.SetPixels(dst);
            edges.Apply(false, false);
            return edges;
        }

        /// <summary>Hard threshold at 0.5. Inpaint masks must be binary, not anti-aliased.</summary>
        public static void Binarize(Texture2D texture)
        {
            if (texture == null)
                return;

            Color[] pixels = texture.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                float value = pixels[i].grayscale >= 0.5f ? 1f : 0f;
                pixels[i] = new Color(value, value, value, 1f);
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
        }

        public static void FlipVertical(Texture2D texture)
        {
            Color[] pixels = texture.GetPixels();
            int width = texture.width;
            int height = texture.height;
            var flipped = new Color[pixels.Length];

            for (int y = 0; y < height; y++)
                Array.Copy(pixels, (height - 1 - y) * width, flipped, y * width, width);

            texture.SetPixels(flipped);
            texture.Apply(false, false);
        }

        /// <summary>Copies any texture into a readable RGBA32 texture, e.g. a compressed import.</summary>
        public static Texture2D MakeReadable(Texture source)
        {
            if (source == null)
                return null;

            int width = Mathf.Max(1, source.width);
            int height = Mathf.Max(1, source.height);
            RenderTexture temp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(source, temp);
                return ReadPixels(temp);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(temp);
            }
        }

        public static void Destroy(params Texture2D[] textures)
        {
            foreach (Texture2D texture in textures)
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
