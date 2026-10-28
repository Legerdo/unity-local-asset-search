# 기술 계약

사용자 흐름과 설치는 루트 [README](../README.md), 에이전트 설정은 [Agent 안내](../Agent~/README.md)를 기준으로 합니다.

## 입력

- 텍스트: UTF-8(BOM 허용), `.txt .md .json .csv .xml .yaml .yml .cs .shader .compute .uss .uxml .lua`, 최대 2 MB. 1800자 청크/200자 겹침. 반환 offset은 문자 위치입니다.
- 이미지: Unity Texture2D 주 에셋. GPU에서 최대 1024px PNG를 생성하고 RGB로 임베딩합니다. importer Read/Write를 바꾸지 않습니다. 스프라이트 시트는 텍스처 단위, 알파/HDR 의미 보존은 보장하지 않습니다.
- 오디오: libsndfile이 읽는 `.wav .ogg .flac .mp3 .aif .aiff`, 최대 300초/256 MB. mono 16kHz, 20초 청크/2초 겹침. 반환 offset은 초입니다. 25ms 미만 오디오는 거부합니다.
- 범위: Assets 내부의 명시적 폴더/파일. 최대 처리 수 기본 1000, 상한 10000. 초과하면 일부를 조용히 생략하지 않고 중단합니다.

## 인덱스 수명

내용 SHA-256과 경로/종류가 같으면 재임베딩하지 않습니다. 선택 해제된 자료의 벡터는 남지만 검색 범위에서 제외합니다. 원본이 삭제된 결과는 반환하지 않습니다. 모델 가중치/설정/차원/전처리/주요 라이브러리가 바뀌면 생성 인덱스를 비우고 재색인해야 합니다. 모델 파일 변경 전에는 백엔드를 중지하세요.

모델을 로드한 상태의 증분 작업은 파일 해시만 검사합니다. 프로세스 재시작 뒤 첫 증분 작업은 모델 로딩·지문 검사를 포함합니다. 작업 취소는 현재 추론이 끝난 뒤 반영됩니다. 파일별 SQLite 트랜잭션이므로 기존 파일의 벡터를 반쯤 교체하지 않습니다. 실패한 자료는 진단에 표시합니다.

## 프로세스/HTTP 경계

Unity가 직접 argv로 Python을 실행합니다. 셸 명령 문자열로 실행하지 않습니다. 임의 포트의 `127.0.0.1`만 사용하고 HTTP proxy/redirect, CORS는 사용하지 않습니다. Origin이 있는 요청과 정확한 loopback Host가 아닌 요청을 거부합니다. 모든 API가 Bearer 자격 증명을 검사합니다. Editor 자격 증명은 환경 변수로만 전달합니다. `ready.json`에는 port/pid/session만 있습니다.

별도 에이전트 토큰은 생성된 `Library/LocalAssetSearch/agent-connection.json`에만 저장하며 `/status`, `/search`만 허용합니다. 에이전트 검색의 roots/excluded는 `agent-scope.json`의 에디터 설정으로 대체합니다. 이는 같은 사용자 프로세스에 대한 OS 보안 격리를 뜻하지 않습니다. 생성 파일을 Git이나 로그에 넣지 마세요.

모든 엔드포인트는 JSON POST입니다. 최대 요청 크기는 8 MB입니다.

| Endpoint | Editor 기능 |
|---|---|
| `/status` | 작업 상태, 자료 수, 장치, 차원, 로딩 시간, GPU 메모리 |
| `/warmup` | 모델 로딩·지문 확인 |
| `/index` | `{items:[{guid,path,kind,source}]}`의 자료만 색인 |
| `/cancel` | 현재 작업 취소 |
| `/search` | `{query,kind,top,code,roots,excluded}` 검색, 최대 100결과 |
| `/clear` | 생성된 벡터/지문만 비우기 |

모델 추론은 Hugging Face offline 설정과 `local_files_only=True`, `trust_remote_code=False`를 사용합니다. BF16 지원 CUDA에서는 BF16, 그 외 FP32이며 FP16은 사용하지 않습니다. 검색 점수는 FP32 단위 벡터의 코사인이고 확률이 아닙니다. `all`은 타입 사이 점수가 보정되지 않은 혼합 순위입니다. 목적에 맞는 종류 필터를 권장합니다.

Python 표준 출력·오류는 UTF-8로 강제하고 C# 수신 스트림도 같은 인코딩으로 읽습니다. API JSON은 ASCII escape로도 안전하게 전달되고 CLI/MCP는 UTF-8을 사용합니다. 이미 잘못 인코딩되어 저장된 외부 로그 파일을 자동 복구하는 기능은 아닙니다.

## 회귀 검사

격리 환경의 Python으로 저장소 루트에서 실행합니다.

```powershell
python -m unittest discover -s 'Backend~' -p 'test_*.py' -v
python -m unittest discover -s 'Agent~' -p 'test_*.py' -v
python 'Backend~/validate_package.py'
```

이 검사는 실제 모델의 검색 품질을 대신하지 않습니다. 실제 모델·Unity·MCP 결과는 [BENCHMARKS](BENCHMARKS.md)에 따릅니다.
