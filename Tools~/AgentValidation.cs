// Copy into Assets/Editor of a validation project. Requires the already indexed benchmark fixture.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LocalAssetSearch;
using UnityEditor;

public static class AgentValidation
{
    public static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var settings = SearchSettings.instance;
        string previous = EditorJsonUtility.ToJson(settings);
        try {
            settings.python = Environment.GetEnvironmentVariable("UNITY_ASSET_SEARCH_PYTHON") ?? settings.python;
            settings.modelDirectory = Environment.GetEnvironmentVariable("UNITY_ASSET_SEARCH_MODEL") ?? settings.modelDirectory;
            settings.roots = new List<string> { "Assets/LocalAssetSearchBenchmark" }; settings.excluded.Clear();
            BackendClient.StartAsync().GetAwaiter().GetResult();
            BackendClient.WarmupAsync().GetAwaiter().GetResult();
            string script = Path.Combine(BackendClient.PackageDirectory, "Tools~/validate_agent.py");
            string output = Path.Combine(BackendClient.ProjectRoot, "Logs/agent-integration.json");
            using (var run = Process.Start(new ProcessStartInfo(settings.python, "\"" + script + "\" --project \"" + BackendClient.ProjectRoot + "\" --output \"" + output + "\"") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 })) {
                string data = run.StandardOutput.ReadToEnd(), error = run.StandardError.ReadToEnd(); run.WaitForExit();
                if (run.ExitCode != 0) throw new InvalidOperationException(error);
                UnityEngine.Debug.Log("ASSET_SEARCH_MCP_PASS " + data);
            }
        }
        finally { BackendClient.Stop(); EditorJsonUtility.FromJsonOverwrite(previous, settings); settings.Persist(); }
    }
}
