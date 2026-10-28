using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace LocalAssetSearch
{
    public static class AssetCollector
    {
        static readonly HashSet<string> TextExtensions = new HashSet<string> {
            ".txt", ".md", ".json", ".csv", ".xml", ".yaml", ".yml", ".cs", ".shader", ".compute", ".uss", ".uxml", ".lua"
        };
        static readonly HashSet<string> AudioExtensions = new HashSet<string> { ".wav", ".ogg", ".flac", ".mp3", ".aif", ".aiff" };

        public static string Kind(string path)
        {
            var settings = SearchSettings.instance;
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (settings.text && TextExtensions.Contains(extension)) return "text";
            if (settings.audio && AudioExtensions.Contains(extension)) return "audio";
            if (settings.image && typeof(Texture2D).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(path))) return "image";
            return null;
        }

        public static List<string> Scan()
        {
            var settings = SearchSettings.instance;
            if (settings.roots.Count == 0) throw new InvalidOperationException("Add folders or assets to the indexing scope first");
            var paths = new HashSet<string>();
            foreach (string root in settings.roots)
            {
                if (!(root == "Assets" || root.StartsWith("Assets/", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Only Assets folders/assets can be indexed: " + root);
                if (AssetDatabase.IsValidFolder(root))
                {
                    string filter = string.Join(" ", new[] { settings.text ? "t:TextAsset t:Shader t:ComputeShader" : "",
                        settings.image ? "t:Texture2D" : "", settings.audio ? "t:AudioClip" : "" }.Where(s => s.Length > 0));
                    // Unity ORs type filters. The root list is mandatory; there is no startup scan.
                    if (filter.Length > 0)
                        foreach (string guid in AssetDatabase.FindAssets(filter, new[] { root })) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
                    // Some Unity versions don't classify Markdown/JSON as TextAsset.
                    if (settings.text)
                        foreach (string guid in AssetDatabase.FindAssets("", new[] { root }))
                        {
                            string path = AssetDatabase.GUIDToAssetPath(guid);
                            if (TextExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) paths.Add(path);
                        }
                }
                else if (File.Exists(Path.Combine(BackendClient.ProjectRoot, root))) paths.Add(root);
                else throw new InvalidOperationException("Scope no longer exists: " + root);
            }
            var result = paths.Where(path => File.Exists(Path.Combine(BackendClient.ProjectRoot, path)) && Kind(path) != null
                && !settings.excluded.Any(ex => path == ex || path.StartsWith(ex.TrimEnd('/') + "/", StringComparison.Ordinal)))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (result.Count > settings.assetLimit) throw new InvalidOperationException("Scope contains " + result.Count
                + " assets; limit is " + settings.assetLimit + ". Narrow the scope or increase the limit. Nothing was indexed.");
            return result;
        }

        public static async Task<IndexRequest> PrepareAsync(List<string> paths, Func<bool> cancelled = null, bool yieldBetweenAssets = true)
        {
            var request = new IndexRequest();
            string previewDirectory = Path.Combine(BackendClient.StateDirectory, "Previews");
            Directory.CreateDirectory(previewDirectory);
            foreach (string path in paths)
            {
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                string guid = AssetDatabase.AssetPathToGUID(path);
                string source = Path.Combine(BackendClient.ProjectRoot, path);
                string kind = Kind(path);
                if (kind == null) continue;
                if (kind == "image")
                {
                    // Cache rendered previews by dependency hash; no importer/readability changes.
                    string hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
                    source = Path.Combine(previewDirectory, guid + "-" + hash + ".png");
                    if (!File.Exists(source)) ExportTexture(path, source);
                }
                request.items.Add(new AssetItem { guid = guid, path = path, kind = kind, source = source });
                if (yieldBetweenAssets) await Task.Yield();
            }
            return request;
        }

        static void ExportTexture(string path, string destination)
        {
            var original = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (original == null) throw new InvalidOperationException("Could not load texture: " + path);
            float scale = Mathf.Min(1f, 1024f / Mathf.Max(original.width, original.height));
            int width = Mathf.Max(1, Mathf.RoundToInt(original.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(original.height * scale));
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D copy = null;
            try
            {
                Graphics.Blit(original, target);
                RenderTexture.active = target;
                copy = new Texture2D(width, height, TextureFormat.RGB24, false, false);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0); copy.Apply();
                File.WriteAllBytes(destination, copy.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
            }
        }
    }
}
