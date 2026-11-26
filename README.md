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
| `qaplay check <규칙 파일>` | 실행하지 않고 셀렉터를 지금 화면에 대어 본다 |
| `qaplay dump`, `qaplay values` | 지금 화면의 요소와, 규칙이 읽을 수 있는 게임 값을 보여 준다. 규칙을 쓰기 전에 본다 |
| `qaplay serve -- python 스크립트.py` | Python 스크립트를 실행한다. 러너가 게임과의 사이를 중계한다 |
| `qaplay bugs ...` | 버그 리포트. 게임에서 건 제보(F12, 세 손가락 1초)를 가져오고(`pull`), 고치고(`set`), 회귀 규칙으로 만들고(`promote`), 고쳐졌는지 확인한다(`verify`) |
| `qaplay devices` | 연결된 Android 기기를 보여 준다. 다른 명령에 `--device 시리얼`을 붙이면 그 기기에 붙는다 |

- 게임에서 C# 예외, 크래시, 프리징이 나면 그 규칙은 실패다(`exception`, `crash`, `freeze`). `--app 게임.exe`(PC 빌드)나 `--device 시리얼 --package 패키지`(Android)로 러너가 게임을 직접 띄우게 하면, 죽거나 멈춘 게임의 로그를 남기고 다시 띄워 남은 규칙을 이어 간다
- Android 기기에서 전체 화면 앱을 처음 띄우면 시스템 안내(Viewing full screen)가 뜬다. 닫기 전에는 게임이 멈춰 있어 러너가 붙지 못한다(60초 뒤 종료 코드 2). 한 번 닫아 두거나 `adb shell settings put secure immersive_mode_confirmations confirmed`로 미리 꺼 둔다
- 종료 코드: 0은 모두 통과(다시 돌려 통과한 "불안정" 포함), 1은 실패가 있음, 2는 사용법이 틀렸거나 게임에 닿지 못함
- 규칙 파일을 쓰는 법은 [규칙 파일 명세](docs/rules.md)에 있다. 에디터의 `qaplay > Rules` 창에서 조작을 기록해 만들 수도 있다
- 연타(`tapOn`의 `repeat`)와 동시 클릭(`tapTogether`)으로 버튼의 중복 처리와 입력 충돌을 확인한다. 샘플에 일부러 심은 버그를 잡는 규칙이 `unity/Assets/Sample/BuggyRules`에 있다(실패하는 것이 정상이다)
- `run`, `check`, `dump`, `values`에 `--json`을 붙이면 결과를 JSON으로 낸다. Claude Code 같은 AI 코딩 도구에 규칙 작성을 맡길 때는 [AI 도구용 안내](unity/Packages/com.qaplay.core/Documentation~/ai-guide.md)를 읽힌다

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
- [AI 도구용 안내](unity/Packages/com.qaplay.core/Documentation~/ai-guide.md)

## 라이선스

[MIT](LICENSE)
