# AI 에이전트 연결 / Agent integration

MCP 도구를 지원하는 에이전트에는 다음 stdio 서버를 등록하세요. 경로는 실제 절대 경로로 바꿉니다. 클라이언트별 설정 파일 형식은 다를 수 있으며, 이 패키지는 전역 MCP 설정을 자동 수정하지 않습니다.

```json
{
  "mcpServers": {
    "unity-assets": {
      "command": "python",
      "args": ["<package>/Agent~/mcp_server.py", "--project", "<Unity project>"]
    }
  }
}
```

직접 호출 도구: `unity_asset_status({})`, `unity_asset_search({"query":"a red square","kind":"image","top":5})`. MCP/CLI 호출에는 Computer Use가 필요하지 않습니다. 최초 설치·범위 설정·색인은 Editor에서 준비하며, Unity 자동화 도구가 있으면 아래 공개 C# API로 같은 작업을 실행할 수 있습니다.

고급 조건 예: `unity_asset_search({"query":"a red square","kind":"image","minScore":0.7,"sort":"similarity","pathContains":"Icons","extensions":"png,jpg","top":20})`. CLI는 `--min-score 0.7 --sort similarity --path-contains Icons --extensions png,jpg`를 받습니다. `matched`는 하한선과 필터에 맞는 전체 자료 수이고 `results`에는 최대 top개가 담깁니다. 필터는 검색만 제한하며 에디터가 정한 범위를 넓힐 수 없습니다.

```csharp
// Unity main thread에서 실행. 기존에 저장한 범위와 설정을 사용합니다.
var paths = LocalAssetSearch.AssetCollector.Scan();
var request = await LocalAssetSearch.AssetCollector.PrepareAsync(paths);
await LocalAssetSearch.BackendClient.StartAsync();
await LocalAssetSearch.BackendClient.IndexAsync(request);
// StatusAsync로 running=false와 errors를 확인한 뒤 SearchAsync를 호출합니다.
```

MCP transport follows the [official stdio specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports). Tools expose schemas and read-only annotations. No extra MCP SDK dependency is needed.

Unity에서 모델을 연결하고 지정한 폴더를 인덱싱하면, 같은 컴퓨터의 에이전트가 Python 표준 라이브러리만으로 검색할 수 있습니다.

```powershell
python "<package>/Agent~/search.py" --project "<Unity project>" --status
python "<package>/Agent~/search.py" --project "<Unity project>" --query "a red square icon" --kind image --top 5
python "<package>/Agent~/search.py" --project "<Unity project>" --query "플레이어 이동 코드" --kind text --code
```

성공은 종료 코드 0과 stdout JSON, 실패는 종료 코드 2와 stderr JSON입니다. 서비스는 에디터가 실행하고 종료하며, 에이전트는 검색과 상태 조회만 할 수 있습니다. 검색 폴더와 제외 경로는 에디터의 최신 설정이 강제 적용됩니다. 접속 정보는 생성 파일 `Library/LocalAssetSearch/agent-connection.json`에만 저장합니다. 이 파일을 로그나 프롬프트에 붙여 넣지 마세요.

스킬을 지원하는 도구에는 `skills/unity-asset-search` 폴더를 해당 프로젝트의 `.agents/skills/` 등 도구가 지정한 위치에 복사할 수 있습니다. 자동으로 전역 설정을 변경하지 않습니다. `AGENTS.md`의 관련 안내만 기존 프로젝트 지침에 합치는 방식도 가능합니다.

For English: connect and index selected folders in the Editor, then run the CLI above. Output is UTF-8 JSON. The separate agent credential permits status and search only; the server enforces the Editor's current scope. Stop/reload rotates the connection. The optional bundled skill can be copied into your agent tool's project-level skill directory. No global configuration is installed.
