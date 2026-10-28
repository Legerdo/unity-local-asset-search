# Local Asset Search for Unity

[한국어](README.md) · [Benchmarks](Documentation~/BENCHMARKS.md) · [Troubleshooting](Documentation~/TROUBLESHOOTING.md) · [Todo](Documentation~/TODO.md)

Search text, images and audio in the Unity Editor using natural-language queries. Index only the folders you choose, then select the original assets, copy their paths or drag their preview icons into Unity. The UI defaults to Korean and includes English.

EmbeddingGemma 2 runs in a separate local Python process. The package has no Sentis/Inference Engine or render-pipeline dependency and does not include model inference in player builds.

![Actual Unity Editor walkthrough](Documentation~/media/quick-start.gif)

Captured from version 0.2.0. The GIF combines real Editor steps; its playback duration is not a latency measurement.

## Features

| Task | Controls |
|---|---|
| Find code, documents and images | Queries such as `player movement code` or `a red square` |
| Find audio candidates | Describe a sound, then listen in the Inspector; sound-event recognition remains limited |
| Narrow the scope | Include/exclude folders, asset kind, path substring and extensions |
| Filter results | Optional minimum cosine, similarity/name/path ordering, 10/20/50/100 results |
| Use an asset | Select/open, copy path/GUID, drag its preview icon |
| Connect agents | Read-only MCP/CLI and public C# Editor APIs |

Version 0.2.2 embeds image/audio content together with the filename and two nearby folder names. Incorrect metadata can hurt results. [Quality checks](Documentation~/QUALITY.md) separate filename-assisted retrieval from content understanding.

## Install

Prepare Python 3.11–3.13 on Windows. CPU and NVIDIA CUDA inference are supported. Unity 2021.3 is the declared minimum, but has not been run in validation. See the [tested version matrix](Documentation~/COMPATIBILITY.md).

In Package Manager, choose **Install/Add package from git URL**:

```text
https://github.com/Legerdo/unity-local-asset-search.git#v0.2.2
```

Alternatively, install the `.tgz` from the [release](https://github.com/Legerdo/unity-local-asset-search/releases/tag/v0.2.2) as a tarball, or select a local checkout's `package.json` with package-from-disk.

The public `v0.2.2` tag passed a clean Unity 6.3 installation with a new Python environment, six imported samples, real indexing/search/filter/MCP checks and regression tests. See the [clean-install record](Documentation~/RELEASE-VALIDATION.md) for evidence and exclusions.

A follow-up [CLI UI event test](Documentation~/UI-VALIDATION.md) passed 16 button/input steps without coordinate automation or direct backend operation calls. Pointer hit testing and visual acceptance remain outside that test.

### First search

1. Open `Tools > Local Asset Search > Setup & index` and switch the header language to English.
2. Prepare the recommended isolated Python environment. It lives in the project's Library; global Python packages are untouched. NVIDIA GPUs select the CUDA environment.
3. Select an existing native Hugging Face EmbeddingGemma 2 folder. If absent, choose an empty download destination and download after environment setup finishes. Only required files from the pinned revision are fetched; no ONNX/GGUF conversion is needed.
4. Connect the model, import the Quick Start sample or add specific Assets folders.
5. Preview the count, then index. The whole project is never indexed automatically. Unchanged files are skipped on later updates.
6. Search for `a red square`, choose images and select/open a result.

Search controls stay fixed while results scroll. Run the search again after changing filters. Cosine scores range from -1 to 1 and are not probabilities; choose thresholds for the asset kind and task.

When upgrading to 0.2.2, stop/reconnect the backend, clear the generated index in Setup and reindex. Its media preprocessing fingerprint rejects older vectors. Clearing the index does not remove original assets or model files.

## Measurements

Measured on 2026-10-28 with Windows 11, RTX 5080 16 GB, 32 GB system RAM and 768-dimensional CUDA BF16 embeddings. The model was warm. Indexing excludes Unity import, preview generation and startup; search includes HTTP round trips but excludes UI rendering.

| Dataset and entry point | Index | New query p50 / p95 |
|---|---:|---:|
| 134 authored fixtures through Unity, v0.2.0 | 2.616 s | 42.37 / 49.93 ms |
| 192 real resources through product HTTP/Engine, v0.2.2 | 14.56 s | 46.33 / 58.85 ms |

The fixture set contains 130 short texts, two shapes and two tones. The real set contains 64 assets per modality and 426 vectors. Different datasets/versions prevent a direct speedup comparison. Real 1k/5k/10k fully embedded datasets have not been measured. See [conditions and reproduction](Documentation~/BENCHMARKS.md) and [memory checks](Documentation~/MEMORY.md).

In a small held-out image set, all nine content-based queries ranked a relevant image first without metadata. Audio has no equivalent independent listening ground truth. A small CPU comparison favored CLAP Q8 in cost and automatic-reference agreement, but it is not integrated. EmbeddingGemma 2 remains the product model. These observations are not general retrieval accuracy claims.

## Agent integration

Register `Agent~/mcp_server.py --project <Unity project>` as an MCP stdio server after connecting and indexing in Unity:

```text
unity_asset_status({})
unity_asset_search({"query":"a red square", "kind":"image", "top":5})
```

The standard-library CLI returns UTF-8 JSON:

```powershell
python "<package>/Agent~/search.py" --project "<project>" --query "a metal sword" --kind text --top 10
```

[Configuration, filters and C# APIs](Agent~/README.md) · [Optional agent skill](Agent~/skills/unity-asset-search/SKILL.md). Agent credentials allow status/search only; the server enforces the Editor's current scope. Indexing and configuration use the Editor or its public APIs on the Unity main thread.

## Data and limits

Settings live in `UserSettings/LocalAssetSearch.asset`. Generated indexes, previews, diagnostics, environment and agent connection files live under `Library/LocalAssetSearch/`. Models live in the selected directory. Keep generated data and credentials out of source control. Asset content is not uploaded; network access is used for explicit dependency setup and model downloads.

The default scope limit is 1,000 assets, with a maximum of 10,000. Search filters before exact cosine ranking; there is no ANN index. Video, AnimationClip content analysis, prefab/3D rendering, automatic file watching and MPS are unsupported. Non-Windows execution, light-theme visuals, real GPU OOM recovery and long-running large projects remain unverified.

[Technical contracts](Documentation~/README.md) · [Troubleshooting](Documentation~/TROUBLESHOOTING.md) · [Todo](Documentation~/TODO.md) · [Contributing and tests](CONTRIBUTING.md) · [Changelog](CHANGELOG.md).

## License and references

Package code and authored Quick Start samples use the [MIT license](LICENSE.md). Model weights are not bundled; [google/embeddinggemma-2](https://huggingface.co/google/embeddinggemma-2) has separate terms. Pinned revision: `914f7f89142e33e77833254d9c9b90c3cef7303b`. Installed Python dependencies retain their own licenses.

[ayutaz/unity-embeddinggemma-2](https://github.com/ayutaz/unity-embeddinggemma-2) informed the approach comparison; this is a separate implementation with separate validation. Korean prose was edited using the [ai-slop-thresher](https://github.com/Burntgogi/ai-slop-thresher) guidelines.
