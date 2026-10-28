# v0.2.2 GitHub 새 설치 검증

2026-10-28. 기존 테스트 프로젝트의 Assets·Packages·설정·Library·Python 환경·인덱스를 삭제하고 Unity CLI로 새 프로젝트를 만들었습니다. 빈 루트 폴더는 다른 프로세스가 잡고 있어 그대로 재사용했습니다. 기존 프로젝트 내용은 재사용하지 않았습니다.

공개 GitHub 태그를 Unity Package Manager의 `Client.Add`로 설치했습니다. 로컬 저장소의 `file:` 연결이나 소스 복사로 대신하지 않았습니다. `packages-lock.json`의 소스는 `git`, 해시는 `502a4c729ebce33862c8dc2e7536508235d01f4e`로 공개 `v0.2.2` 태그와 일치했습니다.

## 환경과 결과

Unity 6000.3.24f1, Windows 11, Python 3.13, NVIDIA CUDA 환경입니다. Unity를 추가 설치하지 않았습니다. 배포된 `BackendClient.SetupAsync`로 새 격리 환경을 만들고, 프로젝트 밖의 기존 EmbeddingGemma 2 모델을 사용했습니다. 이번에는 모델 다운로드를 반복하지 않았습니다. 모델 없는 새 대상 다운로드 검사는 [기존 기록](BENCHMARKS.md#다운로드초기-설치에이전트)에 있습니다.

| 검사 | 결과 |
|---|---|
| 공개 저장소·README·GIF 접근 | 인증 없이 접근하고 공개 파일 내용 일치 확인 |
| Git URL 설치·C# 컴파일 | 통과, `GITHUB_UPM_INSTALL_PASS` |
| 새 Python 환경 준비 | 통과, `FRESH_ENVIRONMENT_PASS` |
| UPM Quick Start import | 6개 샘플 가져오기 성공 |
| 실제 모델 색인 | 6/6 성공 |
| 변경 없는 재색인 | 0개 갱신, 6개 건너뛰기 |
| 텍스트 검색 | `a sharp metal sword` → Sword.txt 1위 |
| 이미지 검색 | `a red square` → RedSquare.png 1위 |
| 오디오 검색 | `an electronic tone` → 톤 샘플 반환, 유한한 점수 확인 |
| 유사도 하한선 | 이미지 결과 2개→1개→0개 확인 |
| 경로·확장자·이름 정렬·결과 수 | 통과 |
| MCP stdio | 초기화·도구 목록·상태·세 종류 검색·필터 통과 |
| 한국어/영어 UI 구성·UTF-8 로그 | Unity에서 UXML/USS와 바인딩, 한글 왕복 검사 통과 |
| 다운로드한 패키지의 회귀 검사 | Backend 19개 + Agent 2개 통과 |
| 패키지 검사 | 101개 로케일 키·인자·UXML 참조·샘플·UPM 의존성 검사 통과 |

최종 샘플 실행 마커는 `GITHUB_SAMPLE_VALIDATION_PASS`입니다. [구조화된 결과](benchmarks/release-0.2.2-clean-install.json)에 커밋·진입점·검색 결과와 제한을 남겼습니다. 검증용으로 임시 추가한 Editor 스크립트는 종료 후 제거하고, 새 프로젝트에는 설치한 패키지·샘플·환경·검색 인덱스를 남깁니다.

임시 Editor 스크립트를 제거한 뒤 `unity run <project> --timeout 180 -- -logFile <log>`를 실행했고 종료 코드 0과 `Exiting batchmode successfully`를 확인했습니다. C# 컴파일 오류는 없었습니다. Unity 종료 로그에는 `debugger-agent: Unable to listen on 4700`과 일부 `abort_threads` 경고가 남았습니다. 이 엔진 종료 경고를 해결했다고 주장하지 않습니다.

## 검증 명령과 재현

설치된 에디터 버전을 지정하고 별도 프로젝트에서 진행합니다.

```powershell
unity projects create UnityGemma --path '<parent>' --editor-version 6000.3.24f1 --template com.unity.template.3d --bare --no-cloud --non-interactive --format json
```

UPM 설치는 비동기 `Client.Add("https://github.com/Legerdo/unity-local-asset-search.git#v0.2.2")`와 `EditorApplication.update` 완료 처리를 사용했습니다. `unity open --batchmode --args '-executeMethod GitHubInstall.Run ...'`로 실행하고 성공/실패 시 검증 스크립트가 Editor를 종료했습니다. `unity run`의 자동 `-quit`로 비동기 설치를 중간에 끝내지 않았습니다.

배포 샘플은 `Sample.Import`로 가져온 뒤 제품의 `AssetCollector`와 `BackendClient` API를 호출했습니다. 이 임시 검사 도구의 `ReleaseSampleCheck.Setup`과 `ReleaseSampleCheck.Run`도 같은 batch-mode 진입 방식을 사용했습니다. 패키지에 포함된 재사용 통합 도구는 [BENCHMARKS 재현](BENCHMARKS.md#재현)을 따릅니다.

새 프로젝트의 격리 Python으로 GitHub에서 받은 패키지 경로에 대해 실행한 최종 게이트입니다.

```powershell
& '<new-project>/Library/LocalAssetSearch/env/Scripts/python.exe' -m unittest discover -s '<resolved-package>/Backend~' -p 'test_*.py' -v
& '<new-project>/Library/LocalAssetSearch/env/Scripts/python.exe' -m unittest discover -s '<resolved-package>/Agent~' -p 'test_*.py' -v
& '<new-project>/Library/LocalAssetSearch/env/Scripts/python.exe' '<resolved-package>/Backend~/validate_package.py'
```

## 확인 범위

이번 새 설치에서 제품 오류는 검출하지 못했습니다. 일회성 검사 스크립트의 `PackageInfo` 네임스페이스 충돌은 완전한 타입명으로 고친 뒤 실행했습니다. 제품 코드는 변경하지 않았습니다.

UI는 Unity batch mode에서 구성·바인딩을 검사했습니다. 새 인터랙티브 화면의 시각적 검수나 모든 클릭 동작을 재시험한 것은 아닙니다. 오디오 톤의 정상 반환은 효과음 의미 검색 정확도를 뜻하지 않습니다. Unity 7, 실제 대규모 코퍼스, 장시간 사용과 모든 설치 환경까지 무결함을 보장하지 않습니다.

## English summary

The old test project's contents were removed and a new Unity 6000.3.24f1 project was created with the CLI. UPM installed the public Git tag; the lock-file hash matched the published commit. A new Python environment was prepared through the product API while existing external model weights were reused. All six imported samples indexed successfully, unchanged indexing skipped all six, search/filter/MCP/UTF-8 checks passed, and 21 regression tests plus the 101-key package validator passed against the downloaded package. UI checks covered construction and bindings in batch mode, not new visual acceptance. See the JSON for evidence and exclusions.
