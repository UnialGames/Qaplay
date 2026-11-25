# qaplay 프로토콜 v0 (초안)

러너와 에이전트가 주고받는 메시지의 기준 문서다. 에이전트(C#), 러너(C#), Python 클라이언트는 이 문서만 보고 구현한다. 문서에 없는 동작이 필요하면 문서를 먼저 고친다.

- 프로토콜 버전: `1`
- 상태: 초안. 구현하면서 고칠 수 있다. 3단계까지 구현한 내용과 맞춰져 있다
- 명령: `hello`, `dump`, `find`, `tap`, `wait`, `get`, `seed`
- 이벤트: `heartbeat`, `exception`

## 1. 전송과 프레임

- TCP 연결 하나를 쓴다. 누가 접속을 걸었는지와 상관없이 연결 이후의 규칙은 같다.
- 메시지 하나는 프레임 하나다. 프레임은 `길이(4바이트, 부호 없는 빅 엔디언) + 본문`이다.
- 본문은 UTF-8로 인코딩한 JSON 객체 하나다. 길이는 본문의 바이트 수이고 길이 필드 자신은 세지 않는다.
- 본문은 최대 16 MiB(16,777,216바이트)다. 더 큰 길이를 받으면 본문을 읽지 않고 연결을 끊는다.

접속 (1차 구현: 에이전트가 기다리고 러너가 접속)

- 에이전트는 게임이 시작되면 `127.0.0.1`의 포트 `27275`에서 기다린다. 그 기기 안에서 오는 접속만 받는다.
- 포트를 바꾸려면 게임의 실행 인자 `-qaplay-port N`, 그다음으로 환경 변수 `QAPLAY_PORT`를 쓴다.
- 러너는 항상 자기 쪽 localhost 포트로 접속한다. 에디터와 PC 빌드는 그 포트 그대로, Android는 `adb forward`로 기기의 포트에 이어 준다.
- 에이전트는 연결을 한 번에 하나만 받는다. 두 번째 접속은 첫 연결이 끊길 때까지 처리되지 않는다.
- 포트를 열지 못해도(다른 게임이나 에디터가 쓰는 중) 게임은 그대로 돈다. 에이전트는 경고만 남긴다.

예: 본문이 아래 34바이트이면 프레임은 `00 00 00 22` 뒤에 본문 34바이트가 온다.

```json
{"type":"req","id":1,"cmd":"dump"}
```

## 2. 메시지

메시지는 세 종류이고 `type`으로 구분한다.

| type | 방향 | 뜻 |
|---|---|---|
| `req` | 러너 → 에이전트 | 요청 |
| `res` | 에이전트 → 러너 | 요청 하나에 대한 응답 |
| `evt` | 에이전트 → 러너 | 요청과 무관하게 수시로 보내는 이벤트 |

요청

```json
{"type":"req","id":7,"cmd":"tap","args":{"selector":{"name":"BuyButton"}}}
```

| 필드 | 필수 | 뜻 |
|---|---|---|
| `id` | 예 | 양의 정수. 한 연결 안에서 요청마다 다르게 러너가 정한다 |
| `cmd` | 예 | 명령 이름 |
| `args` | 아니오 | 명령 인자. 생략하면 `{}` |

성공 응답과 실패 응답

```json
{"type":"res","id":7,"ok":true,"result":{"point":{"x":0.5,"y":0.845}}}
```

```json
{"type":"res","id":7,"ok":false,"error":{"code":"not_found","message":"no element matched the selector"}}
```

| 필드 | 뜻 |
|---|---|
| `id` | 요청의 `id`를 그대로 돌려준다 |
| `ok` | `true`이면 `result`가, `false`이면 `error`가 있다 |
| `result` | 명령별 결과 객체 |
| `error.code` | 오류 코드(3절). 프로그램은 이 값으로만 분기한다 |
| `error.message` | 사람이 읽는 영어 설명. 로그용이며 문구는 바뀔 수 있다 |
| `error.data` | 오류별 추가 정보. 없을 수 있다 |

이벤트

```json
{"type":"evt","name":"heartbeat","data":{"frame":18422}}
```

공통 규칙

- 에이전트는 요청을 받은 순서대로 하나씩 처리하고, 응답도 그 순서로 보낸다. 러너는 응답을 기다리지 않고 다음 요청을 미리 보내도 된다.
- 이벤트는 응답 사이 어디에나 끼어들 수 있다.
- 모르는 필드와 모르는 이벤트 이름은 무시한다. 오류로 처리하지 않는다.
- 숫자: 시간은 밀리초 정수, 좌표는 실수.
- JSON으로 해석할 수 없거나 `type`이 없는 프레임을 받으면 연결을 끊는다. `id`를 읽을 수 있는 잘못된 요청에는 `bad_request`로 응답한다.

## 3. 오류 코드

| code | 뜻 | `error.data` |
|---|---|---|
| `bad_request` | 인자가 없거나 형식이 틀림 | |
| `unknown_command` | 에이전트가 모르는 명령 | |
| `hello_required` | `hello` 전에 다른 명령을 보냄 | |
| `unsupported_protocol` | 양쪽이 함께 지원하는 버전이 없음 | `{"min":1,"max":1}` 에이전트 지원 범위 |
| `unauthorized` | 토큰이 필요한데 없거나 틀림 | |
| `not_found` | 셀렉터에 걸리는 요소가 없음 | |
| `ambiguous` | 하나여야 하는 셀렉터에 여러 요소가 걸림 | `{"count":3,"matches":[...]}` 요소 최대 10개 |
| `not_tappable` | 요소는 있으나 누를 수 없음 | `{"reason":"..."}` (`tap` 참고) |
| `timeout` | `wait`가 제한 시간 안에 조건을 만족하지 못함 | `{"count":0}` 마지막에 걸린 요소 수 |
| `unknown_key` | 등록되지 않은 게임 값 | `{"keys":["..."]}` 없는 키 |
| `internal` | 에이전트 내부 오류 | |

## 4. UI 요소

좌표

- 화면 왼쪽 위가 `(0, 0)`, 오른쪽 아래가 `(1, 1)`인 정규화 좌표를 쓴다. y는 아래로 갈수록 커진다.
- 픽셀 좌표는 주고받지 않는다. 필요하면 `screen` 크기를 곱해서 구한다.
- 화면 밖으로 나간 요소는 0보다 작거나 1보다 큰 값을 가질 수 있다.

요소(노드)

uGUI와 UI Toolkit 어댑터가 같은 형식으로 출력한다. 활성 상태인 요소만 존재하는 것으로 본다. 비활성 요소는 `dump`에도 나오지 않고 셀렉터에도 걸리지 않는다.

```json
{
  "name": "BuyButton",
  "type": "Button",
  "id": "shop.buy",
  "text": "구매",
  "rect": {"x": 0.35, "y": 0.82, "w": 0.3, "h": 0.05},
  "visible": true,
  "interactable": true,
  "children": []
}
```

| 필드 | 뜻 |
|---|---|
| `name` | 요소 이름 (uGUI는 GameObject 이름, UI Toolkit은 `name`) |
| `type` | 요소 종류. 아래 공통 종류 중 하나 |
| `id` | 프로젝트가 붙인 고유 ID. 붙이지 않았으면 필드가 없다 (uGUI는 요소에 `QaplayId` 컴포넌트를 붙인다) |
| `text` | 요소가 보여 주는 글자. 없으면 필드가 없다 |
| `rect` | 화면에서 차지하는 영역(정규화 좌표). `x`, `y`는 왼쪽 위 꼭짓점 |
| `visible` | 화면에 그려지고 있고 `rect`가 화면과 겹침. 스크롤 영역 같은 마스크에 완전히 잘린 요소는 `false`다. 다른 요소에 가려졌는지는 따지지 않는다 |
| `interactable` | 입력을 받는 요소이고 비활성화(disabled) 상태가 아님 |
| `children` | 자식 요소 목록. 문서 순서 |

- 공통 종류: `Button`, `Toggle`, `Slider`, `InputField`, `Dropdown`, `ScrollView`, `Text`, `Image`, `Node`(그 밖의 모든 요소)
- uGUI의 구형 `Text`와 TextMesh Pro는 구분하지 않고 같은 종류로 나온다.
- `text`는 요소에 설정된 원문이다. 서식 태그(`<color=...>` 등)가 들어 있으면 태그까지 포함한다.
- `text` 규칙: 자신의 글자가 없는 조작 요소(`Button`, `Toggle`, `Dropdown`)는 자손 `Text`의 글자를 문서 순서대로 공백으로 이어 붙인 값을 가진다. uGUI의 버튼(글자가 자식에 있음)과 UI Toolkit의 버튼(글자가 자신에게 있음)을 같은 셀렉터로 찾기 위한 규칙이다.

## 5. 셀렉터

요소를 조건으로 찾는 JSON 객체다. 적은 조건을 모두 만족하는 요소가 걸린다. 조건이 하나도 없는 셀렉터는 `bad_request`다.

```json
{"type": "Button", "text": "구매", "within": {"name": "ShopPopup"}, "index": 0}
```

| 조건 | 뜻 |
|---|---|
| `name` | 이름이 정확히 일치 |
| `type` | 종류가 정확히 일치 |
| `text` | 글자가 정확히 일치 |
| `id` | 고유 ID가 정확히 일치 |
| `within` | 이 셀렉터에 걸리는 요소의 자손만 대상으로 함. `within` 셀렉터는 요소 하나에만 걸려야 한다 |
| `index` | 위 조건에 걸린 요소들 중 문서 순서로 몇 번째인지(0부터). 다른 조건을 모두 적용한 뒤 마지막에 적용한다 |

- 문서 순서: `dump`의 트리를 깊이 우선으로 앞에서부터 방문하는 순서
- 하나여야 하는 자리(`tap`의 대상, `within`)에서 걸린 요소가 없으면 `not_found`, 둘 이상이면 `ambiguous`다. 아무거나 하나를 고르지 않는다.
- 예외: 걸린 요소들이 한 줄로 이어진 조상과 자손이면 가장 바깥 요소 하나로 정한다. 버튼은 자손의 글자를 가지므로(4절) `{"text": "구매"}`에는 버튼과 그 안의 `Text`가 함께 걸리는데, 이때 버튼으로 정해진다. 걸린 요소 중 서로 조상과 자손이 아닌 쌍이 하나라도 있으면 `ambiguous`다. `find`는 이 규칙과 무관하게 걸린 요소를 모두 돌려준다.
- 목록 아이템처럼 의도된 중복은 `within`이나 `index`로 구분한다.

## 6. 명령

### hello

연결 후 러너가 가장 먼저 보낸다. 버전을 맞추고 에이전트 정보를 받는다.

```json
{"type":"req","id":1,"cmd":"hello","args":{
  "protocol": {"min": 1, "max": 1},
  "client": {"name": "qaplay-runner", "version": "0.1.0"}
}}
```

| 인자 | 필수 | 뜻 |
|---|---|---|
| `protocol.min`, `protocol.max` | 예 | 러너가 지원하는 프로토콜 버전 범위 |
| `client` | 아니오 | 러너 이름과 버전. 로그용 |
| `token` | 아니오 | 에이전트가 토큰을 요구하도록 설정된 경우에만 필요 |

```json
{"type":"res","id":1,"ok":true,"result":{
  "protocol": 1,
  "agent": {"version": "0.1.0"},
  "app": {"id": "com.example.game", "version": "1.4.2"},
  "unity": "6000.3.0f1",
  "platform": "Android",
  "screen": {"width": 1080, "height": 2400},
  "ui": ["ugui"],
  "heartbeatMs": 1000,
  "commands": ["hello", "dump", "find", "tap", "wait", "get", "seed"]
}}
```

| 결과 | 뜻 |
|---|---|
| `protocol` | 이 연결에서 쓸 버전. 양쪽 범위가 겹치는 값 중 가장 큰 값 |
| `agent.version` | 에이전트 패키지 버전 |
| `app` | 게임의 식별자와 버전 |
| `unity` | Unity 버전 문자열 |
| `platform` | Unity의 `Application.platform` 값 (`WindowsEditor`, `Android`, `IPhonePlayer` 등) |
| `screen` | 화면 크기(픽셀) |
| `ui` | 활성화된 UI 어댑터 (`ugui`, `uitk`) |
| `heartbeatMs` | `heartbeat` 이벤트 주기 |
| `commands` | 에이전트가 지원하는 명령 목록 |

오류: `unsupported_protocol`, `unauthorized`

### dump

현재 화면의 UI 트리 전체를 돌려준다. 인자는 없다.

```json
{"type":"res","id":2,"ok":true,"result":{
  "screen": {"width": 1080, "height": 2400},
  "roots": [
    {"name": "Canvas", "type": "Node", "rect": {"x": 0, "y": 0, "w": 1, "h": 1},
     "visible": true, "interactable": false, "children": [
      {"name": "GoldLabel", "type": "Text", "text": "1,200",
       "rect": {"x": 0.7, "y": 0.02, "w": 0.25, "h": 0.03},
       "visible": true, "interactable": false, "children": []},
      {"name": "BuyButton", "type": "Button", "id": "shop.buy", "text": "구매",
       "rect": {"x": 0.35, "y": 0.82, "w": 0.3, "h": 0.05},
       "visible": true, "interactable": true, "children": []}
    ]}
  ]
}}
```

### find

셀렉터에 걸리는 요소를 모두 돌려준다. 0개나 여러 개여도 오류가 아니다. 규칙 검증과 화면 탐색에 쓴다.

```json
{"type":"req","id":3,"cmd":"find","args":{"selector":{"type":"Button","text":"구매"}}}
```

```json
{"type":"res","id":3,"ok":true,"result":{"matches":[
  {"name": "BuyButton", "type": "Button", "id": "shop.buy", "text": "구매",
   "rect": {"x": 0.35, "y": 0.82, "w": 0.3, "h": 0.05},
   "visible": true, "interactable": true,
   "path": "Canvas/BuyButton"}
], "resolves": "one"}}
```

- 결과의 요소에는 `children`이 없고 `path`가 있다. `path`는 사람이 위치를 알아보기 위한 표시용이며 셀렉터로 쓸 수 없다.
- `resolves`는 이 셀렉터를 요소 하나가 필요한 자리(`tap` 등)에 쓰면 어떻게 되는지다: `none`(`not_found`가 됨), `one`, `many`(`ambiguous`가 됨). 버튼과 그 안의 글자처럼 한 줄로 이어진 조상과 자손이 함께 걸리면 `matches`는 여러 개여도 `one`이다(5절).

오류: `bad_request`, `within`이 하나로 정해지지 않으면 `not_found` 또는 `ambiguous`

### tap

요소 하나를 가상 터치로 누른다. 셀렉터는 요소 하나에만 걸려야 한다.

```json
{"type":"req","id":4,"cmd":"tap","args":{"selector":{"id":"shop.buy"},"timeoutMs":10000}}
```

```json
{"type":"res","id":4,"ok":true,"result":{"point":{"x":0.5,"y":0.845}}}
```

| 인자 | 필수 | 뜻 |
|---|---|---|
| `selector` | 예 | 누를 대상 |
| `timeoutMs` | 아니오 | 대상이 나타나고 누를 수 있게 될 때까지 기다리는 제한 시간. 생략하면 `0`(기다리지 않음). 기본으로 넣을 값은 `wait`처럼 러너와 클라이언트가 정한다 |

동작

1. 셀렉터로 요소 하나를 정한다.
2. `rect`의 중심점을 누를 위치로 정한다.
3. 누를 수 있는지 검사한다.
4. 1~3에서 요소가 없거나(`not_found`) 누를 수 없으면(`not_tappable`) 터치를 넣지 않고, `timeoutMs`가 지날 때까지 매 프레임 1부터 다시 한다. 제한 시간이 지나면 마지막 검사의 오류로 응답한다(`timeout`이 아니다).
5. 누를 수 있으면 그 위치에 터치 누름과 뗌을 넣는다. 뗌이 처리된 프레임이 끝나면 응답한다.

- 기다리는 덕분에 열리는 중인 팝업의 버튼이나 로딩이 끝나야 켜지는 버튼을 `wait` 없이 누를 수 있다.
- `ambiguous`와 `bad_request`는 기다려도 달라지지 않는 요청 쪽 문제로 보고, 기다리지 않고 바로 응답한다.
- 응답은 "터치를 넣었다"는 뜻이다. 눌린 결과(팝업 열림 등)는 `wait`로 확인한다.
- 버튼의 클릭 처리 함수를 직접 부르지 않는다. 항상 실제 터치와 같은 입력 경로를 거친다.

`not_tappable`의 `reason`. 표의 위에서부터 차례로 검사하고 처음 걸린 이유를 돌려준다.

| reason | 뜻 | 추가 data |
|---|---|---|
| `off_screen` | 중심점이 화면 밖 | |
| `not_visible` | 대상의 `visible`이 `false` (투명도 0 등) | |
| `not_interactable` | 대상의 `interactable`이 `false` | |
| `occluded` | 중심점에서 입력을 받는 맨 위 요소가 대상, 대상의 자손, 대상의 조상 중 어느 것도 아님. 그 자리에서 입력을 받는 요소가 아예 없는 경우도 포함한다 | `by`: 가린 요소 |

- 제한: 중심점 한 곳만 검사한다. 중심은 가려졌지만 가장자리는 보이는 요소도 `occluded`가 된다. 스크롤 영역에 일부만 걸쳐 중심이 잘린 아이템도 마찬가지다.
- `rect`는 마스크에 잘리기 전의 전체 영역이다. 일부만 보이는 요소의 중심점은 보이지 않는 부분에 있을 수 있다.

오류: `not_found`, `ambiguous`, `not_tappable`

### wait

조건이 만족될 때까지 기다린다. 에이전트가 매 프레임 검사하므로 러너가 `find`를 반복해 부르는 것보다 짧게 지나가는 상태를 놓치지 않는다.

```json
{"type":"req","id":5,"cmd":"wait","args":{
  "selector": {"name": "GoldLabel", "text": "1,100"},
  "state": "visible",
  "timeoutMs": 5000
}}
```

```json
{"type":"res","id":5,"ok":true,"result":{"elapsedMs":320}}
```

| 인자 | 필수 | 뜻 |
|---|---|---|
| `selector` | 예 | 기다릴 대상 |
| `state` | 예 | `visible`: 걸리는 요소 중 `visible`인 것이 하나 이상 있음 / `gone`: 걸리는 요소 중 `visible`인 것이 없음 |
| `timeoutMs` | 예 | 제한 시간. 기본값은 프로토콜이 아니라 러너와 클라이언트가 정한다 |

- 요청을 받은 시점에 이미 조건이 만족되어 있으면 바로 응답한다.
- 글자가 바뀌기를 기다릴 때는 셀렉터에 `text`를 넣고 `visible`로 기다린다.
- `within`이 가리키는 요소가 없으면 오류가 아니라 걸리는 요소가 0개인 것으로 본다. 아직 열리지 않은 팝업 안의 버튼을 기다릴 수 있다. `within`에 여러 요소가 걸리면 기다리지 않고 `ambiguous`로 응답한다.
- `timeout`의 `count`는 마지막 검사에서 셀렉터에 걸린 요소 수다. `visible` 여부와 무관하게 센다. `visible`을 기다리다 `count`가 1 이상으로 끝났다면 요소는 있지만 보이지 않는 것이다.
- `wait` 중에는 다음 요청이 처리되지 않는다(2절 공통 규칙).

오류: `timeout`, `bad_request`, `ambiguous`(`within`)

### get

프로젝트가 등록한 게임 값(재화 등)을 읽는다.

```json
{"type":"req","id":6,"cmd":"get","args":{"keys":["gold"]}}
```

```json
{"type":"res","id":6,"ok":true,"result":{"values":{"gold":1100}}}
```

- `keys`를 생략하면 등록된 값을 모두 돌려준다. 어떤 값을 읽을 수 있는지 조회할 때 쓴다.
- 값은 JSON 숫자, 문자열, 불리언, `null` 중 하나다.

오류: `unknown_key`

### seed

Unity 기본 랜덤(`UnityEngine.Random`)의 시드를 정한다. 테스트를 시작할 때 보내면, 같은 조작에 같은 난수가 나와 결과가 매번 같아진다.

```json
{"type":"req","id":7,"cmd":"seed","args":{"seed":12345}}
```

```json
{"type":"res","id":7,"ok":true,"result":{}}
```

| 인자 | 필수 | 뜻 |
|---|---|---|
| `seed` | 예 | 시드. 32비트 정수 |

- 도구가 맞추는 것은 `UnityEngine.Random` 하나다. 게임이 `System.Random`이나 서버에서 받은 난수를 쓰는 부분은 프로젝트가 따로 맞춘다.
- 이 명령 뒤에 게임 코드가 시드를 다시 정하면 그 값이 쓰인다.

오류: `bad_request`

## 7. 이벤트

### heartbeat

에이전트가 살아 있음을 알린다. `hello` 응답을 보낸 뒤부터 `heartbeatMs`마다 보낸다.

```json
{"type":"evt","name":"heartbeat","data":{"frame":18422}}
```

- `frame`: 메인 스레드가 마지막으로 끝낸 프레임 번호
- 메인 스레드가 아닌 별도 스레드에서 보낸다. 메인 스레드가 멈춰도 이벤트는 계속 오고 `frame`만 멈춘다.
- 러너의 판정: `frame`이 그대로면 프리징, 연결이 끊기면 크래시. 몇 초를 프리징으로 볼지는 러너 설정이다(기본 10초).

### exception

게임에서 처리되지 않은 C# 예외가 났음을 알린다. 앱은 살아 있다. `hello` 응답을 보낸 뒤부터 보낸다.

```json
{"type":"evt","name":"exception","data":{
  "message": "InvalidOperationException: sample exception from ExceptionButton",
  "stack": "SampleShop+<>c.<Build>b__1_1 () (at Assets/Sample/SampleShop.cs:97)\n..."
}}
```

- `message`: 예외 종류와 설명. `stack`: Unity가 로그에 남기는 스택 그대로. 없으면 빈 문자열이다.
- 예외가 로그에 남는 그 순간에 보낸다. 명령(`tap` 등)이 일으킨 예외는 그 명령의 응답보다 먼저 도착한다.
- 명령과 무관하게 나중에 난 예외도 온다. 러너는 테스트를 끝내기 전에 아무 요청이나 하나 보내고 그 응답까지 읽어, 그 앞에 보낸 이벤트를 모두 받는다(예: `keys`가 빈 `get`).
- 오류 로그(`Debug.LogError`)는 보내지 않는다. 예외만 보낸다.

## 8. 버전 규칙

- 명령이나 이벤트, 선택 필드를 추가하는 변경은 버전을 올리지 않는다. 러너는 `hello`의 `commands`로 지원 여부를 확인한다.
- 기존 명령의 뜻이나 필수 필드를 바꾸는 변경만 프로토콜 버전을 올린다.

## 9. 예시: 탭 한 번

"구매 버튼을 누르면 골드가 1,200에서 1,100이 된다"를 확인하는 흐름이다. `→`는 러너가 보내는 메시지, `←`는 에이전트가 보내는 메시지다.

```
→ {"type":"req","id":1,"cmd":"hello","args":{"protocol":{"min":1,"max":1}}}
← {"type":"res","id":1,"ok":true,"result":{"protocol":1,"agent":{"version":"0.1.0"},"app":{"id":"com.example.game","version":"1.4.2"},"unity":"6000.3.0f1","platform":"Android","screen":{"width":1080,"height":2400},"ui":["ugui"],"heartbeatMs":1000,"commands":["hello","dump","find","tap","wait","get","seed"]}}
→ {"type":"req","id":2,"cmd":"get","args":{"keys":["gold"]}}
← {"type":"res","id":2,"ok":true,"result":{"values":{"gold":1200}}}
← {"type":"evt","name":"heartbeat","data":{"frame":18422}}
→ {"type":"req","id":3,"cmd":"tap","args":{"selector":{"id":"shop.buy"}}}
← {"type":"res","id":3,"ok":true,"result":{"point":{"x":0.5,"y":0.845}}}
→ {"type":"req","id":4,"cmd":"wait","args":{"selector":{"name":"GoldLabel","text":"1,100"},"state":"visible","timeoutMs":5000}}
← {"type":"res","id":4,"ok":true,"result":{"elapsedMs":320}}
→ {"type":"req","id":5,"cmd":"get","args":{"keys":["gold"]}}
← {"type":"res","id":5,"ok":true,"result":{"values":{"gold":1100}}}
```

## 10. v0에 없는 것

필요한 단계에서 추가한다.

- 입력: 길게 누르기, 드래그, 스와이프, 멀티 터치, 텍스트 입력, 좌표 직접 탭
- 셀렉터: 부분 일치와 정규식, 프로젝트 컴포넌트 이름으로 찾기
- 대기: 프로젝트가 등록한 대기 조건 (로딩 종료 등)
- 이벤트: 로그, 예외, 입력 기록, 측정 샘플
- 그 밖: 스크린샷, 결정성 신호(랜덤 고정, 시간 설정, 계정 초기화), 큰 바이너리 전송
