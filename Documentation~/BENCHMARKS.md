# 실제 측정과 검증

## 결과 읽기

아래 Unity 통합 표는 0.2.0의 직접 제작 134개 샘플입니다. 후속 0.2.2 제품 HTTP/Engine 검사에서는 실제 텍스트·이미지·오디오 각 64개, 총 192개를 14.56초에 색인했고 426벡터를 만들었습니다. 새 검색어 HTTP 지연은 p50 46.33ms, p95 58.85ms였습니다. 원본을 복제하지 않고 읽었으며 전후 SHA-256이 같았습니다. Unity UI 표시는 측정하지 않았습니다.

실제 표본과 샘플은 서로 달라 직접 성능 개선율을 계산할 수 없습니다. 실제 1천·5천·1만개 전체를 임베딩한 대규모 결과는 없습니다. [공개 집계](benchmarks/real-resource-summary.json)와 [메타데이터 제거·오디오 비교](QUALITY.md)를 함께 보세요. 실제 표본의 원본 경로·원시 벡터는 로컬에만 보관하며 공개 재현용으로 배포하지 않습니다.

## 0.2.0 측정 환경

측정일: 2026-10-28. Windows 11, NVIDIA RTX 5080 16 GB, 시스템 RAM 32 GB, Unity 6000.3.24f1. EmbeddingGemma 2 리비전 `914f7f89142e33e77833254d9c9b90c3cef7303b`, 768차원, CUDA BF16 + FP32 벡터 정규화. Python 3.13 격리 환경, 고정 의존성은 `Backend~/requirements.txt`에 있습니다.

## Unity 실제 통합 경로

직접 만든 짧은 텍스트 130개, 단순 도형 이미지 2개, 2초 합성 톤 2개, 총 134개를 사용했습니다. `AssetCollector.Scan/PrepareAsync → BackendClient → 실제 모델 → SQLite → 검색`을 실행했습니다. 모의 임베딩이나 대체 응답을 사용하지 않았습니다.

| 항목 | 기본 배치 | 단건 처리 |
|---|---:|---:|
| 색인 시간, 134개 | 2.616 s | 7.774 s |
| 변경 없음, 134개 전부 건너뛰기 | 0.352 s | 0.366 s |
| 한 파일 변경, 1개 갱신/133개 건너뛰기 | 0.408 s | 0.347 s |
| 서로 다른 검색어 30개 평균 | 42.732 ms | 42.601 ms |
| 검색 p50 | 42.372 ms | 42.754 ms |
| 검색 p95 | 49.927 ms | 56.440 ms |
| 동일 검색어 벡터 캐시 적중 | 5.075 ms | 4.850 ms |
| PyTorch GPU 메모리 최고값 | 2146.134 MiB | 1537.723 MiB |
| 백엔드 프로세스 준비 | 6.422 s | 6.259 s |
| 모델 로드·검증 | 3.314 s | 3.362 s |

기본 배치는 CUDA 텍스트 64, 이미지 4, 오디오 2입니다. 최종 실행의 같은 데이터 비교에서는 색인 시간이 약 66.3% 줄었습니다. 앞선 별도 실행은 5.94초 대 8.56초(약 30.6% 감소)였습니다. 배치 색인의 관측 범위는 2.62~5.94초이고, 장비·캐시·부하를 통제한 반복 실험 결과는 아닙니다. 앞선 결과도 [별도 원본](benchmarks/unity-6000.3-initial.json)으로 보관합니다. 표의 색인 시간은 준비된 입력을 `/index`로 전송한 뒤 완료될 때까지이며, Unity 초기 import/미리보기 생성·Python 시작·모델 로딩 시간은 포함하지 않습니다. 모델 로딩 수치에는 약 1초의 전체 가중치 지문 계산이 포함됩니다. 모든 모드에서 동일한 134개와 30개의 서로 다른 검색어를 사용했습니다.

이미 로드된 모델과 생성된 미리보기를 사용한 웜 상태 측정입니다. 파일 시스템/라이브러리 JIT 캐시는 통제하지 않았고 각 모드를 한 번 측정했습니다. 이전 개발 실행과의 시간 차이를 최적화 효과로 합산하지 않았습니다. 반복 검색어 캐시 수치를 일반 검색 지연으로 표현하지 않습니다.

원본: [unity-6000.3.json](benchmarks/unity-6000.3.json). 필드 `updated=0, skipped=134`는 변경 없음 검사 결과이며 최초 색인 결과는 검증 코드에서 `updated==134`로 강제합니다.

## 텍스트 인코더만 비교

같은 모델에서 짧은 설명 128개를 단건·배치로 인코딩했습니다. 모델 로딩, Unity, SQLite, 이미지·오디오는 제외됩니다. 단건 출력과 배치 출력 사이 코사인을 비교했고 모든 벡터가 0.999 이상이라는 조건을 통과했습니다.

| 배치 | 128개 인코딩 | PyTorch GPU 최고값 | 단건 대비 최소 코사인 |
|---:|---:|---:|---:|
| 1 | 4.672 s | 1475 MiB | 1.000000 |
| 16 | 0.466 s | 1638 MiB | 0.999946 |
| 32 | 0.212 s | 1812 MiB | 0.999946 |
| 64 | 0.147 s | 2147 MiB | 0.999946 |

짧은 텍스트에서 배치 64가 효율적이어서 기본으로 선택했습니다. 이 인코더 수치를 프로젝트 전체 처리 속도로 일반화하지 마세요. 긴 청크·큰 오디오·낮은 VRAM에서는 다릅니다. 실제 OOM 유발 실험은 하지 않았습니다. 원본: [text-batches.json](benchmarks/text-batches.json).

## 다운로드·초기 설치·에이전트

- 기존 모델을 사용하지 않는 새 모델 대상 폴더에서 고정 HF snapshot을 실제로 받아 필수 파일 13개를 확인했습니다. 불필요한 ONNX/GGUF/README 복제본은 받지 않았습니다.
- 새로운 가상 환경에서 `setup.py --cuda`가 완료됐고, 해당 새 환경과 새 모델로 위의 Unity 통합 검증이 통과했습니다. 다운로드 속도·완전한 콜드 네트워크 설치 시간은 측정하지 않았습니다.
- 별도로 모델 경로가 빈 Editor UI에서 다운로드 버튼을 실행하고 모델 폴더가 설정되는 흐름을 확인했습니다. 전역 Python과 기존 원본 모델은 수정하지 않았습니다.
- C# 실제 프로세스 stdout/stderr에 한국어 문장을 각각 출력하고 UTF-8로 정확히 수신했는지 검사했습니다.
- 실제 Editor가 실행한 백엔드에 CLI와 MCP stdio `initialize → tools/list → tools/call`로 직접 연결했습니다. 텍스트·이미지·오디오 모두 유한한 검색 결과를 반환했습니다. [MCP 원본 결과](benchmarks/agent-integration.json).
- MCP 질의 `a sharp metal sword`는 Sword.txt, `a red square`는 RedSquare.png를 1위로 반환했습니다. 합성 톤 검색은 입출력/추론 검증이며 실제 효과음 검색 정확도 평가는 아닙니다.
- 당시 Python 회귀 검사는 15개였습니다. 0.2.2에서는 Backend 19개와 Agent 2개, 총 21개입니다. 인증/Host/Origin, 경로 경계, 기존 모델 덮어쓰기 방지, 청크 끝부분, 증분/실패 격리/취소, SQL 범위 선필터, 질의 캐시, 정규화, 필터·정렬, 읽기 전용 에이전트, 한글 CLI, MCP, 메모리 수명과 미디어 문맥 등을 검사합니다. 알고리즘 fixture는 실제 모델 성능 측정과 분리합니다.

## 재현

검증 전용 Unity 프로젝트에 패키지를 설치하고 `Tools~/AssetSearchValidation.cs`, `Tools~/AgentValidation.cs`를 `Assets/Editor/`에 복사합니다. 원래 프로젝트를 통째로 색인하지 않으며 직접 만든 `Assets/LocalAssetSearchBenchmark` 폴더만 사용합니다. 검증은 이 패키지의 생성 인덱스를 비우므로 평소 검색 인덱스를 보존하려면 반드시 별도 프로젝트를 사용하세요.

```powershell
$env:UNITY_ASSET_SEARCH_PYTHON='<isolated environment>/Scripts/python.exe'
$env:UNITY_ASSET_SEARCH_MODEL='<fresh native model folder>'
unity run '<validation project>' --timeout 600 -- -executeMethod AssetSearchValidation.Run -logFile '<log path>'
unity run '<validation project>' --timeout 240 -- -executeMethod AgentValidation.Run -logFile '<agent log path>'
```

완료 마커는 `ASSET_SEARCH_VALIDATION_PASS`, `ASSET_SEARCH_MCP_PASS`이며 결과는 프로젝트 `Logs/asset-search-benchmark.json`, `Logs/agent-integration.json`에 저장됩니다. UI 구성만 검사하려면 `AssetSearchValidation.LayoutOnly`를 호출합니다. 텍스트 단독 측정은 격리 환경 Python으로 `Tools~/profile_text_batches.py --project ... --model ... --output ...`을 실행합니다.

Windows의 일시적 파일 잠금 때문에 범위 설정 교체가 실패한 문제를 재현했고, 원자적 교체를 유지한 최대 200ms 재시도를 추가했습니다. 실제 파일을 70ms 잠그는 Unity 회귀 검사가 2022.3·6.3·6.7에서 통과했습니다. 고급 필터는 실제 모델의 이미지 결과 두 개에서 하한선으로 하나/없음, 경로·확장자 선별과 이름 정렬/결과 수를 검사했습니다.

## 아직 검증하지 않은 것

대규모 실제 에셋셋의 Recall@K/nDCG, 장시간 메모리 안정성, 수만 개·수십만 개 자료, 모든 오디오 코덱, 전체 프로젝트의 CPU 처리량, VRAM 고갈, 비 Windows OS, 밝은 테마 실제 화면, 검증 표에 없는 Unity 버전은 확인하지 않았습니다. CPU 오디오 소표본 비교는 [QUALITY](QUALITY.md)에 별도로 있습니다. 이 결과는 일반 임베딩 리더보드나 다른 모델과의 공정한 정확도 비교를 대신하지 않습니다.
