"""샘플 상점을 다루는 예. 규칙 파일로는 쓸 수 없는 조건과 반복이 들어 있다.

실행(게임이 Play 중일 때): qaplay serve -- python python/examples/shop.py
"""

import qaplay

with qaplay.connect() as game:
    # 구매하면 골드가 100 줄고 알림이 떴다 사라진다
    before = game.get("gold")
    game.tap(id="shop.buy")
    game.wait(name="Toast")
    assert game.get("gold") == before - 100, f"gold {before} -> {game.get('gold')}"
    game.wait(name="GoldLabel", text=f"{before - 100:,}")
    game.wait_gone(name="Toast")

    # 반복: 골드가 800이 될 때까지 산다. 글자 하나는 text 조건이다
    while game.get("gold") > 800:
        game.tap("구매")
    assert game.get("gold") == 800

    # 실패는 오류 코드로 구분한다
    try:
        game.tap(name="CoveredButton", timeout_ms=0)
        raise AssertionError("a covered button must not be tappable")
    except qaplay.QaplayError as e:
        assert e.code == "not_tappable" and e.data["reason"] == "occluded", e

    assert len(game.find(name="Item")) == 6
    print("ok: gold", before, "->", game.get("gold"), "on", game.info["platform"])
