# qaplay

Unity 게임용 자동화 QA 도구. 게임 안의 에이전트와 PC의 러너가 통신하며, 유저와 같은 경로(가상 터치)로 UI를 조작하고 결과를 검증한다.

> 초기 개발 단계다. 아직 설치해서 쓸 수 있는 코드는 없다.

## 구조

```
Python 테스트 스크립트 ──► 러너 (.NET CLI) ──TCP──► 에이전트 (게임 내, QA 빌드에만 포함)
```

- 에이전트: 요소 조회, 가상 터치, 대기, 측정 명령을 수행
- 러너: 기기 연결, 테스트 실행과 판정, 크래시 복구, 리포트
- 대상: Unity 6.3 LTS 이상, Input System, uGUI (이후 UI Toolkit)

## 문서

- [요구사항](docs/requirements.md)
- [작업 계획](docs/plan.md)
- [프로토콜 명세](protocol/README.md)
- [규칙 파일 명세](docs/rules.md)

## 라이선스

[MIT](LICENSE)
