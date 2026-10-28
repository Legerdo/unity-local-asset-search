using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace LocalAssetSearch
{
    public sealed class AssetSearchWindow : EditorWindow
    {
        Vector2 scroll;
        bool busy, cancelPrepare, showSetup = true, showScope = true, showLog;
        string message = "Select an indexing scope to begin.";
        string query = "", kind = "all";
        bool code;
        List<string> scanned;
        JobStatus status;
        SearchHit[] hits = Array.Empty<SearchHit>();
        double nextPoll;

        [MenuItem("Tools/Local Asset Search")]
        public static void Open() { GetWindow<AssetSearchWindow>("Local Asset Search"); }

        void OnEnable() { minSize = new Vector2(560, 560); EditorApplication.update += Tick; }
        void OnDisable() { EditorApplication.update -= Tick; }

        async void Tick()
        {
            if (!BackendClient.IsRunning || busy || EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 1;
            try { status = await BackendClient.StatusAsync(); Repaint(); }
            catch (Exception ex) { message = ex.Message; nextPoll += 5; }
        }

        async void Run(Func<Task> action)
        {
            if (busy) return;
            busy = true;
            try { await action(); }
            catch (OperationCanceledException) { message = "Preparation cancelled; source assets were not changed."; }
            catch (Exception ex) { message = ex.Message; Debug.LogException(ex); }
            finally { busy = false; Repaint(); }
        }

        void OnGUI()
        {
            var settings = SearchSettings.instance;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Local Asset Search", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Editor-only text, image and audio search. Inference runs in a separate local Python process. Sentis is not required.", MessageType.Info);
            bool running = status != null && status.running;
            showSetup = EditorGUILayout.Foldout(showSetup, "1. Backend and model", true);
            if (showSetup)
            {
                using (new EditorGUI.DisabledScope(busy || running || BackendClient.IsRunning))
                {
                    EditorGUI.BeginChangeCheck();
                    settings.python = EditorGUILayout.TextField("Python executable", settings.python);
                    if (GUILayout.Button("Choose Python executable"))
                    {
                        string picked = EditorUtility.OpenFilePanel("Python from the backend environment", "", "");
                        if (!string.IsNullOrEmpty(picked)) settings.python = picked;
                    }
                    settings.modelDirectory = FolderField("Installed model folder", settings.modelDirectory);
                    settings.device = new[] { "auto", "cpu", "cuda" }[EditorGUILayout.Popup("Device", Math.Max(0, Array.IndexOf(new[] { "auto", "cpu", "cuda" }, settings.device)), new[] { "Auto", "CPU", "CUDA" })];
                    int[] dimensions = { 768, 512, 256 };
                    settings.dimensions = dimensions[EditorGUILayout.Popup("Vector dimensions", Math.Max(0, Array.IndexOf(dimensions, settings.dimensions)), new[] { "768", "512", "256" })];
                    if (EditorGUI.EndChangeCheck()) settings.Persist();
                }
                using (new EditorGUI.DisabledScope(busy || BackendClient.IsRunning))
                {
                    if (GUILayout.Button("Set up isolated Python environment…"))
                    {
                        string directory = EditorUtility.SaveFolderPanel("Choose a new backend environment directory", BackendClient.StateDirectory, "env");
                        if (!string.IsNullOrEmpty(directory)) Run(async () => {
                            message = "Installing the backend environment. See backend log for progress.";
                            await BackendClient.SetupAsync(directory);
                            message = "Backend environment ready.";
                        });
                    }
                    bool weightsPresent = File.Exists(Path.Combine(settings.modelDirectory, "model.safetensors"))
                        || File.Exists(Path.Combine(settings.modelDirectory, "model.safetensors.index.json"));
                    if (!weightsPresent)
                    {
                        settings.downloadDirectory = FolderField("Download destination", settings.downloadDirectory);
                        if (GUILayout.Button("Download missing model from Hugging Face")) Run(async () => {
                            if (string.IsNullOrWhiteSpace(settings.downloadDirectory)) throw new InvalidOperationException("Choose a download destination first");
                            message = "Downloading model. Existing complete weights will be reused.";
                            await BackendClient.DownloadAsync(settings.downloadDirectory);
                            settings.modelDirectory = settings.downloadDirectory; settings.Persist();
                            message = "Model download completed.";
                        });
                    }
                    else EditorGUILayout.LabelField("Existing weights found — no model download needed.");
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(busy || BackendClient.IsRunning))
                        if (GUILayout.Button("Start backend")) Run(async () => { await BackendClient.StartAsync(); status = await BackendClient.StatusAsync(); message = "Local backend connected. The model loads on first indexing/search."; });
                    using (new EditorGUI.DisabledScope(busy || !BackendClient.IsRunning))
                        if (GUILayout.Button("Stop backend")) { BackendClient.Stop(); status = null; }
                }
            }

            showScope = EditorGUILayout.Foldout(showScope, "2. Indexing scope", true);
            if (showScope)
            {
                using (new EditorGUI.DisabledScope(busy || running))
                {
                    EditorGUI.BeginChangeCheck();
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        settings.text = EditorGUILayout.ToggleLeft("Text / Code", settings.text);
                        settings.image = EditorGUILayout.ToggleLeft("Images", settings.image);
                        settings.audio = EditorGUILayout.ToggleLeft("Audio", settings.audio);
                    }
                    settings.assetLimit = EditorGUILayout.IntSlider("Maximum assets", settings.assetLimit, 1, 10000);
                    ScopeList("Included folders / assets", settings.roots);
                    ScopeList("Excluded folders / assets", settings.excluded);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Include Project selection")) AddSelection(settings.roots);
                        if (GUILayout.Button("Exclude Project selection")) AddSelection(settings.excluded);
                    }
                    if (EditorGUI.EndChangeCheck()) { settings.Persist(); scanned = null; }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Preview scope"))
                        {
                            try { scanned = AssetCollector.Scan(); message = "Selected scope: " + scanned.Count + " supported assets. Nothing indexed yet."; }
                            catch (Exception ex) { message = ex.Message; }
                        }
                        if (GUILayout.Button("Index selected scope")) Run(async () => {
                            scanned = AssetCollector.Scan();
                            if (scanned.Count == 0) throw new InvalidOperationException("No supported assets in this scope");
                            cancelPrepare = false; message = "Preparing selected assets…";
                            var request = await AssetCollector.PrepareAsync(scanned, () => cancelPrepare);
                            await BackendClient.StartAsync(); status = await BackendClient.IndexAsync(request);
                            message = "Indexing selected assets; unchanged assets will be skipped.";
                        });
                    }
                }
                if (busy && GUILayout.Button("Cancel preparation")) cancelPrepare = true;
                if (running && GUILayout.Button("Cancel indexing after current inference")) Run(async () => { await BackendClient.CancelAsync(); });
                if (scanned != null) EditorGUILayout.LabelField("Scope preview", scanned.Count + " assets");
            }
            if (status != null)
            {
                EditorGUILayout.LabelField("Index", status.count + " assets · " + status.device + " · " + status.dimensions + "d");
                EditorGUILayout.LabelField("Last job", status.done + "/" + status.total + " · updated " + status.updated + " · unchanged " + status.skipped + " · failed " + status.failed + (status.cancelled ? " · cancelled" : ""));
                if (status.running) EditorGUILayout.LabelField(status.current ?? "", EditorStyles.wordWrappedLabel);
                if (status.errors != null) foreach (string error in status.errors.Take(8)) EditorGUILayout.HelpBox(error, MessageType.Error);
            }

            EditorGUILayout.Space(); EditorGUILayout.LabelField("3. Search", EditorStyles.boldLabel);
            query = EditorGUILayout.TextField("Query", query);
            kind = new[] { "all", "text", "image", "audio" }[EditorGUILayout.Popup("Asset type", Math.Max(0, Array.IndexOf(new[] { "all", "text", "image", "audio" }, kind)), new[] { "All (mixed ranking)", "Text", "Image", "Audio" })];
            code = EditorGUILayout.Toggle("Code retrieval prompt", code);
            using (new EditorGUI.DisabledScope(busy || running || string.IsNullOrWhiteSpace(query)))
                if (GUILayout.Button("Search indexed assets")) Run(async () => {
                    hits = (await BackendClient.SearchAsync(query, kind, 20, code)).results ?? Array.Empty<SearchHit>();
                    message = hits.Length + " results in the current included/excluded scope.";
                });
            foreach (var hit in hits)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(hit.guid));
                    GUILayout.Label(AssetPreview.GetMiniThumbnail(asset), GUILayout.Width(24), GUILayout.Height(24));
                    if (GUILayout.Button(hit.path + (hit.kind == "audio" ? " @ " + hit.offset.ToString("F1") + "s" : ""), EditorStyles.linkLabel))
                    { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); }
                    GUILayout.Label(hit.score.ToString("F3"), GUILayout.Width(44));
                    if (GUILayout.Button("Open", GUILayout.Width(48))) AssetDatabase.OpenAsset(asset);
                }
            }
            EditorGUILayout.Space(); EditorGUILayout.HelpBox(message ?? "", MessageType.None);
            using (new EditorGUI.DisabledScope(busy || running || !BackendClient.IsRunning))
                if (GUILayout.Button("Clear generated index (source assets are preserved)")) Run(async () => {
                    await BackendClient.ClearAsync(); hits = Array.Empty<SearchHit>(); status = await BackendClient.StatusAsync(); message = "Generated index cleared.";
                });
            showLog = EditorGUILayout.Foldout(showLog, "Backend log", true);
            if (showLog) EditorGUILayout.TextArea(BackendClient.Log, GUILayout.MinHeight(100));
            EditorGUILayout.EndScrollView();
        }

        static string FolderField(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                value = EditorGUILayout.TextField(label, value);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.OpenFolderPanel(label, value, "");
                    if (!string.IsNullOrEmpty(picked)) { value = picked; GUI.changed = true; }
                }
            }
            return value;
        }

        static void ScopeList(string label, List<string> list)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            for (int i = 0; i < list.Count; i++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(list[i]);
                    if (GUILayout.Button("Remove", GUILayout.Width(64))) { list.RemoveAt(i); GUI.changed = true; break; }
                }
        }
        static void AddSelection(List<string> list)
        {
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if ((path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal)) && !list.Contains(path)) list.Add(path);
            }
            GUI.changed = true;
        }
    }
}
