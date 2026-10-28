using System;
using System.Collections.Generic;
using UnityEditor;

namespace LocalAssetSearch
{
    [FilePath("UserSettings/LocalAssetSearch.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class SearchSettings : ScriptableSingleton<SearchSettings>
    {
        public string python = "python";
        public string modelDirectory = "";
        public string downloadDirectory = "";
        public string device = "auto";
        public int dimensions = 768;
        public bool text = true;
        public bool image = true;
        public bool audio = true;
        public int assetLimit = 1000;
        public List<string> roots = new List<string>();
        public List<string> excluded = new List<string>();
        public void Persist() { Save(true); }
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
        public string[] roots = Array.Empty<string>();
        public string[] excluded = Array.Empty<string>();
    }
    [Serializable] public sealed class SearchHit
    {
        public string guid, path, kind;
        public float score, offset;
    }
    [Serializable] public sealed class SearchResponse { public SearchHit[] results; public string fingerprint; }
    [Serializable] public sealed class JobStatus
    {
        public bool running, cancelled;
        public int done, total, updated, skipped, failed, count, dimensions;
        public string current, device;
        public string[] errors;
    }
    [Serializable] internal sealed class Ready { public int port, pid; public string session; }
    [Serializable] internal sealed class ApiError { public string error; }
}
