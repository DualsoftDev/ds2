# csvForAI/v1 생성 지침

## 0. 이 형식의 약속 — 먼저 이것부터 읽어라

1. **행 순서에는 아무 의미도 없다.** 기본 3열은 «행 순서 = 실행 순서» 였고 그것이 Flow 병렬 착시의 원인이었다(Flow 를 나눠 적어도 매퍼가 한 줄 체인으로 이었다). csvForAI 는 모든 관계를 `ARROW` 행과 `Detail` 셀에 명시한다. 정렬해도 모델이 바뀌지 않는다.
2. **2열 `Name` 은 언제나 ds2 이름 그대로다.** Work 는 `Flow.Work`, API·Call 은 `Device.Api`. 마디 수(2 vs 4)가 Work/Call 판정이다.
3. **`;` = 여러 개, `>` = 그 다음.** 어느 셀에서든 뜻이 같다.
4. **공란 · `?` · 값** 은 다르다. 공란 = 미기입(경고 대상), `?` = 의도적 미확정(경고 없이 «미확정 N건» 으로 집계), 값 = 확정.
5. **System 은 평면 컬렉션이다.** 부모·자식 열이 아예 없다. 호출이 몇 단계든 포함 계층을 만들지 않는다.

## 1. 7열 × 6 행종류

| Kind | Name | Type | Detail | Time | InTag/OutTag |
|---|---|---|---|---|---|
| `SYS` | System 이름 | `Active`/`Passive` | SystemType(선택) | — | — |
| `FLOW` | Flow 이름 | — | — | — | — |
| `WORK` | `Flow.Work` | `Source+Sink+Ignore+Finish` 조합 | Call DAG 또는 `ref(...)` | Call 없을 때만 | — |
| `ARROW` | 출발점 | `Start Reset StartReset ResetReset Group` | 도착점 `;` 목록 | — | — |
| `API` | `Device.Api` | `<Action>/<Sensing>` | (예약·공란) | 동작 시간 | 입·출력 태그 |
| `COND` | 소유자 경로 | `AutoAux ComAux SkipAction` | 조건식 | — | — |

`#` 로 시작하는 행은 주석이다. 근거·출처·미확정 사유는 여기에 적어라(별도 Evidence 열은 없다).

## 2. 이름 규칙

- 금지 문자: `.` `>` `;` `=` `,` `"` `&` `|` `!` `(` `)` `/` `+` `@` `#` — 모두 문법 기호다. `.` 는 경로 구분자이므로 이름 안에 넣을 수 없다.
- 예약어: 디바이스 `BUFFER`/`CLEAR`, 액션 `DO`/`-`.
- `Active` System 은 **정확히 1개**. 여러 Active System 은 v1 범위 밖이다.
- 같은 System 안 Flow 이름, 같은 Flow 안 Work 이름, 같은 Device 안 Api 이름은 유일해야 한다.

## 3. 모델링 절차 — 이 순서대로 결정하라

### (1) Capa 를 정한다 = FLOW 행 개수
**Flow 1개 = 동시에 보유하는 고유 제품 1개.** 공정 단계 수가 아니다. 스테이션이 5개라도 제품이 한 번에 1개만 있으면 `FLOW` 행은 1줄이다. 자세 변경(회전·승강)은 같은 제품이므로 Flow 를 늘리지 않는다. 불명확하면 1개로 시작하라. **Capa 를 적는 칸은 없다 — 행 개수가 곧 Capa 다.**

### (2) Work 를 나눈다 — 최소로
나누는 근거는 셋뿐이다: ① 같은 Call 이 다시 필요하다(왕복) ② 공정 위상이 바뀐다 ③ 사용자가 명시했다. 길다고 나누지 마라. **조건 때문에 Work 를 쪼개지 마라** — 그건 `COND` 행이 할 일이다(기본 3열에는 조건 칸이 없어 LLM 이 Work 분할로 위조하던 자리다).

### (3) 토큰 경계를 정한다 — `Source` 와 `Sink`
- 진입 Work 에 `Source`. 없으면 자동 시작되지 않고 «Source 후보» 경고가 난다.
- 배출 Work 에 `Sink`. Sink 는 Reset 선행 검사에서 제외된다.
- Group 안 기능 Work 에 `Ignore`. **Group 한 묶음에 비-Ignore 는 정확히 1개**(전달자)여야 한다. 2개 이상이면 경고다.
- `Finish` 는 TokenRole 이 아니라 «초기 상태가 Finish» 표시다(R1 상호 재무장의 복귀측에 쓴다). 아무 행도 `Finish` 를 적지 않으면 엔진이 auto-homing 추론 → Work 이름의 `RET` 문자열 휴리스틱 순으로 스스로 초기 Finish 집합을 만든다. 의도가 있으면 명시하라.

### (4) Work 사이 관계를 전부 적는다 — `ARROW`
| 타입 | 뜻 | 쓰는 곳 |
|---|---|---|
| `Start` | source 완료 → target 시작 | 진입·인계 |
| `Reset` | source 시작 → target 리셋 | 재무장(다중 선행은 OR, 대상은 Finish 뿐) |
| `StartReset` | Start + 역방향 Reset | 파이프라인 인계(다음이 시작하면 이전 자리가 비워짐) |
| `ResetReset` | 양방향 상호 리셋 | R1 상호 재무장 |
| `Group` | 동시 시작·동시 리셋, 외부 관계 공유 | 협동 작업 |

- Group 은 인접쌍만 적으면 된다(유니온-파인드가 합친다). **Group 내부에 Start/Reset 을 덧붙이지 마라** — 내부 순서는 Call 이 정한다.
- 4마디 끝점(`Flow.Work.Device.Api`)이면 Call 레벨 화살표이고 **`Start` 와 `Group` 만 허용**된다. `StartReset` 을 적으면 조용히 강등하지 않고 오류로 거부한다(에디터가 금지한 상태를 CSV 가 합법화하면 안 된다). 두 끝점은 같은 Work 소속이어야 한다.
- Call 사이 `Start` 는 `WORK.Detail` 의 `>` 로 적는 것이 정상이다. Call 레벨 `ARROW` 행은 사실상 `Group` 전용이다.

### (5) 반복을 닫는다 — 이 형식의 핵심 점검
**Reset 화살표를 빠뜨리는 것이 가장 흔한 치명적 결함이다.** Sink 가 아닌 모든 Work 에는 Reset 선행이 있어야 한다. 선행이 되는 경우는 둘이다:
- 누군가 그 Work 로 `Reset`/`ResetReset` 을 보냄, 또는
- 그 Work 가 누군가에게 `StartReset`/`ResetReset` 을 보냄.

배출(Sink)에서 앞단 Work 들로 `Reset` 을 한 줄로 몰아 적는 것이 가장 읽기 쉽다:
`ARROW,제품.배출,Reset,제품.투입;제품.클램프제어;제품.공정제어`
Sink 자신도 재무장해야 하므로 `ARROW,제품.투입,Reset,제품.배출` 을 잊지 마라.

**주의**: Source Work 에 `Start`/`StartReset` 선행을 걸면 자동 시작이 막혀 «Source 인데 선행 있음» 경고가 난다. 투입으로 되돌아오는 재무장은 반드시 `Reset` 이어야 한다.

### (6) 장치를 연결한다 — `API`
한 `API` 행 = 장치쪽 Work 1개 + ApiDef 1개. `Device` 이름이 Passive System 이름이 된다.

| 회로 | Type | InTag | OutTag |
|---|---|---|---|
| 복동 실린더 | `Normal/Normal` | 필요 | 필요 |
| 전기 유지 출력(체결) | `Latch/Normal` | 필요 | 필요 |
| 스프링 복귀(출력 없음) | `Virtual/Normal` | 필요 | 공란 |
| 펄스 기동 | `Pulse(10)/Normal` | 필요 | 필요 |
| 시간 기준 완료(센서 없음) | `Normal/Virtual(500)` | 공란 | 필요 |
| 입력 안정 확인 | `Normal/Normal(100)` | 필요 | 필요 |
| 논리 API(센서·출력 없음) | `Virtual/Virtual(200)` | 공란 | 공란 |

시간 파라미터 규칙(타입이 강제한다): **`Latch`·`Virtual` 출력에는 T 가 없고, `Latch`·`Virtual` 감지에는 T 가 필수다.** 논리 API 를 `Type` 공란으로 두면 기본값 `Normal/Normal` 이 되어 V1·V2 두 Error 가 난다 — 반드시 `Virtual/Virtual(T)` 로 명시하라.

`Time` 은 그 API 의 실제 동작 시간이다. 사용자가 말하지 않으면 추정하라: 공압 실린더 300~800MS, 전동 축 1~3S, 용접 0.5~2S, 로봇 5~10S, 순수 논리 API 는 `?`.

### (7) 조건을 적는다 — `COND`
소유자 경로의 마디 수로 수준이 정해진다. 2마디 = Work(**`SkipAction` 만**), 4마디 = Call(3종 모두).

```
COND,제품.공정제어.공정기.RUN,AutoAux,실린더.ADV               # 체결 완료 후 가공 허가
COND,제품.공정제어.공정기.RUN,AutoAux,실린더.ADV & 안전.DOOR     # AND
COND,제품.가공,SkipAction,!(사양.A)                            # 사양 A 가 아니면 이 Work 생략
COND,제품.품질.기록.NG,SkipAction,판정.OK                       # 판정이 OK 면 NG 처리 Call 생략
COND,제품.이송.축.MOVE,AutoAux,(전공정.DONE | 우회.DONE) & 도착.EMPTY
COND,제품.공정.설비.RUN,AutoAux,레시피.READ=21
```

문법:
- `&`=AND, `|`=OR. **한 괄호 안에 `&` 와 `|` 를 섞지 마라** — ds2 의 조건 노드는 AND/OR 플래그가 하나뿐이라 표현 자체가 불가능하다. 섞으려면 괄호로 층을 나눠라.
- `!` 는 **괄호 바로 앞에만** 올 수 있다. 부정은 그룹의 속성이고, leaf 의 반전은 접점(`/A`)이다. leaf 앞에 `!` 를 쓰면 오류.
- leaf 는 `Device.Api` — 반드시 `API` 행으로 선언된 것이어야 한다. 미선언 참조는 오류다(엔진에서는 조용히 `Const false` 가 되어 SkipAction 은 «영원히 생략 안 함», AutoAux 는 «영원히 실행 못 함» 으로 정반대 증상을 낸다).
- 기대값을 생략하면 `BOOL true` 다. `Undefined` 로 두면 «항상 참» 이 되어 판정이 통째로 무력화되므로 로더가 채운다.
- 접점: `/A`=Nc, `A(R)`=상승, `A(F)`=하강, 상수 `_ON`/`_OFF`. SkipAction 에서는 펄스가 접히므로(Rising→No, Falling→Nc) 그대로 적어도 무방하나 의미를 확인하라.
- **`SkipAction` 의 극성은 «참이면 생략» 이다.** 실행 조건을 그대로 적으면 정반대로 동작한다. 필요한 기능의 실행 조건이 `A` 라면 `!(A)` 라고 적어라.
- **SkipAction 으로 제품 경로를 막을 수 없다.** 배타 인계는 «그 순간 수신 가능한 목적지를 하나로 만드는» 문제이지 생략 조건의 문제가 아니다.

## 4. 자동으로 만들어지는 것 — 적지 마라, 대신 알고 있어라

1. **Passive 디바이스 일체** — `API` 행의 `Device` 마다 Passive System + Flow(`<Device>_Flow`) + Work(= Api 이름) + ApiDef(Tx=Rx=자기 Work) + ApiCall 이 자동 생성된다. `SYS,<Device>,Passive` 행은 SystemType 을 주고 싶을 때만 쓰는 선택 사항이다.
2. **같은 디바이스 API Work 끼리 `ResetReset`** — 등장 순서 인접쌍으로 자동 연결된다(ADV↔RET, PREPARE↔RUN). 적지 마라.
3. **API 가 1개뿐인 디바이스** — `DONE` 더미 Work + `Start` + `ResetReset` 이 자동 추가되고, 그 결과 «Source 후보» 경고가 뜬다. 구동류(실린더·모터)라면 반대 동작(RET/하강/OFF)을 반드시 함께 적어라. 센서·출력 전용 디바이스면 정상이다.
4. **Source Work 의 TokenSpec** — Source 마다 라벨 = Work 이름인 TokenSpec 이 자동 등록된다(없으면 «TokenSpec 미설정» 경고).
5. **Tx/Rx** — 항상 자기 대상 Work 다(단일 API 디바이스만 Rx=DONE). v1 에는 지정 칸이 없다.
6. **Call 레벨 Group 추론은 없다.** 기본 3열은 선행·후행이 같은 Call 들을 자동으로 Group 으로 묶었다(예측 불가한 부작용). csvForAI 는 추론하지 않는다 — Group 이 필요하면 `ARROW … Group` 으로 적어라.

## 5. 흔한 실수 12

1. Flow 를 공정 단계로 쪼갠다 → Flow = 동시 보유 제품 수.
2. 조건을 표현하려고 Work 를 쪼갠다 → `COND` 행을 써라.
3. SkipAction 에 실행 조건을 그대로 적는다 → 극성이 반대다. `!(…)`.
4. leaf 앞에 `!` 를 붙인다 → 부정은 그룹이 진다. leaf 반전은 `/A`.
5. 한 괄호에 `&` 와 `|` 를 섞는다 → 표현 불가. 괄호로 층을 나눠라.
6. Reset 화살표를 빠뜨린다 → 첫 제품만 흐르고 멈춘다. 비-Sink Work 전부 점검.
7. Sink 자신의 재무장을 잊는다 → 두 번째 제품이 배출되지 않는다.
8. 투입(Source)으로 `StartReset` 을 되돌린다 → 자동 시작이 막힌다. `Reset` 이어야 한다.
9. Group 멤버를 전부 비-Ignore 로 둔다 → 전달자를 1개만 남기고 나머지는 `Ignore`.
10. 호출측 Work 에 `Time` 을 적는다 → 동작 시간은 `API` 행에만. Call 이 있는 WORK 행의 `Time` 은 오류다.
11. 논리 API 의 `Type` 을 비운다 → 기본값 Normal/Normal 로 V1·V2 Error. `Virtual/Virtual(T)`.
12. 디바이스를 한 방향만 적는다(전진만, ON만) → DONE 더미가 끼어들고 경고가 난다. 상보 동작을 쌍으로.

## 6. 자가 점검 목록 — 출력 직전에 전부 확인하라

- [ ] 헤더가 정확히 `Kind,Name,Type,Detail,Time,InTag,OutTag` 이고 모든 데이터 행이 7열인가(주석 행 제외)
- [ ] `SYS … Active` 가 정확히 1줄인가
- [ ] `FLOW` 행 개수 = 내가 의도한 Capa 인가
- [ ] 진입 Work 에 `Source`, 배출 Work 에 `Sink` 가 있는가
- [ ] `Sink` 가 아닌 **모든** Work 에 Reset 선행이 있는가(Reset/ResetReset 수신 또는 StartReset/ResetReset 발신)
- [ ] Source Work 에 Start/StartReset 선행이 없는가
- [ ] Group 묶음마다 비-Ignore 가 정확히 1개인가
- [ ] `WORK.Detail` 에 쓴 모든 `Device.Api` 에 대응하는 `API` 행이 있는가
- [ ] `COND` 의 모든 leaf 가 `API` 행으로 선언돼 있는가
- [ ] 모든 디바이스에 API 가 2개 이상인가(센서·출력 전용 제외)
- [ ] `Sensing ≠ Virtual` 인 API 에 InTag, `Action ≠ Virtual` 인 API 에 OutTag 가 있는가
- [ ] `Latch`/`Virtual` **출력**에 T 가 없고, `Latch`/`Virtual` **감지**에 T 가 있는가
- [ ] Call 을 가진 WORK 행의 `Time` 이 비어 있는가
- [ ] Call 레벨 `ARROW`(4마디)의 타입이 `Start` 또는 `Group` 뿐인가
- [ ] 이름에 금지 문자(`. > ; = , " & | ! ( ) / + @ #`)가 없는가
- [ ] 모르는 값은 빈칸이 아니라 `?` 로 두었는가

## 7. 오류 코드 → 고칠 곳

| 코드 | 뜻 | 고칠 곳 |
|---|---|---|
| AI001/002/003 | 헤더·열 개수·알 수 없는 Kind | 1행과 Kind 열 |
| AI004/005/006 | 필수 셀 빈 값 / 이름 중복 / 금지 문자·예약어 | Name 열 |
| AI010 | Active System 이 0개 또는 2개 이상 | SYS 행 |
| AI011/012 | 미선언 Flow / 미선언 API 참조 | FLOW·API 행 추가 |
| AI020/021/022 | WORK Type 오타 / Call 있는데 Time 있음 / ref 행에 다른 값 | WORK 행 |
| AI030/031/032/033 | 끝점 마디 수 불일치 / Call 레벨 금지 타입 / 다른 Work 소속 / 자기 화살표 | ARROW 행 |
| AI040/041 | Action/Sensing 문법 / T 유무 규칙 위반 | API Type |
| AI050/051/052/053 | COND 경로 마디 수 / Work 에 AutoAux·ComAux / 조건식 문법 / leaf 미해결 | COND 행 |
| DAG001/002 | Call 셀 자기 엣지 / 순환 | WORK Detail |
| DUR001/002 | 시간 단위 누락·상한 초과 / 같은 API 시간 충돌 | Time |
| AIW001~008 | 경고: Reset 없음·Source 후보·Group 비-Ignore 2개·단일 API 디바이스·미확정 `?` N건·V1/V2 예고·Time 미지정 | 무시 가능하나 근거를 주석으로 남겨라 |

---

## 부록 A. 예제 — 그대로 붙여넣어 동작한다

```csv
Kind,Name,Type,Detail,Time,InTag,OutTag
# csvForAI/v1 — 지그셀: 한 제품을 고정하고 공정한 뒤 해제한다 (tutorials_Ver71/11_spec_modeling.md 모델 1)
# Capa 1 = FLOW 행 1개. 행 순서에는 의미가 없다 — 모든 관계는 ARROW 행과 Detail 셀에 적혀 있다.
SYS,지그셀,Active,,,,
SYS,실린더,Passive,Cylinder_2Pos,,,
SYS,공정기,Passive,,,,
FLOW,제품,,,,,
WORK,제품.투입,Source,,,,
WORK,제품.클램프제어,,실린더.ADV>실린더.RET,,,
WORK,제품.공정제어,Ignore,공정기.PREPARE>공정기.RUN,,,
WORK,제품.배출,Sink,,,,
ARROW,제품.투입,Start,제품.클램프제어,,,
ARROW,제품.클램프제어,Group,제품.공정제어,,,
ARROW,제품.클램프제어,Start,제품.배출,,,
ARROW,제품.배출,Reset,제품.투입;제품.클램프제어;제품.공정제어,,,
ARROW,제품.투입,Reset,제품.배출,,,
API,실린더.ADV,Latch/Normal,,100MS,%IX0.0.0,%QX0.0.0
API,실린더.RET,Virtual/Normal,,100MS,%IX0.0.1,
API,공정기.PREPARE,Virtual/Virtual(200),,?,,
API,공정기.RUN,Normal/Normal,,150MS,%IX0.0.2,%QX0.0.2
COND,제품.공정제어.공정기.RUN,AutoAux,실린더.ADV,,,
COND,제품.클램프제어.실린더.RET,AutoAux,공정기.RUN,,,
```
