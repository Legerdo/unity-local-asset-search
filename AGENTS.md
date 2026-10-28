# Local Asset Search

Unity Editor 전용 UPM 패키지입니다. 기본 언어는 한국어이고 영어 문자열은 같은 키로 관리합니다.

- `Editor/`: UI Toolkit 창, 에셋 수집, Python 프로세스 소유권과 API. Runtime/Sentis 의존성을 추가하지 마세요.
- `Backend~/`: 고정 버전 모델 다운로드, 로컬 추론, SQLite 증분 인덱스. 일반 추론에서 네트워크/remote code를 사용하지 않습니다.
- `Agent~/search.py`: 에디터가 실행한 서비스에 접근하는 읽기 전용 CLI. 아래 스킬을 참고하세요.
- `Agent~/skills/unity-asset-search/SKILL.md`: 다른 AI 도구에 복사해 사용할 수 있는 선택적 스킬.
- `Tools~/AssetSearchValidation.cs`: 별도 Unity 검증 프로젝트에서 실제 모델로 실행하는 통합 검증. 실행법과 측정 한계는 `Documentation~/BENCHMARKS.md`에 있습니다.

동작을 수정할 때 명시적으로 선택한 Assets 범위, 제외 경로, 기존 모델 파일, 전역 Python 환경을 보존하세요. 모델/환경/인덱스/접속 토큰은 Git에 넣지 않습니다. 외부 업로드나 GitHub 게시를 자동 수행하지 마세요.

문자열/로그는 UTF-8입니다. Python 프로세스의 `PYTHONUTF8`, `PYTHONIOENCODING`과 C# 리다이렉트 스트림의 UTF-8 디코딩을 함께 유지하세요. JSON 로케일 키와 포맷 인자는 한국어/영어가 같아야 합니다.

변경한 경계에 맞춰 패키지의 격리 환경 Python으로 실행하세요:

```powershell
python -m unittest discover -s 'Backend~' -p 'test_*.py' -v
python -m unittest discover -s 'Agent~' -p 'test_*.py' -v
python 'Backend~/validate_package.py'
```

이 테스트는 알고리즘/프로토콜 검증입니다. 실제 다운로드, GPU 추론, Unity 컴파일, 렌더링 검증을 대신하지 않습니다. 성능 보고에는 장비, 모델 리비전, 데이터셋, 배치, 콜드/웜 상태와 캐시 여부를 명시하세요. 합성 샘플 결과를 일반 검색 품질 벤치마크로 표현하지 마세요.
