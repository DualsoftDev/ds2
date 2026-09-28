# csvForAI/v1 스키마

한 장의 CSV 로 «Active System 1개의 제어 모델 전체» 를 적는 태그형(멀티섹션) 형식이다. 1열 `Kind` 가 행 종류(SYS/FLOW/WORK/ARROW/API/COND)를 정하고, 2열 `Name` 은 언제나 그 엔티티의 ds2 이름 그대로(`Flow.Work`, `Device.Api`)다. 기존 3열이 못 담던 Arrow 5종·조건 트리·ApiDef Action/Sensing·IO 태그·재무장을 모두 명시 행으로 싣는다. **행 순서에는 어떤 의미도 없다** — 기존 형식의 «행 순서 StartReset 체인» 암묵 규약을 버리고 모든 관계를 명시한다. Passive 디바이스 System·ApiDef·ApiCall·TokenSpec 은 기존 캐스케이드가 그대로 만든다(ImportPlanOperation 추가 0건, Ds2.Core 무수정).

## 열 정의

| 열 | 필수 | 뜻 | 값 문법 | 예 |
|---|---|---|---|---|
| `Kind` | 필수 | 행 종류. 이 값이 나머지 6열의 뜻을 결정한다. 6종 + 주석. `#` 로 시작하면 주석 행이며 열 개수 검사에서 면제된다(Excel 이 채운 빈 칸도, 쉼표가 든 한 칸짜리 메모도 통과). | SYS \| FLOW \| WORK \| ARROW \| API \| COND \| #… | `WORK` |
| `Name` | 필수 | 그 행의 주어. **언제나 ds2 가 쓰는 이름 철자 그대로** 적는다 — Work 는 `Flow.Work`(= ds2 Work.Name), API/Call 은 `Device.Api`(= ds2 Call.Name). ARROW 행에서는 화살표의 출발점, COND 행에서는 조건을 소유한 Work(2마디) 또는 Call(4마디). 마디 수가 곧 종류 판정이라 별도의 «레벨» 열이 필요 없다. | SYS/FLOW: `<이름>` · WORK: `<Flow>.<Work>` · API: `<Device>.<Api>` · ARROW: `<Flow>.<Work>` 또는 `<Flow>.<Work>.<Device>.<Api>` · COND: 같음(2마디=Work, 4마디=Call) | `제품.클램프제어` |
| `Type` | 선택 | 그 행의 열거값. Kind 마다 어휘가 서로소라 오타가 곧바로 «이 Kind 에는 없는 값» 으로 잡힌다. WORK 만 `+` 로 이어 붙인 플래그 집합이고 나머지는 단일 값. API 는 Action 과 Sensing 을 한 칸에 `/` 로 적되 시간 파라미터는 각자 괄호 안에 둔다(둘은 서로 다른 값이므로 한 칸으로 뭉개면 안 된다). | SYS: `Active\|Passive` · WORK: `{Source\|Sink\|Ignore\|Finish}` 를 `+` 로 결합(빈 칸 = 역할 없음) · ARROW: `Start\|Reset\|StartReset\|ResetReset\|Group` · API: `<Action>/<Sensing>` — Action=`Normal[(ms)]\|Pulse[(ms)]\|Latch\|Virtual`, Sensing=`Normal[(ms)]\|Latch(ms)\|Virtual(ms)` · COND: `AutoAux\|ComAux\|SkipAction` | `Latch/Normal` |
| `Detail` | 선택 | 그 행의 목적어. Kind 별로: SYS=SystemType(선택), WORK=Call DAG 또는 `ref(...)`, ARROW=도착점 목록, COND=조건식. API 행에서는 **v1 예약(반드시 공란)** — 비우지 않으면 오류. 세 칸 모두에서 `;` 는 «서로 독립인 여러 개», `>` 는 «그 다음» 이라는 뜻을 일관되게 갖는다. | WORK: `Dev.Api>Dev.Api;Dev.Api>…`(`>`=Call Start 엣지, `;`=병렬 경로, 같은 이름=같은 노드) 또는 `ref(<Flow>.<Work>)` · ARROW: `<대상>;<대상>;…`(전부 Name 과 같은 마디 수) · COND: `A & B`, `A \| B`, `!(A & B)`, `/A`(Nc접점), `A(R)`/`A(F)`(상승·하강), `A=21`, `A=[10..20]`, `A="OK"`, `_ON`/`_OFF` | `실린더.ADV>실린더.RET` |
| `Time` | 선택 | 동작 시간. **API 행에 적는 것이 정상 위치** — 값은 그 API 의 «Call 없는 대상 Work.Duration» 에 실린다(계약: 동작 시간은 Call 없는 Work 에만 한 번). WORK 행에는 Detail 이 빈(=Call 이 없는) Work 일 때만 허용하고, Call 을 가진 WORK 행에 값이 있으면 오류(AI021) — 계약 위반을 구조로 막는다. 단위 필수(MS\|S), 상한 24시간. 공란 = 미지정(Duration 없음, 즉시 완료) + 경고, `?` = 의도적 미지정(경고 없음). | `<숫자>MS` \| `<숫자>S` \| `?` \| 공란 | `100MS` |
| `InTag` | 선택 | 확인(입력) 태그. API 행 전용. `SensingType ≠ Virtual` 이면 채워야 한다(없으면 V2 Error 를 예고하는 경고). 심벌·자료형·기대값은 선택이며 기본은 심벌 `<Device>_<Api>_IN`, 자료형 BOOL, 기대값 true. | `[<심벌>@]<주소>[:<DataType>][=<기대값>]` — DataType ∈ BOOL\|SINT\|INT\|DINT\|LINT\|USINT\|UINT\|UDINT\|ULINT\|REAL\|LREAL\|STRING | `%IX0.0.0` |
| `OutTag` | 선택 | 요청(출력) 태그. API 행 전용. `ActionType ≠ Virtual` 이면 채워야 한다(없으면 V1 Error 예고 경고). Virtual 출력(스프링 복귀·논리 API)은 공란이 정상이다 — 공란과 «미기입» 을 구분하려면 `?` 를 쓴다. | InTag 와 동일 | `%QX0.0.0` |

## 범위 밖 — 넣지 않기로 한 것

- 여러 Active System · System 계층 — 한 파일 = Project 1 + Active System 1. 기존 CsvImporter.buildStore 가 그 구조를 만들고 Passive 는 캐스케이드가 만든다. Active 는 «Control 모드의 제어 기준 선택» 이라 프로젝트당 보통 1개이며, 복수 Active 는 store 빌더부터 달라진다. 계층은 계약이 금지하므로 parent 열 자체를 만들지 않았다.
- ApiDef 의 Tx/Rx 명시 지정 — DS2_ApiDef_Contracts 실측에서 장치급 ApiDef 239건이 **전부 Tx==Rx** 이고, Tx≠Rx 인 15건은 System 공개 경계 API(열 집합이 아예 다른 별도 층)다. 2열을 추가해 239행이 같은 값을 두 번 적게 하는 대신 자동 배선(Tx=Rx=자기 Work, 단일 API 디바이스만 Rx=DONE)을 유지했다. API 행의 Detail 칸을 예약해 두어 v2 에서 열 수 있다.
- 같은 Call 에 ApiCall 여러 개(솔레노이드 1개 ↔ 실린더 N개) — 표준 9열의 «CSV 한 행 = 버킷 1건» 의미가 이미 이것을 담당한다(LATCH.RET = 5행 = ApiCall 5). csvForAI 는 (Call, ApiDef) 당 ApiCall 1개로 고정했다. 두 의미를 한 표에 섞으면 호출 화살표의 양 끝이 같은 이름으로 충돌한다.
- CallType(SkipIfCompleted) · Interlocked · SequenceLabel · Position — SkipIfCompleted 는 실측 239건 중 1건(A08.Axis.AT_BASE), Interlocked 는 v12 abnormal 게이팅 전용, SequenceLabel 은 Promaker 가 화살표 토폴로지에서 자동 지정, Position 은 AutoLayout 소관. 전부 열 하나를 전 행에 붙일 만한 빈도가 아니다.
- MinDuration / MaxDuration — v12 이상감지 기준값이며 «runtime 상태 진행에는 쓰지 않는다»(V10Validation 주석). 시퀀스 모델이 아니라 감시 파라미터라 시퀀스 CSV 의 열이 아니다.
- TokenSpec.Fields(제품 데이터 바인딩) — Source 당 TokenSpec 1개는 자동 생성(라벨=Work 이름)하되 Fields 는 비운다. 00_model_contract 가 «TokenSpec.Fields 가 조건·출력에 자동 연결되거나 비교·카운터가 자동 생성된다는 뜻은 아니다» 라고 명시한다. 채울 칸을 주면 «적었으니 동작한다» 는 오해를 형식이 만든다.
- 유한 반복(목표 횟수·계수·재요청) — DS 원시요소에 카운터가 없고 10장이 «전체 실행 미검증» 으로 남겨 둔 영역이다. 열을 만들면 LLM 이 발명한다.
- 큐·승인 정책·값 비교 알고리즘(Atlas D·E 범위) — Atlas 자신이 «큐는 ReferenceOf 의 자동 기능이 아니라 별도 상태», «동적 비교는 adapter 책임» 으로 봉인한 것이다. 표로 적을 수 없는 세 가지(인계 원자성 · 어댑터 알고리즘 · liveness)는 생성 대상이 아니라 선언된 의무다.
- 행마다 Evidence(design|code|run) 열 — ds2 에 저장 필드가 없어 왕복에서 소실되고, 소실되는 열은 «검증됐다» 는 거짓 확신만 남긴다. 대신 `#` 주석 행(자유 서술)과 `?` 합법값(+ 로더가 «미확정 N건» 집계)으로 대체했다. 형식이 지킬 수 있는 정직성만 형식에 넣었다.
- upsert(OP=add|update|remove) — 현행 CSV 경로는 ReplaceStore 전량 교체이고 ImportPlanOperation 의 RemoveEntity/RenameEntity 는 CSV 에서 단 한 번도 발행되지 않는다(CSV 는 순수 add-only). 병합은 안정 키 설계와 applyTracked 파급을 동반하는 별도 과제다.
- 내보내기(Export) — v1 은 import 전용. 현행 CsvExporter 는 표준 9열만 지원하며 그마저 화살표·Duration·Condition 이 소실돼 무손실이 아니다. 무손실 왕복은 별도 목표로 두어야 한다.
- Group 을 포함 계층으로 두는 것 — 00_model_contract 가 «Group 은 Work 사이의 관계이며 별도 포함 계층이 아니다» 라고 명시한다. 그래서 Group 전용 Kind 를 만들지 않고 ArrowType 5번째 값으로만 표현한다.

## 구현 결정

- 미지정 Time → 500ms (기존 3열 `ImportPlanDeviceOps.defaultWorkDuration` 과 같은 값)
- 센서 전용 디바이스 → 선언 표기로 DONE 더미·Source 후보 경고 면제
- `ref(...)`(ReferenceOf) → v1 제외
- 헤더 → 영문 고정 (한글 별칭 없음)
