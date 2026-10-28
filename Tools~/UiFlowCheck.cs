// Copy to Assets/Editor of a disposable sample-only project; invoke Run via Unity CLI.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LocalAssetSearch;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class UiFlowCheck
{
    static AssetSearchWindow window;
    static Report report;
    [Serializable] public class Step { public string name, notice, first; public int results; }
    [Serializable] public class Report { public string unity, package, mode="UI Toolkit NavigationSubmitEvent and ChangeEvent; no direct search/index/connect API calls"; public bool passed; public string error; public List<Step> steps=new List<Step>(); }
    static T Q<T>(string name) where T:VisualElement => window.rootVisualElement.Q<T>(name);
    static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
    static string Notice => Q<Label>("notice")?.text ?? "";
    static int Count => Q<VisualElement>("results").childCount;
    static string First => Q<VisualElement>("results").Q<Label>(className:"result-name")?.text ?? "";
    static void Submit(string name) {
        var button=Q<Button>(name); Require(button!=null && button.enabledInHierarchy,"Button unavailable: "+name);
        for (VisualElement element=button;element!=null;element=element.parent)
            Require(element.resolvedStyle.display!=DisplayStyle.None,"Hidden UI button: "+name);
        button.Focus();
        using (var evt=NavigationSubmitEvent.GetPooled()) { evt.target=button; button.SendEvent(evt); }
    }
    static async Task Until(Func<bool> predicate,string reason,int timeout=120000) {
        double deadline=EditorApplication.timeSinceStartup+timeout/1000.0;
        while (!predicate()) {
            if (Q<Label>("notice")?.ClassListContains("error")==true) throw new InvalidOperationException("UI error: "+Notice);
            Require(EditorApplication.timeSinceStartup<deadline,"Timeout: "+reason+"; notice="+Notice);
            await Task.Delay(100);
        }
    }
    static void Record(string name) { report.steps.Add(new Step{name=name,notice=Notice,results=Count,first=First}); }
    static async Task Search(string name,int count,string first=null) {
        Submit("searchButton");
        await Until(()=>Notice==Locale.Text("notice.searched",count) && Q<Button>("searchButton").enabledInHierarchy,name);
        Require(Count==count,"Wrong rendered result count: "+name);
        if (first!=null) Require(First==first,"Wrong rendered first result: "+name+"; "+First);
        Record(name);
    }
    public static async void Run() {
        report=new Report{unity=Application.unityVersion};
        try {
            var info=UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AssetSearchWindow).Assembly);
            report.package=info.name+"@"+info.version;
            Require(info.source==UnityEditor.PackageManager.PackageSource.Git,"Expected GitHub-installed package");
            Require(SearchSettings.instance.roots.Count==1 && SearchSettings.instance.roots[0].StartsWith("Assets/Samples/Local Asset Embedding Search/0.2.2/"),"Only disposable sample scope may be indexed");
            AssetSearchWindow.Open(); window=EditorWindow.GetWindow<AssetSearchWindow>();
            await Until(()=>Q<Button>("settingsTab")!=null && window.rootVisualElement.panel!=null,"UI attachment");
            Q<DropdownField>("language").index=0;
            Submit("settingsTab");Q<Foldout>("setupSection").value=true;await Task.Delay(200);
            Submit("checkEnvironment");await Until(()=>Notice.StartsWith("연결 완료") && Q<Button>("stopBackend").enabledInHierarchy,"model connect");Record("connect button");
            Submit("importSample");await Until(()=>Notice==Locale.Text("sample.ready"),"sample button");Record("sample button");
            Submit("previewScope");await Until(()=>Notice==Locale.Text("notice.preview",6),"scope preview");Record("preview 6 samples");
            Q<Foldout>("diagnostics").value=true;await Task.Delay(200);
            Submit("clearIndex");await Until(()=>Notice==Locale.Text("notice.cleared") && Q<Button>("clearIndex").enabledInHierarchy,"clear generated sample index");Record("clear generated index button");
            Submit("indexScope");await Until(()=>Notice==Locale.Text("notice.indexed",6,0),"fresh sample indexing");Record("index button: 6 updated");
            Submit("settingsTab");await Task.Delay(200);Submit("indexScope");await Until(()=>Notice==Locale.Text("notice.indexed",0,6),"incremental indexing");Record("index button: 6 skipped");
            Submit("searchTab");await Task.Delay(200);Q<TextField>("queryField").value="a red square";Q<DropdownField>("resultType").index=2;
            await Search("image search button",2,"RedSquare");
            Q<Foldout>("searchFilters").value=true;Q<Toggle>("hideLowScores").value=true;Q<FloatField>("minimumScore").value=.8f;
            await Search("minimum score 0.8",1,"RedSquare");
            Q<FloatField>("minimumScore").value=.95f;await Search("minimum score 0.95",0);
            Q<Toggle>("hideLowScores").value=false;Q<TextField>("extensions").value="PNG";Q<TextField>("pathContains").value="BlueCircle";
            await Search("path and extension",1,"BlueCircle");
            Submit("resetFilters");await Task.Delay(200);
            Require(!Q<Toggle>("hideLowScores").value && Q<TextField>("pathContains").value=="" && Q<TextField>("extensions").value=="","Reset did not update controls");
            Q<Foldout>("searchFilters").value=true;Q<DropdownField>("resultSort").index=1;await Search("name sorting",2,"BlueCircle");
            Submit("resetFilters");await Task.Delay(200);
            Q<DropdownField>("resultType").index=1;Q<TextField>("queryField").value="a sharp metal sword";await Search("text search",2,"Sword");
            Q<DropdownField>("resultType").index=3;Q<TextField>("queryField").value="an electronic tone";await Search("audio search",2);
            Q<DropdownField>("language").index=1;await Task.Delay(200);Require(Q<Label>("title").text==Locale.Text("title"),"English title mismatch");await Search("English UI search",2);
            Q<DropdownField>("language").index=0;await Task.Delay(200);
            Submit("settingsTab");Q<Foldout>("diagnostics").value=true;await Task.Delay(200);
            Submit("stopBackend");await Until(()=>Notice==Locale.Text("notice.stopped") && !Q<Button>("stopBackend").enabledInHierarchy,"stop button");Record("stop button");
            Q<Foldout>("setupSection").value=true;await Task.Delay(200);
            Submit("checkEnvironment");await Until(()=>Notice.StartsWith("연결 완료"),"reconnect before Editor shutdown");Record("reconnect button");
            report.passed=true;File.WriteAllText("Logs/ui-event-flow.json",JsonUtility.ToJson(report,true));
            Debug.Log("UI_EVENT_FLOW_PASS");EditorApplication.Exit(0);
        } catch(Exception e) {
            report.error=e.ToString();File.WriteAllText("Logs/ui-event-flow.json",JsonUtility.ToJson(report,true));Debug.LogException(e);EditorApplication.Exit(1);
        }
    }
}
