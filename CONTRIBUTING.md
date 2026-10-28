# Contributing

버그 제보에는 재현 단계, 기대/실제 결과, 패키지·Unity·OS·Python 버전과 관련 오류를 적어주세요. 토큰·로컬 사용자 경로·제3자 원본 에셋은 제외하고, 가능하면 직접 만든 작은 샘플로 재현합니다.

## 구조

| 경로 | 역할 |
|---|---|
| `Editor/` | UI Toolkit, 에셋 수집, 백엔드 프로세스 관리 |
| `Backend~/` | 환경 준비, 모델 다운로드·추론, SQLite 검색 |
| `Agent~/` | MCP stdio, CLI, 선택적 스킬 |
| `Tools~/` | 별도 프로젝트용 통합·메모리 검증 |
| `Samples~/` | 직접 만든 Quick Start 샘플 |
| `Documentation~/` | 계약, 측정 결과, 한계 |

Unity는 `~`로 끝나는 폴더를 일반 에셋처럼 import하지 않습니다. 모델·환경·인덱스는 저장소 밖에 둡니다. Python과 모델의 고정 버전은 `Backend~/requirements.txt`와 `Backend~/server.py`가 기준입니다.

## 검사

설치된 격리 환경의 Python으로 저장소 루트에서 실행합니다.

```powershell
python -m unittest discover -s 'Backend~' -p 'test_*.py' -v
python -m unittest discover -s 'Agent~' -p 'test_*.py' -v
python 'Backend~/validate_package.py'
git diff --check
```

회귀 검사는 모델 품질이나 실제 Unity 실행을 대신하지 않습니다. Editor/프로세스/다운로드 변경은 [통합 검증](Documentation~/BENCHMARKS.md#재현), 수명 변경은 [메모리 검증](Documentation~/MEMORY.md)을 사용합니다. 검증 도구는 별도 프로젝트에서 실행하세요. 일부 도구는 테스트 범위를 설정하고 생성 인덱스를 초기화합니다.

성능 보고에는 데이터 수·유형·벡터 수·장비·정밀도·캐시·웜/콜드 상태와 측정 구간을 적습니다. 검색 품질은 파일명 참조와 내용 정답을 구분합니다. 원본 리소스가 공개되지 않으면 다른 사람이 완전히 재현할 수 없다는 점도 명시합니다.

## English

Include a minimal reproduction, expected/actual behavior and environment versions. Remove credentials, private paths and third-party assets. Run the commands above with the isolated environment. Use a separate Unity project for integration tools because they can reset generated test indexes. Preserve public APIs, explicit asset scope, UTF-8 output and paired locale keys. Follow [AGENTS.md](AGENTS.md) for agent-assisted changes.
