# Local Asset Search for Unity

Version 0.2.2 embeds the asset name and two nearby folder names together with image/audio content. Rebuild the generated index after upgrading; the preprocessing fingerprint rejects older vectors. Scores remain cosine similarities. Metadata can help asset retrieval but misleading names can also hurt it. See the [paired real-resource quality audit](Documentation~/QUALITY.md).

Memory and process lifetime fixes are included in **0.2.1**. Version 0.2.0 retained GPU weights across model reloads and could leave a backend child running after stop. See the [measured audit and reproduction commands](Documentation~/MEMORY.md) for evidence and limits.

[한국어](README.md)

An Editor-only UPM package for semantic search across local text, images and audio using EmbeddingGemma 2. Inference runs in an isolated Python process, with no Sentis, runtime component or render-pipeline dependency. Korean is the default UI language; select English in the window header.

## Quick start

1. Install `package.json` through Package Manager's package-from-disk option, or install the release `.tgz` as a tarball.
2. Open **Tools > Local Asset Search > Setup & index**. Use Python 3.11–3.13 and create the recommended isolated environment. NVIDIA GPUs automatically select CUDA. Global Python packages are untouched.
3. Select a native Hugging Face EmbeddingGemma 2 checkpoint folder, or choose an empty download folder. Prepare the Python environment before downloading. Only essential files from the pinned revision are downloaded; existing weights are not overwritten.
4. Connect the model, import the Quick Start sample or add specific Assets folders, preview the count, then index. The initial scope is empty and indexing is always explicit.
5. Search from the Search tab. Controls stay fixed while results scroll. Select/open results using Unity's standard asset tools.

The collapsed **Filters & sorting** panel provides an optional minimum cosine, similarity/name/path ordering, result limit, path substring and comma-separated extensions. Apply changes by searching again. Thresholds are not probabilities and differ by modality. Copy paths/GUIDs or drag a result's preview icon into the Scene/Inspector. MCP and CLI expose the same search filters.

![Actual Editor walkthrough](Documentation~/media/quick-start.gif)

The GIF combines real step captures and does not represent elapsed processing time. [Measured results](Documentation~/BENCHMARKS.md) include the raw JSON, hardware, synthetic dataset and reproduction instructions. These are integration/performance checks, not a general retrieval-quality benchmark.

## Direct agent tools

Register `Agent~/mcp_server.py --project <project>` as an MCP stdio server for `unity_asset_search` and `unity_asset_status`. The standard-library CLI `Agent~/search.py` returns UTF-8 JSON as well. No Computer Use is needed. [Setup, examples and public Editor API](Agent~/README.md), [optional skill](Agent~/skills/unity-asset-search/SKILL.md).

Agent credentials permit only search/status. Include/exclude scope is enforced by the backend, and credentials remain in generated Library files. This is a same-user local integration, not an OS sandbox. Model/environment/index management is performed through the Editor or its public C# API on the Unity main thread.

## Implementation and limits

Content-hash incremental indexing, modality batching, preview caching, a 64-query LRU, SQL filtering before vector reads, bounded exact cosine ranking, FP32 re-normalization of BF16 vectors, cancellation and atomic per-asset commits are implemented. No cloud asset uploads or remote model code are used.

Text is UTF-8, up to 2 MB, chunked at 1,800 characters with 200 overlap. Images are Unity Texture2D main assets exported as maximum-1,024px RGB previews without importer changes. Supported audio codecs are those readable by libsndfile, up to 300 seconds/256 MB, resampled to mono 16 kHz and chunked at 20 seconds with 2 overlap. Sprites are indexed at texture level. Unsupported: video, AnimationClip analysis, automatic prefab/3D rendering, ANN, automatic change watching and MPS. Real OOM recovery and non-Windows execution remain unverified.

The minimum declared Unity version is 2021.3. See the [verified version matrix](Documentation~/COMPATIBILITY.md); compatibility with all future versions is not guaranteed. Settings are project-local UserSettings; generated caches, environment and agent credentials live under Library. Model files remain external and are not bundled.

Reference: [ayutaz/unity-embeddinggemma-2](https://github.com/ayutaz/unity-embeddinggemma-2). This package is a separate implementation and does not inherit that repository's test evidence. Model: [google/embeddinggemma-2](https://huggingface.co/google/embeddinggemma-2), pinned revision `914f7f89142e33e77833254d9c9b90c3cef7303b`; original model terms apply.
