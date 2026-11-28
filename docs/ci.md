# CI에서 돌리기

빌드가 나올 때마다, 또는 밤마다 QA 규칙을 자동으로 돌리는 방법이다. GitHub Actions와, 기기가 연결된 자체 러너(빌드 서버)를 전제로 한다.

- 워크플로 예시: 저장소의 `samples/ci/github-actions/qa.yml`. 게임 저장소의 `.github/workflows/`에 복사해 쓴다
- 상태: 예시의 각 단계는 Windows PC와 Android 에뮬레이터에서 실행해 확인했다. 실제 GitHub Actions의 자체 러너에서는 아직 돌려 보지 않았다

## 1. 왜 자체 러너인가

qaplay의 러너는 기기에 직접 붙는다(USB의 adb, 또는 PC 빌드의 프로세스). GitHub가 빌려주는 러너에는 팀의 기기가 없고, 기기 대여 서비스는 이 구조와 맞지 않아 다루지 않는다. 그래서 기기가 꽂혀 있는 PC 한 대를 자체 러너로 등록해 쓴다.

```
GitHub Actions ──(작업)──► 빌드 서버(자체 러너) ──USB──► Android 기기들
                              ├ qaplay 러너
                              └ 리포트 폴더 ◄── qaplay dashboard (브라우저로 본다)
```

## 2. 빌드 서버에 갖춰 둘 것

| 무엇 | 왜 |
|---|---|
| GitHub Actions 자체 러너 | 게임 저장소의 Settings > Actions > Runners에서 등록한다. 라벨을 하나 붙인다(예시는 `qa-devices`) |
| .NET 10 SDK | qaplay 러너를 빌드한다. 처음 빌드할 때 NuGet에서 패키지를 받으므로 인터넷이 필요하다 |
| Android platform-tools(`adb`)를 PATH에 | 앱을 설치하고 기기와 이야기한다 |
| 기기 | USB 디버깅을 켜고, 그 PC의 디버깅을 "항상 허용"으로 승인해 둔다. `qaplay devices`에 `device`로 나와야 한다 |
| 리포트를 쌓을 폴더 | 작업 폴더 밖의 고정된 경로(예시는 `D:\qaplay\reports`, `D:\qaplay\bugs`) |

기기 쪽 설정

- 개발자 옵션의 "화면 켜짐 상태 유지"(충전 중)를 켠다. 예시는 실행 전에 기기를 깨우지만(`KEYCODE_WAKEUP`), 잠금 화면이 걸려 있으면 풀지 못한다. 잠금은 "없음"으로 둔다.
- 러너는 앱을 띄울 때 OS가 띄우는 창(권한, 전체 화면 안내)이 뜨지 않게 미리 처리한다. 그 기기의 설정과 앱의 권한이 바뀐 채로 남으므로 QA 전용 기기를 쓴다.

## 3. 워크플로가 하는 일

| 단계 | 하는 일 |
|---|---|
| 게임 저장소 받기 | 규칙 파일을 받는다 |
| qaplay 받기, 러너 빌드 | qaplay 저장소를 `.qaplay`에 받아 러너를 빌드하고 PATH에 넣는다 |
| 기기를 깨우고 앱 설치 | `adb install -r`로 QA 빌드를 설치한다 |
| 규칙 실행 | `qaplay run`이 앱을 띄우고 규칙을 돌린다. 실패한 규칙이 있으면 이 단계가 실패한다 |
| 결과와 증거 올리기 | 이번 실행의 리포트 폴더를 작업의 아티팩트로 올린다 |

러너는 GitHub Actions 안에서 돌면 두 가지를 스스로 남긴다(Actions가 주는 환경 변수로 안다).

- 작업 요약: 통과, 불안정, 실패의 수와 규칙마다의 결과 표, 실패한 규칙의 내용. 작업 화면의 Summary에 나온다.
- 단계의 출력 `report`: 이번 실행의 리포트 폴더. 올리기 단계가 `${{ steps.qa.outputs.report }}`로 받는다.

리포트 폴더에는 요약(`run.json`), JUnit XML(`junit.xml`), 실패한 화면, 게임 로그가 들어 있다([실행 리포트](reports.md)).

바꿀 곳(예시 파일에 "바꿀 곳"이라고 적혀 있다)

| 값 | 뜻 |
|---|---|
| `APK` | QA 빌드가 놓이는 경로. `QAPLAY` 심벌을 넣은 빌드여야 한다 |
| `PACKAGE` | 게임의 패키지 이름 |
| `RULES` | 규칙 파일의 폴더 |
| `REPORTS`, `BUGS` | 실행 리포트와 버그 리포트를 쌓는 폴더. 작업 폴더 밖에 둔다 |
| `runs-on`의 라벨 | 빌드 서버에 붙인 라벨 |
| `matrix.device` | 기기마다 한 줄: 이름, 시리얼, 포트 |
| qaplay의 `ref` | 팀이 쓰기로 한 qaplay의 버전 |

- 리포트와 버그 폴더를 작업 폴더 밖에 두는 까닭: `checkout`은 작업 폴더를 비운다. 안에 두면 실행 이력이 매번 사라지고, 버그 번호가 매번 1번부터 다시 매겨져 같은 버그가 새 버그로 등록된다.
- 기기마다 포트를 다르게 주는 까닭: 러너는 이 PC의 포트 하나를 기기의 에이전트로 이어 놓고 그리로 이야기한다. 한 PC에서 두 기기를 함께 돌리면 같은 포트를 두고 부딪친다.
- 기기 여럿이 실제로 동시에 도는지는 그 PC에 등록한 자체 러너의 수에 달려 있다. 하나뿐이면 기기마다 차례로 돈다.
- qaplay 저장소가 비공개이면 두 번째 `checkout`에 그 저장소를 읽을 수 있는 토큰을 준다(`with: token:`).
- 보관 기한은 두 겹이다. 빌드 서버의 리포트 폴더는 qaplay의 기한을 따르고(`qaplay reports retention`), GitHub에 올린 아티팩트는 `retention-days`를 따른다.
- 프로젝트 설정 파일(`qaplay.json`, [프로젝트 설정](settings.md))이 게임 저장소에 있으면 러너는 그 값을 쓴다. 예시의 실행 단계가 명령줄로 주는 값(기기, 패키지, 포트, 폴더)은 그 파일보다 먼저다. 기기와 포트는 기기마다 달라서 명령줄로 주고, 다시 돌리는 횟수 같은 값은 설정 파일에 두면 된다. 러너는 실행한 폴더의 `qaplay.json`을 읽으므로, Unity 프로젝트가 저장소의 하위 폴더에 있으면 실행 단계에 `working-directory`를 주거나 `--settings 파일`을 준다.

## 4. 빌드와 잇기

예시는 QA 빌드가 빌드 서버의 정해진 경로에 놓인다고 본다. 팀의 빌드 파이프라인에 맞춰 바꾼다.

- 같은 워크플로에 빌드 작업이 있으면: QA 작업에 `needs: build`를 적고, 빌드 작업이 올린 APK를 `actions/download-artifact`로 받아 그 경로를 `APK`에 준다.
- 빌드가 다른 워크플로이면: 그 워크플로가 끝날 때 이 워크플로를 돌리게 하거나(`workflow_run`), 빌드가 APK를 빌드 서버의 정해진 경로에 놓게 한다.

## 5. 결과 보기

| 어디서 | 무엇을 |
|---|---|
| GitHub의 작업 화면 | 성공과 실패, Summary의 규칙별 표, 아티팩트(리포트 폴더) |
| 빌드 서버의 대시보드 | `qaplay dashboard --reports D:\qaplay\reports`를 띄워 두면 CI가 돌린 모든 실행과 추이를 브라우저로 본다. 다른 PC에서 보려면 `--listen 0.0.0.0:27280` |
| 시트 | Google Sheets를 쓰면 TC마다의 최신 결과가 시트에 적힌다. 프로젝트 설정 파일(`qaplay.json`)의 `sheet` 부분이 그 시트를 가리키고, 키는 빌드 서버에서 러너를 돌리는 계정의 사용자 폴더에 둔다([시트 연동](sheet.md) 6절). 저장소 안의 CSV나 xlsx는 CI에서 고쳐도 작업 폴더와 함께 사라지므로 맞지 않다 |

JUnit XML을 읽어 GitHub 화면에 테스트 목록으로 보여 주는 액션이 여럿 있다. 필요하면 팀이 고른 것을 올리기 단계 뒤에 더한다. 예시는 GitHub가 직접 만든 액션만 쓴다.

## 6. PC 빌드로 돌리기

기기 없이 Windows 빌드로 돌릴 수도 있다. 설치 단계를 빼고 실행 단계를 이렇게 바꾼다.

```yaml
      - name: 규칙 실행
        id: qa
        run: qaplay run "$env:RULES" --app "D:\builds\latest\Game.exe" --junit results/junit.xml --reports "$env:REPORTS" --bugs "$env:BUGS"
```

게임이 화면을 그릴 수 있어야 실패한 화면이 남는다. 자체 러너를 Windows 서비스로 돌리면 화면이 없는 세션에서 실행되므로, PC 빌드는 로그인한 세션에서 띄운 러너(`run.cmd`)로 돌리는 편이 안전하다(서비스로 돌렸을 때의 동작은 확인하지 않았다).

## 7. 다른 CI에서

러너는 명령 하나이고 종료 코드로 결과를 알린다(0 모두 통과, 1 실패 있음, 2 게임에 닿지 못함). Jenkins나 GitLab CI에서도 같은 명령을 쓴다. 작업 요약과 `report` 출력은 GitHub Actions에서만 나온다. 다른 CI에서는 `--junit`의 XML을 그 CI의 테스트 보고 기능에 주고, 리포트 폴더는 `qaplay run ... --json`의 `report` 값이나 `qaplay reports show latest`로 찾는다.
