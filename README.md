# Local Asset Search for Unity

[English](README.en.md) · [벤치마크](Documentation~/BENCHMARKS.md) · [문제 해결](Documentation~/TROUBLESHOOTING.md) · [Todo](Documentation~/TODO.md)

Unity Editor에서 텍스트·이미지·오디오를 문장으로 검색하는 UPM 패키지입니다. 필요한 폴더만 색인하고 결과에서 원본 에셋을 선택하거나 경로를 복사할 수 있습니다. 한국어 UI가 기본이며 영어로 전환할 수 있습니다.

EmbeddingGemma 2는 별도 로컬 Python 프로세스에서 실행합니다. Sentis/Inference Engine이나 렌더 파이프라인에 의존하지 않으며 플레이어 빌드에 모델을 포함하지 않습니다.

![Unity에서 종류 필터와 유사도 하한선으로 결과 좁히기](Documentation~/media/quick-start.gif)

실제 Unity 화면을 단계별로 캡처한 GIF입니다. 0.2.0에서 촬영했으며 재생 시간은 처리 속도를 나타내지 않습니다.

## 주요 기능

| 작업 | 사용 방법 |
|---|---|
| 코드·문서 찾기 | `player movement code`, `metal sword and shield`처럼 내용을 설명합니다. |
| 이미지 찾기 | `a red square`, `a gray document page icon`처럼 모양과 색을 설명합니다. |
| 오디오 후보 찾기 | 소리 설명으로 검색한 뒤 Inspector에서 들어봅니다. 효과음 내용 구분에는 한계가 있습니다. |
| 범위 줄이기 | 포함 폴더·제외 경로, 자료 종류, 확장자, 경로 문자열을 지정합니다. |
| 결과 정리 | 최소 유사도, 결과 수 10/20/50/100, 유사도·이름·경로 정렬을 선택합니다. |
| 에셋 사용 | 원본 선택·열기, 경로·GUID 복사, 미리보기 아이콘 드래그를 지원합니다. |
| 에이전트 연결 | MCP·CLI·공개 C# Editor API로 UI 조작 없이 호출합니다. |

0.2.2는 이미지·오디오의 내용에 파일명과 가까운 두 폴더 이름을 함께 넣어 임베딩합니다. 잘못된 이름은 결과에도 영향을 줍니다. 파일명 기반 참조 검색의 개선을 소리 인식 향상으로 해석하지 않도록 [내용 기준 검증](Documentation~/QUALITY.md)을 별도로 정리했습니다.

## 설치

Windows에서 Python 3.11~3.13을 준비합니다. NVIDIA CUDA와 CPU를 지원합니다. UPM 최소 선언은 Unity 2021.3이고, 해당 버전의 실행은 미검증입니다. 실제 확인 범위는 [호환성 표](Documentation~/COMPATIBILITY.md)에 있습니다.

Unity의 `Window > Package Manager > + > Install package from git URL`에 다음 주소를 넣습니다. 버전에 따라 메뉴 이름이 `Add package from git URL`일 수 있습니다.

```text
https://github.com/Legerdo/unity-local-asset-search.git#v0.2.2
```

Git 설치가 어려우면 [릴리스](https://github.com/Legerdo/unity-local-asset-search/releases/tag/v0.2.2)의 `.tgz`를 내려받아 tarball 설치 메뉴를 사용합니다. 저장소를 직접 받은 경우 package-from-disk 메뉴에서 `package.json`을 선택합니다.

### 처음 검색하기

1. `Tools > Local Asset Search > 설정 · 색인`을 엽니다.
2. `권장 설정으로 Python 환경 준비`를 누릅니다. 프로젝트 Library에 격리 환경을 만들고 NVIDIA GPU가 있으면 CUDA 환경을 선택합니다. 전역 Python 패키지는 변경하지 않습니다.
3. 기존 Hugging Face 원본 EmbeddingGemma 2 모델 폴더를 지정합니다. 모델이 없으면 빈 다운로드 대상 폴더를 지정하고 다운로드합니다. 환경 준비가 먼저 끝나야 합니다. 고정 리비전의 필수 파일만 받으며 ONNX/GGUF 변환은 필요 없습니다.
4. `모델 불러오기 · 연결 확인`을 누릅니다. `샘플로 먼저 체험`을 선택하거나 `폴더 추가`로 Assets 범위를 지정합니다.
5. `자료 수 확인 → 선택한 자료 색인 · 갱신`을 실행합니다. 전체 프로젝트를 자동 색인하지 않으며 이후 갱신에서는 바뀌지 않은 파일을 건너뜁니다.
6. 검색 탭에서 `a red square`를 입력하고 이미지 종류를 선택합니다. 결과의 `선택`이나 `열기`로 원본을 확인합니다.

검색 입력과 필터는 고정되고 결과 목록만 스크롤합니다. 필터를 바꾼 뒤에는 검색을 다시 누르세요. 유사도는 -1~1 범위의 코사인 점수이며 확률이 아닙니다. 낮은 점수를 숨기는 기준은 자료 종류와 검색 목적에 맞춰 조정합니다.

### 이전 버전에서 갱신하기

0.2.2의 미디어 전처리는 이전 벡터와 호환되지 않습니다. 백엔드를 중지하고 새 버전으로 연결한 뒤 설정 탭에서 생성 인덱스를 초기화하고 다시 색인합니다. 원본 에셋과 모델 파일은 초기화 대상이 아닙니다.

## 측정 결과

2026-10-28, Windows 11, RTX 5080 16 GB, RAM 32 GB, 768차원 CUDA BF16 측정입니다. 모델이 로드된 상태이며 색인 시간에서 Unity import·미리보기 준비·프로세스 시작을 제외했습니다. 검색 시간에는 HTTP 왕복을 포함하고 UI 표시는 제외합니다.

| 데이터와 경로 | 색인 | 새 검색어 p50 / p95 | 해석 |
|---|---:|---:|---|
| 직접 만든 134개, Unity 통합 경로, 0.2.0 | 2.616초 | 42.37 / 49.93ms | 문서 130개·도형 2개·톤 2개로 기능과 지연 측정 |
| 실제 리소스 192개, 제품 HTTP/Engine, 0.2.2 | 14.56초 | 46.33 / 58.85ms | 텍스트·이미지·오디오 각 64개, 426벡터 |

데이터와 버전이 달라 두 행을 개선율 비교에 사용할 수 없습니다. 실제 1천·5천·1만개 전체를 임베딩한 속도는 아직 측정하지 않았습니다. [측정 조건·원본·재현](Documentation~/BENCHMARKS.md), [검색 품질과 오디오 모델 비교](Documentation~/QUALITY.md), [메모리 점검](Documentation~/MEMORY.md)을 확인하세요.

파일명을 가린 새 이미지 표본에서는 내용 기준 질의 9개 모두 관련 이미지가 1위였습니다. 오디오는 같은 수준의 독립 청취 정답이 없어 정확도를 확정하지 못했습니다. 효과음 전용 CLAP Q8은 소규모 자동 참조 비교에서 더 낮은 CPU 비용을 보였지만 제품에는 통합하지 않았습니다. 현재 모델은 EmbeddingGemma 2를 유지합니다.

## AI 에이전트에서 호출

`Agent~/mcp_server.py --project <Unity project>`를 MCP stdio 서버로 등록합니다. Unity에서 모델 연결과 색인을 준비하면 다음 도구를 호출할 수 있습니다.

```text
unity_asset_status({})
unity_asset_search({"query":"a red square", "kind":"image", "top":5})
```

CLI는 Python 표준 라이브러리만 사용합니다.

```powershell
python "<package>/Agent~/search.py" --project "<project>" --query "a metal sword" --kind text --top 10
```

[MCP 설정·필터·C# API](Agent~/README.md)와 [선택적 에이전트 스킬](Agent~/skills/unity-asset-search/SKILL.md)을 제공합니다. 에이전트 자격 증명은 검색·상태 조회만 허용하고 서버가 에디터의 포함/제외 범위를 적용합니다. 색인과 설정 변경은 에디터 또는 Unity main thread의 공개 C# API를 사용합니다.

## 저장 위치와 제한

| 항목 | 위치 |
|---|---|
| 프로젝트 설정 | `UserSettings/LocalAssetSearch.asset` |
| 격리 환경·인덱스·미리보기·진단·에이전트 접속 정보 | `Library/LocalAssetSearch/` |
| 모델 | 사용자가 지정한 폴더 |

생성 데이터와 접속 정보는 Git에 넣지 마세요. 에셋 내용은 설치·추론 과정에서 외부로 전송하지 않으며 네트워크는 명시적 환경 설치와 모델 다운로드에 사용합니다.

선택 범위는 기본 1,000개, 상한 10,000개입니다. 범위를 먼저 좁힌 뒤 정확한 코사인 순위를 계산하며 ANN 인덱스는 없습니다. 비디오, AnimationClip 내용 분석, 프리팹·3D 렌더링, 자동 변경 감시, macOS MPS는 지원하지 않습니다. 비 Windows 실행, 밝은 테마 실제 화면, GPU 메모리 고갈 복구, 장시간 대규모 사용은 미검증입니다.

## 문서와 개발

| 문서 | 내용 |
|---|---|
| [문제 해결](Documentation~/TROUBLESHOOTING.md) | 설치·다운로드·색인·검색·로그·MCP 문제 |
| [벤치마크](Documentation~/BENCHMARKS.md) | 장비, 웜 상태, 캐시, 재현 명령 |
| [검색 품질](Documentation~/QUALITY.md) | 파일명 제거 실험과 오디오 비교의 한계 |
| [호환성](Documentation~/COMPATIBILITY.md) | Unity 버전별 확인 범위 |
| [기술 계약](Documentation~/README.md) | 형식, 청크, HTTP, 인덱스 수명 |
| [Todo](Documentation~/TODO.md) | 완료 범위와 남은 과제 |
| [기여 안내](CONTRIBUTING.md) | 테스트 실행과 버그 제보 |
| [변경 이력](CHANGELOG.md) | 버전별 변경 |

## 라이선스와 참고

패키지 코드와 직접 만든 Quick Start 샘플은 [MIT](LICENSE.md)입니다. 모델 가중치는 포함하지 않으며 [google/embeddinggemma-2](https://huggingface.co/google/embeddinggemma-2)의 별도 이용 조건을 따릅니다. 고정 리비전은 `914f7f89142e33e77833254d9c9b90c3cef7303b`입니다. 설치되는 Python 라이브러리에도 각 라이선스가 적용됩니다.

[ayutaz/unity-embeddinggemma-2](https://github.com/ayutaz/unity-embeddinggemma-2)를 접근 방식의 참고 자료로 사용했습니다. 이 패키지는 별도 구현이며 해당 프로젝트의 검증 결과를 승계하지 않습니다. 한국어 문서는 [ai-slop-thresher](https://github.com/Burntgogi/ai-slop-thresher)의 윤문 지침을 적용해 다듬었습니다.
