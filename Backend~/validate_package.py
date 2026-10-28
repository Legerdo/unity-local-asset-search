"""Offline package and localization validation; does not need Unity or model weights."""
import json
from pathlib import Path
import re
import string
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
tables = {}
for language in ("ko", "en"):
    entries = json.loads((root / f"Editor/Locales/{language}.json").read_text("utf-8"))["entries"]
    tables[language] = {e["key"]: e["value"] for e in entries}
    assert len(tables[language]) == len(entries), "duplicate translation key"
assert tables["ko"].keys() == tables["en"].keys(), "translation key mismatch"
formatter = string.Formatter()
for key in tables["ko"]:
    placeholders = lambda s: sorted(name for _, name, _, _ in formatter.parse(s) if name is not None)
    assert placeholders(tables["ko"][key]) == placeholders(tables["en"][key]), key
source = (root / "Editor/AssetSearchWindow.cs").read_text("utf-8")
keys = set(re.findall(r'(?:Locale.Text|SetNotice)\("([\w.]+)"\s*[,)]', source))
keys.update("type." + kind for kind in ("text", "image", "audio"))
keys.update(re.findall(r'(?:Text|Toggle|Button|Foldout)\("[^"]+",\s*"([\w.]+)"', source))
assert not keys - tables["ko"].keys(), "missing keys: " + str(keys - tables["ko"].keys())
tree = ET.parse(root / "Editor/AssetSearchWindow.uxml")
names = {element.attrib["name"] for element in tree.iter() if "name" in element.attrib}
queried = set(re.findall(r'Q<[^>]+>\("([^"]+)"', source))
assert not queried - names, "missing UXML nodes: " + str(queried - names)
named = {e.attrib["name"]: e for e in tree.iter() if "name" in e.attrib}
assert len(named) == sum("name" in e.attrib for e in tree.iter()), "duplicate UXML name"
assert not any(e.attrib.get("name") == "queryField" for e in named["resultsScroll"].iter()), "Search input must remain outside results scrolling"
assert any(e.attrib.get("name") == "queryField" for e in named["searchPane"].iter())
assert not any(e.attrib.get("name") == "queryField" for e in named["settingsPane"].iter()), "Setup and search must be separate panes"
manifest = json.loads((root / "package.json").read_text("utf-8"))
assert not manifest.get("dependencies"), "Unexpected UPM dependencies"
assert (root / manifest["samples"][0]["path"]).is_dir()
print(f"PASS package {manifest['version']}: {len(tables['ko'])} paired locale keys, placeholders, UXML references, sample and no UPM dependencies")
