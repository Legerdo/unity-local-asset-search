// Copy into a validation project's Assets/Editor. Run via Unity CLI with the environment variables below.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LocalAssetSearch;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class AssetSearchValidation
{
    [Serializable] public sealed class RunResult
    {
        public string mode;
        public int assets, updated, skipped;
        public double backend_start_s, model_load_s, model_hash_s, first_index_s, unchanged_index_s, one_changed_s;
        public double uncached_query_mean_ms, uncached_query_p50_ms, uncached_query_p95_ms, repeated_query_ms;
        public float gpu_allocated_mb, gpu_peak_mb;
        public double[] query_samples_ms;
    }
    [Serializable] public sealed class Report
    {
        public string unity, gpu, os, precision = "BF16", dataset = "134 synthetic assets: 130 short texts, 2 simple images, 2 two-second tones";
        public bool fresh_model_inference, korean_ui, english_ui;
        public bool utf8_process_logs;
        public bool agent_cli;
        public bool advanced_filters;
        public bool scope_write_retry;
        public List<RunResult> runs = new List<RunResult>();
        public List<string> checks = new List<string>();
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static double Seconds(Action action) { var timer = Stopwatch.StartNew(); action(); return timer.Elapsed.TotalSeconds; }
    static JobStatus WaitJob()
    {
        var timer = Stopwatch.StartNew(); JobStatus status;
        while ((status = BackendClient.StatusAsync().GetAwaiter().GetResult()).running)
        { Require(timer.Elapsed.TotalMinutes < 8, "Indexing timeout"); Thread.Sleep(50); }
        Require(status.failed == 0 && (status.errors == null || status.errors.Length == 0), "Indexing failed: " + string.Join(";", status.errors ?? Array.Empty<string>()) + BackendClient.Log);
        return status;
    }
    public static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var settings = SearchSettings.instance;
        string previous = EditorJsonUtility.ToJson(settings);
        var report = new Report { unity = Application.unityVersion, gpu = SystemInfo.graphicsDeviceName, os = SystemInfo.operatingSystem };
        try
        {
            settings.python = Environment.GetEnvironmentVariable("UNITY_ASSET_SEARCH_PYTHON") ?? settings.python;
            settings.modelDirectory = Environment.GetEnvironmentVariable("UNITY_ASSET_SEARCH_MODEL") ?? settings.modelDirectory;
            settings.device = "cuda"; settings.dimensions = 768; settings.text = settings.image = settings.audio = true;
            BackendClient.ValidateUnicodeAsync().GetAwaiter().GetResult();
            ValidateScopeWrites(); report.scope_write_retry = true;
            report.utf8_process_logs = true;
            report.checks.Add("Korean UTF-8 stdout and stderr round-trip through Editor ProcessStartInfo");
            settings.roots = new List<string> { "Assets/LocalAssetSearchBenchmark" }; settings.excluded.Clear(); settings.assetLimit = 1000;
            string folder = Path.Combine(Application.dataPath, "LocalAssetSearchBenchmark"); Directory.CreateDirectory(folder);
            foreach (string source in Directory.GetFiles(Path.Combine(BackendClient.PackageDirectory, "Samples~/QuickStart")))
                if (!source.EndsWith(".meta")) File.Copy(source, Path.Combine(folder, Path.GetFileName(source)), true);
            string[] subjects = { "medieval sword and metal shield", "green forest and leafy plants", "blue magic potion", "stone castle and wooden gate", "fast character movement", "an electronic alarm" };
            for (int n = 0; n < 128; n++)
                File.WriteAllText(Path.Combine(folder, "Description" + n.ToString("D3") + ".txt"),
                    "Asset reference " + n + ". " + string.Join(" ", Enumerable.Repeat("This asset describes " + subjects[n % subjects.Length] + " for a Unity game environment.", 5)));
            AssetDatabase.Refresh();
            var paths = AssetCollector.Scan(); Require(paths.Count == 134, "Expected 134 benchmark assets, found " + paths.Count);
            var request = AssetCollector.PrepareAsync(paths, yieldBetweenAssets: false).GetAwaiter().GetResult();
            foreach (string mode in new[] { "balanced", "single" })
            {
                settings.batchProfile = mode; BackendClient.Stop();
                var result = new RunResult { mode = mode, assets = paths.Count };
                result.backend_start_s = Seconds(() => BackendClient.StartAsync().GetAwaiter().GetResult());
                BackendClient.ClearAsync().GetAwaiter().GetResult();
                var warm = BackendClient.WarmupAsync().GetAwaiter().GetResult(); Require(warm.model_loaded, "Model did not load");
                result.model_load_s = warm.load_seconds; result.model_hash_s = warm.hash_seconds;
                result.first_index_s = Seconds(() => { BackendClient.IndexAsync(request).GetAwaiter().GetResult(); var done = WaitJob(); Require(done.updated == 134, "Fresh indexing incomplete"); });
                result.unchanged_index_s = Seconds(() => { BackendClient.IndexAsync(request).GetAwaiter().GetResult(); var done = WaitJob(); result.updated = done.updated; result.skipped = done.skipped; Require(done.skipped == 134 && done.updated == 0, "Unchanged files re-embedded"); });
                string edited = Path.Combine(folder, "Description000.txt"); string original = File.ReadAllText(edited);
                try
                {
                    File.WriteAllText(edited, original + " A polished steel blade.");
                    result.one_changed_s = Seconds(() => { BackendClient.IndexAsync(request).GetAwaiter().GetResult(); var done = WaitJob(); Require(done.updated == 1 && done.skipped == 133, "Changed-file invalidation failed"); });
                }
                finally { File.WriteAllText(edited, original); }
                BackendClient.IndexAsync(request).GetAwaiter().GetResult(); WaitJob();
                foreach (string kind in new[] { "text", "image", "audio" }) BackendClient.SearchAsync("warmup", kind).GetAwaiter().GetResult();
                var timings = new List<double>();
                for (int n = 0; n < 30; n++)
                {
                    string kind = new[] { "text", "image", "audio" }[n % 3];
                    string query = "Find " + subjects[n % subjects.Length] + ", reference " + n;
                    double ms = Seconds(() => { var response = BackendClient.SearchAsync(query, kind).GetAwaiter().GetResult(); Require(response.results.Length > 0, "Missing " + kind + " results"); }) * 1000;
                    timings.Add(ms);
                }
                result.query_samples_ms = timings.ToArray(); var sorted = timings.OrderBy(v => v).ToArray();
                result.uncached_query_mean_ms = timings.Average(); result.uncached_query_p50_ms = sorted[14]; result.uncached_query_p95_ms = sorted[28];
                BackendClient.SearchAsync("repeat cache probe", "text").GetAwaiter().GetResult();
                result.repeated_query_ms = Seconds(() => BackendClient.SearchAsync("repeat cache probe", "text").GetAwaiter().GetResult()) * 1000;
                var final = BackendClient.StatusAsync().GetAwaiter().GetResult(); result.gpu_allocated_mb = final.gpu_allocated_mb; result.gpu_peak_mb = final.gpu_peak_mb;
                report.runs.Add(result);
                var images = BackendClient.SearchAsync(new SearchRequest { query = "a red square", kind = "image", top = 100 }).GetAwaiter().GetResult();
                Require(images.results.Length == 2, "Image fixture missing");
                float threshold = (images.results[0].score + images.results[1].score) / 2;
                var filtered = BackendClient.SearchAsync(new SearchRequest { query = "a red square", kind = "image", minScore = threshold }).GetAwaiter().GetResult();
                Require(filtered.results.Length == 1 && filtered.results[0].path.EndsWith("RedSquare.png"), "Similarity cutoff failed");
                filtered = BackendClient.SearchAsync(new SearchRequest { query = "a red square", kind = "image", minScore = images.results[0].score + .001f }).GetAwaiter().GetResult();
                Require(filtered.results.Length == 0, "Below-threshold results leaked");
                filtered = BackendClient.SearchAsync(new SearchRequest { query = "a red square", kind = "image", extensions = "PNG", pathContains = "BlueCircle" }).GetAwaiter().GetResult();
                Require(filtered.results.Length == 1 && filtered.results[0].path.EndsWith("BlueCircle.png"), "Path/extension filter failed");
                filtered = BackendClient.SearchAsync(new SearchRequest { query = "a red square", kind = "image", sort = "name", top = 1 }).GetAwaiter().GetResult();
                Require(filtered.matched == 2 && filtered.results[0].path.EndsWith("BlueCircle.png"), "Sorting/count/limit failed");
                report.advanced_filters = true;
                report.checks.Add(mode + ": 134 assets, unchanged skip, one-file update, 30 unique queries and all modalities passed");
                using (var agent = Process.Start(new ProcessStartInfo(settings.python,
                    Quote(Path.Combine(BackendClient.PackageDirectory, "Agent~/search.py"))
                    + " --project " + Quote(BackendClient.ProjectRoot) + " --query sword --kind text --top 3") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 }))
                {
                    string data = agent.StandardOutput.ReadToEnd(); string error = agent.StandardError.ReadToEnd(); agent.WaitForExit();
                    Require(agent.ExitCode == 0, "Agent CLI failed: " + error);
                    var found = JsonUtility.FromJson<SearchResponse>(data);
                    Require(found.results.Length == 3 && found.results.All(h => h.path.StartsWith("Assets/LocalAssetSearchBenchmark/")), "Agent scope failed");
                    report.agent_cli = true;
                }
            }
            report.fresh_model_inference = true;
            ValidateLayout(report);
            string output = Path.Combine(BackendClient.ProjectRoot, "Logs/asset-search-benchmark.json"); File.WriteAllText(output, JsonUtility.ToJson(report, true));
            UnityEngine.Debug.Log("ASSET_SEARCH_VALIDATION_PASS " + output);
        }
        finally { BackendClient.Stop(); EditorJsonUtility.FromJsonOverwrite(previous, settings); settings.Persist(); }
    }

    static string Quote(string value) => "\"" + value.Replace("\\", "/").Replace("\"", "\\\"") + "\"";
    static void ValidateLayout(Report report)
    {
            var settings = SearchSettings.instance;
            foreach (string language in new[] { "ko", "en" })
            {
                settings.language = language;
                var window = EditorWindow.GetWindow<AssetSearchWindow>(); window.CreateGUI();
                Require(window.rootVisualElement.Q<Label>("title").text == Locale.Text("title"), "Locale binding failed");
                Require(window.rootVisualElement.Q<Button>("downloadModel").text == Locale.Text("download.action"), "Download control missing");
                Require(window.rootVisualElement.Q<TextField>("queryField") != null, "Search field missing");
                Require(window.rootVisualElement.Q<ScrollView>("resultsScroll") != null, "Results must scroll independently of search controls");
                Require(window.rootVisualElement.Q<Button>("settingsTab").text == Locale.Text("tab.settings"), "Setup tab missing");
                Require(window.rootVisualElement.Q<Foldout>("searchFilters").value == false, "Advanced filters should start collapsed");
                Require(window.rootVisualElement.Q<FloatField>("minimumScore") != null, "Similarity control missing");
                if (language == "ko") report.korean_ui = true; else report.english_ui = true;
                window.Close();
            }
            report.checks.Add("UXML/USS loaded; Korean and English controls bound in Unity");
    }

    public static void LayoutOnly()
    {
        string language = SearchSettings.instance.language;
        try { ValidateScopeWrites(); ValidateLayout(new Report()); UnityEngine.Debug.Log("ASSET_SEARCH_LAYOUT_PASS " + Application.unityVersion + " scope-write-lock-retry"); }
        finally { SearchSettings.instance.language = language; }
    }

    static void ValidateScopeWrites()
    {
        SearchSettings.instance.Persist();
        string path = Path.Combine(BackendClient.StateDirectory, "agent-scope.json");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = System.Threading.Tasks.Task.Run(() => { Thread.Sleep(70); held.Dispose(); });
        try { SearchSettings.instance.Persist(); }
        finally { held.Dispose(); release.GetAwaiter().GetResult(); }
        Require(File.ReadAllText(path).Contains("roots"), "Atomic scope replacement failed after temporary Windows reader lock");
    }
}
