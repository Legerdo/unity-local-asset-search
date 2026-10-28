# Local Asset Embedding Search

Unity Editor 전용 텍스트·이미지·오디오 검색 패키지입니다. 추론은 별도 Python 프로세스에서 수행하며 Sentis, Unity Inference Engine, 특정 렌더 파이프라인에 의존하지 않습니다. **최소 선언 버전은 Unity 2021.3입니다. 모든 Unity 버전에서의 호환성을 보장한다는 의미는 아닙니다.** 실제 검증 범위는 프로젝트의 검증 보고서를 확인하세요.

## 시작

1. `Tools > Local Asset Search`를 엽니다.
2. `Python executable`에 Python 3.11~3.13 실행 파일을 지정합니다. `Set up isolated Python environment`로 별도 환경을 만들면 의존성을 전역 환경에 설치하지 않습니다. `Device = CUDA`를 먼저 선택하면 CUDA 13.0용 PyTorch를 설치합니다. CPU도 지원하지만 이미지·오디오 색인이 느릴 수 있습니다. macOS의 전용 가속은 현재 구현하지 않았습니다.
3. `Installed model folder`에서 Hugging Face 원본 EmbeddingGemma 2 체크포인트 폴더를 선택합니다. Sentis/ONNX/GGUF 변환 모델은 이 백엔드의 입력 형식이 아닙니다.
4. 모델 가중치가 없을 때만 나타나는 `Download destination`을 지정하고 다운로드 버튼을 누릅니다. 공식 `google/embeddinggemma-2`의 고정 revision만 다운로드하며 모델 Python 코드를 실행하지 않습니다. 모델 동의나 인증이 필요한 환경에서는 별도로 Hugging Face 표준 로그인을 완료하세요. 토큰을 Unity 설정에 입력하거나 저장하지 않습니다.
5. Project 창에서 원하는 폴더 또는 개별 에셋을 선택하고 `Include Project selection`을 누릅니다. 제외 항목은 `Exclude Project selection`으로 추가합니다. 기본 범위는 비어 있습니다. 자동 전체 스캔/색인은 없습니다.
6. 타입 체크박스와 최대 에셋 수를 지정하고 `Preview scope`로 확인한 뒤 `Index selected scope`를 누릅니다. 범위가 최대 수를 넘으면 일부만 몰래 처리하지 않고 중단합니다.
7. 검색어를 입력하고 타입 필터를 선택합니다. 경로 클릭은 원본 에셋을 Project 창에서 선택하고 `Open`은 Unity의 기본 에셋 열기를 사용합니다. AudioClip의 청취는 Unity Inspector의 오디오 미리보기를 사용합니다.

다른 프로젝트에는 Package Manager의 **Add package from disk**에서 이 폴더의 `package.json`을 지정하거나 `Packages/com.local.embedding-search` 폴더를 복사합니다. Python 환경과 모델 경로는 각 프로젝트에서 별도로 지정합니다. 플레이어에 들어가는 런타임 코드가 없습니다.

## 저장 위치와 수명

- 사용자 설정: `UserSettings/LocalAssetSearch.asset` (Python/모델 경로, 선택 범위, 제외 범위). 소스 관리에서 제외하는 것을 권합니다.
- 생성 데이터: `Library/LocalAssetSearch/index.sqlite3`, `Previews/`, 선택한 Python 환경. SQLite/미리보기는 재생성 가능한 캐시입니다.
- 모델: 사용자가 지정한 외부 폴더. 기존 원본이나 importer 설정은 수정하지 않습니다.
- Editor 재컴파일/종료 시 이 패키지가 실행한 백엔드를 종료합니다. 창을 다시 열고 Start/Index/Search를 누르면 다시 시작합니다. 취소는 현재 추론이 완료된 후 반영됩니다.
- 선택을 해제한 에셋의 기존 벡터는 보관하지만 검색 범위에서 필터링합니다. 삭제된 원본은 검색 결과에서 제외합니다. `Clear generated index`로 생성된 벡터를 모두 지우고 다시 색인할 수 있습니다.

## 실제 입력 범위

- Text: UTF-8 `.txt .md .json .csv .xml .yaml .yml .cs .shader .compute .uss .uxml .lua`, 파일당 최대 2 MB. 1800자/200자 겹침 청크로 마지막 부분까지 색인합니다. 제목에는 파일명을 사용합니다.
- Image: Unity에서 로드할 수 있는 `Texture2D`의 주 에셋. 최대 1024px PNG 미리보기로 내보냅니다. Read/Write 옵션을 바꾸지 않습니다. 스프라이트 시트는 개별 스프라이트가 아니라 텍스처 단위입니다. 알파 전용 정보와 HDR 값은 이 RGB 미리보기에 보존되지 않습니다.
- Audio: `.wav .ogg .flac .mp3 .aif .aiff`, libsndfile에서 읽을 수 있는 형식. 16kHz mono 변환 후 20초 구간/2초 겹침으로 색인합니다. 최대 300초, 파일당 최대 256 MB. 검색 결과는 가장 관련 있는 구간의 초 단위를 반환합니다. 파일이 길면 원본을 별도 클립으로 나누세요. 플랫폼 전용 codec 전체를 보장하지 않습니다.
- 이번 버전은 영상, AnimationClip, 3D 모델, 프리팹, 머티리얼의 자동 렌더링/분석을 포함하지 않습니다.

지원 버전/런타임을 고정한 `Backend~/requirements.txt`와 `setup.py`를 제공합니다. BF16 지원 CUDA에서는 BF16, 다른 환경에서는 FP32를 사용하고 FP16은 사용하지 않습니다. 첫 추론 시 모델 가중치·설정의 SHA-256을 계산합니다. 모델/차원/전처리/주요 라이브러리가 바뀐 인덱스는 혼용하지 않고 재색인을 요구합니다. 가중치가 바뀌었다면 먼저 백엔드를 중지한 뒤 변경하고 다시 시작하세요.

## AI 에이전트 연결

Unity main thread에서 Editor API를 호출할 수 있습니다. 별도의 런타임 컴포넌트를 만들 필요가 없습니다.

```csharp
var result = await LocalAssetSearch.BackendClient.SearchAsync(
    "a sharp metal sword", kind: "image", top: 10);
foreach (var hit in result.results)
    UnityEngine.Debug.Log($"{hit.guid} | {hit.path} | {hit.score}");
```

반환값: Unity GUID, 경로, 타입, 점수, 오디오 구간 시작 시간. 에이전트는 이 경로/GUID로 AssetDatabase를 조회하고 Inspector/preview를 열 수 있습니다. 범위는 Editor 창에서 저장한 include/exclude 설정을 따릅니다. 이번 버전은 MCP 서버를 별도로 등록하지 않습니다. `all`은 모달리티 사이의 점수가 보정되지 않은 혼합 순위이므로 원하는 타입이 있으면 필터를 사용하세요. 임베딩 점수는 확률이 아닙니다.

## 백엔드 프로토콜과 경계

Editor가 `server.py`를 직접 실행합니다. 서버는 임의 포트의 `127.0.0.1`에만 바인딩합니다. 세션 토큰은 환경 변수로만 전달하고 CLI 인수/로그/ready.json에 넣지 않습니다. Origin이 있는 요청과 잘못된 Host를 거부하며 모든 API는 Bearer 토큰을 검사합니다. 이는 같은 사용자 권한으로 실행되는 다른 프로세스로부터의 OS 격리를 제공하지는 않습니다.

모든 엔드포인트는 JSON POST이며 `Authorization: Bearer <session token>`이 필요합니다. `ready.json`에는 port와 PID만 있습니다.

- `/status`: 색인 작업 상태, 에셋 수, 장치와 차원.
- `/index`: `{items:[{guid,path,kind,source}]}`. path는 해당 프로젝트의 Assets 파일, source는 Assets 또는 생성된 Previews 내부여야 합니다. 명시된 항목만 처리합니다. 항목별 트랜잭션이며 성공한 벡터만 저장합니다.
- `/cancel`: 현재 작업 취소 요청.
- `/search`: `{query,kind,top,code,roots,excluded}`. kind는 all/text/image/audio. 검색어만 새로 임베딩하며 기존 에셋 벡터는 재사용합니다.
- `/clear`: 생성된 인덱스 초기화.

실제 추론은 `local_files_only=True`, `trust_remote_code=False`, Hugging Face offline 설정을 사용합니다. 외부 통신은 명시적으로 누른 의존성 설치와 모델 다운로드에서만 수행합니다. 결과 로그에 에셋 내용은 넣지 않지만 에셋 경로와 에러가 포함될 수 있습니다.

## 참고

- 아키텍처 비교 참고: https://github.com/ayutaz/unity-embeddinggemma-2 (Sentis 기반). 이 패키지는 해당 SDK의 코드를 복사하지 않은 별도 구현이며, 그 SDK의 검증 결과를 승계하지 않습니다.
- 모델: https://huggingface.co/google/embeddinggemma-2
- 모델 라이선스는 해당 모델 카드와 원본 배포 조건을 확인하세요. 이 패키지에는 가중치를 포함하지 않습니다.
