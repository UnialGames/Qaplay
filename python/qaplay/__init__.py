"""qaplay Python 클라이언트.

러너(qaplay serve)를 거쳐 게임 안의 에이전트에 명령을 보낸다. 조건과 반복이 필요한
시나리오를 규칙 파일 대신 Python으로 쓸 때 쓴다. 표준 라이브러리만 쓴다.

    import qaplay

    with qaplay.connect() as game:
        game.tap(id="shop.buy")
        game.wait(name="GoldLabel", text="1,100")
        assert game.get("gold") == 1100

실행: qaplay serve -- python my_test.py
"""

import json
import os
import socket
import struct
import time

__version__ = "0.1.0"

DEFAULT_PORT = 27276        # qaplay serve가 여는 포트
DEFAULT_TIMEOUT_MS = 10000  # 누르기와 기다리기의 기본 제한 시간
_RESPONSE_MARGIN_S = 10.0   # 명령의 제한 시간이 지나고도 이만큼 더 응답이 없으면 연결이 죽은 것으로 본다
_MAX_FRAME = 16 * 1024 * 1024


class QaplayError(Exception):
    """명령이 실패했다. code는 프로토콜의 오류 코드(not_found, ambiguous, not_tappable, timeout 등)다.

    에이전트가 아니라 연결에서 난 문제는 code가 connection_lost 또는 no_response다.
    """

    def __init__(self, code, message, data=None):
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message
        self.data = data or {}


def connect(host="127.0.0.1", port=None):
    """러너에 접속한다. port를 생략하면 환경 변수 QAPLAY_RUNNER_PORT, 그다음 27276이다."""
    return Game(host, port)


def _selector(selector, conditions):
    # 글자 하나는 text 조건의 줄임이다. 규칙 파일의 "tapOn: 구매"와 같다
    if isinstance(selector, str):
        selector = {"text": selector}
    selector = dict(selector or {})
    selector.update(conditions)
    if not selector:
        raise ValueError("a selector needs at least one condition, e.g. name='BuyButton'")
    return selector


class Game:
    """게임과의 연결 하나. 접속하면 바로 hello를 보내고, 그 결과를 info에 둔다."""

    def __init__(self, host="127.0.0.1", port=None):
        if port is None:
            port = int(os.environ.get("QAPLAY_RUNNER_PORT", DEFAULT_PORT))
        self._sock = socket.create_connection((host, port))
        self._sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self._last_id = 0
        self.info = self.request(
            "hello",
            {"protocol": {"min": 1, "max": 1}, "client": {"name": "qaplay-python", "version": __version__}},
        )

    # ---- 명령 ----

    def tap(self, selector=None, *, timeout_ms=DEFAULT_TIMEOUT_MS, **conditions):
        """요소 하나를 가상 터치로 누른다. 나타나고 누를 수 있게 될 때까지 timeout_ms 안에서 기다린다."""
        return self.request("tap", {"selector": _selector(selector, conditions), "timeoutMs": timeout_ms})

    def wait(self, selector=None, *, timeout_ms=DEFAULT_TIMEOUT_MS, **conditions):
        """걸리는 요소 중 보이는 것이 하나 이상 생길 때까지 기다린다. 글자가 바뀌기를 기다릴 때는 text를 넣는다."""
        return self._wait("visible", selector, timeout_ms, conditions)

    def wait_gone(self, selector=None, *, timeout_ms=DEFAULT_TIMEOUT_MS, **conditions):
        """걸리는 요소 중 보이는 것이 없어질 때까지 기다린다."""
        return self._wait("gone", selector, timeout_ms, conditions)

    def find(self, selector=None, **conditions):
        """걸리는 요소를 모두 돌려준다. 0개여도 오류가 아니다."""
        return self.request("find", {"selector": _selector(selector, conditions)})["matches"]

    def get(self, *keys):
        """게임이 등록한 값을 읽는다. 키 하나면 그 값을, 여러 개나 0개(전체)면 dict를 돌려준다."""
        values = self.request("get", {"keys": list(keys)} if keys else {})["values"]
        return values[keys[0]] if len(keys) == 1 else values

    def seed(self, seed):
        """Unity 기본 랜덤의 시드를 정한다."""
        self.request("seed", {"seed": seed})

    def dump(self):
        """화면의 요소 전체를 돌려준다."""
        return self.request("dump")

    def _wait(self, state, selector, timeout_ms, conditions):
        return self.request(
            "wait", {"selector": _selector(selector, conditions), "state": state, "timeoutMs": timeout_ms}
        )

    # ---- 전송: 프레임은 길이(4바이트, 빅 엔디언) + UTF-8 JSON ----

    def request(self, cmd, args=None):
        """요청 하나를 보내고 결과를 돌려준다. 실패 응답이면 QaplayError."""
        self._last_id += 1
        request_id = self._last_id
        args = args or {}
        body = json.dumps({"type": "req", "id": request_id, "cmd": cmd, "args": args}, ensure_ascii=False).encode("utf-8")
        deadline = time.monotonic() + args.get("timeoutMs", 0) / 1000 + _RESPONSE_MARGIN_S
        try:
            self._sock.sendall(struct.pack(">I", len(body)) + body)
            while True:
                (length,) = struct.unpack(">I", self._read(4, deadline, cmd))
                if length > _MAX_FRAME:
                    raise QaplayError("connection_lost", "frame is larger than 16 MiB")
                message = json.loads(self._read(length, deadline, cmd).decode("utf-8"))
                # 사이에 끼어드는 이벤트(생존 신호 등)는 건너뛴다
                if message.get("type") == "res" and message.get("id") == request_id:
                    break
        except OSError as e:
            if isinstance(e, socket.timeout):
                raise QaplayError("no_response", f"no response to {cmd}") from None
            raise QaplayError("connection_lost", str(e)) from None
        if not message["ok"]:
            error = message["error"]
            raise QaplayError(error["code"], error["message"], error.get("data"))
        return message["result"]

    def _read(self, count, deadline, cmd):
        chunks = []
        while count > 0:
            left = deadline - time.monotonic()
            if left <= 0:
                raise QaplayError("no_response", f"no response to {cmd}")
            self._sock.settimeout(left)
            chunk = self._sock.recv(count)
            if not chunk:
                raise QaplayError("connection_lost", "the connection was closed (is the game running?)")
            chunks.append(chunk)
            count -= len(chunk)
        return b"".join(chunks)

    def close(self):
        self._sock.close()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()
