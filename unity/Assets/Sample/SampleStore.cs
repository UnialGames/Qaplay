using System;
using System.Collections;
using Qaplay;
using UnityEngine;

// 결제를 Mock으로 바꿔 검증하는 방법의 예. 결제는 세 구간이다:
//   요청(버튼을 누르고 스토어 SDK를 부른다) → 승인(스토어의 화면에서 사용자가 결제한다) → 결과(콜백을 받고 보석을 준다)
// 승인 구간은 게임 밖의 화면이라 테스트 도구가 누를 수 없다. 그래서 스토어를 인터페이스 뒤에 두고,
// QA 빌드에서는 그 자리에 Mock을 넣는다. Mock이 어떻게 응답할지는 테스트가 고른다(규칙의 mock 명령).

public enum PayResult { Paid, Failed, Cancelled }

/// <summary>프로젝트가 스토어 SDK를 감싸 둔 인터페이스. 게임 코드는 SDK를 직접 부르지 않고 이것만 쓴다.</summary>
public interface IStore
{
    /// <summary>결제를 시작한다. 끝나면 done이 한 번 불린다.</summary>
    void Buy(string productId, Action<PayResult> done);
}

/// <summary>실제 스토어. 진짜 게임에서는 여기서 스토어 SDK(Unity IAP 등)를 부른다. 샘플에는 SDK가 없어 늘 실패한다.</summary>
public sealed class RealStore : IStore
{
    public void Buy(string productId, Action<PayResult> done)
    {
        Debug.LogWarning("[sample] there is no store SDK in the sample; the purchase of " + productId + " fails");
        done(PayResult.Failed);
    }
}

public static class SampleStore
{
    /// <summary>게임이 쓸 스토어를 고른다. QA 빌드와 에디터에서는 Mock, 출시 빌드에서는 실제 스토어다.</summary>
    public static IStore Create(MonoBehaviour host)
    {
#if QAPLAY || UNITY_EDITOR
        return new MockStore(host);
#else
        return new RealStore();
#endif
    }
}

// Mock은 QA 빌드와 에디터에만 있다. 이 가드를 빼면 출시 빌드에서 Mocks를 찾지 못해 컴파일이 실패한다:
// Mock이 출시 빌드에 섞이는 실수를 빌드가 막아 준다.
#if QAPLAY || UNITY_EDITOR
/// <summary>가짜 스토어. 스토어의 화면을 띄우는 대신, 테스트가 고른 시나리오대로 콜백을 부른다.</summary>
public sealed class MockStore : IStore
{
    public const string Name = "store";
    readonly MonoBehaviour m_Host;

    public MockStore(MonoBehaviour host)
    {
        m_Host = host;
        // 첫 번째가 기본값이다. 이름과 뜻은 프로젝트가 정한다
        Mocks.Register(Name, "success", "fail", "cancel", "slow", "duplicate");
    }

    public void Buy(string productId, Action<PayResult> done) => m_Host.StartCoroutine(Respond(done));

    IEnumerator Respond(Action<PayResult> done)
    {
        string scenario = Mocks.Scenario(Name);
        // 실제 스토어처럼 조금 뒤에 답한다. slow는 응답이 늦는 경우다
        yield return new WaitForSeconds(scenario == "slow" ? 2f : 0.1f);
        switch (scenario)
        {
            case "fail": done(PayResult.Failed); break;
            case "cancel": done(PayResult.Cancelled); break;
            // 스토어가 같은 결제의 완료를 두 번 알려 주는 경우. 게임은 한 번만 지급해야 한다
            case "duplicate": done(PayResult.Paid); done(PayResult.Paid); break;
            default: done(PayResult.Paid); break;
        }
    }
}
#endif
