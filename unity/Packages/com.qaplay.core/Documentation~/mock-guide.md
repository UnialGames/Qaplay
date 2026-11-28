# Mock 연결 안내

결제, 로그인, 광고처럼 스토어나 OS의 화면을 거치는 흐름을 qaplay로 검증하려면, 프로젝트가 그 구간을 Mock으로 바꿀 수 있게 해 두어야 한다. 이 문서는 그 방법이다. 실행해 볼 수 있는 예는 샘플 프로젝트의 `Assets/Sample/SampleStore.cs`와 `Assets/Sample/SampleShop.cs`의 결제 부분이다.

## 왜 Mock인가

결제를 예로 들면 흐름은 세 구간이다.

| 구간 | 하는 일 | qaplay가 할 수 있는 것 |
|---|---|---|
| 요청 | 버튼을 누르고 스토어 SDK를 부른다 | 버튼을 누른다 |
| 승인 | 스토어의 화면에서 사용자가 결제하거나 취소한다 | 없다. 게임 프로세스 밖의 화면이다 |
| 결과 | 콜백을 받고, 영수증을 확인하고, 재화를 준다 | 화면과 게임 값을 확인한다 |

승인 구간을 건너뛸 방법이 없으므로, 그 자리에 "스토어가 이렇게 답했다"를 흉내 내는 Mock을 넣는다. 테스트는 Mock이 어떻게 답할지를 고르고, 게임이 그 답을 제대로 처리하는지를 본다. 성공만이 아니라 실패, 취소, 늦은 응답, 두 번 오는 콜백처럼 실제로는 만들기 어려운 경우를 마음대로 만들 수 있다.

## 연결하는 순서

### 1. SDK 호출을 인터페이스 뒤에 둔다

게임 코드가 SDK를 직접 부르면 바꿔 끼울 자리가 없다. 프로젝트의 인터페이스 하나를 두고 게임 코드는 그것만 쓴다.

```csharp
public enum PayResult { Paid, Failed, Cancelled }

public interface IStore
{
    void Buy(string productId, Action<PayResult> done);
}

public sealed class RealStore : IStore   // 여기서 실제 스토어 SDK를 부른다
{
    public void Buy(string productId, Action<PayResult> done) { /* SDK 호출 */ }
}
```

### 2. Mock을 만들고 응답의 종류를 등록한다

```csharp
#if QAPLAY || UNITY_EDITOR
public sealed class MockStore : IStore
{
    readonly MonoBehaviour m_Host;

    public MockStore(MonoBehaviour host)
    {
        m_Host = host;
        Mocks.Register("store", "success", "fail", "cancel", "slow", "duplicate");   // 첫 번째가 기본값
    }

    public void Buy(string productId, Action<PayResult> done) => m_Host.StartCoroutine(Respond(done));

    IEnumerator Respond(Action<PayResult> done)
    {
        string scenario = Mocks.Scenario("store");                      // 테스트가 고른 응답
        yield return new WaitForSeconds(scenario == "slow" ? 2f : 0.1f);
        switch (scenario)
        {
            case "fail": done(PayResult.Failed); break;
            case "cancel": done(PayResult.Cancelled); break;
            case "duplicate": done(PayResult.Paid); done(PayResult.Paid); break;
            default: done(PayResult.Paid); break;
        }
    }
}
#endif
```

- `Mocks.Register(이름, 시나리오...)`: Mock 지점과 응답의 종류를 알린다. 이름과 시나리오는 프로젝트가 정한다. 도구는 뜻을 모르고, 테스트가 고른 이름을 전해 줄 뿐이다.
- `Mocks.Scenario(이름)`: 지금 고른 시나리오를 읽는다. 응답할 때마다 읽는다. 테스트가 중간에 바꿀 수 있다.
- 어떤 응답을 만들지는 그 SDK가 실제로 낼 수 있는 경우를 따른다. 성공, 실패, 취소, 지연, 콜백 중복이 기본이다.

### 3. QA 빌드에서만 Mock을 쓴다

```csharp
IStore store =
#if QAPLAY || UNITY_EDITOR
    new MockStore(host);
#else
    new RealStore();
#endif
```

### 4. SDK를 부르는 자리에 호출 기록 두 줄을 넣는다

```csharp
PluginCalls.Started("store.buy");
store.Buy("gems100", result =>
{
    PluginCalls.Finished("store.buy", result == PayResult.Paid);
    // 결과 처리
});
```

테스트가 `plugin.store.buy.started`, `.succeeded`, `.failed`, `.pending`, `.lastMs`를 게임 값으로 읽을 수 있게 된다. 콜백이 오지 않으면 `pending`이 남고, 두 번 오면 음수가 된다. 한 번도 부르지 않은 것을 확인해야 하면 시작할 때 `PluginCalls.Track("store.buy")`를 불러 둔다.

`PluginCalls`의 호출은 실제 SDK를 부르는 코드에도 그대로 둘 수 있다. QA 빌드와 에디터에서만 컴파일되고 출시 빌드에서는 호출 자체가 사라진다.

## Mock이 출시 빌드에 들어가지 않게

- `Mocks`는 QA 빌드(`QAPLAY` 심벌)와 에디터에만 있는 API다. Mock은 그것을 읽어야 하므로, `#if QAPLAY || UNITY_EDITOR`로 감싸지 않은 Mock이 출시 빌드에 섞이면 컴파일이 실패한다(`The name 'Mocks' does not exist in the current context`). 실수를 빌드가 막아 준다.
- Mock이 여럿이면 `#if` 대신 Mock만 모은 어셈블리(asmdef)를 따로 두고 `defineConstraints`에 `QAPLAY || UNITY_EDITOR`를 적어도 된다. 이 패키지의 QA 전용 어셈블리가 그렇게 되어 있다.
- 3번의 조립 코드는 반드시 `#else`에 실제 구현을 둔다. 출시 빌드가 어떤 구현을 쓰는지가 그 한 곳에서 보인다.

## 테스트에서 쓰기

```yaml
- mock: { store: cancel }
- tapOn: { name: PayButton }
- assertVisible: { name: PayResult, text: 결제 취소 }
- assertValue: { gems: 0, plugin.store.buy.pending: 0 }
```

```python
game.mock(store="duplicate")
game.tap(name="PayButton")
game.wait(name="PayResult", text="결제 완료")
assert game.get("gems") == 100      # 콜백이 두 번 와도 한 번만 지급
```

- 등록된 Mock과 시나리오는 `qaplay mocks`로 본다.
- 규칙은 끝날 때 Mock을 기본 응답으로 돌려놓는다. Python 스크립트는 `game.reset_mocks()`를 직접 부른다.

## 하지 않는 것

- SDK별 기본 Mock(Unity IAP용 등)은 제공하지 않는다. SDK의 버전에 묶이고, 프로젝트마다 감싸는 모양이 달라서다.
- 실제 스토어의 화면을 누르는 것(테스트 계정으로 실제 결제)은 하지 않는다. 그 화면이 뜨면 러너는 게임에 닿지 못해 실패한다.
