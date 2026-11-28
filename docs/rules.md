# qaplay 규칙 파일 v0

테스트 하나를 어떻게 실행하고 무엇을 확인할지 적는 YAML 파일의 기준 문서다. 규칙을 읽는 쪽(에디터, 러너)과 쓰는 쪽(사람, AI, 입력 기록)은 이 문서만 보고 구현한다.

- 상태: 확정. 2단계를 구현하면서 고칠 일이 생기면 이 문서를 먼저 고친다
- 명령 이름과 구조는 [Maestro](https://maestro.mobile.dev)를 따른다. 다른 점은 6절에 모았다
- 조건과 반복이 필요한 복잡한 시나리오는 규칙 파일이 아니라 Python 스크립트로 쓴다

## 1. 예

```yaml
# 상점에서 구매하면 골드가 100 줄고 알림이 떴다 사라진다
tc: TC-012
name: 상점 구매
---
- seed: 12345
- tapOn: { id: shop.buy }
- assertVisible: { name: Toast }
- assertVisible: { name: GoldLabel, text: "1,100" }
- assertValue: { gold: 1100 }
- assertNotVisible: { name: Toast }

# 안내 팝업을 열고 닫는다
- tapOn: { name: InfoButton }
- tapOn: { text: 닫기, within: { name: InfoPopup } }
- assertNotVisible: { name: InfoPopup }
```

## 2. 파일 구조

- 파일 하나가 테스트 하나다. 확장자는 `.yaml`, 인코딩은 UTF-8이다.
- 머리말과 명령 목록을 `---`로 나눈다. 머리말이 없으면 `---` 없이 명령 목록만 적는다.
- `#` 뒤는 주석이다. 왜 이렇게 썼는지를 남기는 데 쓴다.

머리말

| 필드 | 필수 | 뜻 |
|---|---|---|
| `tc` | 아니오 | 시트의 TC 번호. 실행 결과를 시트의 그 줄에 적는 데 쓴다 |
| `name` | 아니오 | 사람이 읽는 이름. 리포트에 나온다 |

- 표에 없는 필드는 오류다. 오타를 조용히 넘기지 않는다.

명령 목록

- 목록의 항목 하나가 명령 하나다. 항목은 "명령 이름: 인자" 한 쌍만 가진다.
- 위에서부터 차례로 실행한다. 명령 하나가 실패하면 거기서 멈추고 테스트는 실패다. `leakCheck` 아래의 명령만 여러 번 되풀이한다(4절).
- 모르는 명령 이름은 실행 전에 오류로 알린다. 한 줄도 실행하지 않는다.

읽는 YAML의 범위

YAML 전체가 아니라 이 문서에 나온 모양만 읽는다. 그 밖의 문법은 조용히 넘기지 않고 오류로 알린다.

- 명령 하나는 한 줄에 적는다. 인자가 여럿이면 `{ }` 안에 적고, 같은 줄에서 닫는다.
- 목록 `[ ]`도 한 줄에 적는다. `tapTogether`의 인자로만 쓴다.
- 여러 줄로 들여 쓴 중첩, 앵커(`&`, `*`), 여러 줄 글자(`|`, `>`)는 읽지 않는다.

```yaml
- tapOn: { id: shop.buy, timeout: 30000 }   # 읽는다

- tapOn:                                    # 오류: { } 안에 한 줄로 적는다
    id: shop.buy
```

- 따옴표 없는 `100`, `true`, `null`은 글자가 아니라 숫자, 참거짓, 없음이다. 글자로 찾으려면 따옴표로 감싼다: `tapOn: "100"`
- `{ }`와 `[ ]` 안에서 쉼표나 `]`, `}`가 든 글자, 그리고 `: `이 든 글자는 따옴표로 감싼다: `text: "1,100"`
- 공백 뒤의 `#`부터는 주석이다. `#`이 든 글자도 따옴표로 감싼다.
- 큰따옴표 안에서는 `\"`, `\\`, `\n`, `\t`, `\uXXXX`를 쓸 수 있다. 작은따옴표 안에서는 `''`가 작은따옴표 한 글자다.
- 오류는 처음 만난 것 하나를 "몇 번째 줄: 이유"로 알린다.

## 3. 요소 가리키기 (셀렉터)

[프로토콜 명세 5절](../protocol/README.md)의 셀렉터를 그대로 쓴다. 조건은 `id`, `name`, `type`, `text`, `within`, `index`다.

```yaml
- tapOn: { id: shop.buy }                                # 고유 ID
- tapOn: { name: InfoButton }                            # 이름
- tapOn: { type: Button, text: 방패, within: { name: ItemList } }   # 범위 안에서 글자로
- tapOn: { name: Item, within: { name: ItemList }, index: 1 }       # 범위 안에서 순서로
```

줄여 쓰기: 인자가 글자 하나면 `text` 조건이다. 아래 두 줄은 같다.

```yaml
- tapOn: 구매
- tapOn: { text: 구매 }
```

- 글자는 정확히 일치해야 한다(부분 일치나 정규식이 아니다).
- 셀렉터가 요소 하나로 정해지지 않으면 그 명령은 실패한다. 아무거나 하나를 고르지 않는다.

## 4. 명령

요소를 다루는 명령은 셀렉터와 같은 자리에 `timeout`(밀리초)을 함께 적을 수 있다. 적지 않으면 10000(10초)이다.

```yaml
- tapOn: { name: StartButton, timeout: 30000 }
```

| 명령 | 인자 | 하는 일 | 실패하는 경우 |
|---|---|---|---|
| `tapOn` | 셀렉터 | 요소를 가상 터치로 누른다. 요소가 나타나고 누를 수 있게 될 때까지 `timeout` 안에서 기다린다. `repeat`를 적으면 연타한다(아래) | 시간 안에 누를 수 없음, 여러 요소가 걸림 |
| `tapTogether` | 셀렉터 목록 | 요소 2~10개를 같은 순간에 누른다(아래) | 시간 안에 모두를 누를 수 없음, 여러 요소가 걸림 |
| `assertVisible` | 셀렉터 | 걸리는 요소 중 보이는 것이 하나 이상 생길 때까지 `timeout` 안에서 기다린다 | 시간 안에 보이지 않음 |
| `assertNotVisible` | 셀렉터 | 걸리는 요소 중 보이는 것이 없어질 때까지 `timeout` 안에서 기다린다 | 시간 안에 사라지지 않음 |
| `assertValue` | 키와 기대값 | 게임이 등록한 값을 읽어 기대값과 같은지 확인한다. 여러 개를 한 번에 적을 수 있다 | 값이 다름, 등록되지 않은 키 |
| `assertMemory` | 계층과 예산(MB) | 게임이 지금 쓰는 메모리를 재서 예산 이하인지 확인한다(아래) | 예산을 넘음, 그 플랫폼에서 읽을 수 없는 계층 |
| `assertFrameTime` | 지표와 예산 | 규칙이 시작된 뒤(또는 앞의 `assertFrameTime` 뒤)부터 여기까지의 프레임 시간이 예산 이하인지 확인한다(아래) | 예산을 넘음, 잰 프레임이 없음 |
| `assertDevice` | 지표와 예산 | OS에서만 알 수 있는 기기 지표(CPU 사용률, PSS, 발열 단계, 배터리 온도)가 예산 이하인지 확인한다. 러너로 돌릴 때만 된다(아래) | 예산을 넘음, 읽을 수 없는 지표 |
| `mock` | Mock 이름과 시나리오 | 게임이 등록한 Mock(결제, 로그인, 광고 등)이 다음부터 어떻게 응답할지 고른다(아래) | 등록되지 않은 Mock, 그 Mock에 없는 시나리오 |
| `leakCheck` | 되풀이 횟수 | 이 줄 아래의 명령들을 되풀이하면서 메모리를 재서, 계속 늘면 실패로 본다(아래) | 메모리가 계속 늚 |
| `seed` | 정수 | Unity 기본 랜덤의 시드를 정한다. 보통 맨 처음에 적는다 | |

- 글자가 바뀌었는지는 `assertVisible`에 `text`를 넣어 확인한다: `assertVisible: { name: GoldLabel, text: "1,100" }`
- `assertValue`는 기다리지 않고 그 순간의 값을 본다. 값이 늦게 바뀌는 경우에는 화면의 변화를 `assertVisible`로 먼저 기다린다.
- `assertValue`의 기대값은 숫자, 문자열, `true`/`false`, `null` 중 하나다. 숫자는 값으로 비교한다(`1100`과 `1100.0`은 같다).

입력 스트레스: 연타와 동시 클릭

버튼이 중복 처리를 막는지, 함께 눌리면 안 되는 버튼들이 서로를 막는지 확인한다. 누르기만으로는 판정하지 않는다. 뒤에 `assertValue`나 `assertVisible`로 기대 결과를 적어야 한다. 기대 결과를 적지 않은 버튼은 판정하지 않는다(예외, 크래시, 프리징은 5절대로 따로 잡힌다).

```yaml
# 연타: 선물 받기를 5번 연달아 눌러도 요청은 한 번만 나가야 한다
- tapOn: { id: shop.gift, repeat: 5 }
- assertValue: { gift.requests: 1 }

# 동시 클릭: 확인과 취소를 같은 순간에 눌러도 하나만 처리되어야 한다
- tapTogether: [ { name: ConfirmButton }, { name: CancelButton } ]
- assertNotVisible: { name: SellPopup }
- assertValue: { popups: 0 }
```

| 적는 곳 | 뜻 |
|---|---|
| `tapOn`의 `repeat` | 같은 자리를 연달아 누르는 횟수. 1~100. 적지 않으면 1 |
| `tapOn`의 `delay` | 누름 사이에 더 기다리는 시간(밀리초). 0~10000. 적지 않으면 0(가장 빠르게: 두 프레임에 한 번) |
| `tapTogether`의 목록 | 누를 요소들. 항목은 `{ 조건 }` 또는 글자 하나(`text` 조건의 줄임)다. `timeout`은 적을 수 없고 10초다 |

- 연타는 누를 자리를 처음에 한 번만 정한다. 첫 클릭으로 버튼이 꺼지거나 팝업에 가려져도 같은 자리를 계속 누른다. 사람의 연타와 같다.
- 동시 클릭은 요소가 모두 누를 수 있는 상태가 된 프레임에 한꺼번에 누른다. 하나라도 누를 수 없으면 아무것도 누르지 않는다.
- 값을 바로 확인하려면 누르는 즉시 바뀌는 값(요청 수, 열린 팝업 수)을 게임이 등록해 두는 편이 좋다. 서버 응답 뒤에 바뀌는 값(재화)은 응답을 기다린 뒤에 확인해야 하고, 응답이 몇 번 올지 모르는 상태에서는 기다림이 끝났는지 알기 어렵다.
- 실행해 볼 수 있는 예: 샘플 프로젝트의 `unity/Assets/Sample/Rules/rapid-buy.yaml`(통과), 그리고 `unity/Assets/Sample/BuggyRules/`의 두 파일. 샘플에 일부러 심은 버그를 잡는 규칙이라 실패하는 것이 정상이다.

메모리 예산

그 화면까지 간 뒤의 메모리가 정해 둔 상한을 넘지 않는지 확인한다.

```yaml
- tapOn: { name: InfoButton }
- assertMemory: { managed: 64, process: 1536 }   # C# 힙 64MB 이하, 프로세스 전체 1536MB 이하
```

| 계층 | 뜻 |
|---|---|
| `managed` | C# 힙에서 쓰고 있는 크기(GC 대상) |
| `native` | Unity가 네이티브 쪽에서 할당한 크기(텍스처, 메시, 오디오 등) |
| `process` | OS가 본 프로세스 전체의 메모리(Windows의 작업 집합, Android의 RSS). 네이티브 플러그인이 쓴 것도 들어 있다. 가만히 있어도 잴 때마다 조금씩 달라지므로 예산에 여유를 둔다 |

- 예산은 MB 숫자다. 단위를 붙여 `64MB`로 적으면 오류다. 소수도 된다(`0.5`).
- 적은 계층만 확인한다. 하나만 적어도 된다.
- 재기 전에 쓰레기 수집을 한 번 한다. 곧 치워질 쓰레기는 세지 않으므로 같은 화면에서는 잴 때마다 비슷한 값이 나온다.
- 예산을 정하기 전에 `qaplay memory`로 지금 값을 본다. 값은 플랫폼과 빌드마다 다르다. 에디터에서는 에디터 자신의 메모리가 들어 있어 훨씬 크므로, 예산은 빌드에서 잰 값으로 정한다.
- 그 플랫폼에서 읽을 수 없는 계층에 예산을 적으면 실패다. 확인하지 못한 것을 통과로 두지 않는다.

프레임 시간 예산

동작하는 동안 화면이 버벅이거나 끊기지 않았는지 확인한다.

```yaml
- tapOn: { name: InventoryTab }
- tapOn: { name: SortButton }
- assertFrameTime: { average: 20, p99: 40, hitches: 0 }
```

| 적는 것 | 뜻 |
|---|---|
| `average` | 평균 프레임 시간의 상한(ms). 60fps는 16.7, 30fps는 33.3이다 |
| `p99` | 느린 쪽 1%의 경계(99번째 백분위)의 상한(ms). 가끔 느려지는 프레임을 본다 |
| `hitches` | 끊김(50ms보다 오래 걸린 프레임)이 몇 번까지 괜찮은지. `0`이면 한 번도 안 된다. 30fps 게임은 프레임 하나가 33ms라 조금만 밀려도 50ms를 넘으므로 `hitchOver`를 100쯤으로 올려 쓴다 |
| `hitchOver` | 끊김으로 셀 기준(ms)을 바꾼다. 1~10000. 적지 않으면 50. 예산이 아니므로 이것만 적을 수는 없다 |

- 재는 구간은 규칙이 시작된 때부터 이 명령까지다. 한 규칙에 여러 번 적으면 둘째부터는 바로 앞의 `assertFrameTime` 뒤부터 잰다. 보고 싶은 동작만 따로 재려면 그 동작 앞에 느슨한 `assertFrameTime`을 한 번 둔다.
- 프레임 시간은 게임 시간이 아니라 실제 시간이다. 기다리는 명령(`assertVisible` 등)이 기다린 동안의 프레임도 들어간다.
- 예산은 숫자만 적는다. `16ms`처럼 단위를 붙이면 오류다.
- 값은 기기와 빌드마다 다르다. `qaplay frames`로 지금 화면의 값을 본 뒤에 정한다. 에디터의 값은 에디터가 하는 일이 섞여 있어 빌드와 다르다.
- `assertMemory`와 `leakCheck`는 잴 때 쓰레기 수집을 하므로 그 프레임이 끊김으로 잡힐 수 있다. 프레임 시간을 보는 규칙과 메모리를 보는 규칙은 따로 쓴다.
- 러너는 `assertFrameTime`이 있는 규칙마다, 통과했어도 마지막으로 잰 값을 한 줄로 알린다: `frames: average 6.9 ms, p99 9.1 ms, longest 12.4 ms, 0 hitch(es) over 50 ms, 601 frames in 4.2 s`
- 실행해 볼 수 있는 예: 샘플 프로젝트의 `unity/Assets/Sample/BuggyRules/hitch-sort.yaml`. 샘플의 정렬 버튼에 일부러 심은 0.15초 끊김을 잡는 규칙이라 실패하는 것이 정상이다.

기기 지표 예산

게임 안에서는 알 수 없고 OS에서만 읽을 수 있는 값을 확인한다. 러너가 읽는다.

```yaml
- tapOn: { name: BattleStart }
- assertVisible: { name: ResultPopup, timeout: 120000 }
- assertDevice: { cpu: 150, pss: 1200, thermal: 1, batteryTemp: 40 }
```

| 적는 것 | 뜻 | Android | PC 빌드 |
|---|---|---|---|
| `cpu` | 규칙이 시작된 뒤(또는 앞의 `assertDevice` 뒤)부터 여기까지 게임 프로세스의 평균 CPU 사용률(%). 코어 하나를 다 쓰면 100이고, 여러 코어를 쓰면 100을 넘는다 | 됨 | 됨(`--app`으로 띄웠을 때) |
| `pss` | 지금 프로세스의 메모리(MB). 다른 프로세스와 함께 쓰는 부분은 나눠 센 값(PSS)이다 | 됨 | 안 됨 |
| `thermal` | 지금 기기의 발열 단계. 0(정상)부터 6까지이고, 1은 약함, 2는 보통, 3 이상이면 기기가 성능을 크게 낮춘다 | 됨 | 안 됨 |
| `batteryTemp` | 지금 배터리 온도(섭씨) | 됨 | 안 됨 |

- 러너가 OS에서 읽으므로 Android는 `--device`, PC 빌드는 `--app`이 있어야 한다. 떠 있는 게임이나 에디터에 붙기만 한 경우에는 읽을 수 없다. 에디터의 `qaplay > Rules` 창에서 돌리면 `unknown_command`로 실패한다.
- 읽을 수 없는 지표에 예산을 적으면 실패다(`not available here`). 확인하지 못한 것을 통과로 두지 않는다. 플랫폼마다 되는 지표만 적는다.
- `cpu`는 구간이 1초보다 짧으면 재지 못한 것으로 본다. OS가 CPU 시간을 10~16ms 단위로 세어서 짧은 구간의 값은 믿을 수 없다. 몇 초 넘게 걸리는 동작에 쓴다.
- 예산은 숫자만 적는다. `80%`처럼 단위를 붙이면 오류다.
- `pss`와 `assertMemory`의 `process`는 다른 값이다. `process`는 함께 쓰는 부분을 통째로 세어 더 크다(에뮬레이터에서 `process` 281MB일 때 `pss` 159MB).
- 배터리는 남은 양(%)도 함께 읽어 결과에 보여 주지만 예산은 없다. 기기를 USB로 연결하면 충전되므로 소모량을 판정할 수 없다.
- 값은 `qaplay device --seconds 10`으로 먼저 본다. 러너는 `assertDevice`가 있는 규칙마다, 통과했어도 읽은 값을 한 줄로 알린다: `device: cpu 17.5 %, pss 163.2 MB, thermal 0, battery 100 %, battery temperature 25 C`

Mock의 응답 고르기

결제, 로그인, 광고는 스토어나 OS의 화면을 거친다. 그 화면은 게임 밖이라 누를 수 없으므로, 프로젝트가 그 구간을 Mock으로 바꿔 두고 테스트가 응답을 고른다. 연결하는 방법은 [Mock 연결 안내](../unity/Packages/com.qaplay.core/Documentation~/mock-guide.md)에 있다.

```yaml
# 사용자가 결제 창에서 취소하면 보석이 들어오면 안 된다
- mock: { store: cancel }
- tapOn: { name: PayButton }
- assertVisible: { name: PayResult, text: 결제 취소 }
- assertValue: { gems: 0, plugin.store.buy.pending: 0 }
```

- `{ Mock 이름: 시나리오 }`로 적는다. 여러 개를 한 번에 적을 수 있다. 이름과 시나리오는 게임이 등록한 것이어야 하고, `qaplay mocks`로 본다.
- 고른 응답은 다시 고를 때까지 그대로다. 고르지 않은 Mock은 기본 응답(등록할 때의 첫 번째)이다.
- `mock`을 쓴 규칙이 끝나면(통과든 실패든) 모든 Mock을 기본 응답으로 돌려놓는다. 다음 규칙은 앞 규칙이 고른 응답을 물려받지 않는다.
- 시나리오의 뜻(성공, 실패, 취소, 지연, 콜백 중복 등)은 프로젝트의 Mock이 정한다. 도구는 고른 이름을 전해 줄 뿐이다.
- SDK 호출이 제대로 끝났는지는 게임이 `PluginCalls`로 세어 둔 값을 `assertValue`로 본다: `plugin.이름.started`(부른 횟수), `.succeeded`, `.failed`, `.pending`(콜백을 기다리는 호출 수. 콜백이 두 번 오면 음수), `.lastMs`(마지막 호출이 걸린 시간).
- 응답이 얼마나 걸려도 되는지는 결과를 기다리는 명령의 `timeout`으로 적는다: `assertVisible: { name: PayResult, text: 결제 완료, timeout: 5000 }`
- 실행해 볼 수 있는 예: 샘플 프로젝트의 `unity/Assets/Sample/Rules/pay.yaml`(응답 네 가지, 통과), `unity/Assets/Sample/BuggyRules/pay-duplicate.yaml`(샘플에 일부러 심은 "콜백이 두 번 오면 두 번 지급" 버그를 잡는 규칙이라 실패하는 것이 정상이다)

누수 검사

같은 동작(팝업을 열고 닫기 등)을 되풀이해도 메모리가 계속 늘지 않는지 확인한다.

```yaml
- tapOn: 상점                    # 이 줄 위는 준비다. 한 번만 실행한다
- leakCheck: 20                  # 이 줄 아래를 20번 되풀이한다
- tapOn: { name: RewardButton }
- tapOn: { name: ClaimButton }
- assertNotVisible: { name: RewardPopup }
```

- `leakCheck` 위의 명령은 한 번만 실행하고, 아래의 명령은 파일 끝까지를 한 묶음으로 되풀이한다. 묶음은 끝났을 때 시작한 화면으로 돌아와 있어야 한다.
- 한 번 되풀이할 때마다 메모리를 잰다(`assertMemory`와 같은 방법). 다 되풀이한 뒤, 잰 값의 뒤쪽 절반이 되풀이 한 번에 평균 얼마나 늘었는지로 판정한다.
- 앞쪽 절반은 보지 않는다. 처음 몇 번은 풀과 캐시가 차느라 늘 수 있고 그것은 누수가 아니다.
- 판정하는 계층은 `managed`와 `native`다. `process`는 기록만 한다. 가만히 있어도 OS가 수십 MB씩 올리고 내려서 추세를 가릴 수 없다.
- 되풀이 한 번에 0.05MB(약 50KB) 넘게 늘면 실패다. 다른 값을 쓰려면 `- leakCheck: { repeat: 20, allow: 0.5 }`처럼 `allow`(MB)를 적는다.
- 횟수는 10~1000이다. 조금씩 새는 누수는 많이 되풀이해야 드러난다.
- 파일 하나에 한 번만 쓸 수 있고, 아래에 명령이 하나 이상 있어야 한다.
- 되풀이하는 중에 명령이 실패하면 그 명령의 실패이고, 몇 번째 되풀이였는지 덧붙는다: `(repeat 7 of 20)`
- 실행해 볼 수 있는 예: 샘플 프로젝트의 `unity/Assets/Sample/Rules/leak-info.yaml`(통과), `unity/Assets/Sample/BuggyRules/leak-reward.yaml`(샘플에 일부러 심은 누수를 잡는 규칙이라 실패하는 것이 정상이다)

프로토콜과의 대응

| 규칙 명령 | 보내는 요청 |
|---|---|
| `tapOn` | `tap` (`timeoutMs` 포함. `repeat`는 `count`, `delay`는 `intervalMs`) |
| `tapTogether` | `tapTogether` (`timeoutMs` 10000) |
| `assertVisible` | `wait`, `state: visible` |
| `assertNotVisible` | `wait`, `state: gone` |
| `assertValue` | `get` 뒤에 읽는 쪽에서 비교 |
| `assertMemory` | `memory` 뒤에 읽는 쪽에서 비교 |
| `assertFrameTime` | `frames`(`hitchOver`는 `hitchMs`) 뒤에 읽는 쪽에서 비교. 이 명령이 있는 규칙은 시작할 때 `frames`를 한 번 보내 재기를 시작한다 |
| `assertDevice` | 에이전트에 보내지 않는다. 러너가 OS에서 읽어 답하고(adb, 또는 띄운 프로세스), 읽는 쪽에서 비교한다. 이 명령이 있는 규칙은 시작할 때 CPU의 기준점을 한 번 잡는다 |
| `mock` | `mock`(`set`). 이 명령이 있는 규칙은 끝날 때 `mock`(`reset`)을 한 번 더 보낸다 |
| `leakCheck` | 요청이 아니다. 아래 명령들을 되풀이하고, 되풀이마다 `memory`를 보내 읽는 쪽에서 판정 |
| `seed` | `seed` |

## 5. 실행 결과

- 모든 명령이 성공하면 통과, 하나라도 실패하면 실패다.
- 실패하면 몇 번째 명령인지(1부터), 그 명령의 원문, 에이전트가 돌려준 오류 코드와 설명을 알린다.
- `assertValue`의 값이 다르면 오류 코드는 `value_mismatch`이고, 다른 키만 기대값과 실제 값을 알린다.

```
failed at command 2 (line 2): - assertValue: { gold: 999 } -> value_mismatch: gold: expected 999, got 1100
```

- `assertMemory`가 예산을 넘으면 오류 코드는 `over_budget`이고, 넘은 계층만 쓴 양과 예산을 알린다.

```
failed at command 2 (line 2): - assertMemory: { managed: 64 } -> over_budget: managed: 80.2 MB, budget 64 MB
```

- `assertFrameTime`이 예산을 넘어도 오류 코드는 `over_budget`이다. 넘은 지표만 알리고, 끊김은 가장 오래 걸린 프레임도 함께 알린다.

```
failed at command 2 (line 6): - assertFrameTime: { hitches: 0 } -> over_budget: hitches: 1 frame(s) over 50 ms (longest 151.3 ms), budget 0
```

- `assertDevice`가 예산을 넘어도 오류 코드는 `over_budget`이다: `over_budget: cpu: 187.4 %, budget 150 %; thermal: 2, budget 1`
- `leakCheck`에서 메모리가 계속 늘면 오류 코드는 `leak`이고, 는 계층마다 되풀이 한 번에 는 양과 처음과 끝의 값을 알린다.

```
failed at command 1 (line 5): - leakCheck: 20 -> leak: native grew 1.033 MB per repeat (58.9 MB -> 78.5 MB over 20 repeats, allowed 0.05 MB per repeat)
```

- 러너는 `leakCheck`가 있는 규칙마다, 통과했어도 증가 추세를 한 줄로 알리고 잰 값을 `--artifacts` 폴더의 `규칙이름.memory.csv`에 남긴다(바이트, 되풀이마다 한 줄). `allow`를 정할 때와 빌드 사이의 추이를 볼 때 쓴다.

```
PASS  rules/leak-info.yaml (2.1s)
      memory: managed 0 MB per repeat, native +0.001 MB per repeat, history saved to qaplay-artifacts/leak-info.memory.csv
```

- 실행해 볼 수 있는 예: 샘플 프로젝트의 `unity/Assets/Sample/Rules/shop.yaml` (1절의 예와 같다)
- 재시도와 "불안정" 판정은 규칙 파일이 아니라 러너가 한다.

러너가 덧붙이는 실패

명령이 모두 성공했더라도, 규칙을 실행하는 동안 게임에 아래 일이 생기면 그 규칙은 실패다. 이 셋은 다시 돌리지 않는다(가끔만 나더라도 버그다).

| 코드 | 뜻 | 러너가 아는 방법 |
|---|---|---|
| `exception` | 처리되지 않은 C# 예외가 났다. 앱은 살아 있다 | 에이전트의 `exception` 이벤트 |
| `crash` | 앱이 죽었다 | 연결이 끊김 |
| `freeze` | 앱은 살아 있는데 화면이 멈췄거나 응답이 없다 | 생존 신호의 프레임 번호가 10초(`--freeze-seconds`) 동안 그대로이거나, 명령의 제한 시간이 지나고도 응답이 없음 |

- 크래시와 프리징 뒤에 남은 규칙을 이어 가려면 러너가 게임을 직접 띄워야 한다(`--app` 또는 `--package`). 러너는 게임의 로그를 `--artifacts` 폴더에 남기고 게임을 다시 띄운다.
- 이미 떠 있는 게임이나 에디터에 붙은 경우에는 다시 띄울 수 없다. 남은 규칙은 `not_connected`로 실패한다.
- 러너가 띄우지 않은 게임의 크래시는 `freeze`로 나올 수도 있다. 죽는 게임이 크래시 기록을 쓰는 동안에는 연결이 끊기지 않는데, 그 시간이 프리징 기준보다 길면 프리징이 먼저 잡힌다(손으로 띄운 Windows 빌드에서 11번 중 10번은 약 5초 뒤 `crash`, 1번은 `freeze`). 러너가 띄운 게임은 로그로 구분하므로 `crash`로 나온다.
- 메인 스레드를 한 번에 10초 넘게 막는 로딩은 프리징으로 잡힌다(9초는 통과, 11초는 `freeze`). 그런 게임은 `--freeze-seconds`를 늘린다. 로딩 중에도 프레임이 가끔 도는 게임(3초씩 막기를 8번)은 걸리지 않는다.

## 6. Maestro와 다른 점

- 글자는 정규식이 아니라 정확히 일치로 본다.
- 머리말에 `appId`가 없고 `launchApp`도 없다. 어떤 게임에 붙을지는 러너가 정한다.
- `assertValue`, `assertMemory`, `assertFrameTime`, `assertDevice`, `mock`, `leakCheck`, `seed`, `tapTogether`는 qaplay에만 있다.
- Maestro의 `repeat` 명령(명령 묶음을 여러 번 실행)은 없다. 되풀이는 `leakCheck`에서만 하고, 묶음을 들여 쓰지 않고 그 줄 아래 전부로 정한다.
- `tapOn`의 `repeat`와 `delay`는 Maestro와 이름이 같다. `delay`를 적지 않았을 때 Maestro는 100ms를 쉬고, qaplay는 쉬지 않는다.
- v0에 없는 것: 글자 입력(`inputText`), 스크롤과 끌기(`scroll`, `swipe`), 뒤로 가기(`back`), 스크린샷(`takeScreenshot`), 다른 규칙 부르기(`runFlow`). 에이전트 명령이 생기는 대로 같은 이름으로 추가한다.

## 7. 입력 기록이 요소를 적는 순서

사람의 조작을 기록할 때, 누른 요소를 아래 순서로 시도해 처음으로 요소 하나로 정해지는 표기를 쓴다. 기록한 결과는 이 문서의 규칙 파일 형식 그대로다.

1. 고유 ID가 있으면 `{ id }`
2. 이름: `{ name }`
3. 글자: `{ text }`
4. 범위를 덧붙인다. 범위는 누른 요소의 조상 중 1~2로 하나로 정해지는 가장 가까운 요소다: `{ name, within }`, 다음으로 `{ text, within }`
5. 그래도 여러 개면 순서를 덧붙인다: `{ name, within, index }`

- 기록하는 요소는 터치가 닿은 요소가 아니라 그 입력을 받은 요소다. 버튼 안의 글자를 눌렀으면 버튼을 적는다.
- 이름을 글자보다 먼저 쓰는 이유: 언어를 바꾸거나 문구를 고쳐도 규칙이 깨지지 않는다.
- 범위로 쓸 조상이 하나도 없으면 화면 전체에서 순서를 센다: `{ name, index }`
- "하나로 정해지는지"는 재생할 때 에이전트가 쓰는 판정과 같은 코드로 본다. 기록된 셀렉터는 기록한 화면에서 반드시 그 요소를 가리킨다.

기록하는 범위

- 탭만 기록한다. 누른 자리에서 뗀 것만 탭으로 보고, 끌고 가서 떼면(스크롤 등) 기록하지 않는다.
- 마우스, 터치, 펜을 같은 길로 받는다. UI 입력 모듈(Input System)이 쓰는 누르기 입력을 함께 듣는다.
- 클릭을 받는 요소가 없는 자리를 누르면 명령 대신 주석을 남긴다: `# not recorded: no element takes a tap at (0.5, 0.2)`
- 확인 명령(`assertVisible`, `assertValue` 등)은 기록되지 않는다. 기록한 뒤 사람이나 AI가 덧붙인다.
- 기록한 줄은 `- tapOn: { ... }` 모양이다. 따옴표 없이 적어도 틀림없이 같은 글자로 읽히는 것만 그대로 적고, 나머지는 큰따옴표로 감싼다.

## 8. 사전 검증

규칙을 실행하지 않고, 셀렉터를 지금 화면에 대어 문제가 있는 명령을 찾는다. 아무것도 누르지 않는다. 규칙을 쓰는 중에 이름과 조건이 맞는지 바로 확인하는 데 쓴다.

| 종류 | 뜻 | 대상 명령 |
|---|---|---|
| `none` | 걸리는 요소가 없다 | `tapOn`, `assertVisible`, `assertNotVisible` |
| `many` | 요소 하나가 필요한데 여럿이 걸린다 | `tapOn`, `tapTogether`, 그리고 모든 명령의 `within` |
| `unknown_key` | 게임이 등록하지 않은 값이다 | `assertValue` |

```
command 4 (line 4): - tapOn: { name: Item } -> many: selector matched 6 elements
```

- `tapTogether`는 요소마다 따로 본다. 문제가 있는 요소마다 한 줄이 나오고, 몇 번째 요소인지 앞에 붙는다: `element 2: selector matched 0 elements`
- `assertVisible`과 `assertNotVisible`은 여럿이 걸려도 문제가 아니다. 하나 이상 보이는지, 하나도 안 보이는지를 보기 때문이다.
- `assertValue`는 값이 등록되어 있는지만 본다. 기대값과 비교하지 않는다.
- `assertNotVisible`의 `none`은 눈여겨본다. 이름을 잘못 적은 `assertNotVisible`은 실행하면 조용히 통과한다.
- 지금 화면 하나만 본다. 뒤 화면에서 나올 요소(알림, 아직 열지 않은 팝업 안의 버튼)는 `none`으로 나온다. 그 명령이 실행될 화면에서 검증해야 뜻이 있다.
- 실행 방식: 요소는 프로토콜의 `find`로 세고(`resolves`), 게임 값은 `get`으로 확인한다.

## 9. 에디터에서 쓰기

메뉴 `qaplay > Rules`로 창을 연다. 모든 기능은 Play 중인 게임 화면을 대상으로 한다. 게임이 멈춰 있을 때 버튼을 누르면 게임을 먼저 시작한 뒤에 한다.

| 버튼 | 하는 일 |
|---|---|
| Start game / Stop game | 에디터의 Play 버튼과 같다. 맨 위에 게임이 실행 중인지 보인다 |
| Start recording / Stop recording | Game 뷰에서 누른 요소를 규칙 파일의 줄로 쌓는다. 기록 중인 내용이 창에 보인다 |
| Save recording to a file... | 기록한 내용을 `.yaml` 파일로 저장한다. 저장한 파일이 아래의 규칙 파일 칸에 채워진다 |
| Check | 고른 규칙 파일을 지금 화면에 사전 검증한다 (8절). 아무것도 누르지 않는다 |
| Run | 고른 규칙 파일을 지금 화면에서 실행하고 통과와 실패를 알린다 (5절) |
| Run from start | 게임을 껐다 켠 뒤에 실행한다. 기록한 규칙은 기록을 시작한 화면에서 돌려야 통과하므로 이쪽을 쓴다 |

- 결과는 창과 Console에 함께 나온다.
- 문법이 틀린 파일은 실행하지 않고 "몇 번째 줄: 이유"를 보여 준다.
