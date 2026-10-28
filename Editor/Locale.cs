using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace LocalAssetSearch
{
    public static class Locale
    {
        [Serializable] sealed class Entry { public string key, value; }
        [Serializable] sealed class Table { public Entry[] entries; }
        static readonly Dictionary<string, Dictionary<string, string>> Tables = new Dictionary<string, Dictionary<string, string>>();
        public static CultureInfo Culture => CultureInfo.GetCultureInfo(SearchSettings.instance.language == "en" ? "en-US" : "ko-KR");
        static Dictionary<string, string> Load(string language)
        {
            if (Tables.TryGetValue(language, out var result)) return result;
            var path = Path.Combine(BackendClient.PackageDirectory, "Editor", "Locales", language + ".json");
            result = new Dictionary<string, string>();
            foreach (var entry in JsonUtility.FromJson<Table>(File.ReadAllText(path)).entries) result.Add(entry.key, entry.value);
            Tables.Add(language, result);
            return result;
        }
        public static string Text(string key, params object[] args)
        {
            string language = SearchSettings.instance.language == "en" ? "en" : "ko";
            if (!Load(language).TryGetValue(key, out string value) && !Load("ko").TryGetValue(key, out value)) value = key;
            return args.Length == 0 ? value : string.Format(Culture, value, args);
        }
    }
}
