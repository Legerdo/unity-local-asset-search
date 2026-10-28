# 문제 해결 / Troubleshooting

오류를 확인할 때 설정 탭의 진단과 `Library/LocalAssetSearch/diagnostics.log`를 먼저 봅니다. 버그 제보에는 Unity·OS·Python 버전, CPU/GPU, 패키지 버전, 실패 단계와 관련 오류만 포함하세요. 로컬 경로는 가리고 `agent-connection.json`이나 인증 토큰은 공유하지 마세요.

| 증상 | 확인과 조치 |
|---|---|
| Git URL로 설치할 수 없음 | Git 실행 파일과 네트워크를 확인하거나 릴리스 `.tgz`를 tarball로 설치합니다. URL 끝의 버전 태그를 확인합니다. |
| Python 환경 준비 실패 | Python 3.11~3.13 실행 파일을 지정합니다. 쓰기 가능한 환경 경로와 디스크 여유, 패키지 다운로드 연결을 확인합니다. 전역 환경 대신 프로젝트 격리 환경을 사용합니다. |
| 모델 다운로드 실패 | 먼저 Python 환경을 준비합니다. 비어 있는 다운로드 폴더를 선택하고 네트워크·디스크 공간을 확인합니다. 접근 동의나 인증이 필요하면 Hugging Face의 표준 절차로 처리합니다. 패키지 UI에 토큰을 저장하지 않습니다. |
| 기존 모델을 불러올 수 없음 | ONNX/GGUF가 아닌 원본 Hugging Face 모델 폴더인지 확인합니다. 가중치뿐 아니라 tokenizer/config 등 필수 파일도 필요합니다. 기존 모델을 지우기보다 새 빈 폴더로 다운로드를 시도합니다. |
| 모델/전처리 지문 불일치 | 백엔드를 중지하고 새 설정으로 연결한 뒤 생성 인덱스를 초기화하고 다시 색인합니다. 이 작업은 이전 검색 벡터를 지우므로 범위를 먼저 확인합니다. 0.2.2 업그레이드에도 필요합니다. |
| 색인 대상이 없거나 너무 많음 | 포함 범위가 Assets 아래인지, 자료 종류·제외 경로가 맞는지 확인합니다. 기본 한도 1,000개, 상한 10,000개이며 한도를 넘기면 중단합니다. 필요한 폴더만 선택합니다. |
| 일부 파일만 실패 | 진단에서 개별 오류를 확인합니다. 텍스트 인코딩·크기, 오디오 코덱·길이, 이미지 주 에셋 여부를 [입력 계약](README.md#입력)과 비교합니다. 실패 원인을 고친 뒤 갱신하면 됩니다. |
| 검색 결과가 없음 | 색인이 완료됐는지 확인하고 최소 유사도를 끈 다음 종류·확장자·경로 필터를 풀어봅니다. 조건 변경 후 검색을 다시 실행합니다. 범위 밖·삭제된 원본은 반환하지 않습니다. |
| 오디오 결과가 설명과 다름 | 오디오 종류로 좁히고 원본을 들어봅니다. 전체 종류 검색은 타입별 점수를 보정하지 않습니다. 파일명·폴더도 임베딩에 들어가므로 이름과 실제 소리가 어긋날 수 있습니다. [품질 검증](QUALITY.md)을 참고하세요. |
| GPU 메모리 부족 | 다른 GPU 작업을 줄이거나 CPU를 선택하고 다시 연결합니다. 배치 축소 구현은 있으나 실제 VRAM 고갈 시험은 하지 않았습니다. |
| 취소가 바로 끝나지 않음 | 현재 추론이 끝난 뒤 취소가 반영됩니다. 완료·취소 상태를 확인한 뒤 다음 작업을 실행합니다. |
| 한글 로그가 깨짐 | 패키지는 Python 출력과 C# 수신을 UTF-8로 처리합니다. 외부 터미널·파일 뷰어도 UTF-8로 엽니다. 이미 손상되어 저장된 로그는 자동 복구하지 않습니다. |
| MCP/CLI 연결 실패 | Unity에서 모델을 연결한 상태인지 확인하고 `--project`를 프로젝트 절대 경로로 지정합니다. 재시작하면 접속 정보가 바뀌므로 토큰/포트를 직접 복사하지 말고 제공된 클라이언트를 사용합니다. |

## 안전하게 정리하기

평소에는 설정 탭의 인덱스 초기화를 사용합니다. `Library/LocalAssetSearch/` 전체를 삭제하면 격리 환경이나 그 안에 지정한 모델까지 잃을 수 있습니다. 모델·환경의 실제 위치를 확인하고 백엔드를 중지한 뒤 필요한 생성 데이터만 정리하세요. `UserSettings`와 원본 Assets는 캐시가 아닙니다.

## English quick reference

Check diagnostics in Setup and `Library/LocalAssetSearch/diagnostics.log`. Prepare the isolated Python environment before downloading a model. Use a native Hugging Face checkpoint, not ONNX/GGUF weights alone. A fingerprint mismatch requires clearing the generated index and reindexing after reconnecting.

For empty results, disable the score threshold and review scope, kind, extension and path filters. Audio matches need listening; mixed-modality cosine scores are not calibrated. For agent connection failures, keep the Editor backend running and point `--project` at the correct project. Do not publish connection files or tokens. Avoid deleting the entire Library subfolder: it may also contain your model and Python environment.
