// Copy into Assets/Editor of a separate validation project containing the benchmark fixture.
// Run with unity open --batchmode --args "-executeMethod MemoryValidation.Run ..." (no -quit).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LocalAssetSearch;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UIElements;

[InitializeOnLoad]
public static class MemoryValidation
{
    [Serializable] public sealed class Sample
    {
        public string phase;
        public long managed_bytes, native_allocated_bytes;
        public int textures, render_textures, windows, callbacks;
    }
    [Serializable] public sealed class Report
    {
        public string unity;
        public bool passed, preview_resources_stable, window_callbacks_released, process_stops, reload_process_released, interrupted_index_recovered;
        public int window_cycles, preview_exports;
        public List<Sample> windows = new List<Sample>(), previews = new List<Sample>();
        public List<int> stopped_pids = new List<int>();
        public string error;
    }
    [Serializable] sealed class Ready { public int pid; }
    [Serializable] sealed class ReloadMarker { public int pid; public long startTicks; public string previousSettings; public Report report; }
    static string Result => Path.Combine(BackendClient.ProjectRoot, "Logs/asset-search-memory.json");
    static string Marker => Path.Combine(BackendClient.StateDirectory, "memory-reload-check.json");
    static readonly MethodInfo Search = typeof(AssetSearchWindow).GetMethod("Search", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo Busy = typeof(AssetSearchWindow).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic);

    static MemoryValidation() { EditorApplication.delayCall += ResumeReload; }
    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static void FullGC() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    static Sample Snapshot(string phase)
    {
        FullGC();
        return new Sample { phase = phase, managed_bytes = GC.GetTotalMemory(false), native_allocated_bytes = Profiler.GetTotalAllocatedMemoryLong(),
                textures = Resources.FindObjectsOfTypeAll<Texture2D>().Length, render_textures = Resources.FindObjectsOfTypeAll<RenderTexture>().Length,
                windows = Resources.FindObjectsOfTypeAll<AssetSearchWindow>().Length,
                callbacks = EditorApplication.update == null ? 0 : EditorApplication.update.GetInvocationList().Count(d => d.Target is AssetSearchWindow) };
    }
    static async Task WindowCycle(int n)
    {
        AssetSearchWindow window = null;
        try {
            window = EditorWindow.GetWindow<AssetSearchWindow>(); window.CreateGUI();
            window.rootVisualElement.Q<TextField>("queryField").value = "memory validation sword " + n % 6;
            // Invoke the exact Search button callback; it uses real HTTP/model results and Unity UI bindings.
            Search.Invoke(window, null);
            var timeout = Stopwatch.StartNew();
            while ((bool)Busy.GetValue(window)) { Require(timeout.Elapsed.TotalSeconds < 30, "UI search timed out"); await Task.Delay(20); }
            Require(window.rootVisualElement.Q<VisualElement>("results").childCount > 0, "Real UI results missing");
        }
        finally { if (window != null) window.Close(); window = null; }
        await Task.Delay(30);
    }
    static int WorkerPid() => JsonUtility.FromJson<Ready>(File.ReadAllText(Path.Combine(BackendClient.StateDirectory, "ready.json"))).pid;
    static long StartTicks(int pid) { using (var process = Process.GetProcessById(pid)) return process.StartTime.ToUniversalTime().Ticks; }
    static bool Alive(int pid, long ticks)
    {
        try { using (var process = Process.GetProcessById(pid)) return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (ArgumentException) { return false; }
    }
    static async Task AssertStopped(int pid, long ticks)
    {
        var timeout = Stopwatch.StartNew();
        while (Alive(pid, ticks) && timeout.Elapsed.TotalSeconds < 3) await Task.Delay(50);
        Require(!Alive(pid, ticks), "Owned Python worker survived Stop: " + pid);
    }
    public static async void Run()
    {
        var settings = SearchSettings.instance;
        string previous = EditorJsonUtility.ToJson(settings);
        var report = new Report { unity = Application.unityVersion };
        bool handingOff = false;
        try {
            settings.python = Environment.GetEnvironmentVariable("UNITY_ASSET_SEARCH_PYTHON") ?? settings.python;
            settings.modelDirectory = Environment.GetEnvironmentVariable("UNITY_ASSET_SEARCH_MODEL") ?? settings.modelDirectory;
            settings.device = "cuda"; settings.text = settings.image = settings.audio = true;
            settings.hideLowScores = false; settings.pathContains = settings.extensions = ""; settings.resultLimit = 20;
            settings.roots = new List<string> { "Assets/LocalAssetSearchBenchmark" }; settings.excluded.Clear();
            var paths = AssetCollector.Scan();
            var request = await AssetCollector.PrepareAsync(paths);
            await BackendClient.StartAsync(); await BackendClient.WarmupAsync(); await BackendClient.IndexAsync(request);
            while ((await BackendClient.StatusAsync()).running) await Task.Delay(50);
            for (int n = 0; n < 6; n++) await WindowCycle(n);
            report.windows.Add(Snapshot("windows-warmed"));
            for (int group = 0; group < 6; group++) {
                for (int n = 0; n < 5; n++) { await WindowCycle(group * 5 + n); report.window_cycles++; }
                report.windows.Add(Snapshot("windows-" + report.window_cycles));
            }
            Require(report.windows.All(s => s.windows == 0 && s.callbacks == 0), "Closed EditorWindow or update subscription retained");
            Require(report.windows.Last().managed_bytes - report.windows[0].managed_bytes < 16L * 1048576, "Managed UI memory grew by 16 MiB after warmup");
            Require(report.windows.Last().native_allocated_bytes - report.windows[0].native_allocated_bytes < 32L * 1048576, "Native UI allocation grew by 32 MiB after warmup");
            report.window_callbacks_released = true;
            var images = request.items.Where(i => i.kind == "image").ToList();
            var imagePaths = images.Select(i => i.path).ToList();
            Require(images.Count == 2, "Expected two owned image fixtures");
            for (int group = -1; group < 5; group++) {
                for (int n = 0; n < 10; n++) {
                    var active = RenderTexture.active;
                    foreach (var item in images) {
                        Require(Path.GetFullPath(item.source).StartsWith(Path.GetFullPath(Path.Combine(BackendClient.StateDirectory, "Previews")) + Path.DirectorySeparatorChar), "Only generated previews may be removed");
                        if (File.Exists(item.source)) File.Delete(item.source);
                    }
                    await AssetCollector.PrepareAsync(imagePaths);
                    Require(RenderTexture.active == active, "Active RenderTexture was not restored");
                    if (group >= 0) report.preview_exports += images.Count;
                }
                await Task.Delay(30); report.previews.Add(Snapshot(group == -1 ? "previews-warmed" : "previews-" + report.preview_exports));
            }
            Require(report.previews.All(s => s.textures == report.previews[0].textures), "Texture2D count grows across preview export cycles");
            Require(report.previews.Last().render_textures <= report.previews[0].render_textures + 2, "Temporary render texture pool grows");
            report.preview_resources_stable = true;
            // Stop during a real fresh indexing job, then reopen its SQLite index.
            await BackendClient.ClearAsync(); await BackendClient.WarmupAsync(); await BackendClient.IndexAsync(request);
            int indexingPid = WorkerPid(); long indexingTicks = StartTicks(indexingPid);
            await Task.Delay(50); BackendClient.Stop(); await AssertStopped(indexingPid, indexingTicks);
            report.stopped_pids.Add(indexingPid);
            await BackendClient.StartAsync(); await BackendClient.WarmupAsync(); await BackendClient.IndexAsync(request);
            JobStatus recovered;
            do { await Task.Delay(50); recovered = await BackendClient.StatusAsync(); } while (recovered.running);
            Require(recovered.failed == 0 && recovered.count == 134, "Interrupted index failed to recover");
            report.interrupted_index_recovered = true;
            for (int n = 0; n < 3; n++) {
                int pid = WorkerPid(); long ticks = StartTicks(pid);
                BackendClient.Stop(); await AssertStopped(pid, ticks); report.stopped_pids.Add(pid);
                await BackendClient.StartAsync(); await BackendClient.WarmupAsync();
            }
            // Also stop while a real model-loading request is in flight.
            BackendClient.Stop(); await BackendClient.StartAsync();
            int loadingPid = WorkerPid(); long loadingTicks = StartTicks(loadingPid);
            var loading = BackendClient.WarmupAsync(); await Task.Delay(100); BackendClient.Stop();
            try { await loading; } catch (Exception) { }
            await AssertStopped(loadingPid, loadingTicks); report.stopped_pids.Add(loadingPid); report.process_stops = true;
            await BackendClient.StartAsync(); await BackendClient.WarmupAsync();
            int reloadPid = WorkerPid();
            File.WriteAllText(Marker, JsonUtility.ToJson(new ReloadMarker { pid = reloadPid, startTicks = StartTicks(reloadPid), previousSettings = previous, report = report }));
            handingOff = true;
            UnityEngine.Debug.Log("ASSET_SEARCH_MEMORY_RELOAD_REQUESTED");
            EditorUtility.RequestScriptReload();
        }
        catch (Exception ex) { report.error = ex.ToString(); UnityEngine.Debug.LogException(ex); }
        finally {
            if (!handingOff) {
                BackendClient.Stop(); EditorJsonUtility.FromJsonOverwrite(previous, settings); settings.Persist();
                File.WriteAllText(Result, JsonUtility.ToJson(report, true)); EditorApplication.Exit(1);
            }
        }
    }
    static async void ResumeReload()
    {
        if (!File.Exists(Marker)) return;
        var marker = JsonUtility.FromJson<ReloadMarker>(File.ReadAllText(Marker)); File.Delete(Marker);
        try {
            await AssertStopped(marker.pid, marker.startTicks);
            marker.report.reload_process_released = marker.report.passed = true;
            UnityEngine.Debug.Log("ASSET_SEARCH_MEMORY_PASS " + Result);
        }
        catch (Exception ex) { marker.report.error = ex.ToString(); UnityEngine.Debug.LogException(ex); }
        finally {
            EditorJsonUtility.FromJsonOverwrite(marker.previousSettings, SearchSettings.instance); SearchSettings.instance.Persist();
            File.WriteAllText(Result, JsonUtility.ToJson(marker.report, true)); EditorApplication.Exit(marker.report.passed ? 0 : 1);
        }
    }
}
