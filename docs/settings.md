# 프로젝트 설정

이 프로젝트를 어떻게 테스트할지를 한곳에서 정한다. Unity 에디터의 **Project Settings > qaplay**에서 고치고(메뉴 **qaplay > Settings...**), 값은 Unity 프로젝트의 맨 위 폴더의 `qaplay.json`에 들어간다. 러너와 CI와 팀이 같은 파일을 읽으니 저장소에 넣는다.

## 1. 설정 화면

Unity의 다른 설정(예: Services 아래의 Ads Mediation, Diagnostics)처럼 **qaplay** 아래에 하위 페이지가 있다. 맨 위 **qaplay**는 시작하기 페이지다: 해야 할 일을 순서대로 보여 주고(완료, 할 일, 선택), 줄마다 그 페이지를 여는 "설정하기" 버튼이 있다(Unity XR의 Project Validation과 같은 방식). 아래의 "러너로 설정 확인"은 러너가 `qaplay run`과 같게 읽어 확인한다: 규칙 폴더가 있는지, PC 빌드 파일이 있는지, Android 기기가 연결되어 있고 앱이 설치되어 있는지.

| 페이지 | 정하는 것 |
|---|---|
| qaplay (시작하기) | 러너를 찾았는지, 설정 파일의 위치, 그리고 아래 1~5가 되어 있는지 |
| 1. 게임과 실행 | QA 빌드(플랫폼마다 에이전트를 넣을지, 켜면 `QAPLAY` 심벌), 게임을 어디서 돌릴지(에디터 Play, PC 빌드의 `.exe`와 실행 인자, Android의 패키지 이름과 기기), 규칙 폴더, 실행 값(다시 돌리는 횟수, 프리징으로 보는 시간, 게임이 뜨기를 기다리는 시간, 포트) |
| 2. TC 시트 | 시트의 종류(Google Sheets, Excel, CSV)를 고르고 잇는다([시트 연동](sheet.md) 6절). 골라서 쓴다 |
| 3. 결과와 리포트 | 실행 리포트 폴더, 얼마나 둘지(요약, 통과한 규칙의 증거, 실패한 규칙의 증거, 조건을 붙인 규칙. [실행 리포트](reports.md) 3절), 대시보드(열기, 화면의 언어, 추이에 쓰는 실행 수와 측정값, 포트) |
| 4. 버그 | 버그 리포트 폴더, 테스터가 제보하는 방법(키, 손가락 수, 시간. QA 빌드에는 빌드할 때 들어간다), 버그를 올릴 곳(사용하지 않음, Jira. [버그 리포트](bugs.md) 8절) |
| 5. 배치 검사 예외 | 의도한 겹침과 넘침. 등록된 예외의 사유를 고치고 뺀다. Play 중이면 지금 화면의 문제를 찾아 사유와 함께 등록한다. 필요할 때만 |
| Preferences > qaplay | 이 PC에서만: qaplay 화면의 언어, 러너(qaplay.exe)의 위치. 파일에 들어가지 않는다 |

- 값을 바꾸면 바로 파일에 들어간다. 글자를 넣는 칸은 Enter를 누르거나 다른 칸으로 옮겨 가면 들어간다.
- 파일을 읽을 수 없으면(손으로 고치다 깨진 경우) 화면은 까닭을 보여 주고 고칠 때까지 아무것도 쓰지 않는다.
- Android 기기를 "연결된 기기 하나"로 두면 러너가 실행할 때마다 연결된 기기를 찾는다. 하나뿐이면 그것을 쓰고, 없거나 여럿이면 무엇을 하라고 알린다. QA PC마다 기기가 다르면 이렇게 둔다(설정 파일은 팀이 함께 쓴다).
- 러너가 띄우는 앱은 이미 설치되어 있어야 한다. 러너는 설치하지 않는다(CI에서는 워크플로가 설치한다).

## 2. 값을 정하는 순서

명령줄에 준 값, 그다음 `qaplay.json`, 그다음 기본값이다.

```
qaplay run                       설정의 규칙 폴더를, 설정의 대상에서, 설정의 횟수로 돌린다
qaplay run qa/smoke --retries 0  이 폴더만, 다시 돌리지 않고
qaplay settings                  지금 쓰이는 값과 그 값이 어디서 왔는지, 문제가 있는지
```

- 실행 대상은 명령줄에 하나라도 주면(`--app`, 또는 `--package`나 `--device`) 설정 파일의 다른 대상은 쓰지 않는다.
- 러너는 실행한 폴더의 `qaplay.json`을 읽는다. 다른 파일은 `--settings 파일`로 고른다.
- 파일 안의 경로는 그 파일이 있는 폴더 기준이다.

## 3. 파일의 내용

```json
{
  "target": {
    "kind": "android",
    "package": "com.example.game",
    "device": ""
  },
  "rules": ["Assets/QA/Rules"],
  "run": {
    "retries": 1,
    "freezeSeconds": 10,
    "launchSeconds": 60,
    "port": 27275
  },
  "reports": "qaplay-reports",
  "retention": { "summary": 365, "passed": 14, "failed": 90, "rules": [{ "when": { "result": "failed", "kind": "screenshot" }, "days": "forever" }] },
  "dashboard": { "lang": "ko", "trendRuns": 50, "metrics": ["frames.averageMs", "device.pss"], "port": 27280 },
  "bugs": "qaplay-bugs",
  "bugReport": { "key": "F12", "fingers": 3, "seconds": 1 },
  "sheet": { "type": "google", "spreadsheet": "https://docs.google.com/spreadsheets/d/.../edit", "tc": "TC", "summary": "요약" }
}
```

| 이름 | 뜻 | 대신하는 옵션 | 기본 |
|---|---|---|---|
| `target.kind` | `editor`, `pc`, `android` | | `editor` |
| `target.app`, `target.appArgs` | `pc`: 띄울 빌드와 덧붙일 실행 인자 | `--app`, `--app-args` | |
| `target.package`, `target.device` | `android`: 앱의 패키지 이름과 기기의 시리얼. 기기가 비어 있으면 연결된 기기 하나 | `--package`, `--device` | |
| `rules` | 규칙 폴더들 | `qaplay run`의 규칙 | 없음 |
| `run.retries` | 실패한 규칙을 다시 돌리는 횟수 | `--retries` | 1 |
| `run.freezeSeconds` | 프레임이 이만큼 멈추면 프리징 | `--freeze-seconds` | 10 |
| `run.launchSeconds` | 게임이 뜨고 에이전트가 답하기를 기다리는 시간 | `--launch-seconds` | 60 |
| `run.port` | 에이전트의 포트 | `--port` | 27275 |
| `reports`, `bugs` | 실행 리포트와 버그 리포트의 폴더 | `--reports`, `--bugs` | `qaplay-reports`, `qaplay-bugs` |
| `retention` | 실행 리포트의 보관 기한: `summary`, `passed`, `failed`(날 수나 `forever`)와 조건을 붙인 `rules`([실행 리포트](reports.md) 3절) | `qaplay reports retention` | 365, 14, 90일 |
| `dashboard.lang` | 대시보드 화면의 언어: `auto`(브라우저를 따른다), `en`, `ko` | | `auto` |
| `dashboard.trendRuns` | 추이에 쓰는 최근 실행의 수 | | 50 |
| `dashboard.metrics` | 추이로 그리는 측정값: `frames.averageMs`, `frames.p99Ms`, `frames.hitches`, `growth.managed`, `growth.native`, `device.cpu`, `device.pss`, `device.batteryTemp` | | 전부 |
| `dashboard.port` | 대시보드의 포트(이 PC에서만 열린다) | `--listen` | 27280 |
| `issues` | 버그를 올리는 이슈 관리 도구: `type`(`jira`)과 그 도구의 값(`site`, `project`, `issueType`). 토큰은 넣지 않는다 | | 없음(이 PC에만) |
| `bugReport.key` | 테스터가 제보하는 키(Input System의 키 이름: `F12`, `F10`, `Backquote` 등). `none`이면 키로는 하지 않는다 | | `F12` |
| `bugReport.fingers`, `bugReport.seconds` | 화면을 몇 손가락으로 몇 초 누르면 제보하는지. `fingers`가 0이면 손가락으로는 하지 않는다 | | 3, 1 |
| `artifacts` | 러너가 띄운 게임의 로그를 받는 폴더 | `--artifacts` | `qaplay-artifacts` |
| `exceptions` | 배치 문제의 예외 목록 파일 | `--exceptions` | `qaplay-layout-exceptions.json` |
| `sheet` | 이어진 TC 시트. 설정 화면의 "TC 시트"나 `qaplay tc init`이 쓴다 | | 없음 |

- 모르는 이름이나 틀린 값(음수, 소수, 없는 대상)은 읽을 때 알리고 실행하지 않는다(종료 코드 2). 오타 난 설정이 조용히 무시되면 사람은 그 값이 쓰인 줄 안다.
- 손으로 고쳐도 된다. 화면과 러너는 고칠 때마다 파일을 다시 읽고, 자기가 맡은 부분만 바꾼다.
- `bugReport`는 게임 안의 에이전트가 읽는다. 에디터의 Play는 이 파일을 바로 읽고, QA 빌드에는 빌드할 때 들어간다(빌드하는 동안만 `Assets/QaplayBuild/Resources/qaplay-agent.json`이 생겼다가 지워진다). 바꾼 뒤에는 다시 빌드한다. 빌드에 무엇이 들어갔는지는 에이전트의 `hello`가 알린다(`bugReport`).
