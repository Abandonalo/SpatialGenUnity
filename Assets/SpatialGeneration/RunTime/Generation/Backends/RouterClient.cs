using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SpatialGeneration.Generation.Backends
{
    /// <summary>
    /// The single HTTP surface onto the FastAPI router. Generation and refinement both
    /// go through here so timeouts, error text and the "router is down" hint live in one place.
    /// </summary>
    public static class RouterClient
    {
        // HttpClient's 100s default is far below a multi-minute TripoSR run, so the only
        // deadline that applies is the per-call CancellationTokenSource below.
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>
        /// State of the whole backend: the router, and the ComfyUI behind it.
        ///
        /// Both matter and they fail independently, so a single boolean would send the user
        /// to the wrong process half the time.
        /// </summary>
        public readonly struct BackendHealth
        {
            public readonly bool RouterReachable;
            public readonly bool ComfyReachable;

            /// <summary>Where the router expects ComfyUI. Also where we launch it locally.</summary>
            public readonly string ComfyUrl;

            public readonly string Detail;

            public BackendHealth(bool routerReachable, bool comfyReachable, string comfyUrl, string detail)
            {
                RouterReachable = routerReachable;
                ComfyReachable = comfyReachable;
                ComfyUrl = string.IsNullOrWhiteSpace(comfyUrl) ? "http://127.0.0.1:8188" : comfyUrl;
                Detail = detail ?? string.Empty;
            }

            public static BackendHealth RouterDown(string detail) => new(false, false, null, detail);

            public bool IsReady => RouterReachable && ComfyReachable;
        }

        public static async Task<BackendHealth> CheckHealthAsync(BackendSettings settings)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.RouterBaseUrl))
                return BackendHealth.RouterDown("No router URL is configured.");

            try
            {
                using HttpResponseMessage response =
                    await SendAsync(HttpMethod.Get, settings.Endpoint("health"), null, settings.requestTimeoutSeconds);
                if (!response.IsSuccessStatusCode)
                    return BackendHealth.RouterDown($"Router returned {(int)response.StatusCode} {response.ReasonPhrase}.");

                HealthBody health = JsonUtility.FromJson<HealthBody>(await response.Content.ReadAsStringAsync());

                // Tolerate a router that predates the ComfyUI probe rather than reporting a
                // false outage.
                return new BackendHealth(
                    routerReachable: true,
                    comfyReachable: health == null || health.comfy_reachable,
                    comfyUrl: health?.comfy_url,
                    detail: health == null ? string.Empty : health.detail);
            }
            catch (Exception ex)
            {
                return BackendHealth.RouterDown(ex.Message);
            }
        }

        public static async Task<string> PostJsonAsync(string url, string json, int timeoutSeconds)
        {
            using var content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await SendAsync(HttpMethod.Post, url, content, timeoutSeconds);
            return await ReadOrThrowAsync(response, url);
        }

        public static async Task<string> GetStringAsync(string url, int timeoutSeconds)
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, url, null, timeoutSeconds);
            return await ReadOrThrowAsync(response, url);
        }

        public static async Task<byte[]> GetBytesAsync(string url, int timeoutSeconds)
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, url, null, timeoutSeconds);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GET {url} failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            return await response.Content.ReadAsByteArrayAsync();
        }

        /// <summary>
        /// What the user should do next, naming the process that is actually down.
        /// Returns an empty string when everything is up.
        /// </summary>
        public static string DescribeProblem(BackendSettings settings, BackendHealth health)
        {
            if (settings == null)
                return "No backend settings asset is loaded.";

            bool isColab = settings.backendPreset == BackendPreset.Colab;

            if (!health.RouterReachable)
            {
                string where = $"The router is not reachable at {settings.RouterBaseUrl}.";
                string detail = string.IsNullOrWhiteSpace(health.Detail) ? string.Empty : $"\n{health.Detail}";
                if (isColab)
                    return $"{where} Run {settings.colabNotebookPath} in Colab until the zrok share is " +
                           $"serving, then check again.{detail}";

                // Generate starts it automatically; this only reports a genuine failure.
                return settings.autoStartRouter
                    ? $"{where} Generate will start it automatically; this check does not.{detail}"
                    : $"{where} Start it with:\n    ./tools/start_backend.sh{detail}";
            }

            if (!health.ComfyReachable)
            {
                string where = $"The router is up, but it cannot reach ComfyUI at {health.ComfyUrl}.";
                string detail = string.IsNullOrWhiteSpace(health.Detail) ? string.Empty : $"\n{health.Detail}";

                if (isColab)
                    return $"{where} Re-run the notebook cell that launches ComfyUI.{detail}";

                // Locally, Generate starts ComfyUI itself unless the user turned that off.
                return settings.autoStartComfy
                    ? $"{where} Generate will start it automatically; this check does not.{detail}"
                    : $"{where} Start it yourself, or enable Auto Start ComfyUI.{detail}";
            }

            return string.Empty;
        }

        private static async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string url,
            HttpContent content,
            int timeoutSeconds)
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
            return await Http.SendAsync(request, cts.Token);
        }

        private static async Task<string> ReadOrThrowAsync(HttpResponseMessage response, string url)
        {
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{url} returned {(int)response.StatusCode} {response.ReasonPhrase}\n{body}");
            return body;
        }
    }
}
