# AI 코딩 도구용 qaplay 안내

Claude Code, Codex 같은 AI 코딩 도구가 qaplay로 테스트 규칙을 쓰고 확인하는 방법이다. 터미널에서 `qaplay` 명령을 실행해 게임 화면을 조회하고, 규칙 파일을 쓰고, 검사하고, 시험 실행한다. 사람도 같은 명령을 쓴다.

- 규칙 문법의 기준 문서: <https://github.com/UnialGames/Qaplay/blob/main/docs/rules.md>
- 이 문서는 그 요약과 작업 순서다. 둘이 다르면 기준 문서가 맞다.

## 전제

- 게임이 실행 중이어야 한다. Unity 에디터의 Play 모드이거나 QA 빌드다. 러너는 `127.0.0.1:27275`로 게임에 붙는다. 붙지 못하면 종료 코드 2로 끝난다.
- 러너는 저장소에서 빌드한다: `dotnet build runner -c Release`. 실행 파일은 `runner/bin/Release/net10.0/qaplay`다(Windows는 `qaplay.exe`).
- 규칙은 프로젝트 저장소의 `.yaml` 파일이다. 만들고 고치고 지우는 것은 파일을 직접 편집해서 한다. 그것을 위한 명령은 따로 없다.

## 작업 순서

먼저 `qaplay settings`로 이 프로젝트의 설정을 본다: 게임을 어디서 돌리는지(에디터, PC 빌드, Android), 규칙 폴더, 이어진 시트. 이 값은 Unity의 Project Settings > qaplay에서 사람이 정하고 `qaplay.json`에 들어 있다. 명령줄에 준 값이 그 파일보다 먼저다. 새 규칙 파일은 설정의 규칙 폴더에 둔다. 그래야 `qaplay run`(규칙을 주지 않으면 그 폴더를 돌린다)과 시트의 규칙 상태에 잡힌다.

시트의 TC로 규칙을 쓰는 일이면 먼저 그 TC를 읽는다: `qaplay tc show TC-012`. 내용과 기대 결과가 규칙이 확인해야 할 것이다. 규칙의 머리말에 `tc: TC-012`를 적어야 실행 결과가 시트의 그 줄에 적힌다. 자세한 것은 아래의 "시트의 TC로 규칙 쓰기"에 있다.

1. `qaplay dump`로 지금 화면에 어떤 요소가 있는지 본다.
2. `qaplay values`로 읽을 수 있는 게임 값을 본다.
3. 규칙 파일을 쓴다.
4. `qaplay check 규칙.yaml`로 문법과 셀렉터를 확인한다. 아무것도 누르지 않는다.
5. `qaplay run 규칙.yaml --retries 0`으로 실행해 본다.
6. 실패하면 출력의 오류 코드를 보고 규칙을 고친다. 규칙이 맞는데 실패한다면 게임의 버그다. 규칙을 고쳐서 통과시키지 않는다.
   - 버그인지 규칙이나 값을 잘못 이해한 것인지 가리려면 대조 규칙을 돌려 본다. 연타는 한 번만 누르는 규칙, 동시 클릭은 하나씩 누르는 규칙이다. 대조 규칙은 통과하고 원래 규칙만 실패하면 게임의 버그다.

화면을 옮겨 가는 규칙(팝업을 열고 그 안을 누르는 규칙 등)은 이 순서가 편하다: 그 화면까지 가는 규칙을 `run` → 그 화면에서 `dump`와 `check` → 게임을 다시 시작 → 전체 규칙을 `run`.

주의할 점

- `run`은 실제로 누르므로 게임 상태가 바뀐다. 규칙은 그 규칙이 시작하는 화면에서 돌려야 한다. 처음 화면에서 시작하는 규칙을 다시 돌리려면 게임을 다시 시작한다.
  - 에디터: Play를 껐다 켠다. 사람에게 부탁해야 한다.
  - 빌드: 설정의 대상이 PC 빌드나 Android이면 `qaplay run 규칙.yaml`만으로 러너가 게임을 직접 띄운다. 설정에 없으면 `qaplay run 규칙.yaml --app 게임.exe`로 준다. 실행할 때마다 게임을 새로 띄우고 끝나면 끄므로 늘 처음 화면에서 시작한다. 같은 포트를 쓰는 다른 게임(에디터의 Play 등)은 꺼 둔다.
- `dump`와 `check`는 지금 화면 하나만 본다. 팝업 안의 요소는 팝업을 연 뒤에 보인다.
  - 뒤 화면을 보려면 그 화면까지 가는 명령만 담은 규칙을 `run`해서 게임을 그 화면에 놓은 뒤 `dump`와 `check`를 한다. 예: `- tapOn: { name: SellButton }` 한 줄짜리 규칙으로 팝업을 열고 `dump`한다.

## 명령

모든 명령에 `--json`을 붙일 수 있다. 붙이면 표준 출력에는 결과 JSON 하나만 나오고, 사람이 읽는 줄은 표준 오류로 간다.

| 명령 | 하는 일 | 종료 코드 |
|---|---|---|
| `qaplay settings` | 지금 쓰이는 설정 값과 그 값이 어디서 왔는지, 문제(없는 규칙 폴더, 없는 빌드 파일, 연결되지 않은 기기)를 보여 준다. 게임이 떠 있지 않아도 된다 | 0 문제 없음, 1 문제 있음 |
| `qaplay tc list`, `qaplay tc show TC-012` | 시트의 TC 목록과, TC 한 줄의 모든 칸을 보여 준다. 게임이 떠 있지 않아도 된다 | 0 |
| `qaplay reports list`, `qaplay reports show latest` | 지난 실행의 기록을 보여 준다: 규칙마다의 결과, 실패 내용, 측정 수치, 증거 파일(실패한 화면, 게임 로그)의 경로. 게임이 떠 있지 않아도 된다 | 0 |
| `qaplay dump` | 지금 화면의 요소를 트리로 보여 준다 | 0 |
| `qaplay values` | 게임이 등록한 값과 지금의 값을 보여 준다 | 0 |
| `qaplay layout` | 지금 화면의 배치 문제(글자 넘침, 요소 겹침)를 찾아 번호와 함께 보여 준다. 아무것도 누르지 않는다. `--screenshot 파일.png`로 화면도 저장한다 | 0 문제 없음, 1 문제 있음 |
| `qaplay exceptions list / add / set / delete` | 의도된 배치 문제의 예외 목록을 보고, 등록하고, 사유를 고치고, 지운다 | 0 |
| `qaplay mocks` | 게임이 등록한 Mock과 고를 수 있는 시나리오, 지금 고른 것을 보여 준다 | 0 |
| `qaplay memory` | 게임이 지금 쓰는 메모리를 보여 준다. `assertMemory`의 예산을 정할 때 본다 | 0 |
| `qaplay frames --seconds 5` | 지금 화면의 프레임 시간을 그 시간 동안 재서 보여 준다. `assertFrameTime`의 예산을 정할 때 본다 | 0 |
| `qaplay device --seconds 10` | OS에서 읽는 기기 지표(CPU 사용률, Android는 PSS와 발열 단계와 배터리)를 보여 준다. `--device`나 `--app`이 있어야 한다 | 0 |
| `qaplay check 규칙.yaml` | 문법을 검사하고, 셀렉터를 지금 화면에 대어 본다 | 0 문제 없음, 1 문제 있음 |
| `qaplay run 규칙.yaml...` | 규칙을 실행한다. 폴더를 주면 그 아래의 `.yaml`을 모두 실행한다 | 0 모두 통과, 1 실패 있음 |

어느 명령이든 사용법이 틀렸거나 게임에 붙지 못하면 종료 코드 2다.

### dump

```
screen 1920x1080
Canvas  Node
  GoldLabel  Text  "1,200"
  BuyButton  Button  id=shop.buy  "구매"  [tappable]
    Text  Text  "구매"
  SoldOutButton  Button  "품절"
  FadedPanel  Image  [hidden]
```

한 줄이 요소 하나다. 들여쓰기가 부모와 자식이고, 차례로 이름(`name`), 종류(`type`), 고유 ID(`id=`), 글자(`text`)다. `[tappable]`은 입력을 받는 요소, `[hidden]`은 화면에 보이지 않는 요소다.

`[tappable]`은 그 요소가 버튼처럼 입력을 받는 종류이고 꺼져 있지 않다는 뜻이다. 다른 요소에 가려졌는지는 따지지 않는다. 팝업이 떠 있어도 뒤의 버튼은 `[tappable]`로 나오고, 누르려 하면 `not_tappable`로 실패한다.

`--json`은 요소마다 `name`, `type`, `id`, `text`, `rect`(화면 크기에 대한 비율, 왼쪽 위 원점), `visible`, `interactable`, `children`을 준다.

```json
{"screen":{"width":1920,"height":1080},"roots":[{"name":"Canvas","type":"Node","rect":{"x":0,"y":0,"w":1,"h":1},"visible":true,"interactable":false,"children":[]}]}
```

### values

```
gift.requests = 0
gold = 1200
```

```json
{"values":{"gold":1200,"gift.requests":0}}
```

여기에 없는 값은 `assertValue`로 확인할 수 없다. 필요한 값은 게임 코드에서 `GameValues.Register("키", () => 값)`으로 등록한다.

### memory

```
managed     18.4 MB
native      95.2 MB
process    412.1 MB
```

```json
{"managed":19293798,"native":99824435,"process":432118169}
```

- 사람이 읽는 줄은 MB, JSON은 바이트다. 규칙의 `assertMemory: { managed: 64 }`에는 MB로 적는다.
- 그 플랫폼에서 읽을 수 없는 값은 `not available on this platform`(JSON은 `null`)으로 나온다.
- 에디터에서는 에디터 자신의 메모리가 들어 있다. 예산은 빌드에서 잰 값으로 정한다.

### check

```
command 2 (line 2): - tapOn: { name: Item } -> many: selector matched 6 elements
1 issue(s) on the current screen
```

```json
{"issues":[{"command":2,"line":2,"source":"- tapOn: { name: Item }","kind":"many","count":6,"message":"selector matched 6 elements"}]}
```

| kind | 뜻 | 고치는 법 |
|---|---|---|
| `none` | 걸리는 요소가 없다 | 이름과 글자를 `dump`와 맞춘다. 뒤 화면의 요소라면 그 화면에서 다시 검사한다 |
| `many` | 요소 하나가 필요한데 여럿이 걸린다 | `within`으로 범위를 좁히거나 `index`를 붙인다 |
| `unknown_key` | 게임이 등록하지 않은 값이다 | `values`에 나오는 키를 쓴다 |

- `none`이 모두 잘못은 아니다. 지금 화면에 대어 보는 것이라, 앞의 명령을 실행한 뒤에야 맞는 확인(`assertVisible: { name: GoldLabel, text: "1,100" }`, 알림, 팝업 안의 버튼)은 `none`으로 나온다. 이름과 조건이 `dump`와 맞는지만 보고, 맞다면 `run`으로 판정한다.
- `assertNotVisible`도 걸리는 요소가 없으면 `none`으로 나온다. 이름을 잘못 적은 `assertNotVisible`은 실행하면 조용히 통과하기 때문에 알려 주는 것이다. 그 요소가 있는 화면에서 이름을 확인한다.
- `tapTogether`는 요소마다 한 줄이 나오고 몇 번째 요소인지 앞에 붙는다: `none: element 2: selector matched 0 elements`
- `within`으로 적은 범위가 문제면 문구가 `within:`으로 시작한다: `none: within: no element matched the selector`

문법이 틀리면 게임에 붙지 않고 바로 알린다: `{"syntaxError":"rule line 1: unknown command 'tapp'"}`

### run

```
FAIL  rules/buy.yaml (0.3s)
      attempt 1: failed at command 2 (line 5): - assertValue: { gold: 999 } -> value_mismatch: gold: expected 999, got 1100
1 rule(s): 0 passed, 0 flaky, 1 failed
report: C:\project\qaplay-reports\20261128-164344
```

```json
{"passed":0,"flaky":0,"failed":1,"report":"C:\\project\\qaplay-reports\\20261128-164344","rules":[{"file":"rules/buy.yaml","name":"구매","tc":null,"status":"failed","seconds":0.3,"code":"value_mismatch","failures":["failed at command 2 (line 5): - assertValue: { gold: 999 } -> value_mismatch: gold: expected 999, got 1100"]}]}
```

- `status`는 `passed`, `flaky`(다시 돌려서 통과), `failed`다. 규칙을 쓰는 중에는 `--retries 0`으로 다시 돌리기를 끈다.
- `code`는 마지막 실패의 오류 코드다.
- `report`는 이 실행의 기록이 남은 폴더다. 규칙이 실패했으면 그 안에 실패한 그 화면이 PNG로 있다(`qaplay reports show latest`가 경로를 알려 준다). 문구만으로 원인을 알 수 없을 때 그 그림을 본다. 게임이 화면을 그리지 않는 배치 모드에서는 그림이 없다.

| code | 뜻 |
|---|---|
| `syntax_error` | 규칙 파일의 문법이 틀렸다 |
| `not_found` | 제한 시간 안에 요소가 나타나지 않았다 |
| `ambiguous` | 셀렉터에 여러 요소가 걸렸다 |
| `not_tappable` | 요소는 있는데 누를 수 없다(가려짐, 비활성, 화면 밖) |
| `timeout` | `assertVisible`이나 `assertNotVisible`이 제한 시간 안에 만족되지 않았다 |
| `value_mismatch` | `assertValue`의 값이 다르다 |
| `unknown_key` | 게임이 등록하지 않은 값이다 |
| `layout` | `assertLayout`이 배치 문제를 찾았다(글자가 잘리거나 칸 밖으로 삐져나옴, 요소끼리 겹침). 게임의 문제다. 규칙을 고쳐서 없애지 않는다 |
| `ghosting` | `assertGhosting`이 잔상을 찾았다. 이미지를 불러오는 동안 이전 그림이 보였다. 게임의 버그다 |
| `unknown_mock` | 게임이 등록하지 않은 Mock이거나 그 Mock에 없는 시나리오다. `qaplay mocks`에 나오는 이름을 쓴다 |
| `over_budget` | `assertMemory`, `assertFrameTime`, `assertDevice`의 예산을 넘었다. 문구가 `not available here`이면 그 환경에서 읽을 수 없는 지표에 예산을 적은 것이다. 예산이 그 플랫폼의 평소 값보다 작게 잡힌 것이 아니라면 게임의 문제다 |
| `leak` | `leakCheck`로 되풀이하는 동안 메모리가 계속 늘었다. 게임의 버그다 |
| `exception` | 실행하는 동안 게임에서 C# 예외가 났다. 명령이 모두 성공했어도 실패다 |
| `crash`, `freeze` | 게임이 죽었거나 멈췄다 |

`exception`, `crash`, `freeze`는 게임의 버그다. 규칙을 고쳐서 없애지 않는다. 러너가 `qaplay-bugs` 폴더에 버그 리포트로 등록한다.

## 규칙 파일

```yaml
# 상점에서 구매하면 골드가 100 준다
name: 상점 구매
---
- tapOn: { id: shop.buy }
- assertVisible: { name: GoldLabel, text: "1,100" }
- assertValue: { gold: 1100 }
- assertNotVisible: { name: Toast }
```

- 머리말(`tc`, `name`)은 없어도 된다. 없으면 `---`도 뺀다.
- 명령 하나는 한 줄이다. 인자가 여럿이면 `{ }` 안에 적는다. 여러 줄로 들여 쓰지 않는다.
- 따옴표 없는 `100`, `true`, `null`은 글자가 아니다. 글자로 쓰려면 `"100"`처럼 감싼다. 쉼표나 `: `이 든 글자도 감싼다: `"1,100"`.

| 명령 | 인자 | 하는 일 |
|---|---|---|
| `tapOn` | 셀렉터 | 요소를 누른다. 나타나고 누를 수 있게 될 때까지 기다린다 |
| `tapTogether` | 셀렉터 목록 `[ ]` | 요소 2~10개를 같은 순간에 누른다 |
| `assertVisible` | 셀렉터 | 보이는 요소가 하나 이상 생길 때까지 기다린다 |
| `assertNotVisible` | 셀렉터 | 보이는 요소가 없어질 때까지 기다린다 |
| `assertValue` | `{ 키: 기대값 }` | 게임 값을 읽어 비교한다. 기다리지 않는다 |
| `assertMemory` | `{ managed \| native \| process: MB }` | 지금 쓰는 메모리가 예산(MB 숫자, 단위 없이) 이하인지 확인한다. 적은 계층만 본다 |
| `assertFrameTime` | `{ average \| p99: ms, hitches: 횟수 }` | 규칙의 시작(또는 앞의 `assertFrameTime`)부터 여기까지의 프레임 시간이 예산 이하인지 확인한다 |
| `assertDevice` | `{ cpu: %, pss: MB, thermal: 단계, batteryTemp: 섭씨 }` | OS에서 읽는 기기 지표가 예산 이하인지 확인한다. Android(`--device`)에서 모두 되고, PC 빌드(`--app`)는 `cpu`만 된다 |
| `assertLayout` | `all`, `overflow`, `overlap` | 지금 화면에 배치 문제가 없는지 확인한다: 글자가 자기 칸에 다 들어가는지(`overflow`), 요소끼리 겹치지 않는지(`overlap`) |
| `assertGhosting` | `{ frames: 허용하는 프레임 수 }` | 규칙의 시작(또는 앞의 `assertGhosting`)부터 여기까지, 이미지를 불러오는 동안 이전 그림이 보인 프레임이 그 수 이하인지 확인한다. 보통 0 |
| `mock` | `{ Mock 이름: 시나리오 }` | 게임이 등록한 Mock(결제, 로그인, 광고 등)이 다음부터 어떻게 응답할지 고른다. 규칙이 끝나면 기본 응답으로 돌아간다 |
| `leakCheck` | 횟수(10~1000) | 이 줄 아래의 명령들을 그만큼 되풀이하며 메모리를 잰다. 계속 늘면 실패다 |
| `seed` | 정수 | Unity 기본 랜덤의 시드를 정한다 |

- 셀렉터와 같은 `{ }` 안에 함께 적는 것: `timeout`(`tapOn`, `assertVisible`, `assertNotVisible`), `repeat`와 `delay`(`tapOn`만. 아래 "입력 스트레스 규칙")
- 기다리는 시간은 10초다. 셀렉터 옆에 `timeout`(밀리초)으로 바꾼다: `{ name: StartButton, timeout: 30000 }`
- 글자가 바뀌었는지는 `assertVisible`에 `text`를 넣어 확인한다.
- 값이 늦게 바뀌면 화면의 변화를 `assertVisible`로 먼저 기다린 뒤에 `assertValue`를 쓴다.

### 셀렉터

조건은 `id`, `name`, `type`, `text`, `within`, `index`다. 모두 정확히 일치해야 한다(부분 일치나 정규식이 아니다). 요소 하나로 정해지는 조건을 아래 순서로 고른다.

1. `{ id: shop.buy }`: 프로젝트가 붙인 고유 ID. `dump`에 `id=`로 나온다
2. `{ name: InfoButton }`: 이름
3. `{ text: 구매 }` 또는 줄여서 `tapOn: 구매`: 글자. 언어나 문구가 바뀌면 깨진다
4. `{ name: Item, within: { name: ItemList } }`: 범위 안에서
5. `{ name: Item, within: { name: ItemList }, index: 1 }`: 그래도 여럿이면 순서(0부터)

## 입력 스트레스 규칙

버튼이 중복 처리를 막는지, 함께 눌리면 안 되는 버튼들이 서로를 막는지 확인한다. 누른 뒤에 기대 결과를 반드시 적는다. 기대 결과가 없으면 판정할 것이 없다.

```yaml
# 연타: 5번 연달아 눌러도 요청은 한 번만 나가야 한다
- tapOn: { id: shop.gift, repeat: 5 }
- assertValue: { gift.requests: 1 }

# 동시 클릭: 확인과 취소를 같은 순간에 눌러도 하나만 처리되어야 한다
- tapTogether: [ { name: ConfirmButton }, { name: CancelButton } ]
- assertNotVisible: { name: SellPopup }
- assertValue: { popups: 0 }
```

- `repeat`는 1~100이다. `delay`(밀리초)를 함께 적으면 누름 사이를 그만큼 더 쉰다. 적지 않으면 가장 빠르게(두 프레임에 한 번) 누른다.
- 연타는 처음 정한 자리를 계속 누른다. 첫 클릭으로 버튼이 꺼지거나 팝업에 가려져도 그렇다.
- `assertValue`는 값 하나와 같은지만 본다. "둘 중 하나"나 범위는 적을 수 없다. 그런 판정이 필요하면 Python 스크립트로 쓰거나, 한 값으로 판정할 수 있는 값을 게임에 등록한다.
- 값의 뜻(무엇을 세는지, 언제 바뀌는지)은 게임 코드의 `GameValues.Register`를 보고 확인한다.
- 기대 결과로는 누르는 즉시 바뀌는 값(요청 수, 열린 팝업 수)이 좋다. 서버 응답 뒤에 바뀌는 값은 응답이 몇 번 올지 모르는 상태에서 언제 확인해야 할지 정하기 어렵다. 그런 값이 필요하면 게임에 값을 등록해 달라고 요청한다.
- 어떤 버튼을 검증할지는 규칙을 쓰는 쪽이 정한다. 돈이나 재화가 움직이는 버튼, 서버 요청을 보내는 버튼, 팝업을 여닫는 버튼부터 쓴다.

### 메모리: 예산과 누수 검사

```yaml
# 예산: 이 화면까지 온 뒤의 메모리가 상한 이하여야 한다. 단위는 MB이고 단위 글자를 붙이지 않는다
- assertMemory: { managed: 64, process: 1536 }

# 누수 검사: 이 줄 아래를 20번 되풀이하면서 메모리가 계속 늘지 않는지 본다
- leakCheck: 20
- tapOn: { name: RewardButton }
- tapOn: { name: ClaimButton }
- assertNotVisible: { name: RewardPopup }
```

- `leakCheck` 위의 명령은 한 번만 실행한다(그 화면까지 가는 준비). 아래의 명령은 파일 끝까지가 되풀이할 묶음이다. 묶음이 끝나면 시작한 화면으로 돌아와 있어야 다음 되풀이가 된다.
- 판정은 러너가 한다: 뒤쪽 절반의 되풀이에서 `managed`나 `native`가 한 번에 0.05MB 넘게 늘면 `leak`이다. 기대 결과를 따로 적지 않는다.
- 통과해도 `memory: managed ... MB per repeat, native ... MB per repeat` 줄이 나온다. `--json`에서는 규칙마다 `growth`(되풀이 한 번에 MB)와 `memoryLog`(잰 값을 남긴 CSV 경로)가 있다.
- `leak`은 게임의 버그다. `allow`를 키워서 통과시키지 않는다. `allow`는 그 게임에서 누수가 아닌 증가(기록이 쌓이는 화면 등)가 확인됐을 때만 `- leakCheck: { repeat: 20, allow: 0.5 }`처럼 올린다.
- 횟수는 10~1000이다. 조금씩 새는 누수가 의심되면 횟수를 늘린다.
- 예산의 숫자는 `qaplay memory`로 빌드에서 잰 값을 보고 정한다. 에디터에서 잰 값은 에디터의 메모리가 섞여 있다.

### 프레임 시간 예산

```yaml
# 정렬을 눌러도 화면이 끊기지 않아야 한다
- tapOn: { name: SortButton }
- assertFrameTime: { average: 20, p99: 40, hitches: 0 }
```

- `average`와 `p99`는 ms(단위 글자 없이), `hitches`는 50ms보다 오래 걸린 프레임이 몇 번까지 괜찮은지다. 적은 것만 본다.
- 재는 구간은 규칙의 시작부터 이 명령까지다. 한 규칙에 여러 번 적으면 둘째부터는 바로 앞의 `assertFrameTime` 뒤부터다.
- 끊김의 기준을 바꾸려면 `hitchOver`를 함께 적는다: `{ hitches: 0, hitchOver: 100 }`
- 통과해도 `frames: average ... ms, p99 ... ms, longest ... ms, N hitch(es) over 50 ms, ...` 줄이 나온다. `--json`에서는 규칙마다 `frames`에 들어 있다.
- 예산의 숫자는 `qaplay frames --seconds 5`로 빌드에서 잰 값을 보고 정한다. 넘으면 `over_budget`이다.
- 메모리를 재는 명령(`assertMemory`, `leakCheck`)은 그 순간 끊김을 만들 수 있다. 프레임 시간을 보는 규칙에 함께 쓰지 않는다.

### 기기 지표 예산

```yaml
# 전투를 끝까지 한 뒤의 CPU 사용률, 메모리, 발열을 본다
- tapOn: { name: BattleStart }
- assertVisible: { name: ResultPopup, timeout: 120000 }
- assertDevice: { cpu: 150, pss: 1200, thermal: 1 }
```

- 러너가 OS에서 읽는 값이다. Android는 `--device`, PC 빌드는 `--app`으로 돌려야 한다. 에디터에 붙어서는 읽을 수 없다.
- `cpu`는 규칙의 시작(또는 앞의 `assertDevice`)부터의 평균이고 코어 하나를 다 쓰면 100이다. 구간이 1초보다 짧으면 재지 못한 것으로 보아 실패한다.
- `pss`(MB), `thermal`(0~6), `batteryTemp`(섭씨)는 Android에서만 된다. PC 빌드의 규칙에는 `cpu`만 적는다.
- 통과해도 `device: cpu ... %, pss ... MB, thermal ..., battery ... %, battery temperature ... C` 줄이 나온다. `--json`에서는 규칙마다 `device`에 들어 있다.

### Mock의 응답 고르기

결제, 로그인, 광고처럼 게임 밖의 화면을 거치는 흐름은 게임이 Mock으로 바꿔 둔 경우에만 검증할 수 있다. `qaplay mocks`로 무엇이 있는지 먼저 본다.

```
store = success  (success, fail, cancel, slow, duplicate)
```

```yaml
# 스토어가 결제 완료를 두 번 알려 줘도 보석은 한 번만 들어와야 한다
- mock: { store: duplicate }
- tapOn: { name: PayButton }
- assertVisible: { name: PayResult, text: 결제 완료 }
- assertValue: { gems: 100 }
```

- `=` 뒤가 지금 고른 응답이고 괄호 안이 고를 수 있는 것이다. 맨 앞이 기본값이다. 시나리오의 뜻은 게임 코드의 Mock을 보고 확인한다.
- 고른 응답은 다시 고를 때까지 그대로이고, 규칙이 끝나면 기본값으로 돌아간다.
- 흐름마다 응답을 하나씩 돌려 본다: 성공, 실패, 취소, 늦은 응답, 콜백 중복. 실패와 취소에서는 재화가 그대로인지, 중복에서는 한 번만 지급되는지를 `assertValue`로 적는다.
- 게임이 `PluginCalls`로 세어 둔 값이 있으면(`qaplay values`에 `plugin.`으로 시작하는 키) 호출과 콜백의 짝을 함께 본다: `assertValue: { plugin.store.buy.pending: 0 }`. `pending`이 남으면 콜백이 오지 않은 것이고, 음수면 두 번 온 것이다.
- `mocks`에 아무것도 없으면 그 게임은 아직 Mock을 연결하지 않은 것이다. 규칙으로 해결할 수 없다. [Mock 연결 안내](mock-guide.md)대로 게임 코드를 고쳐야 한다고 알린다.

### 화면 배치 검사

```yaml
- tapOn: { name: EventButton }
- assertVisible: { name: EventPanel }
- assertLayout: all
```

```
overflow: text is cut off in Canvas/EventPanel/Banner "LIMITED TIME SUPER SALE - EVERYTHING MUST GO TODAY"
overflow: text spills out of Canvas/EventPanel/Notice "오늘 하루만 전설의 검과 방패와 투구를 모두 90% 할인합니다"
overlap: Canvas/BuyButton overlaps Canvas/NewBadge (46% of the smaller one)
3 layout issue(s) on the current screen
```

- 위의 글은 `qaplay layout`의 출력이다. 규칙을 쓰기 전에 그 화면에서 먼저 돌려 본다. `--json`의 `issues`에는 문제마다 `kind`, `reason`(`truncated`는 잘림, `spilled`는 삐져나옴), `path`, `element`가 있다.
- `assertLayout`은 화면이 다 열린 뒤에 적는다. 바로 앞에 그 화면의 요소를 기다리는 `assertVisible`을 둔다.
- 화면을 옮겨 갈 때마다 한 번씩 적으면 그 흐름의 모든 화면을 본다. 팝업을 여는 규칙이면 팝업을 연 뒤에 한 번 더 적는다.
- 문제가 나오면 게임의 배치를 고쳐야 한다. 글자를 줄이거나 칸을 넓히는 것은 게임 쪽의 일이다.
- 겹침(`overlap`)은 누를 수 있는 요소와 글자만 본다. 부모와 자손, 누를 수 없는 장식, 팝업 뒤에 깔린 요소는 처음부터 보지 않는다. `--json`에서는 상대 요소가 `otherPath`와 `otherElement`에, 겹친 정도가 `share`(작은 쪽 넓이에서의 비율)에 있다.
- 겹침은 화면 크기에 따라 달라진다. 넓은 화면에서 통과해도 좁은 화면에서 걸릴 수 있다. 대상 기기의 화면 크기에서 돌린다.

### 배치 문제의 예외 등록

`assertLayout`이 알린 문제가 의도된 모습인지 판단해야 할 때의 순서다.

1. 그 화면에서 `qaplay layout --json --screenshot shot.png`를 실행한다. 문제의 목록과 그 화면의 그림을 함께 받는다.
2. 문제마다 `element`(겹침은 `otherElement`도)의 `rect`를 본다. 정규화 좌표라서 `screen`의 `width`와 `height`를 곱하면 그림에서의 자리다. 그림의 그 자리를 보고 실제로 어떻게 보이는지 확인한다.
3. 의도된 것으로 판단한 문제만 등록한다: `qaplay exceptions add 번호 --reason "사유"`. 번호는 방금 `layout`이 준 순서(1부터)다.
4. `qaplay layout`을 다시 돌려 남은 문제를 확인한다.

```json
{"issues":[{"kind":"overlap","share":0.46,"path":"Canvas/BuyButton","element":{"name":"BuyButton","type":"Button","rect":{"x":0.35,"y":0.82,"w":0.3,"h":0.05},"visible":true,"interactable":true},"otherPath":"Canvas/NewBadge","otherElement":{"name":"NewBadge","type":"Text","text":"NEW","rect":{"x":0.6,"y":0.81,"w":0.08,"h":0.03},"visible":true,"interactable":false}}],"ignored":0,"screenshot":"C:/work/shot.png","screen":{"width":1920,"height":1080}}
```

- 판단이 서지 않으면 등록하지 않는다. 글자가 읽히지 않게 잘렸거나, 버튼이 다른 버튼을 덮어 누르기 어려운 것은 의도된 모습이 아니다. 사람에게 그림과 함께 묻는다.
- 사유에는 왜 괜찮은지를 적는다("디자인 시안대로 배지가 버튼 모서리에 걸친다"). "테스트를 통과시키려고"는 사유가 아니다.
- 예외는 `qaplay-layout-exceptions.json`에 쌓인다. 저장소의 파일이라 사람이 검토한다. 등록한 것을 사람에게 알린다.
- `qaplay exceptions list`로 등록된 것을 보고, `set 번호 --reason`으로 사유를 고치고, `delete 번호`로 지운다.
- 화면을 그리지 않는 실행(Unity 에디터의 배치 모드 테스트)에서는 `--screenshot`이 실패한다. 빌드나 창이 있는 에디터에서 쓴다.

### 이미지 잔상

이미지를 늦게 불러오는 화면에서, 새 그림이 오기 전에 이전 그림이 보이는지 확인한다.

```yaml
- tapOn: { text: 검, within: { name: ItemList } }
- assertVisible: { name: IconLabel, text: 검 }
- tapOn: { text: 방패, within: { name: ItemList } }
- assertVisible: { name: IconLabel, text: 방패 }
- assertGhosting: { frames: 0 }
```

- 잔상은 그림을 바꿀 때 생긴다. 그림이 이미 들어 있는 자리를 다른 내용으로 한 번 더 바꾸는 흐름을 쓴다. 처음 채우는 것만으로는 이전 그림이 없어 잡을 것이 없다.
- `assertGhosting`은 불러오기가 끝난 뒤에 적는다. 끝났음을 알 수 있는 화면의 변화(글자, 표시)를 `assertVisible`로 먼저 기다린다.
- 게임이 `ImageLoads.Started`와 `ImageLoads.Finished`로 알린 이미지만 본다. 잔상이 눈에 보이는데 규칙이 통과한다면 그 불러오기가 연결되지 않은 것이다. 규칙으로는 해결할 수 없고, 게임 코드의 불러오는 자리에 그 두 줄을 넣어야 한다고 알린다.
- 실패하면 `ghosting`이고 게임의 버그다. 허용하는 프레임 수를 올려서 통과시키지 않는다.

### 시트의 TC로 규칙 쓰기

```
> qaplay tc list
TC-012  rule: ok  result: 통과  구매 버튼을 누른다
TC-030  rule: missing  result: 미실행  아이템을 길게 눌러 상세 창을 연다

> qaplay tc show TC-030
TC 번호: TC-030
화면: 인벤토리
내용: 아이템을 길게 눌러 상세 창을 연다
기대 결과: 상세 창에 아이템 이름과 설명이 보인다
rule: missing
```

- `rule: missing`은 규칙이 없는 TC, `outdated`는 규칙을 쓴 뒤에 시트의 내용이나 기대 결과가 바뀐 TC다. 이 둘이 할 일이다.
- `--json`을 붙이면 `cells`에 칸 이름과 글자가, `rules`에 그 TC의 규칙 파일들이 들어 있다.
- 규칙 파일의 머리말에 `tc: TC-030`을 적는다. 번호는 시트의 것과 글자가 같아야 한다.
- TC의 내용을 규칙의 주석에 옮겨 적지 않는다. 시트가 바뀌면 주석이 틀려진다. 왜 그렇게 확인하는지만 적는다.
- 기대 결과를 규칙으로 확인할 수 없으면(읽을 게임 값이 없음, 누를 수 없는 요소) 규칙을 억지로 쓰지 않고, 무엇이 필요한지 알린다(게임 값 등록, Mock 연결 등).
- `outdated`인 TC는 규칙을 바뀐 내용에 맞게 고친 뒤 `qaplay tc accept TC-012`를 실행한다. 규칙을 고치지 않고 `accept`만 하지 않는다.
- 시트의 칸은 직접 고치지 않는다. 사람이 적는 칸은 사람의 것이고, 도구의 칸은 `run`과 `tc` 명령이 적는다.
