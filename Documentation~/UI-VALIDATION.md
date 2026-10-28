# CLI UI 이벤트 검사와 종료 경고

2026-10-28. Unity 6000.3.24f1의 GitHub 설치본 0.2.2에서 UI Toolkit 이벤트 경로를 검사했습니다. 검사 동작에는 Computer Use, 좌표 클릭, 백엔드 검색·색인·연결 API 직접 호출을 사용하지 않았습니다.

## UI 경로: 16단계 통과

`Tools~/UiFlowCheck.cs`는 실제 EditorWindow의 버튼에 `NavigationSubmitEvent`를 보내고 입력 필드·드롭다운·토글의 값을 변경해 `ChangeEvent` 콜백을 실행합니다. 비활성/숨겨진 버튼을 거부하며, 화면의 상태 문구·결과 행 수·첫 결과 이름을 검사합니다. 비공개 메서드를 reflection으로 호출하거나 `Button.clicked`를 직접 실행하지 않습니다.

| 확인한 UI 동작 | 실제 결과 |
|---|---|
| 모델 연결 | CUDA 연결 완료 |
| 샘플 버튼·자료 수 확인 | 샘플 범위 유지, 6개 확인 |
| 생성 인덱스 초기화·색인 | 6개 갱신 |
| 다시 색인 | 갱신 0개, 변경 없는 6개 건너뜀 |
| 이미지 검색 | RedSquare가 첫 결과, 2개 표시 |
| 최소 유사도 0.8 / 0.95 | 1개 / 0개 표시 |
| PNG + BlueCircle 경로 필터 | BlueCircle 1개 표시 |
| 필터 초기화·이름 정렬 | 초기화 값 확인, BlueCircle부터 2개 표시 |
| 텍스트·오디오 검색 | Sword / LowTone이 첫 결과, 각각 2개 표시 |
| 영어 전환 후 검색 | 영어 완료 문구와 결과 2개 표시 |
| 중지·재연결 | 버튼 상태 전환 확인, 재연결 성공 |

최종 마커는 `UI_EVENT_FLOW_PASS`이며 [16단계 JSON](benchmarks/ui-events-0.2.2.json)에 각 단계의 표시 문구가 있습니다. 첫 검사에서는 탭 전환 직후 레이아웃 갱신을 기다리지 않아 숨김 상태 검사가 멈췄습니다. 대기를 추가하고 같은 검사를 유지한 최종 실행이 통과했습니다. 제품 코드 수정은 없었습니다.

이 검사는 키보드 제출과 같은 버튼 이벤트 경로를 검증합니다. 실제 마우스의 hit testing, 화면 잘림·가림, 네이티브 폴더 선택창, 렌더링의 시각적 품질까지 검증한 것은 아닙니다. 초기에 연 Editor에는 Pipeline 제어 채널이 없어 CLI `--force`로 닫고 검사용 프로세스를 새로 실행했습니다. 그 강제 종료는 아래 정상 종료 비교에 포함하지 않습니다.

## 재현

별도 샘플 전용 프로젝트를 사용하세요. 이 검사는 생성 인덱스를 비웁니다. 검색 범위가 0.2.2 Quick Start 한 폴더인지 검사하며 원본 에셋·모델 파일을 삭제하지 않습니다. 먼저 환경·모델을 설정하고 샘플을 가져옵니다.

`main` 저장소의 `Tools~/UiFlowCheck.cs`를 검증 프로젝트의 `Assets/Editor/`에 복사합니다. 이 도구는 0.2.2 공개 후 추가되었으므로 기존 `v0.2.2` 태그의 tarball에는 없습니다. 검사 대상 제품은 Git URL로 설치한 0.2.2 그대로입니다. Editor를 닫은 상태에서 실행합니다.

```powershell
unity open '<validation project>' --batchmode --args '-executeMethod UiFlowCheck.Run -logFile <absolute-log-path>' --format json
```

`unity open`의 성공은 시작 성공일 뿐입니다. `Logs/ui-event-flow.json`의 `passed: true`와 로그의 `UI_EVENT_FLOW_PASS`를 확인해야 합니다. 검사 메서드는 비동기이며 완료 후 `EditorApplication.Exit`를 호출하므로 자동 `-quit`를 넣는 `unity run`으로 이 메서드를 실행하지 마세요. 검사가 끝나면 프로젝트에 복사한 검사 코드와 `.meta`를 제거합니다. 추가 MCP 패키지나 전역 에이전트 설정은 필요하지 않습니다.

## 종료 경고 대조

같은 Unity 6000.3.24f1, 같은 3D 템플릿으로 패키지가 없는 프로젝트를 만들었습니다. 두 manifest의 차이는 `com.local.embedding-search` 하나뿐이었습니다. 이 대조군을 `unity run ... --timeout 180 -- -logFile ...`로 실행한 결과 종료 코드 0에서도 같은 경고가 발생했습니다.

| 조건 | `abort_threads` 경고 | 디버거 소켓 식별자 | 설정된 디버거 주소 |
|---|---:|---:|---|
| 패키지 없는 대조군 | 4회 | 4692 | 127.0.0.1:56020 |
| 패키지 UI 검사·재연결 후 종료 | 4회 | 4620 | 결과 JSON의 실제 기록 참조 |

[대조 결과 JSON](benchmarks/shutdown-baseline.json). UI 검사 종료 후 프로젝트의 검색 백엔드 Python 프로세스는 0개였습니다. 재연결 상태에서 종료한 결과이며, 경고를 피하려고 백엔드를 먼저 없애거나 로그를 필터링하지 않았습니다.

### 이전 기록의 정정

`debugger-agent: Unable to listen on 4700`의 **4700은 TCP 포트가 아닙니다**. Unity의 공개 Mono 소스에서 이 메시지는 `socket_transport_accept`의 `accept` 실패 시 `socket_fd`를 출력합니다. 앞선 문서와 릴리스의 “디버거 포트” 표현을 정정합니다. 실제 설정된 디버거 주소는 시작 로그의 `Using monoOptions ... address=`에 따로 있습니다. [Unity Mono debugger-agent.c](https://github.com/Unity-Technologies/mono/blob/0b594224e67d7832012ffe7c458cd0e0bb239894/mono/mini/debugger-agent.c).

`abort_threads` 메시지는 Mono가 관리 스레드에 중단을 요청하고 실패했을 때 해당 스레드를 종료 대기 대상에서 제외하는 경로에서 출력합니다. [Unity Mono threads.c](https://github.com/Unity-Technologies/mono/blob/0b594224e67d7832012ffe7c458cd0e0bb239894/mono/metadata/threads.c).

현재 증거는 Unity/Mono 종료 경로에서 패키지 없이도 재현되는 경고라는 결론을 지지합니다. 정확한 엔진 스레드의 소유자와 중단 실패 이유는 네이티브 디버깅으로 특정하지 않았습니다. 공개 Mono 소스가 설치 바이너리의 정확한 빌드 리비전이라고 확인한 것도 아닙니다. 이를 패키지 메모리 누수나 모든 상황에서 무해하다는 증거로 확대하지 않습니다. 디버거 설정을 끄거나 경고를 숨기는 변경은 하지 않았습니다.

## English summary

Sixteen UI event steps passed against the GitHub-installed 0.2.2 package. The harness dispatches UI Toolkit submit/change events and checks rendered UI data, rather than invoking backend operations directly. Pointer hit testing and visual acceptance are outside scope. An otherwise matching project without the package reproduced both shutdown warnings and exited with code 0. The number in the debugger warning is a socket identifier, not a TCP port. No search backend process remained after the UI test Editor exited; exact native thread ownership remains unresolved.
