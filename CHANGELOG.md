# Changelog

## 0.2.1

- Release model-card back-references and collect native/GPU allocations when clearing the generated index.
- Reuse one worker for model loading and inference to prevent native threads and handles accumulating across HTTP-triggered reloads.
- Drop failed inference tracebacks before OOM batch retries and per-asset fallback.
- Detect the Editor input pipe closing during Windows startup without locking stdin; terminate the owned backend without waiting on native interpreter teardown.
- Expose reserved GPU memory and query cache size in diagnostics; add repeatable real-model and Unity resource lifetime checks.

## 0.2.0

- Replace the initial IMGUI window with localized UI Toolkit search/setup tabs and independently scrolling results.
- Add novice onboarding, sample import and native text/image/audio fixtures.
- Batch embeddings, filter scope in SQLite before reading vectors, reuse query vectors and normalize stored vectors in FP32.
- Fix Windows UTF-8 process logs and preserve Unity main-thread settings writes before async continuations.
- Add read-only MCP/CLI agent tools, optional skill, repository instructions and portable real-model validation.
- Add server-enforced similarity cutoffs, sorting, path/extension filters, result counts/limits and Editor copy/drag actions.
- Document measured performance, clean first-run download/setup evidence and verified compatibility limits.

## 0.1.0

- Initial local Editor-only implementation with external Python inference and explicit resource scope.
