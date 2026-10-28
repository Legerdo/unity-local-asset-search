# 메모리·프로세스 수명 점검 — 0.2.1

2026-10-28, Windows 11 / RTX 5080 16GB / 시스템 RAM 32GB. **실제 누적 문제를 재현해 수정했으며, 아래 검증 범위에서 재발하지 않았습니다.** 모든 사용 환경에서 무누수를 보장하는 결과는 아닙니다. 0.2.0보다 0.2.1을 사용하세요.

## 발견한 문제와 수정

- 모델 초기화가 SentenceTransformers 모델 카드의 역참조를 남겨 GPU 가중치가 회수되지 않았습니다. 역참조를 해제하고 GC와 CUDA 캐시 반환을 실행합니다. GC만 호출하는 수정은 실제 모델 검증에서 실패해 채택하지 않았습니다.
- 요청별 임시 스레드에서 모델을 다시 로드/추론하면 네이티브 스레드·핸들이 증가했습니다. 로딩만 한 스레드에 두는 중간 수정도 실패했습니다. **로딩과 추론 전체**를 재사용하는 작업 스레드 하나에서 실행해 증가를 막았습니다. 기존에도 모델 접근은 잠금으로 직렬화했으며 검색 조건·배치·API 계약은 유지했습니다.
- OOM 축소 재시도와 배치 실패 후 개별 항목 처리에서 예외 traceback이 실패한 추론 버퍼를 붙잡았습니다. 예외 블록을 벗어나 참조를 정리한 뒤 재시도합니다.
- 중지 후 Windows venv 실행기의 자식 프로세스가 남는 경우를 재현했습니다. 시작 단계부터 입력 파이프 종료를 감시하고, 네이티브 소멸자를 기다리지 않고 소유 백엔드를 종료합니다. Python/CRT 표준입력을 직접 읽으면 콜드 시작 시 잠금 문제가 생겨 `PeekNamedPipe` 방식으로 검증했습니다. 다른 프로세스를 이름으로 검색해 종료하지 않습니다.
- `/status`에 실사용/예약 GPU 메모리와 검색어 캐시 항목 수를 기록합니다. 모델이 내려간 뒤에도 잔여 할당을 숨기지 않습니다.

## 실제 모델 반복 검증

모델은 `google/embeddinggemma-2`, 리비전 `914f7f89142e33e77833254d9c9b90c3cef7303b`, BF16/CUDA, 768차원입니다. Torch 2.14.1+cu130 / SentenceTransformers 6.2.0 / Transformers 5.21.0 / Python 3.13 격리 환경을 사용했습니다. psutil 7.2.2는 감사 도구에서만 사용하며 제품 의존성을 추가하지 않았습니다.

별도 프로젝트의 합성 자료 134개(짧은 문서 130, 이미지 2, 2초 톤 2)를 실제 HTTP→모델→SQLite 경로로 처리했습니다. 검색어 길이와 종류를 바꿔 96회 워밍업 후 서로 다른 검색 768회, 변경 없는 색인 24회, 모델 재로딩 5회를 실행했습니다. 최종 실행 시간은 100.1초입니다. 마지막 검사 기준은 실행 전에 도구에 명시했으며 통과를 위해 완화하지 않았습니다.

| 항목 | 수정 전 0.2.0 | 수정 후 0.2.1 |
|---|---:|---:|
| 워밍업 후 Torch GPU 실사용 | 1464.30MiB | 1464.30MiB |
| 재로딩 3회 후 Torch GPU 실사용 | 5729.45MiB | 1464.30MiB |
| 재로딩 5회 후 Torch GPU 실사용 | 측정하지 않음 | 1464.30MiB |
| 초기화 후 모델 잔여 할당 | 계속 누적 | 매번 32MiB |
| 수정 후 재로딩 5회 스레드 / 핸들 | 해당 최종 수정 전 증가 재현 | 69 / 520 (누적 없음) |
| 중지 후 남은 자식 프로세스 | 1개 | 0개 |

768회 검색 구간에서 실사용 GPU 할당은 1464.30MiB로 일정했습니다. Private Bytes는 7176.07→7187.21MiB(+11.14MiB), Working Set은 2463.74→2475.17MiB였습니다. 검색어 캐시는 항상 64개 이내였습니다. 재로딩까지 완료한 Private Bytes는 7236.62MiB로, 짧은 관찰만으로 장기 추세를 보장할 수 없습니다.

CUDA 예약 캐시 2,840MiB와 실사용 1,464MiB는 서로 다른 지표입니다. 예약 캐시는 재사용을 위한 메모리이며 그 자체를 누수로 판정하지 않았습니다. 모델 초기화 뒤 남은 32MiB는 반복 로딩에 따라 증가하지 않았습니다. Private Bytes는 커밋 크기이며 물리 RAM 점유와 동일하지 않습니다. Working Set은 OS 페이징과 동시에 실행된 Unity 검증의 영향을 받습니다. 이 실행은 지연시간 비교용 벤치마크가 아닙니다.

시작 0.3초 뒤 입력 파이프를 닫는 실험도 3회 모두 강제 실행기 종료 없이 자식 프로세스 0개로 끝났습니다.

## Unity 실제 흐름

이미 설치된 **Unity 6000.7.0a6**에서 다음 검증을 통과했습니다. 추가 Unity 버전은 설치하지 않았습니다.

- 실제 검색 버튼 콜백과 모델 응답을 사용해 창 열기·검색·닫기 30회: 닫힌 창과 update 구독이 모두 0개였습니다. 워밍업 후 관리 힙 증가는 0.676MiB, Unity 전체 네이티브 할당 증가는 0.525MiB였습니다. 테스트에서 GC를 호출한 수치입니다.
- 이미지 미리보기 100회 강제 재생성: Texture2D 730→730, RenderTexture 1→1, 활성 RenderTexture 복원 확인. Unity가 소유한 원본 에셋은 파괴하지 않았습니다.
- 백엔드 중지·재시작 3회, 실제 모델 로딩 도중 중지, 실제 신규 색인 도중 중지, 스크립트 도메인 재로딩: 이전 PID와 생성 시각으로 식별한 소유 프로세스가 종료됐습니다.
- 색인 중단 뒤 다시 열고 134개 정상 색인 완료: 원본 파일은 유지되고 SQLite 복구가 완료됐습니다.

## 자동 검사와 재현

백엔드 18개 + 에이전트 2개, 총 **20개 테스트 통과**. 모델 카드/참조 회수, 공통 작업 스레드, OOM 버퍼 수명, 실패 배치 버퍼 수명 회귀 검사를 포함합니다. OOM은 테스트용 예외로 검증했으며 실제 GPU 메모리를 고갈시키지 않았습니다. 101개 한국어/영어 로케일 키·서식·UXML 검증과 `git diff --check`도 통과했습니다.

```powershell
# 저장소 루트, 설치된 전용 환경 Python 사용
& $taskPython -m unittest discover -s 'Backend~' -p 'test_*.py' -v
& $taskPython -m unittest discover -s 'Agent~' -p 'test_*.py' -v
& $taskPython 'Backend~/validate_package.py'
git diff --check

# 별도 프로젝트에서 AssetSearchValidation으로 134개 샘플을 먼저 준비
& $taskPython 'Tools~/memory_stress.py' --project $validationProject --model $modelDirectory --rounds 8 --queries-per-round 96 --reloads 5 --output $resultJson

# Tools~/MemoryValidation.cs를 해당 별도 프로젝트의 Assets/Editor에 복사
$env:UNITY_ASSET_SEARCH_PYTHON = $taskPython
$env:UNITY_ASSET_SEARCH_MODEL = $modelDirectory
unity open $validationProject --batchmode --args '-executeMethod MemoryValidation.Run -logFile <absolute-log-path>'
# async 검증이 완료 시 종료하므로 -quit을 추가하지 않습니다.
```

`memory_stress.py`는 psutil이 있는 감사용 Python에서 실행합니다. 제품 환경 설치에 필수 라이브러리를 추가하는 명령은 아닙니다. 반복 검증은 프로젝트의 정상 인덱스와 분리된 `Library/LocalAssetSearchMemoryAudit/`를 사용합니다. Unity 검증은 전용 프로젝트의 벤치마크 생성 데이터만 대상으로 하며 종료 시 사용자 설정을 복원합니다. 일반 프로젝트에서 강제로 실행하지 마세요.

원본: [수정 전](memory/before-0.2.0.json), [수정 후](memory/after-0.2.1.json), [Unity](memory/unity-6000.7-0.2.1.json), [시작 중 중지](memory/early-stop-0.2.1.json).

## 아직 검증하지 않은 범위와 다음 개선

수만 개의 실제 프로젝트 자료, 수시간·24시간 연속 사용, 다른 GPU/드라이버/운영체제, 설치·다운로드 작업 도중 Editor 종료, 실제 GPU OOM은 검증하지 않았습니다. Unity 6.5/6.6/7에 대한 기존 호환성 한계도 유지합니다. 이 결과를 모든 버전의 무누수 보장으로 해석하지 마세요.

현재 구현은 실사용을 시작할 수 있는 패키지 수준이며 임시 응답이나 가짜 모델 결과를 사용하지 않습니다. 다음 우선순위는 실제 프로젝트 검색 정답 세트로 품질 측정, 큰 프로젝트 장시간 시험, 오래된 미리보기 파일의 디스크 캐시 정리입니다. 디스크 캐시 증가는 RAM 누수와 별도로 관리할 문제입니다.
