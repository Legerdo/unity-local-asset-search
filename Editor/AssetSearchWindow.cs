using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace LocalAssetSearch
{
    public sealed class AssetSearchWindow : EditorWindow
    {
        bool busy, polling, cancelPrepare, hasError;
        bool showSettings, pageInitialized;
        string noticeKey = "notice.start", details = "", query = "", kind = "all";
        object[] noticeArgs = Array.Empty<object>();
        bool code;
        JobStatus status;
        List<string> scanned;
        int scannedText, scannedImage, scannedAudio;
        SearchHit[] hits = Array.Empty<SearchHit>();
        double nextPoll, lastQueryMs;
        int matched;
        SearchSettings Settings => SearchSettings.instance;
        T Q<T>(string name) where T : VisualElement => rootVisualElement.Q<T>(name);

        [MenuItem("Tools/Local Asset Search")]
        public static void Open()
        {
            var window = GetWindow<AssetSearchWindow>("Local Asset Search", true, typeof(SceneView));
            window.minSize = new Vector2(580, 560);
        }
        void OnEnable() { noticeKey = "notice.start"; noticeArgs = Array.Empty<object>(); hasError = false; EditorApplication.update += Tick; }
        void OnDisable() { EditorApplication.update -= Tick; cancelPrepare = true; }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            string path = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AssetSearchWindow).Assembly).assetPath;
            AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path + "/Editor/AssetSearchWindow.uxml").CloneTree(rootVisualElement);
            rootVisualElement.Q(className: "asset-search").EnableInClassList("light", !EditorGUIUtility.isProSkin);
            foreach (var pair in new Dictionary<string, string> {
                {"title","title"},{"subtitle","subtitle"},{"setupHelp","setup.help"},{"downloadHelp","download.help"},
                {"advancedHelp","advanced.help"},{"scopeTitle","scope.title"},{"scopeHelp","scope.help"},
                {"searchTitle","search.title"},{"queryHint","query.hint"},{"diagnosticsHelp","diagnostics.help"}
            }) Q<Label>(pair.Key).text = Locale.Text(pair.Value);
            var language = Q<DropdownField>("language");
            language.choices = new List<string> { "한국어", "English" };
            language.SetValueWithoutNotify(Settings.language == "en" ? "English" : "한국어");
            language.RegisterValueChangedCallback(e => { Settings.language = e.newValue == "English" ? "en" : "ko"; Settings.Persist(); CreateGUI(); });
            if (!pageInitialized) { showSettings = !HasWeights() || Settings.roots.Count == 0; pageInitialized = true; }
            Button("searchTab", "tab.search", () => ShowSettings(false));
            Button("settingsTab", "tab.settings", () => ShowSettings(true));
            ShowSettings(showSettings);
            Foldout("setupSection", "setup.title", !HasWeights());
            Foldout("advancedSetup", "advanced.title", false);
            Foldout("advancedScope", "scope.advanced", false);
            Foldout("diagnostics", "diagnostics.title", false);
            Foldout("searchFilters", "filters.title", false);
            var hide = Q<Toggle>("hideLowScores"); hide.label = Locale.Text("filters.hide"); hide.SetValueWithoutNotify(Settings.hideLowScores);
            hide.RegisterValueChangedCallback(e => { Settings.hideLowScores = e.newValue; Settings.Persist(); Refresh(); });
            var minimum = Q<FloatField>("minimumScore"); minimum.label = Locale.Text("filters.minimum"); minimum.isDelayed = true; minimum.SetValueWithoutNotify(Settings.minimumScore);
            minimum.RegisterValueChangedCallback(e => { Settings.minimumScore = float.IsNaN(e.newValue) ? .7f : Mathf.Clamp(e.newValue, -1, 1); minimum.SetValueWithoutNotify(Settings.minimumScore); Settings.Persist(); Refresh(); });
            var sorting = Q<DropdownField>("resultSort"); sorting.label = Locale.Text("filters.sort");
            sorting.choices = new List<string> { Locale.Text("sort.similarity"), Locale.Text("sort.name"), Locale.Text("sort.path") };
            sorting.index = Math.Max(0, Array.IndexOf(new[] { "similarity", "name", "path" }, Settings.resultSort));
            sorting.RegisterValueChangedCallback(_ => { Settings.resultSort = new[] { "similarity", "name", "path" }[sorting.index]; Settings.Persist(); });
            var count = Q<DropdownField>("resultLimit"); count.label = Locale.Text("filters.limit"); count.choices = new List<string> { "10", "20", "50", "100" }; count.SetValueWithoutNotify(Settings.resultLimit.ToString());
            count.RegisterValueChangedCallback(e => { Settings.resultLimit = int.Parse(e.newValue); Settings.Persist(); });
            Text("pathContains", "filters.path", Settings.pathContains, value => { Settings.pathContains = value; Settings.Persist(); Refresh(); });
            Text("extensions", "filters.extensions", Settings.extensions, value => { Settings.extensions = value; Settings.Persist(); Refresh(); });
            Q<TextField>("extensions").tooltip = Locale.Text("filters.extensionHelp");
            Q<FloatField>("minimumScore").tooltip = Locale.Text("filters.scoreHelp");
            Button("resetFilters", "filters.reset", () => { Settings.hideLowScores = false; Settings.minimumScore = .7f; Settings.pathContains = Settings.extensions = ""; Settings.resultSort = "similarity"; Settings.resultLimit = 20; Settings.Persist(); CreateGUI(); });
            Text("modelPath", "model.path", Settings.modelDirectory, value => { Settings.modelDirectory = value; Settings.Persist(); Refresh(); });
            Text("downloadPath", "download.path", Settings.downloadDirectory, value => { Settings.downloadDirectory = value; Settings.Persist(); });
            Text("pythonPath", "python.path", Settings.python, value => { Settings.python = value; Settings.Persist(); });
            var device = Q<DropdownField>("device"); device.label = Locale.Text("device");
            device.choices = new List<string> { Locale.Text("device.auto"), "CPU", "CUDA" };
            device.index = Math.Max(0, Array.IndexOf(new[] { "auto", "cpu", "cuda" }, Settings.device));
            device.RegisterValueChangedCallback(_ => { Settings.device = new[] { "auto", "cpu", "cuda" }[device.index]; Settings.Persist(); });
            var dimensions = Q<DropdownField>("dimensions"); dimensions.label = Locale.Text("dimensions");
            dimensions.choices = new List<string> { "768", "512", "256" }; dimensions.SetValueWithoutNotify(Settings.dimensions.ToString());
            dimensions.RegisterValueChangedCallback(e => { Settings.dimensions = int.Parse(e.newValue); Settings.Persist(); });
            Toggle("textType", "type.text", Settings.text, value => Settings.text = value);
            Toggle("imageType", "type.image", Settings.image, value => Settings.image = value);
            Toggle("audioType", "type.audio", Settings.audio, value => Settings.audio = value);
            var limit = Q<IntegerField>("assetLimit"); limit.label = Locale.Text("scope.limit"); limit.SetValueWithoutNotify(Settings.assetLimit);
            limit.RegisterValueChangedCallback(e => { Settings.assetLimit = Mathf.Clamp(e.newValue, 1, 10000); limit.SetValueWithoutNotify(Settings.assetLimit); ChangedScope(); });
            var field = Q<TextField>("queryField"); field.label = Locale.Text("query"); field.SetValueWithoutNotify(query);
            field.RegisterValueChangedCallback(e => { query = e.newValue; Refresh(); });
            field.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Return && !busy && !(status?.running ?? false)) { Search(); e.StopPropagation(); } });
            var types = Q<DropdownField>("resultType"); types.label = Locale.Text("result.type");
            types.choices = new List<string> { Locale.Text("type.all"), Locale.Text("type.text"), Locale.Text("type.image"), Locale.Text("type.audio") };
            types.index = Math.Max(0, Array.IndexOf(new[] { "all", "text", "image", "audio" }, kind));
            types.RegisterValueChangedCallback(_ => kind = new[] { "all", "text", "image", "audio" }[types.index]);
            var codeField = Q<Toggle>("codePrompt"); codeField.label = Locale.Text("code.prompt"); codeField.SetValueWithoutNotify(code);
            codeField.RegisterValueChangedCallback(e => code = e.newValue);
            Button("chooseModel", "model.choose", () => ChooseFolder("model.path", Settings.modelDirectory, p => { Settings.modelDirectory = p; Settings.Persist(); Q<TextField>("modelPath").SetValueWithoutNotify(p); Refresh(); }));
            Button("chooseDownload", "download.choose", () => ChooseFolder("download.path", Settings.downloadDirectory, p => { Settings.downloadDirectory = p; Settings.Persist(); Q<TextField>("downloadPath").SetValueWithoutNotify(p); }));
            Button("choosePython", "python.choose", () => { string p = EditorUtility.OpenFilePanel(Locale.Text("python.choose"), "", ""); if (!string.IsNullOrEmpty(p)) { Settings.python = p; Settings.Persist(); Q<TextField>("pythonPath").SetValueWithoutNotify(p); } });
            Button("setupEnvironment", "setup.install", () => Run(async () => {
                await BackendClient.SetupAsync(Path.Combine(BackendClient.StateDirectory, "env"));
                Q<TextField>("pythonPath").SetValueWithoutNotify(Settings.python); SetNotice("notice.installed");
            }, "notice.installing"));
            Button("checkEnvironment", "setup.check", () => Run(async () => {
                await BackendClient.StartAsync(); status = await BackendClient.WarmupAsync();
                SetNotice("notice.connected", status.device); Q<Foldout>("setupSection").value = false;
            }, "notice.connecting"));
            Button("downloadModel", "download.action", () => Run(async () => {
                if (string.IsNullOrWhiteSpace(Settings.downloadDirectory)) throw new InvalidOperationException(Locale.Text("download.required"));
                await BackendClient.DownloadAsync(Settings.downloadDirectory);
                Settings.modelDirectory = Settings.downloadDirectory; Settings.Persist();
                Q<TextField>("modelPath").SetValueWithoutNotify(Settings.modelDirectory); SetNotice("notice.downloaded");
            }, "notice.downloading"));
            Button("addFolder", "scope.folder", AddFolder);
            Button("addSelection", "scope.selection", () => AddSelection(Settings.roots));
            Button("excludeSelection", "scope.exclude", () => AddSelection(Settings.excluded));
            Button("importSample", "sample.import", ImportSample);
            Button("previewScope", "scope.preview", () => Run(() => { Scan(); SetNotice("notice.preview", scanned.Count); return Task.CompletedTask; }, "notice.preparing"));
            Button("indexScope", "scope.index", () => Run(async () => {
                Scan(); if (scanned.Count == 0) throw new InvalidOperationException(Locale.Text("scope.empty"));
                cancelPrepare = false;
                var request = await AssetCollector.PrepareAsync(scanned, () => cancelPrepare);
                await BackendClient.StartAsync(); status = await BackendClient.IndexAsync(request); SetNotice("notice.indexing");
            }, "notice.preparing"));
            Button("cancel", "cancel", () => { cancelPrepare = true; if (status?.running ?? false) Run(async () => { await BackendClient.CancelAsync(); SetNotice("notice.cancelRequested"); }, "notice.cancelRequested"); });
            Button("searchButton", "search.action", Search);
            Button("stopBackend", "backend.stop", () => { BackendClient.Stop(); status = null; SetNotice("notice.stopped"); Refresh(); });
            Button("clearIndex", "index.clear", () => Run(async () => { await BackendClient.ClearAsync(); hits = Array.Empty<SearchHit>(); status = await BackendClient.StatusAsync(); RenderResults(); SetNotice("notice.cleared"); }, "notice.preparing"));
            RenderScopes(); RenderResults(); Refresh();
        }

        void Search() => Run(async () => {
            var timer = Stopwatch.StartNew(); var response = await BackendClient.SearchAsync(new SearchRequest { query = query, kind = kind, top = Settings.resultLimit, code = code,
                minScore = Settings.hideLowScores ? Settings.minimumScore : -1, pathContains = Settings.pathContains, extensions = Settings.extensions, sort = Settings.resultSort });
            hits = response.results ?? Array.Empty<SearchHit>(); matched = response.matched;
            lastQueryMs = timer.Elapsed.TotalMilliseconds; status = await BackendClient.StatusAsync();
            RenderResults(); SetNotice("notice.searched", hits.Length);
        }, "notice.searching");
        async void Run(Func<Task> action, string pending)
        {
            if (busy) return; busy = true; hasError = false; SetNotice(pending); Refresh();
            try { await action(); }
            catch (OperationCanceledException) { SetNotice("notice.cancelled"); }
            catch (Exception ex) { details = ex.Message; hasError = true; SetNotice("notice.failed"); Q<Foldout>("diagnostics").value = true; }
            finally { busy = false; Refresh(); }
        }
        async void Tick()
        {
            if (!BackendClient.IsRunning || busy || polling || EditorApplication.timeSinceStartup < nextPoll) return;
            polling = true; nextPoll = EditorApplication.timeSinceStartup + 0.5;
            try {
                bool wasRunning = status?.running ?? false; status = await BackendClient.StatusAsync();
                if (wasRunning && !status.running) {
                    if (status.errors?.Length > 0) { details = string.Join("\n", status.errors); hasError = true; SetNotice("notice.indexErrors", status.failed); }
                    else {
                        SetNotice(status.cancelled ? "notice.cancelled" : "notice.indexed", status.updated, status.skipped);
                        if (!status.cancelled) { Q<Foldout>("setupSection").value = false; ShowSettings(false); }
                    }
                }
                Refresh();
            }
            catch (Exception ex) { details = ex.Message; hasError = true; SetNotice("notice.failed"); Refresh(); }
            finally { polling = false; }
        }
        void Refresh()
        {
            if (Q<Label>("notice") == null) return;
            bool indexing = status?.running ?? false;
            Q<FloatField>("minimumScore").SetEnabled(Settings.hideLowScores);
            Q<Label>("filterSummary").text = Settings.hideLowScores || !string.IsNullOrWhiteSpace(Settings.pathContains) || !string.IsNullOrWhiteSpace(Settings.extensions) ? Locale.Text("filters.active") : "";
            Q<Label>("filterSummary").EnableInClassList("hidden", Q<Label>("filterSummary").text.Length == 0);
            Q<Label>("activeScope").text = Locale.Text("scope.active", Settings.roots.Count, status?.count ?? 0);
            Q<VisualElement>("setupControls").SetEnabled(!busy && !indexing && !BackendClient.IsRunning);
            Q<VisualElement>("scopeControls").SetEnabled(!busy && !indexing);
            Q<Button>("searchButton").SetEnabled(!busy && !indexing && !string.IsNullOrWhiteSpace(query) && Settings.roots.Count > 0);
            Q<Button>("cancel").EnableInClassList("hidden", !busy && !indexing);
            Q<Button>("cancel").SetEnabled(indexing || busy && noticeKey == "notice.preparing");
            Q<Button>("stopBackend").SetEnabled(!busy && !indexing && BackendClient.IsRunning);
            Q<Button>("clearIndex").SetEnabled(!busy && !indexing && BackendClient.IsRunning);
            Q<VisualElement>("downloadBox").EnableInClassList("hidden", HasWeights());
            var progress = Q<ProgressBar>("indexProgress"); progress.EnableInClassList("hidden", !indexing);
            if (status != null) {
                progress.highValue = Math.Max(status.total, 1); progress.value = status.done; progress.title = Locale.Text("progress", status.done, status.total);
                Q<Label>("indexSummary").text = Locale.Text("index.summary", status.count, status.updated, status.skipped, status.failed, status.elapsed_seconds);
            }
            Q<Label>("scopeSummary").text = scanned == null ? Locale.Text("scope.notScanned") : Locale.Text("scope.count", scanned.Count,
                scannedText, scannedImage, scannedAudio);
            Q<Label>("notice").text = Locale.Text(noticeKey, noticeArgs); Q<Label>("notice").EnableInClassList("error", hasError);
            Q<TextField>("log").SetValueWithoutNotify(details + "\n" + BackendClient.Log);
        }
        bool HasWeights() => File.Exists(Path.Combine(Settings.modelDirectory, "model.safetensors")) || File.Exists(Path.Combine(Settings.modelDirectory, "model.safetensors.index.json"));
        void ShowSettings(bool value)
        {
            showSettings = value;
            Q<VisualElement>("searchPane").EnableInClassList("hidden", value);
            Q<VisualElement>("settingsPane").EnableInClassList("hidden", !value);
            Q<Button>("searchTab").EnableInClassList("active-tab", !value);
            Q<Button>("settingsTab").EnableInClassList("active-tab", value);
        }
        void Scan()
        {
            scanned = AssetCollector.Scan(); scannedText = scannedImage = scannedAudio = 0;
            foreach (string path in scanned) {
                switch (AssetCollector.Kind(path)) { case "text": scannedText++; break; case "image": scannedImage++; break; case "audio": scannedAudio++; break; }
            }
        }
        void SetNotice(string key, params object[] args) { noticeKey = key; noticeArgs = args; }
        void ChangedScope() { Settings.Persist(); hasError = false; scanned = null; hits = Array.Empty<SearchHit>(); RenderScopes(); RenderResults(); Refresh(); }
        void RenderScopes() { RenderScopeList("includedList", Settings.roots); RenderScopeList("excludedList", Settings.excluded); }
        void RenderScopeList(string name, List<string> paths)
        {
            var list = Q<VisualElement>(name); list.Clear();
            foreach (string path in paths.ToArray()) {
                var row = new VisualElement(); row.AddToClassList("scope-row"); var label = new Label(path); label.AddToClassList("scope-path"); row.Add(label);
                var remove = new Button(() => { paths.Remove(path); ChangedScope(); }) { text = Locale.Text("remove") }; remove.AddToClassList("compact"); row.Add(remove); list.Add(row);
            }
        }
        void AddSelection(List<string> target)
        {
            foreach (var obj in Selection.objects) {
                string path = AssetDatabase.GetAssetPath(obj);
                if ((path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal)) && !target.Contains(path)) target.Add(path);
            }
            ChangedScope();
        }
        void AddFolder() => ChooseFolder("scope.folder", Application.dataPath, folder => {
            string normalized = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).Replace('\\', '/');
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (normalized != assets && !normalized.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)) { hasError = true; SetNotice("scope.insideAssets"); Refresh(); return; }
            string relative = "Assets" + normalized.Substring(assets.Length); if (!Settings.roots.Contains(relative)) Settings.roots.Add(relative); ChangedScope();
        });
        void ImportSample()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AssetSearchWindow).Assembly);
            var samples = Sample.FindByPackage(info.name, info.version).ToArray();
            if (samples.Length == 0) { hasError = true; SetNotice("sample.missing"); Refresh(); return; }
            var sample = samples[0];
            if (!sample.isImported && !sample.Import()) { hasError = true; SetNotice("sample.failed"); Refresh(); return; }
            AssetDatabase.Refresh(); string path = sample.importPath.Replace('\\', '/');
            if (Path.IsPathRooted(path)) path = "Assets" + path.Substring(Application.dataPath.Length);
            if (!Settings.roots.Contains(path)) Settings.roots.Add(path); ChangedScope(); SetNotice("sample.ready"); Refresh();
        }
        void RenderResults()
        {
            var list = Q<VisualElement>("results"); if (list == null) return; list.Clear();
            Q<Label>("emptyResults").text = Locale.Text("results.empty"); Q<Label>("emptyResults").EnableInClassList("hidden", hits.Length > 0);
            Q<Label>("resultSummary").text = hits.Length == 0 ? "" : Locale.Text("results.filteredCount", hits.Length, matched, lastQueryMs);
            foreach (var hit in hits) {
                var asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(hit.guid)); var row = new VisualElement(); row.AddToClassList("result");
                var image = new Image { image = AssetPreview.GetMiniThumbnail(asset), scaleMode = ScaleMode.ScaleToFit }; image.AddToClassList("thumbnail"); row.Add(image);
                var copy = new VisualElement(); copy.AddToClassList("result-copy");
                var title = new Label(Path.GetFileNameWithoutExtension(hit.path)); title.AddToClassList("result-name"); copy.Add(title);
                var path = new Label(hit.path); path.AddToClassList("result-path"); copy.Add(path);
                string extra = hit.kind == "audio" ? Locale.Text("result.offset", hit.offset) : "";
                var score = new Label(Locale.Text("result.score", Locale.Text("type." + hit.kind), hit.score) + extra); score.AddToClassList("result-score"); copy.Add(score); row.Add(copy);
                var select = new Button(() => { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); }) { text = Locale.Text("result.select") }; select.AddToClassList("compact"); row.Add(select);
                var open = new Button(() => AssetDatabase.OpenAsset(asset)) { text = Locale.Text("result.open") }; open.AddToClassList("compact"); row.Add(open); list.Add(row);
                var copyPath = new Button(() => EditorGUIUtility.systemCopyBuffer = hit.path) { text = Locale.Text("result.copyPath") }; copyPath.AddToClassList("compact"); row.Add(copyPath);
                var copyGuid = new Button(() => EditorGUIUtility.systemCopyBuffer = hit.guid) { text = Locale.Text("result.copyGuid") }; copyGuid.AddToClassList("compact"); row.Add(copyGuid);
                image.tooltip = Locale.Text("result.drag");
                image.RegisterCallback<MouseDownEvent>(e => { if (e.button == 0 && asset != null) { DragAndDrop.PrepareStartDrag(); DragAndDrop.objectReferences = new[] { asset }; DragAndDrop.StartDrag(hit.path); e.StopPropagation(); } });
            }
        }
        void Foldout(string name, string key, bool value) { var f = Q<Foldout>(name); f.text = Locale.Text(key); f.value = value; }
        void Text(string name, string key, string value, Action<string> changed) { var f = Q<TextField>(name); f.label = Locale.Text(key); f.SetValueWithoutNotify(value); f.isDelayed = true; f.RegisterValueChangedCallback(e => changed(e.newValue)); }
        void Toggle(string name, string key, bool value, Action<bool> changed) { var f = Q<Toggle>(name); f.text = Locale.Text(key); f.SetValueWithoutNotify(value); f.RegisterValueChangedCallback(e => { changed(e.newValue); ChangedScope(); }); }
        void Button(string name, string key, Action clicked) { var b = Q<Button>(name); b.text = Locale.Text(key); b.clicked += clicked; }
        static void ChooseFolder(string key, string start, Action<string> selected) { string p = EditorUtility.OpenFolderPanel(Locale.Text(key), start, ""); if (!string.IsNullOrEmpty(p)) selected(p); }
    }
}
