# Unity 호환성

실제 검증일: 2026-10-28. 모든 검증은 Windows 11에서 수행했습니다. 최소 UPM 선언 버전과 실제 실행 검증 범위는 구분합니다.

| 버전 | 확인한 범위 | 상태 |
|---|---|---|
| Unity 2021.3 | package.json 최소 버전 선언 | 실제 실행 미검증 |
| Unity 2022.3.62f3 | 독립 프로젝트 UPM 연결, C# 컴파일, 한국어/영어 UXML·USS 구성 | 통과 |
| Unity 6000.3.24f1 (6.3 LTS) | 모델 없는 새 대상 다운로드, 새 환경, 134개 실제 추론·증분·검색, UTF-8 로그, CLI·MCP, 실제 어두운 테마 UI | 통과 |
| Unity 6000.5 / 6000.6 (6.5 / 6.6) | 설치된 에디터 없음; 추가 설치하지 않음 | 직접 실행 미검증 |
| Unity 6000.7.0a6 (6.7 Alpha) | 설치된 에디터로 별도 프로젝트 생성, UPM 연결, 134개 실제 추론·증분·세 종류 검색, 한글 로그, CLI, 한국어/영어 UI 구성 | 통과; 알파 버전에 한정 |
| Unity 7 | 현재 공개 정식 릴리스에서 검증하지 못함 | 미검증; 향후 보장하지 않음 |

6.7 알파의 [원본 보고서](benchmarks/unity-6000.7-alpha.json)에는 `fresh_model_inference`, `korean_ui`, `english_ui`, `utf8_process_logs`, `agent_cli`가 모두 true로 기록되어 있습니다. 6.7 알파 통과가 중간의 모든 6.5/6.6 패치나 향후 Unity 7 호환성을 증명하지는 않습니다.

공식 [Unity 7 안내](https://unity.com/releases/unity-7)는 2027년 초 출시를 예고하며, Unity 6에서의 연속적인 이전을 목표로 설명합니다. 이것은 이 패키지의 시험 결과가 아닙니다. 현재 릴리스 목록은 [Unity archive](https://unity.com/releases/editor/archive)에서 확인할 수 있습니다.

## 버전 의존성을 줄인 부분

- Unity는 자원 수집·GPU 미리보기·UI·프로세스 관리만 담당하고 모델 추론/전처리/검색은 외부 Python에 둡니다.
- Sentis, Inference Engine, 특정 모델 변환, URP/HDRP, Play Mode, 런타임 플랫폼 패키지에 의존하지 않습니다.
- UI Toolkit, AssetDatabase, PackageManager, EditorApplication 등 공개 Editor API를 사용하며 버전 문자열에 따른 상한 차단을 넣지 않았습니다.
- Unity Native API가 필요한 설정 저장은 비동기 대기 전에 main thread에서 처리합니다. 모델 재로딩/Editor 종료 시 자식 프로세스를 정리합니다.
- 공개 C# API는 Unity main thread에서 호출하는 계약입니다. .NET 스레드에서 AssetDatabase를 직접 호출하지 마세요.

새 버전에서 확인할 최소 게이트는 UPM 설치·컴파일, `AssetSearchValidation.LayoutOnly`, 별도 검증 프로젝트의 `AssetSearchValidation.Run`, 그리고 실제 창의 입력/결과 스크롤입니다. 플레이어 빌드는 이 Editor 전용 패키지의 추론 검증 대상이 아닙니다.
