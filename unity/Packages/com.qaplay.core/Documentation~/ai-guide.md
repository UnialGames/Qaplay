# AI 코딩 도구용 qaplay 안내

Claude Code, Codex 같은 AI 코딩 도구가 qaplay로 테스트 규칙을 쓰고 확인하는 방법이다. 터미널에서 `qaplay` 명령을 실행해 게임 화면을 조회하고, 규칙 파일을 쓰고, 검사하고, 시험 실행한다. 사람도 같은 명령을 쓴다.

- 규칙 문법의 기준 문서: <https://github.com/UnialGames/Qaplay/blob/main/docs/rules.md>
- 이 문서는 그 요약과 작업 순서다. 둘이 다르면 기준 문서가 맞다.

## 전제

- 게임이 실행 중이어야 한다. Unity 에디터의 Play 모드이거나 QA 빌드다. 러너는 `127.0.0.1:27275`로 게임에 붙는다. 붙지 못하면 종료 코드 2로 끝난다.
- 러너는 저장소에서 빌드한다: `dotnet build runner -c Release`. 실행 파일은 `runner/bin/Release/net10.0/qaplay`다(Windows는 `qaplay.exe`).
- 규칙은 프로젝트 저장소의 `.yaml` 파일이다. 만들고 고치고 지우는 것은 파일을 직접 편집해서 한다. 그것을 위한 명령은 따로 없다.

## 작업 순서

1. `qaplay dump`로 지금 화면에 어떤 요소가 있는지 본다.
2. `qaplay values`로 읽을 수 있는 게임 값을 본다.
3. 규칙 파일을 쓴다.
4. `qaplay check 규칙.yaml`로 문법과 셀렉터를 확인한다. 아무것도 누르지 않는다.
5. `qaplay run 규칙.yaml --retries 0`으로 실행해 본다.
6. 실패하면 출력의 오류 코드를 보고 규칙을 고친다. 규칙이 맞는데 실패한다면 게임의 버그다. 규칙을 고쳐서 통과시키지 않는다.

주의할 점

- `run`은 실제로 누르므로 게임 상태가 바뀐다. 규칙은 그 규칙이 시작하는 화면에서 돌려야 한다. 처음 화면에서 시작하는 규칙을 다시 돌리려면 게임을 다시 시작한다.
  - 에디터: Play를 껐다 켠다. 사람에게 부탁해야 한다.
  - 빌드: `qaplay run 규칙.yaml --app 게임.exe`로 러너가 게임을 직접 띄우게 한다. 실행할 때마다 게임을 새로 띄우고 끝나면 끄므로 늘 처음 화면에서 시작한다. 같은 포트를 쓰는 다른 게임(에디터의 Play 등)은 꺼 둔다.
- `dump`와 `check`는 지금 화면 하나만 본다. 팝업 안의 요소는 팝업을 연 뒤에 보인다.
  - 뒤 화면을 보려면 그 화면까지 가는 명령만 담은 규칙을 `run`해서 게임을 그 화면에 놓은 뒤 `dump`와 `check`를 한다. 예: `- tapOn: { name: SellButton }` 한 줄짜리 규칙으로 팝업을 열고 `dump`한다.

## 명령

모든 명령에 `--json`을 붙일 수 있다. 붙이면 표준 출력에는 결과 JSON 하나만 나오고, 사람이 읽는 줄은 표준 오류로 간다.

| 명령 | 하는 일 | 종료 코드 |
|---|---|---|
| `qaplay dump` | 지금 화면의 요소를 트리로 보여 준다 | 0 |
| `qaplay values` | 게임이 등록한 값과 지금의 값을 보여 준다 | 0 |
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

문법이 틀리면 게임에 붙지 않고 바로 알린다: `{"syntaxError":"rule line 1: unknown command 'tapp'"}`

### run

```
FAIL  rules/buy.yaml (0.3s)
      attempt 1: failed at command 2 (line 5): - assertValue: { gold: 999 } -> value_mismatch: gold: expected 999, got 1100
1 rule(s): 0 passed, 0 flaky, 1 failed
```

```json
{"passed":0,"flaky":0,"failed":1,"rules":[{"file":"rules/buy.yaml","name":"구매","tc":null,"status":"failed","seconds":0.3,"code":"value_mismatch","failures":["failed at command 2 (line 5): - assertValue: { gold: 999 } -> value_mismatch: gold: expected 999, got 1100"]}]}
```

- `status`는 `passed`, `flaky`(다시 돌려서 통과), `failed`다. 규칙을 쓰는 중에는 `--retries 0`으로 다시 돌리기를 끈다.
- `code`는 마지막 실패의 오류 코드다.

| code | 뜻 |
|---|---|
| `syntax_error` | 규칙 파일의 문법이 틀렸다 |
| `not_found` | 제한 시간 안에 요소가 나타나지 않았다 |
| `ambiguous` | 셀렉터에 여러 요소가 걸렸다 |
| `not_tappable` | 요소는 있는데 누를 수 없다(가려짐, 비활성, 화면 밖) |
| `timeout` | `assertVisible`이나 `assertNotVisible`이 제한 시간 안에 만족되지 않았다 |
| `value_mismatch` | `assertValue`의 값이 다르다 |
| `unknown_key` | 게임이 등록하지 않은 값이다 |
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
| `seed` | 정수 | Unity 기본 랜덤의 시드를 정한다 |

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
