using System;
using System.Collections.Generic;
using UnityEditor;
using System.IO;
using UnityEngine;
using System.Threading;

namespace LocalAssetSearch
{
    [FilePath("UserSettings/LocalAssetSearch.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class SearchSettings : ScriptableSingleton<SearchSettings>
    {
        public string language = "ko";
        public string python = "python";
        public string modelDirectory = "";
        public string downloadDirectory = "";
        public string device = "auto";
        public string batchProfile = "balanced";
        public int dimensions = 768;
        public bool text = true;
        public bool image = true;
        public bool audio = true;
        public int assetLimit = 1000;
        public bool hideLowScores;
        public float minimumScore = 0.7f;
        public int resultLimit = 20;
        public string resultSort = "similarity", pathContains = "", extensions = "";
        public List<string> roots = new List<string>();
        public List<string> excluded = new List<string>();
        public void Persist()
        {
            Save(true);
            string directory = BackendClient.StateDirectory;
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "agent-scope.json");
            string data = JsonUtility.ToJson(new SearchRequest { roots = roots.ToArray(), excluded = excluded.ToArray(), kind = "all" });
            // Windows readers/antivirus may temporarily deny FILE_SHARE_DELETE. Keep atomic
            // replacement, but retry a bounded 200 ms instead of failing a settings update.
            for (int attempt = 0; ; attempt++) {
                string pending = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try {
                    File.WriteAllText(pending, data, new System.Text.UTF8Encoding(false));
                    if (File.Exists(target)) File.Replace(pending, target, null); else File.Move(pending, target);
                    break;
                }
                catch (IOException) when (attempt < 4) { Thread.Sleep(20 * (attempt + 1)); }
                finally { if (File.Exists(pending)) File.Delete(pending); }
            }
        }
    }

    [Serializable] public sealed class AssetItem
    {
        public string guid, path, kind, source;
    }
    [Serializable] public sealed class IndexRequest { public List<AssetItem> items = new List<AssetItem>(); }
    [Serializable] public sealed class SearchRequest
    {
        public string query, kind = "all";
        public int top = 20;
        public bool code;
        public float minScore = -1;
        public string pathContains = "", extensions = "", sort = "similarity";
        public string[] roots = Array.Empty<string>();
        public string[] excluded = Array.Empty<string>();
    }
    [Serializable] public sealed class SearchHit
    {
        public string guid, path, kind;
        public float score, offset;
    }
    [Serializable] public sealed class SearchResponse { public SearchHit[] results; public string fingerprint; public int matched; }
    [Serializable] public sealed class JobStatus
    {
        public bool running, cancelled;
        public int done, total, updated, skipped, failed, count, dimensions;
        public string current, device;
        public string[] errors;
        public bool model_loaded;
        public float elapsed_seconds, load_seconds, hash_seconds, gpu_allocated_mb, gpu_peak_mb;
    }
    [Serializable] internal sealed class Ready { public int port, pid; public string session; }
    [Serializable] internal sealed class ApiError { public string error; }
}
