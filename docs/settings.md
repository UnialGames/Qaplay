# 프로젝트 설정

이 프로젝트를 어떻게 테스트할지를 한곳에서 정한다. Unity 에디터의 **Project Settings > qaplay**에서 고치고(메뉴 **qaplay > Settings...**), 값은 Unity 프로젝트의 맨 위 폴더의 `qaplay.json`에 들어간다. 러너와 CI와 팀이 같은 파일을 읽으니 저장소에 넣는다.

## 1. 설정 화면

| 부분 | 정하는 것 |
|---|---|
| 설정 파일과 "설정 확인" | 파일이 어디에 있는지. "설정 확인"은 러너가 읽는 그대로 확인한다: 규칙 폴더가 있는지, PC 빌드 파일이 있는지, Android 기기가 연결되어 있고 앱이 설치되어 있는지 |
| QA 빌드 | 플랫폼(PC, Android, iOS)마다 빌드에 에이전트를 넣을지. 켜면 그 플랫폼의 Scripting Define Symbols에 `QAPLAY`가 들어간다. 출시 빌드를 만들기 전에 끈다 |
| 게임을 어디서 돌릴지 | 에디터(Play), PC 빌드, Android 가운데 하나. PC 빌드는 띄울 `.exe`("마지막 빌드" 버튼이 있다)와 덧붙일 실행 인자. Android는 앱의 패키지 이름("이 프로젝트의 것" 버튼이 있다)과 기기 |
| 규칙 폴더 | `qaplay run`에 규칙을 주지 않으면 돌리는 폴더들. 시트는 TC마다의 규칙을 여기서 찾는다 |
| 실행 | 실패한 규칙을 다시 돌리는 횟수, 프리징으로 볼 시간, 게임이 뜨기를 기다리는 시간, 포트 |
| 기록을 둘 곳 | 실행 리포트와 버그 리포트의 폴더 |
| TC 시트 | 시트의 종류(Google Sheets, Excel, CSV)를 고르고 잇는다([시트 연동](sheet.md) 6절) |
| 이 PC에서만 | qaplay 화면의 언어(이 PC와 같게, English, 한국어). 이 값은 파일에 들어가지 않는다 |

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
  "bugs": "qaplay-bugs",
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
| `artifacts` | 러너가 띄운 게임의 로그를 받는 폴더 | `--artifacts` | `qaplay-artifacts` |
| `exceptions` | 배치 문제의 예외 목록 파일 | `--exceptions` | `qaplay-layout-exceptions.json` |
| `sheet` | 이어진 TC 시트. 설정 화면의 "TC 시트"나 `qaplay tc init`이 쓴다 | | 없음 |

- 모르는 이름이나 틀린 값(음수, 소수, 없는 대상)은 읽을 때 알리고 실행하지 않는다(종료 코드 2). 오타 난 설정이 조용히 무시되면 사람은 그 값이 쓰인 줄 안다.
- 손으로 고쳐도 된다. 화면과 러너는 고칠 때마다 파일을 다시 읽고, 자기가 맡은 부분만 바꾼다.
