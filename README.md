# qaplay

Unity 게임용 자동화 QA 도구. 게임 안의 에이전트와 PC의 러너가 통신하며, 유저와 같은 경로(가상 터치)로 UI를 조작하고 결과를 검증한다.

> 초기 개발 단계다. 에디터와 PC 빌드에서 규칙 파일과 Python 스크립트로 uGUI 화면을 누르고 확인하는 데까지 되어 있다. 설치 방법과 명령은 바뀔 수 있다.

## 구조

```
Python 테스트 스크립트 ──► 러너 (.NET CLI) ──TCP──► 에이전트 (게임 내, QA 빌드에만 포함)
```

- 에이전트: 요소 조회, 가상 터치, 대기, 측정 명령을 수행
- 러너: 기기 연결, 테스트 실행과 판정, 크래시 복구, 리포트
- 대상: Unity 6.3 LTS 이상, Input System, uGUI (이후 UI Toolkit)

## 써 보기

샘플 프로젝트(`unity/`)를 Unity 6.3으로 열고 Play를 누르면 게임 안의 에이전트가 `127.0.0.1:27275`에서 기다린다. 다른 터미널에서 러너를 빌드해 붙는다(.NET 10 SDK 필요).

```
dotnet build runner -c Release
runner/bin/Release/net10.0/qaplay run unity/Assets/Sample/Rules/shop.yaml
```

| 명령 | 하는 일 |
|---|---|
| `qaplay run <규칙 파일이나 폴더>...` | 규칙을 실행한다. `--junit 파일`로 JUnit XML을 남기고, `--retries N`으로 다시 돌릴 횟수를 정한다(기본 1) |
| `qaplay tc list`, `qaplay tc show TC-012` | 시트(CSV)의 TC 목록과 한 줄의 내용을 보여 준다. 규칙을 쓰기 전에 읽는다. `run`은 실행한 결과를 그 시트의 TC 줄과 요약 표에 적는다 |
| `qaplay check <규칙 파일>` | 실행하지 않고 셀렉터를 지금 화면에 대어 본다 |
| `qaplay dump`, `qaplay values` | 지금 화면의 요소와, 규칙이 읽을 수 있는 게임 값을 보여 준다. 규칙을 쓰기 전에 본다 |
| `qaplay layout` | 지금 화면의 배치 문제를 찾는다: 글자가 자기 칸에 들어가지 않아 잘리거나 삐져나온 것, 버튼이나 글자가 서로 겹친 것. 규칙의 `assertLayout`이 보는 것과 같다 |
| `qaplay exceptions ...` | 의도된 배치 문제(말줄임표로 자른 제목, 디자인대로 겹친 배지 등)를 사유와 함께 예외로 등록하고(`add`), 보고(`list`), 고치고(`set`), 지운다(`delete`). 예외는 저장소에 두는 파일(`qaplay-layout-exceptions.json`)에 쌓인다 |
| `qaplay mocks` | 게임이 등록한 Mock(결제, 로그인, 광고 등을 대신하는 것)과 규칙이 고를 수 있는 응답 시나리오를 보여 준다 |
| `qaplay memory` | 게임이 지금 쓰는 메모리(C# 힙, Unity 네이티브, 프로세스 전체)를 보여 준다. 규칙의 `assertMemory`에 적을 예산을 정할 때 본다 |
| `qaplay frames --seconds 5` | 지금 화면의 프레임 시간(평균, 느린 쪽 1%, 끊김 횟수)을 그 시간 동안 재서 보여 준다. 규칙의 `assertFrameTime`에 적을 예산을 정할 때 본다 |
| `qaplay device --seconds 10` | OS에서만 알 수 있는 기기 지표를 보여 준다: CPU 사용률, 그리고 Android에서는 PSS, 발열 단계, 배터리. `--device`(Android)나 `--app`(PC 빌드)이 있어야 한다. 규칙의 `assertDevice`에 적을 예산을 정할 때 본다 |
| `qaplay serve -- python 스크립트.py` | Python 스크립트를 실행한다. 러너가 게임과의 사이를 중계한다 |
| `qaplay bugs ...` | 버그 리포트. 게임에서 건 제보(F12, 세 손가락 1초)를 가져오고(`pull`), 고치고(`set`), 회귀 규칙으로 만들고(`promote`), 고쳐졌는지 확인한다(`verify`) |
| `qaplay devices` | 연결된 Android 기기를 보여 준다. 다른 명령에 `--device 시리얼`을 붙이면 그 기기에 붙는다 |

- 게임에서 C# 예외, 크래시, 프리징이 나면 그 규칙은 실패다(`exception`, `crash`, `freeze`). `--app 게임.exe`(PC 빌드)나 `--device 시리얼 --package 패키지`(Android)로 러너가 게임을 직접 띄우게 하면, 죽거나 멈춘 게임의 로그를 남기고 다시 띄워 남은 규칙을 이어 간다
- Android에서 OS가 띄우는 창(권한 팝업, 전체 화면 앱의 첫 실행 안내)은 게임 밖이라 누를 수 없고, 떠 있는 동안 게임이 멈춰 러너가 붙지 못한다. 러너가 앱을 직접 띄울 때(`--package`)는 뜨지 않게 미리 처리한다: 앱이 요청한 권한을 미리 주고(`pm grant`), 첫 실행 안내를 본 것으로 해 둔다. 기기의 그 설정과 앱의 권한은 바뀐 채로 남으므로 QA용 기기에서 쓴다
  - 떠 있는 앱에 붙기만 하는 경우(`--package` 없이 `--device`만)에는 처리하지 않는다. 그때는 창을 직접 닫아 두거나 `adb shell settings put secure immersive_mode_confirmations confirmed`와 `adb shell pm grant 패키지 권한`을 먼저 실행한다
  - 미리 처리할 수 없는 창(스토어 결제, 계정 선택 등)이 뜨면 러너는 게임에 닿지 못해 실패한다(종료 코드 2)
- 종료 코드: 0은 모두 통과(다시 돌려 통과한 "불안정" 포함), 1은 실패가 있음, 2는 사용법이 틀렸거나 게임에 닿지 못함
- 규칙 파일을 쓰는 법은 [규칙 파일 명세](docs/rules.md)에 있다. 에디터의 `qaplay > Rules` 창에서 조작을 기록해 만들 수도 있다
- 화면 배치 검사(`assertLayout`)로 그 화면의 글자가 자기 칸에 다 들어가는지, 버튼과 글자가 서로 겹치지 않는지 확인한다. 번역문이 길어지거나 화면이 좁아져 생기는 문제를 잡는다
- 이미지 잔상 검사(`assertGhosting`)로 늦게 불러오는 이미지가 새 그림이 오기 전에 이전 그림을 보여 주는지 확인한다. 게임 코드의 불러오는 자리에 `ImageLoads.Started`와 `ImageLoads.Finished` 두 줄을 넣어 둔다
- 메모리 예산(`assertMemory`), 누수 검사(`leakCheck`: 같은 동작을 되풀이하며 메모리가 계속 느는지 본다), 프레임 시간 예산(`assertFrameTime`: 평균, 느린 쪽 1%, 끊김 횟수), 기기 지표 예산(`assertDevice`: CPU 사용률, PSS, 발열 단계, 배터리 온도)을 규칙에 적을 수 있다
- 결제, 로그인, 광고처럼 게임 밖의 화면을 거치는 흐름은 프로젝트의 Mock으로 바꿔 검증한다. 규칙의 `mock: { store: cancel }`로 Mock의 응답(성공, 실패, 취소, 지연, 콜백 중복)을 고른다. 게임 코드에 연결하는 방법은 [Mock 연결 안내](unity/Packages/com.qaplay.core/Documentation~/mock-guide.md)에 있다. Mock은 QA 빌드에만 들어간다
- 연타(`tapOn`의 `repeat`)와 동시 클릭(`tapTogether`)으로 버튼의 중복 처리와 입력 충돌을 확인한다. 샘플에 일부러 심은 버그(중복 처리, 입력 충돌, 메모리 누수, 끊김, 결제 콜백 중복, 글자 넘침, 요소 겹침, 이미지 잔상)를 잡는 규칙이 `unity/Assets/Sample/BuggyRules`에 있다(실패하는 것이 정상이다)
- `run`, `check`, `dump`, `values`, `layout`, `exceptions`, `mocks`, `memory`, `frames`, `device`에 `--json`을 붙이면 결과를 JSON으로 낸다. Claude Code 같은 AI 코딩 도구에 규칙 작성을 맡길 때는 [AI 도구용 안내](unity/Packages/com.qaplay.core/Documentation~/ai-guide.md)를 읽힌다

조건과 반복이 필요한 시나리오는 Python으로 쓴다. 예: [python/examples/shop.py](python/examples/shop.py)

```python
import qaplay

with qaplay.connect() as game:
    game.tap(id="shop.buy")
    game.wait(name="GoldLabel", text="1,100")
    assert game.get("gold") == 1100
```

자기 프로젝트의 빌드에 넣으려면 Scripting Define Symbols에 `QAPLAY`를 넣는다. 이 심벌이 없는 빌드에는 에이전트가 들어가지 않는다.

## 문서

- [요구사항](docs/requirements.md)
- [작업 계획](docs/plan.md)
- [프로토콜 명세](protocol/README.md)
- [규칙 파일 명세](docs/rules.md)
- [버그 리포트](docs/bugs.md)
- [시트 연동](docs/sheet.md)
- [AI 도구용 안내](unity/Packages/com.qaplay.core/Documentation~/ai-guide.md)
- [Mock 연결 안내](unity/Packages/com.qaplay.core/Documentation~/mock-guide.md)

## 라이선스

[MIT](LICENSE)
