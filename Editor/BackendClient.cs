using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace LocalAssetSearch
{
    [InitializeOnLoad]
    public static class BackendClient
    {
        static Process process;
        static HttpClient client;
        static string address;
        static string startedConfiguration;
        static readonly object logLock = new object();
        static string log = "";
        static bool starting;
        // Capture the Unity API value on initialization; async process cleanup may run on a worker.
        static readonly string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string ProjectRoot => projectRoot;
        public static string StateDirectory => Path.Combine(ProjectRoot, "Library", "LocalAssetSearch");
        public static string PackageDirectory => UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BackendClient).Assembly).resolvedPath;
        public static bool IsRunning => process != null && !process.HasExited && client != null;
        public static string Log { get { lock (logLock) return log; } }

        static BackendClient()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        static void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (logLock) { log += line + "\n"; if (log.Length > 16000) log = log.Substring(log.Length - 16000); }
        }

        internal static string Quote(string value)
        {
            if (value.IndexOfAny(new[] {'\r', '\n', '\0'}) >= 0) throw new ArgumentException("Invalid process argument");
            // ProcessStartInfo uses direct argv, never a shell. Preserve Windows trailing backslashes.
            return "\"" + Regex.Replace(value, "(\\\\*)\"", "$1$1\\\"").TrimEnd('\\')
                + new string('\\', value.Length - value.TrimEnd('\\').Length) + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
        }

        static Process Launch(string arguments, string token = null)
        {
            var info = new ProcessStartInfo(SearchSettings.instance.python, arguments) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = ProjectRoot
            };
            info.EnvironmentVariables["PYTHONUTF8"] = "1";
            info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            info.EnvironmentVariables["HF_HUB_DISABLE_TELEMETRY"] = "1";
            if (token != null) {
                info.EnvironmentVariables["UNITY_EMBEDDING_TOKEN"] = token;
                info.EnvironmentVariables["UNITY_EMBEDDING_AGENT_TOKEN"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            }
            var launched = new Process { StartInfo = info };
            launched.OutputDataReceived += (_, e) => Append(e.Data);
            launched.ErrorDataReceived += (_, e) => Append(e.Data);
            launched.Start();
            launched.BeginOutputReadLine(); launched.BeginErrorReadLine();
            return launched;
        }

        public static async Task StartAsync()
        {
            var settings = SearchSettings.instance;
            string configuration = settings.python + "|" + settings.modelDirectory + "|" + settings.device + "|" + settings.dimensions + "|" + settings.batchProfile;
            if (IsRunning && configuration == startedConfiguration) return;
            if (starting) throw new InvalidOperationException("Backend is starting");
            if (!Directory.Exists(settings.modelDirectory)) throw new DirectoryNotFoundException("Choose an installed model folder, or download into a separate folder first.");
            settings.Persist(); // Main-thread entry, before the first asynchronous continuation.
            starting = true;
            try
            {
                Stop();
                Directory.CreateDirectory(StateDirectory);
                string readyPath = Path.Combine(StateDirectory, "ready.json");
                if (File.Exists(readyPath)) File.Delete(readyPath);
                string token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                string session = Guid.NewGuid().ToString("N");
                process = Launch(Quote(Path.Combine(PackageDirectory, "Backend~", "server.py"))
                    + " --model-dir " + Quote(settings.modelDirectory) + " --project-root " + Quote(ProjectRoot)
                    + " --state-dir " + Quote(StateDirectory) + " --device " + settings.device
                    + " --dimensions " + settings.dimensions + " --batch-profile " + settings.batchProfile + " --session-id " + session, token);
                var timeout = Stopwatch.StartNew();
                while (!File.Exists(readyPath))
                {
                    if (process.HasExited) throw new InvalidOperationException("Backend failed to start:\n" + Log);
                    if (timeout.Elapsed.TotalSeconds > 120) throw new TimeoutException("Backend startup timed out:\n" + Log);
                    await Task.Delay(100);
                }
                var ready = JsonUtility.FromJson<Ready>(File.ReadAllText(readyPath));
                // Windows venv python.exe may be a launcher whose child owns the listening port.
                if (ready.session != session || ready.port < 1 || ready.port > 65535) throw new InvalidOperationException("Invalid backend handshake");
                address = "http://127.0.0.1:" + ready.port;
                client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                startedConfiguration = configuration;
                await StatusAsync();
            }
            catch { Stop(); throw; }
            finally { starting = false; }
        }

        public static void Stop()
        {
            client?.Dispose(); client = null;
            if (process == null) return;
            try { if (!process.HasExited) process.StandardInput.Close(); }
            catch (InvalidOperationException) { }
            // Kill only the child we created if it cannot finish promptly (e.g. busy GPU inference).
            try { if (!process.HasExited && !process.WaitForExit(1500)) process.Kill(); }
            catch (InvalidOperationException) { }
            process.Dispose(); process = null; address = null;
            string agentFile = Path.Combine(StateDirectory, "agent-connection.json");
            if (File.Exists(agentFile)) File.Delete(agentFile);
        }

        public static async Task<T> SendAsync<T>(string endpoint, object body)
        {
            if (!IsRunning) throw new InvalidOperationException("Start the local backend first");
            using (var content = new StringContent(JsonUtility.ToJson(body), Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync(address + endpoint, content))
            {
                string data = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(JsonUtility.FromJson<ApiError>(data)?.error ?? data);
                return JsonUtility.FromJson<T>(data);
            }
        }

        [Serializable] sealed class Empty { public bool ok; }
        public static Task<JobStatus> StatusAsync() => SendAsync<JobStatus>("/status", new Empty());
        public static Task<JobStatus> IndexAsync(IndexRequest request) => SendAsync<JobStatus>("/index", request);
        public static async Task CancelAsync() { await SendAsync<Empty>("/cancel", new Empty()); }
        public static async Task ClearAsync() { await SendAsync<Empty>("/clear", new Empty()); }
        public static Task<JobStatus> WarmupAsync() => SendAsync<JobStatus>("/warmup", new Empty());

        // Public Editor API for AI-agent/MCP integrations. Call from the Unity main thread.
        public static async Task<SearchResponse> SearchAsync(string query, string kind = "all", int top = 20, bool code = false)
            => await SearchAsync(new SearchRequest { query = query, kind = kind, top = top, code = code });

        public static async Task<SearchResponse> SearchAsync(SearchRequest request)
        {
            await StartAsync();
            var settings = SearchSettings.instance;
            if (settings.roots.Count == 0) throw new InvalidOperationException("Select the asset search scope first");
            request.roots = settings.roots.ToArray(); request.excluded = settings.excluded.ToArray();
            return await SendAsync<SearchResponse>("/search", request);
        }

        public static async Task DownloadAsync(string directory)
        {
            if (IsRunning) throw new InvalidOperationException("Stop the backend before downloading a model");
            using (var download = Launch(Quote(Path.Combine(PackageDirectory, "Backend~", "server.py"))
                + " --download --model-dir " + Quote(directory)))
            {
                while (!download.HasExited) await Task.Delay(200);
                if (download.ExitCode != 0) throw new InvalidOperationException("Download failed:\n" + Log);
            }
        }

        public static async Task SetupAsync(string directory)
        {
            using (var setup = Launch(Quote(Path.Combine(PackageDirectory, "Backend~", "setup.py"))
                + " --venv " + Quote(directory) + ((SearchSettings.instance.device == "cuda"
                    || SearchSettings.instance.device == "auto" && SystemInfo.graphicsDeviceVendor.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) ? " --cuda" : "")))
            {
                while (!setup.HasExited) await Task.Delay(200);
                if (setup.ExitCode != 0) throw new InvalidOperationException("Environment setup failed:\n" + Log);
            }
            SearchSettings.instance.python = Path.Combine(directory, Application.platform == RuntimePlatform.WindowsEditor ? "Scripts/python.exe" : "bin/python");
            SearchSettings.instance.Persist();
        }

        public static async Task ValidateUnicodeAsync()
        {
            string expected = "한국어 로그 확인: 모델 준비 완료";
            int before = Regex.Matches(Log, expected).Count;
            using (var check = Launch("-c " + Quote("import sys; print('" + expected + "'); print('" + expected + "', file=sys.stderr)")))
            {
                while (!check.HasExited) await Task.Delay(50);
                check.WaitForExit();
                if (check.ExitCode != 0 || Regex.Matches(Log, expected).Count < before + 2) throw new InvalidOperationException("UTF-8 process log validation failed");
            }
        }
    }
}
