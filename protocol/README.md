# qaplay 프로토콜 v0 (초안)

러너와 에이전트가 주고받는 메시지의 기준 문서다. 에이전트(C#), 러너(C#), Python 클라이언트는 이 문서만 보고 구현한다. 문서에 없는 동작이 필요하면 문서를 먼저 고친다.

- 프로토콜 버전: `1`
- 상태: 초안. 구현하면서 고칠 수 있다. 3단계까지 구현한 내용과 맞춰져 있다
- 명령: `hello`, `dump`, `find`, `tap`, `wait`, `get`, `seed`, `report`, `reports`, `reportGet`, `reportDelete`
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
- 포트를 열지 못해도(다른 게임이나 에디터가 쓰는 중) 게임은 그대로 돈다. 에이전트는 경고를 남기고, 포트가 빌 때까지 2초마다 다시 시도한다.
- 에이전트가 떠 있는 동안 게임은 창에 포커스가 없어도 계속 돈다(러너는 다른 창에서 돌기 때문이다). 에디터에서는 Play가 끝나면 원래 설정으로 돌아간다.

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
| `unknown_mock` | 등록되지 않은 Mock 이름이거나 그 Mock에 없는 시나리오 | `{"keys":["..."]}` 문제가 된 Mock 이름 |
| `unknown_report` | 그 ID의 제보가 게임 안에 없음 | |
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
| `count` | 아니오 | 연타: 같은 자리를 연달아 누르는 횟수. 1~100. 생략하면 `1` |
| `intervalMs` | 아니오 | 연타에서 뗀 뒤 다음 누름까지 더 기다리는 시간. 0~10000. 생략하면 `0`(가장 빠르게) |

동작

1. 셀렉터로 요소 하나를 정한다.
2. `rect`의 중심점을 누를 위치로 정한다.
3. 누를 수 있는지 검사한다.
4. 1~3에서 요소가 없거나(`not_found`) 누를 수 없으면(`not_tappable`) 터치를 넣지 않고, `timeoutMs`가 지날 때까지 매 프레임 1부터 다시 한다. 제한 시간이 지나면 마지막 검사의 오류로 응답한다(`timeout`이 아니다).
5. 누를 수 있으면 그 위치에 터치 누름과 뗌을 넣는다. 네 프레임이 걸린다: 마우스와 펜을 막고 한 프레임, 누름, 뗌, 그리고 뗌이 처리된 다음 프레임. 그 뒤에 응답한다.
   - 그 네 프레임 동안 마우스와 펜에서 오는 입력은 버려진다. 에디터나 PC 빌드에서 사람이 마우스를 쓰는 중에도 탭이 그대로 전달되고, 마우스 버튼 때문에 탭한 요소가 한 번 더 눌리지 않게 하기 위해서다.
   - 탭이 끝나면 마우스와 펜의 위치를 게임에 한 번 다시 알린다(1픽셀 옆으로 갔다가 제자리로). 터치가 들어오면 UI가 마우스의 위치를 잊어서, 그대로 두면 사람이 마우스를 움직이지 않고 누른 다음 클릭이 커서 아래가 아니라 화면 왼쪽 아래 구석에서 처리되기 때문이다.

6. `count`가 2 이상이면 5의 누름과 뗌을 그만큼 되풀이한다. 한 번 더 누를 때마다 두 프레임(누름, 뗌)이 더 걸리고, `intervalMs`가 있으면 그만큼 더 기다린다.
   - 누를 자리는 처음에 한 번만 정한다. 첫 클릭으로 대상이 꺼지거나 가려지거나 사라져도 같은 자리를 계속 누른다. 사람의 연타가 그렇고, 그때 무슨 일이 생기는지가 확인하려는 것이다.
   - 클릭은 빨라야 두 프레임에 한 번이다. 한 프레임 안에 닫히는 틈은 연타로 잡지 못한다. 같은 프레임의 클릭 두 번은 `tapTogether`로 넣는다.
   - 마우스와 펜은 연타가 끝날 때까지 막힌다.
   - 러너와 클라이언트는 응답을 `timeoutMs`에 더해 `count × (intervalMs + 100)`ms만큼 더 기다린다.

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

오류: `not_found`, `ambiguous`, `not_tappable`, `bad_request`(`count`나 `intervalMs`가 범위 밖)

### tapTogether

여러 요소를 같은 순간에 누른다(동시 클릭). 확인과 닫기처럼 함께 눌리면 안 되는 버튼들이 같은 프레임에 눌렸을 때 상태가 꼬이는지 보는 데 쓴다.

```json
{"type":"req","id":5,"cmd":"tapTogether","args":{"selectors":[{"name":"ConfirmButton"},{"name":"CancelButton"}],"timeoutMs":10000}}
```

```json
{"type":"res","id":5,"ok":true,"result":{"points":[{"x":0.3,"y":0.53},{"x":0.7,"y":0.53}]}}
```

| 인자 | 필수 | 뜻 |
|---|---|---|
| `selectors` | 예 | 누를 대상들. 2~10개. 각각 요소 하나에만 걸려야 한다 |
| `timeoutMs` | 아니오 | 대상이 모두 누를 수 있게 될 때까지 기다리는 제한 시간. 생략하면 `0` |

- 대상마다 `tap`의 1~3과 같은 검사를 한다. 같은 프레임에 모두 누를 수 있어야 누른다. 하나라도 안 되면 아무것도 누르지 않고, `tap`처럼 `timeoutMs` 안에서 매 프레임 다시 본다.
- 터치를 대상 수만큼 같은 프레임에 누르고, 다음 프레임에 함께 뗀다. 클릭은 `selectors`에 적은 순서대로 같은 프레임 안에서 처리된다.
- 걸리는 프레임 수와 마우스, 펜을 막는 것은 `tap`과 같다.
- `points`는 `selectors`와 같은 순서다.
- 같은 요소를 두 번 적으면 그 요소가 같은 프레임에 두 번 눌린다(두 손가락으로 누른 것과 같다).

오류: `not_found`, `ambiguous`, `not_tappable`, `bad_request`(`selectors`가 2~10개가 아님)

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

### memory

게임 프로세스가 지금 쓰고 있는 메모리를 세 계층으로 읽는다. 값은 바이트다.

```json
{"type":"req","id":7,"cmd":"memory"}
```

```json
{"type":"res","id":7,"ok":true,"result":{"managed":18432000,"native":95236096,"process":412090368}}
```

| 필드 | 뜻 |
|---|---|
| `managed` | C# 힙에서 쓰고 있는 크기(GC 대상) |
| `native` | Unity가 네이티브 쪽에서 할당한 크기(텍스처, 메시, 오디오 등) |
| `process` | OS가 본 프로세스 전체의 메모리(지금 실제 메모리에 올라와 있는 크기: Windows의 작업 집합, Android의 RSS). 네이티브 플러그인이 쓴 것도 들어 있다 |

- `process`는 다른 프로세스와 함께 쓰는 라이브러리도 통째로 센다. 그래서 Android의 PSS보다 크다(에뮬레이터에서 RSS 281MB일 때 PSS 150MB). 아무것도 하지 않아도 잴 때마다 조금씩 달라진다.
- 재기 전에 쓰레기 수집을 한 번 한다. 곧 치워질 쓰레기는 쓰고 있는 메모리로 세지 않는다. 그래서 응답까지 한 프레임이 걸리고, 그 순간 게임이 잠깐 끊길 수 있다.
- 쓰지 않는 에셋을 내리지는 않는다(`Resources.UnloadUnusedAssets`를 부르지 않는다). 없애지 않고 버린 텍스처 같은 누수가 숫자에 그대로 남아야 한다.
- 그 플랫폼에서 읽을 수 없는 값은 `null`이다.
- 에디터에서는 세 값 모두 에디터 자신이 쓰는 메모리를 포함한다.

### frames

프레임 시간(프레임 하나가 걸린 시간)을 잰 결과를 돌려주고, 그 순간부터 새로 재기 시작한다. 결과는 바로 앞의 `frames` 요청부터 이번 요청까지의 프레임이다.

```json
{"type":"req","id":8,"cmd":"frames","args":{"hitchMs":50}}
```

```json
{"type":"res","id":8,"ok":true,"result":{"count":600,"seconds":10.02,"averageMs":16.7,"p99Ms":21.3,"maxMs":48.1,"hitches":0,"hitchMs":50}}
```

| 인자 | 필수 | 뜻 |
|---|---|---|
| `hitchMs` | 아니오 | 이 시간보다 오래 걸린 프레임을 끊김으로 센다. 1~10000. 생략하면 `50` |

| 결과 | 뜻 |
|---|---|
| `count` | 잰 프레임 수 |
| `seconds` | 잰 프레임 시간의 합 |
| `averageMs` | 평균 프레임 시간 |
| `p99Ms` | 느린 쪽 1%의 경계(99번째 백분위). 프레임 100개 중 가장 느린 하나가 이 값이다 |
| `maxMs` | 가장 오래 걸린 프레임 |
| `hitches` | `hitchMs`보다 오래 걸린 프레임 수 |
| `hitchMs` | 끊김을 센 기준 |

- 처음 보낸 `frames`는 재기를 시작하는 요청이다. 잰 것이 없으므로 `count`는 `0`이고 `averageMs`, `p99Ms`, `maxMs`는 `null`이다.
- 재는 것은 게임 시간이 아니라 실제 시간이다(시간 배율과 일시 정지의 영향을 받지 않는다).
- 최근 36000프레임(60fps로 10분)까지만 담는다. 그보다 길게 재면 오래된 프레임부터 빠진다.
- `memory` 요청은 쓰레기 수집을 하므로 그 프레임이 끊김으로 잡힐 수 있다.

### mock

프로젝트가 등록한 Mock(결제, 로그인, 광고처럼 게임 밖의 화면을 거치는 SDK 호출을 대신하는 것)이 어떻게 응답할지 고른다. 등록된 Mock과 지금 고른 시나리오를 돌려준다.

```json
{"type":"req","id":9,"cmd":"mock","args":{"set":{"store":"cancel"}}}
```

```json
{"type":"res","id":9,"ok":true,"result":{"mocks":{"store":{"scenario":"cancel","scenarios":["success","fail","cancel","slow","duplicate"]}}}}
```

| 인자 | 필수 | 뜻 |
|---|---|---|
| `set` | 아니오 | Mock 이름과 고를 시나리오. 여러 개를 한 번에 적을 수 있다 |
| `reset` | 아니오 | `true`이면 모든 Mock을 기본 시나리오(등록할 때의 첫 번째)로 돌린다. `set`과 함께 주면 돌린 뒤에 고른다 |

- 인자 없이 보내면 바꾸지 않고 지금 상태만 돌려준다. 어떤 Mock이 있는지 조회할 때 쓴다.
- 고른 시나리오는 다시 고르거나 `reset`할 때까지 그대로다. 게임을 다시 시작하면 기본값이다.
- 시나리오의 이름과 뜻은 프로젝트가 정한다(`Mocks.Register`). 도구는 고른 이름을 Mock에 전해 줄 뿐이다.
- `set`에 문제가 하나라도 있으면 아무것도 바꾸지 않는다.

오류: `unknown_mock`

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

### report, reports, reportGet, reportDelete

버그 제보를 만들고, 게임 안에 쌓인 제보를 읽고 지운다. 제보는 테스터가 게임에서 직접 걸 수도 있다(PC는 F12, 기기는 세 손가락으로 1초 누르기). 어느 쪽이든 게임 안의 폴더에 저장되고, 러너가 이 명령들로 가져간다.

| 명령 | 인자 | 결과 |
|---|---|---|
| `report` | `title`, `description` (둘 다 생략 가능) | `{"id": "20261125-131649-7ca2"}` 지금 화면으로 제보를 저장한다 |
| `reports` | 없음 | `{"reports": [ ... ]}` 쌓인 제보의 정보. 오래된 것부터 |
| `reportGet` | `id` | 제보 하나의 내용 전체(아래) |
| `reportDelete` | `id` | `{}` 가져간 제보를 게임에서 지운다 |

```json
{"type":"res","id":9,"ok":true,"result":{
  "report": {
    "id": "20261125-131649-7ca2", "title": "Report 2026-11-25 22:16:49", "description": "",
    "createdAt": "2026-11-25T13:16:49Z",
    "app": {"id": "com.example.game", "version": "1.4.2"}, "unity": "6000.3.29f1", "platform": "Android",
    "device": {"model": "SM-G991N", "os": "Android OS 14"}, "screen": {"width": 1080, "height": 2400}
  },
  "steps": "# qaplay report ...\n- tapOn: { id: shop.buy }\n",
  "log": "[Log] ...",
  "screenshot": "iVBORw0KGgo..."
}}
```

- `steps`: 제보 직전의 조작. [규칙 파일](../docs/rules.md)의 명령 목록 형식이라 그대로 재생할 수 있다. 에이전트는 Play 동안 늘 조작을 듣고 최근 5분(최대 200개)을 남긴다.
- `log`: 최근 로그 200줄. 경고가 아닌 문제 로그에는 스택이 붙는다.
- `screenshot`: 제보한 순간의 화면(PNG를 base64로). 화면을 그리지 않는 실행(배치 모드)에서는 없다.
- 제목을 주지 않으면 날짜로 된 제목이 붙는다. 테스터가 게임에서 건 제보가 그렇다.
- 러너는 `reportGet`으로 받아 저장한 다음에 `reportDelete`를 보낸다.

오류: `bad_request`(`id`가 없거나 제보 ID의 모양이 아님), `unknown_report`

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

- 입력: 길게 누르기, 드래그, 스와이프, 핀치 같은 멀티 터치 동작, 텍스트 입력, 좌표 직접 탭
- 셀렉터: 부분 일치와 정규식, 프로젝트 컴포넌트 이름으로 찾기
- 대기: 프로젝트가 등록한 대기 조건 (로딩 종료 등)
- 이벤트: 로그, 예외, 입력 기록, 측정 샘플
- 그 밖: 스크린샷, 결정성 신호(랜덤 고정, 시간 설정, 계정 초기화), 큰 바이너리 전송
