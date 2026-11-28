# qaplay 버그 리포트

발견된 문제 하나를 기록하고, 고쳐질 때까지 따라가는 방법이다. 테스트 케이스와 테스트 결과와의 구분은 [요구사항 10번](requirements.md)에 있다.

- 버그 리포트는 PC의 폴더에 파일로 둔다. 이슈 관리 도구를 이으면 거기에도 올라간다(8절. 지금은 Jira, 다음은 Notion)
- 모든 동작은 `qaplay bugs` 명령이다. 사람과 AI가 같은 명령을 쓴다

## 1. 흐름

```
게임에서 제보 (F12, 세 손가락 1초)          러너가 잡은 예외, 크래시, 프리징
        │                                            │
   qaplay bugs pull                             (자동 등록)
        └──────────────► qaplay-bugs/0001/ ◄─────────┘
                               │
                    qaplay bugs set    제목, 설명, 심각도, 우선순위를 적는다
                    qaplay bugs promote    재현 스텝을 회귀 규칙 파일로 만든다
                    (사람이 통과 조건을 덧붙인다)
                    qaplay bugs set --status resolved    고쳤다고 표시
                    qaplay bugs verify    회귀 규칙을 돌려 확인
```

## 2. 제보하기

게임이 Play 중이면(에디터, QA 빌드) 언제든 제보할 수 있다. 키와 손가락은 Unity의 **Project Settings > qaplay**의 "테스터가 버그를 제보하는 방법"에서 고른다(게임이 이미 쓰는 키나 제스처와 겹치지 않게). QA 빌드에는 빌드할 때 들어가므로 바꾼 뒤 다시 빌드한다.

| 방법 | 누가 |
|---|---|
| 키보드 F12(설정에서 바꾼다) | PC에서 테스트하는 사람 |
| 세 손가락으로 1초 누르기(손가락 수와 시간은 설정에서 바꾼다) | 기기에서 테스트하는 사람 |
| `qaplay bugs report --title "..."` | PC에서. 지금 화면으로 제보를 만들고 바로 가져온다 |
| Python의 `game.report("...")` | 스크립트가 이상을 발견했을 때 |

- 제보에는 그 순간의 화면, 직전의 조작(최근 5분, 최대 200개), 로그 200줄, 기기와 빌드 정보가 담긴다
- 게임에서 건 제보는 제목을 받지 않고 바로 저장한다. 화면 위쪽 가운데에 `qaplay: report saved`와 제보 ID가 3초 보인다. 제목과 설명은 PC로 가져온 뒤에 적는다
- 제보는 게임 안의 폴더(`persistentDataPath/qaplay-reports`)에 쌓여 있다가 `qaplay bugs pull`로 PC에 온다. 가져간 제보는 게임에서 지워진다

## 3. 저장 형식

`qaplay-bugs/`(위치는 `--bugs DIR`) 아래에 버그마다 폴더가 하나 있다. 폴더 이름이 버그 번호다.

| 파일 | 내용 |
|---|---|
| `bug.json` | 제목, 설명, 상태, 심각도, 우선순위, 종류, 발생 횟수, 처음과 마지막 발생 시각, 빌드와 기기 정보, 회귀 규칙의 경로 |
| `steps.yaml` | 재현 스텝. [규칙 파일](rules.md) 형식이라 그대로 재생된다 |
| `log.txt` | 로그. 크래시와 프리징은 러너가 챙긴 게임 로그다 |
| `screenshot.png` | 제보한 순간의 화면. 자동 등록된 버그에는 없다 |

`bug.json`의 값

| 필드 | 값 |
|---|---|
| `status` | `open`(열림) → `resolved`(고쳤다고 표시) → `verified`(회귀 규칙으로 확인됨). `closed`는 고치지 않고 닫을 때 |
| `severity` | `critical`, `major`, `normal`, `minor`. 도구가 초안을 채운다: 크래시와 프리징은 `critical`, 예외는 `major`, 사람의 제보는 비워 둔다 |
| `priority` | 도구는 비워 둔다. 사람이 정한다 |
| `kind` | `manual`(사람의 제보), `exception`, `crash`, `freeze` |
| `count` | 발생 횟수 |
| `regression` | 확인됐거나 닫힌 뒤에 다시 났으면 `true` |

## 4. 자동 등록

`qaplay run`이 규칙을 돌리다가 예외, 크래시, 프리징을 잡으면 사람의 확인 없이 버그로 올린다. 버그임이 분명한 실패이기 때문이다. 규칙이 기대와 달라서 난 실패(`value_mismatch`, `timeout` 등)는 올리지 않는다.

- 재현 스텝은 그 규칙 파일이다
- 같은 버그가 이미 있으면 새로 만들지 않고 발생 횟수를 올린다
  - 예외: 예외의 종류와 맨 위 호출 지점이 같으면 같은 버그다. 어느 규칙에서 났는지는 따지지 않는다
  - 크래시와 프리징: 같은 규칙 파일에서 났으면 같은 버그다
- `verified`나 `closed`인 버그가 다시 나면 `open`으로 되돌리고 `regression`을 켠다
- 올리지 않으려면 `--bugs off`를 준다

## 5. 회귀 규칙으로 만들기

```
qaplay bugs promote 3 --to rules/regression
```

`steps.yaml`을 규칙 파일 `bug-0003.yaml`로 만들고, 그 경로를 버그에 적어 둔다. 만들어진 규칙에는 조작만 있다.

- 사람이 건 제보: 무엇이 "고쳐진 것"인지는 조작만으로 알 수 없다. 파일을 열어 확인 명령(`assertVisible`, `assertValue`)을 덧붙인다
- 예외, 크래시, 프리징: 덧붙이지 않아도 된다. 그 조작을 했을 때 다시 터지지 않으면 통과다
- 제보 직전 5분의 조작이 모두 들어 있으므로, 버그와 무관한 앞부분은 지워서 줄인다

## 6. 고쳐졌는지 확인하기

```
qaplay bugs set 3 --status resolved
qaplay bugs verify 3
```

`verify`는 게임에 붙어 그 버그의 회귀 규칙을 돌린다. 통과하면 `verified`로, 실패하면 `open`으로 바꾼다. 상태를 `resolved`로 바꾸는 것만으로는 아무것도 실행하지 않는다. 고친 빌드가 준비됐을 때 사람이나 CI가 `verify`를 돌린다.

## 7. 아직 없는 것

- 규칙 실패를 모아 두고 사람이 "버그 / 규칙 오류 / 환경 문제 / 기획 변경"으로 판정하는 확인 대기 목록
- 잘못 묶인 버그를 나누거나 따로 만들어진 버그를 합치는 명령
- 재현율 계산, 수정 이력
- 사람이 건 제보끼리 같은 문제인지 가려 주는 기능
- 앱이 죽었을 때의 조작 이력. 이력은 게임의 메모리에 있어 크래시와 함께 사라진다(규칙을 돌리다 난 크래시는 그 규칙이 재현 스텝이다)
- Notion 연동

## 8. 이슈 관리 도구(Jira)

버그 리포트는 늘 이 PC의 폴더에 있고, 이슈 관리 도구를 이으면 거기에도 올라간다. 지금 되는 도구는 Jira Cloud다(러너의 Jira 모듈, `modules/jira`).

### 잇기

Unity의 **Project Settings > qaplay**의 "버그를 올릴 곳"에서 Jira를 고르고 위에서 아래로 따라간다.

1. 사이트 주소: Jira를 여는 주소(`your-team.atlassian.net`)
2. 로그인: Atlassian 계정의 이메일과 API 토큰. "열기: API 토큰"이 토큰을 만드는 페이지([id.atlassian.com](https://id.atlassian.com/manage-profile/security/api-tokens))를 연다. **Create API token**(범위가 있는 것이 아닌 것)을 누르고 이름(qaplay)과 만료일을 정해 만든 뒤 복사해 붙여 넣고 "로그인"을 누른다
3. 프로젝트: "프로젝트 불러오기"로 이 계정이 보는 프로젝트와 이슈 유형을 받아 고르고 "연결"을 누른다

- 토큰은 이 PC의 사용자 폴더(`.qaplay/jira.json`, `QAPLAY_HOME`으로 바꾼다)에만 두고 프로젝트와 설정 파일에는 넣지 않는다. 로그인은 PC마다 한 번, 토큰이 만료되면 다시 한다
- 설정 파일에는 어디로 보내는지만 들어간다: `"issues": { "type": "jira", "site": "https://your-team.atlassian.net", "project": "QA", "issueType": "Bug" }`
- 범위가 있는 토큰(Create API token with scopes)은 Jira 사이트 주소로는 로그인되지 않는다(Atlassian의 게이트웨이 주소를 거쳐야 한다). 범위 없는 토큰을 쓴다
- Unity 없이: `qaplay bugs jira login --site 사이트 --email 이메일`(토큰은 물어서 받고 화면에 보이지 않는다), `qaplay bugs jira projects --site 사이트`, `qaplay bugs jira connect --site 사이트 --project QA`
- CI(빌드 서버): 로그인 대신 환경 변수 `QAPLAY_JIRA_EMAIL`과 `QAPLAY_JIRA_TOKEN`을 준다(GitHub Actions라면 저장소의 Secrets에 넣는다)

### 무엇이 오가는지

| 언제 | 하는 일 |
|---|---|
| 아직 올리지 않은 버그 | 이슈를 만든다. 제목, 설명과 정보(종류, 횟수, 빌드, 기기), 재현 스텝(코드 블록), 라벨 `qaplay`와 `qaplay-종류`. 화면, 로그, 재현 스텝을 첨부한다 |
| 같은 버그가 다시 났다(이 PC에서 횟수가 늘었다, 회귀 규칙이 다시 실패했다) | 이슈에 덧글을 단다. Jira에서 이미 끝난 이슈면 다시 열고 라벨 `regression`을 붙인다. 새 로그는 다시 첨부한다 |
| Jira에서 끝났다(완료 범주의 상태) | 이 PC에서 `resolved`가 된다. 회귀 규칙이 있으면 `qaplay bugs verify`로 확인해 `verified`로 |
| Jira에서 사람이 다시 열었다 | 이 PC에서도 `open`이 된다 |

- 이은 버그의 상태는 Jira가 기준이다. 이 PC에서 `bugs set --status`로 바꿔도 다음에 맞출 때 Jira의 상태로 돌아온다
- 맞추는 때: `qaplay run`이 버그를 올렸을 때, `bugs pull`, `bugs report`, `bugs verify` 뒤, 그리고 `qaplay bugs sync`(설정 화면의 "지금 보내기")
- 보내지 못하면(네트워크, 권한, 만료된 토큰) 이 PC의 리포트는 그대로 두고 까닭과 할 일을 알린다. 다음에 맞출 때 남은 것부터 보낸다(이미 만든 이슈를 또 만들지 않고, 올리지 않은 첨부만 올린다). 실행의 결과와 종료 코드는 바꾸지 않는다
- 이 PC에서 버그를 지워도(`bugs delete`) Jira의 이슈는 지우지 않는다. 연결만 끊는다
- 다시 열기는 그 이슈의 워크플로에서 "할 일" 범주로 가는 전환을 찾아 쓴다. 그런 전환이 없는 워크플로면 그렇다고 알리고 덧글만 남는다. 그때는 Jira에서 손으로 연다
- 권한: 이 계정에 그 프로젝트의 이슈 만들기, 첨부 만들기, 덧글, 전환 권한이 있어야 한다
