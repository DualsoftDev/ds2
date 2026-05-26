# IO List + 제어시스템 사양 + 조립작업서 기반 Promaker DS 모델 초안 생성 가능성 검토

> 본 문서는 광명2 SIDE OUTER 라인의 **3종 입력 자료**가 같이 주어졌을 때,
> 이를 활용하여 Promaker 의 DS(Dualsoft) 모델 (YAML v0 protocol) **초안**을
> 자동/반자동 생성할 수 있는지를 검토한 결과를 정리합니다.
>
> 비교 대상: `/f/Git/ev2/master/solutions/Ev2.Backend/src/Ev2.Oracle/` (PLC 역공학 →
> tag/공정/device/action 추출 파이프라인) 의 분석 방식.

---

## 0. 한눈 결론

**3 자료의 역할 비유**:

| 자료 | 역할 | 비유 |
|---|---|---|
| **xlsx IO List** | 물리 IO 매핑 (5000+ 비트) | **신경** — 신호의 위치 |
| **PDF 제어시스템 사양** | 명명 규칙 SSOT + 어휘 + 설비 카탈로그 + 통신/안전 사양 | **두뇌** — 의미와 약속 |
| **xlsx 조립작업서** | 공정별 step 시퀀스 + 작업 시간 (간트 차트) | **척추** — 동적 흐름 |

**합산 결과**: PLC Ladder 없이도 **Promaker DS 모델의 핵심 구조 (zones / devices / actions
/ Active Flow / arrows / workDuration) 한정 자동 생성 가능 — 가중치 추정 85~90%**[^cov].
callCondition 트리 · FB call graph · contactKind (NC/NO) · 정확한 timeout/retry 는
별도 가용성 (§6) — Ladder export 가 유일한 정답.

[^cov]: 산식 = Promaker YAML 의 token 비중 가중 평균. §2 매트릭스 22 행 단순 평균은 73~81%
지만, DS 모델의 token volume 중 약 70% 가 zones/devices/actions/Flow/arrow/workDuration
이고 이 영역의 자료 커버리지가 95%+ 라 가중치 적용 시 85~90%. callCondition / FB 구조 /
contactKind 는 token 비중 ~10% 이지만 자료 커버리지 0~20% 라 평균을 끌어내림. **PR-I1
PoC 실측 후 갱신** — 현 값은 단일 라인 (광명2) 가정 추정.

**최종 채택 시나리오 (§7) = A+++++ (LightHouse 색인 시점 specialized 박제 + chat 시작 시 강제 주입)**.
사용자가 KB 폴더 등록 한 번 → 색인 단계에서 signature 인식 + strategy 별 정제 markdown 박제
(`.lighthouse-kb/summary/*.md`) → chat 시작 시 전체 자동 주입 → Anthropic prompt cache hit
→ 모든 후속 turn 입력 비용 ~10x 절감 + 다른 사용자에게도 자동 공유 (KB 차원 — markdown
파일 공유. cache 차원은 Anthropic org-key 단위 격리라 다른 사용자에게 cache hit 자동
전파 안 됨)[^cache].

[^cache]: Anthropic prompt cache TTL default = 5분, extended = 1시간 (cache write 1.25x
cost). 본 시나리오의 turn-gap 분포는 PoC 단계 미측정 — done-promaker-llm-roundtrip-optimization.md
의 측정 결과 참조 권장.

**시나리오 명명 정합**: A → A++ → A+++ → A++++ → A+++++ 의 5 단계 (A+ 의도적 부재 —
시나리오 차이가 step-wise 가 아니라 strategy 위치의 점프). §7.1 표 5 행과 정합.

**구현 단계** (§9 — Phase P1~P4 명명, alphabet A~A+++++ 시나리오와 구분):
- **P1** (xlsx/pdf strategy + 박제 + 주입, 1.5~2주)
- **P2** (Promaker YAML 매퍼 + 도메인 전문가 검수 후 golden 박제, 0.5~1주)
- **P3** (다른 라인 확장, 라인 당 0.5주)
- **P4** (image caption strategy + KMM 매뉴얼 prior-art 활용, 1주)

전체 wall-clock 합 = P1+P2+P4 = 3~4주 + P3 (라인 N 개당 0.5N 주). §8.7 의 "2~2.5주" 는
**strategy 코드 자체** 의 추정치 (P1 + P4 의 일부) 이며 P2/P3 의 매퍼·검수·라인 확장은 별도.

P4 단계가 §8.6 의 `CaptionPromptStrategy` 7 kind 분기를 도입하여 KMM 매뉴얼의 **INTERLOCK
도식 + FLOW CHART** 를 결정 근거로 부분 활용 — **callCondition 트리 ~30~50% + Arrow
Type 결정 ~70~80% 보강** (두 영역 독립).

**HKMC NDA / 외부 LLM 전송 동의** ⚠️ — 본 PoC 의 입력 자료 (xlsx IO List / PDF
제어시스템 사양 / xlsx 조립작업서 / KMM 매뉴얼) 는 **HKMC 사내 자료**. 운영 진입
전 (a) HKMC NDA 정합 확인, (b) 외부 LLM API (Anthropic / OpenAI) 로의 전송 동의 별도
결정, (c) IP 마스킹 (사양 코드 / 차종 ID 등) 정책 필요. 본 문서는 PoC 가능성 검토만
다루며 운영 정책은 별 문서.

---

## 1. 입력 자료 3종 개요

### 1.0 약어 / 도메인 용어 (M20)

| 약어 / 용어 | 의미 |
|---|---|
| **SSOT** | Single Source of Truth — 사실 단일 출처 |
| **KMM** | (멕시코 차체공장 라인 식별자 — KMM 라인 운영 매뉴얼 작성자가 사용한 약어) |
| **HKMC** | 현대-기아 자동차 (Hyundai-Kia Motor Company) |
| **FB** | Function Block — PLC 의 캡슐화 단위 |
| **CT** | Cycle Time — 1 공정의 처음~끝 누적 시간 |
| **UPH** | Units Per Hour — 시간당 생산 단위수 |
| **Active / Passive** | Promaker DS 모델의 시스템 종류 — Active = 제어 흐름, Passive = 장치 |
| **Flow / Work / Call** | Active System 의 entity — Flow ⊃ Work ⊃ Call(=ApiCall) |
| **TokenRole=Source** | Active Flow 의 시작점 (in-degree 0 의 Work) |
| **opposing** | device 의 상호배타 action 쌍 정책 (`chain` = ADV/RET 쌍 자연, `none` = 무관, `all-pairs` = 모든 N 쌍) |
| **Arrow Type** | Work 간 또는 Call 간 전이 종류 (Start / Reset / StartReset / ResetReset / Group / Unspecified) |
| **callCondition** | Call 의 trigger 조건 트리 (recursive AND/OR/NOT) |
| **contactKind** | PLC 입력 신호의 NC (Normally Closed) / NO (Normally Open) 구분 |
| **박제** | 본 문서에서 "(코드/문서에) 코드/data 형태로 고정 박혀 들어감" 의미 — 결정 사항이 코드 SSOT 가 됨 |

### 1.1 자료 A — xlsx IO List

- **경로**: `F:/Git/dualsoft/secrets/KBSamples/core/4-3. 광명2 SIDE OUTER SV IO LIST STD MAP.xlsx`
- **원 작성 경로**: `E:\PROJECT\광명2 SV\4. IO리스트\` (xlsx 메타데이터)
- **시트 수**: 43개
- **PLC**: LS XGT (시트 R1 에 명시)
- **프로젝트**: AutoLand 광명 2 차체공장 / SV / SIDE OTR / SIDE OTR MAIN

#### 시트 분류 (43개)

| # | Sheet name | 비고 |
|---|---|---|
| 1 | COVER | 표지 |
| 2 | MCP | Main Control Panel |
| 3 | S_OTR_PLT_S_BOX1 | Side Outer Plate Sub-Box |
| 4 | REINF_PLT_S_BOX1 | Reinforce Plate Sub-Box |
| 5 | EXTN_PLT_S_BOX1 | Extension Plate Sub-Box |
| 6~9 | S201/S203/S204/S205_S_BOX1 | 스테이션별 Sub-Box |
| 10~23 | S201_RB1_3-01 ~ S205_RB5_3-37 | 로봇 IO (14개 RB) |
| 24 | S_OTR_PLT | Side Outer Plate |
| 25 | S201_ANTI_JIG | S201 Anti Jig |
| 26 | REINF_PLT | Reinforce Plate |
| 27 | S202_RENIF_ARRANGE_JIG | (REINF 오타 — 원본 보존, WorkOrderStrategy 가 normalize 안 함 — source-of-truth 손실 회피) |
| 28 | EXTN_PLT | Extension Plate |
| 29 | S203_EXTN_ARRANGE_JIG | |
| 30 | S203_WRS | Welding Robot System |
| 31~35 | S204_KEY_JIG1/KEY1/WRS1/KEY_JIG2/WRS2 | S204 Key & WRS |
| 36 | S205_RESPOT_JIG | |
| 37 | S205_WRS | |
| 38 | S204_BNR_PNL | B&R Panel |
| 39 | S204_BnR_SERVO_6-11 | B&R Servo |
| 40 | S205_FESTO_PNL | Festo Panel |
| 41~42 | S205_SRV1_6-21, SRV2_6-22 | 서보 |
| 43 | Sheet1 | (빈/임시) |

#### 시트 내부 레이아웃 (실측)

각 데이터 시트는 IO LIST 도면 한 페이지를 그대로 옮긴 형태로,
**세로로 표가 아니라 가로로 4~5 word block 이 나열**됨.

| 열 묶음 | 의미 |
|---|---|
| `B`/`G`/`L`/`Q`/`V` | `IW`/`QW` (영역) + 워드 주소 (예: `2010`, `5410`) |
| `C`/`H`/`M`/`R`/`W` | **Tag Name** (예: `S201_I_RB1_HOME_POS`, `S204_WRS_1ST_PIN1_ADV1`) |
| `D`/`I`/`N`/`S`/`X` | **DataType** (거의 모두 `BOOL`) |
| `E`/`J`/`O`/`T`/`Y` | **Address** (`%IW2010.0` ~ `.15`) |
| `F`/`K`/`P`/`U`/`Z` | **Symbol/외부모듈 ID** (예: `WRS01`, `WRS02`) — 일부 시트만 |
| `R1` | 헤더 (PLC : LS XGT / I/O LIST) |
| `R2` | 메타 (Project, 공정, 파트=시트명) |
| `R3` | 표 헤더 (워드/박스 명/타입/네트워크/심볼) |
| `R4` | Zone meta (zone prefix, Box 명, Network/Slot 번호) |
| `R5` | 컬럼 헤더 (변수명/타입/어드레스/심볼) |
| `R6+` | 데이터 행 — IW/QW 워드 단위로 16 bit (`.0`~`.15`) |

데이터행은 시트당 `R6 ~ R36` 정도. **43 시트 × ~120 비트 ≈ 5000+ 물리 IO 비트**.

---

### 1.2 자료 B — PDF 제어시스템 사양서

- **경로**: `F:/Git/dualsoft/secrets/KBSamples/core/3.광명2_전동화공장_제어시스템(HMI편집됨).pdf`
- **165 페이지** PowerPoint export (한국어 텍스트 정상 추출)
- **작성**: 자동화기술실 / 설비제어기술2팀 / 2022.12.15

#### 4개 핵심 영역

**영역 1 — 표준 심벌 작성 SSOT (P45~50)** ⭐⭐⭐

광명2 차체공장 전체 LS XGI 태그 명명 규칙:

> 표준 SYMBOL = `(①설비/라인/제어반)_(②부품명/인터록/더미)_(③차수+유니트명/인터록기기)_(④상태+인터록내용)`
> SYMBOL 24자 이내 영문, COMMENT 는 공사 넘버링

각 항목 약어 사전 (총 200+ entry):

| Page | 항목 | 핵심 약어 |
|---|---|---|
| P46 | ① 설비/라인/제어반 | SHTL, INDX, LNR, TT, **MCP, LCP1/2, SOP, HOP, AOP**, PLT, **SOTR=SIDE OTR Line**, FF, BB, BR, BC, **S201/S301L=Station** |
| P47 | ② 부품/인터록/더미 | PB, SPB, KS, CAM, EPB, LS, RS, PRS, PHS, SOL, LM, **I=Interlock input, Q=Interlock output, MCI/MCQ=마그네트** |
| P48 | ③ 차수+유니트/인터록기기 | UNIT/U, **PIN/P, LATCH/L**, SHIFT/SHT, CLP, **1ST/2ND/3RD/4TH=차수**, STP, HOOK, HK_UP/HK_DN, EJECT, INV, SRV, EOCR, **RBT9=Robot, MOT** |
| P49 | ④ 상태+인터록 | AUTO/MANU/READY, **ADV/RET/UP/DOWN/FWD/BWD/ON/OFF/OPEN/CLOSE/PRESS**, **HOME_POS, LAST_WK_COMP/1ST_WK_COMP/IN_OK/IN_COMP**, JOG/STEP, NORMAL/ERR/FAULT/TRIP |
| P50 | SAMPLE 17건 | `MCP_KSD_S363_AUTO`, `S711_SOP_PBI1_READY`, `S216_I_INV1_FWD_COMP`, `S366_I_RB1_HOME_POS`, … |

**영역 2 — SIDE 작업 순서 + 토큰방식 (P61, P4/P63)** ⭐⭐⭐

P61 의 SIDE 작업순서 (high-level):
> 로봇 대차 픽킹 → 로딩공정 진입 & 대기 → 판넬로딩 후 클램프 잠김 → 용접공정 이동 → 용접 후 언클램프

P4 + P63 의 토큰방식 통신:
> "송신 데이터 (토큰방식)"
> "출력 신호 OFF 기능 없어서 CPU STOP 시 출력 지속 유지 됨"

→ Promaker DS 모델의 `tokenRole: Source` / Active System Flow / Arrow 구성 근거.

**영역 3 — SIDE 라인 설비 상세 (P87~104)** ⭐⭐⭐

7번 절 "SIDE 작업 내용" 이 IO List 와 1:1 매칭:

| PDF 페이지 | 공정# | 사양 요약 | IO List 시트 매칭 |
|---|---|---|---|
| P88 | — | S/OTR PLT RACK 6set | `S_OTR_PLT_S_BOX1`, `S_OTR_PLT` |
| P89 | **#201** | 안티스패터 정렬지그 — 차종 1, 안착 2 | `S201_ANTI_JIG`, `S201_S_BOX1` |
| P90 | — | REINF PLT RACK 6set | `REINF_PLT_S_BOX1`, `REINF_PLT` |
| P91 | **#202** | REINF 정렬지그 — 차종 1, 사양 2, 안착 2 | `S202_RENIF_ARRANGE_JIG` |
| P92 | — | EXTN PLT RACK 6set | `EXTN_PLT_S_BOX1`, `EXTN_PLT` |
| P93 | **#203** | EXTN 정렬지그 — 4 sensors | `S203_EXTN_ARRANGE_JIG`, `S203_WRS` |
| P94 | **#204** | S/OTR KEY 지그 — **핀 3SET, 래치 20SET, 파트 4EA, 턴테이블 12EA, B&R 서보 1SET, EtherNet/IP** | `S204_KEY1`, `S204_KEY_JIG1/2`, `S204_BNR_PNL`, `S204_BnR_SERVO_6-11` |
| P95 | — | FDR MTG BRKT 피더기 | (별도 시트 없음) |
| P96 | **#205** | S/OTR 증타지그 — **핀 3SET, 래치 9SET, 파트 8EA, 다차종 서보 2SET (FESTO 1축)** | `S205_RESPOT_JIG`, `S205_FESTO_PNL`, `S205_SRV1_6-21`, `S205_SRV2_6-22` |

**P104 SIDE 라인 예상 I/O 표** — 어댑터 sanity check 표준값:

| 공정# | 설비명 | 입력 | 출력 | 특이사항 |
|---|---|---|---|---|
| #201 | 안티스패터 정렬지그 | 3 | 0 | |
| #202 | REINF 정렬지그 | 5 | 0 | |
| #203 | EXTN 정렬지그 | 4 | 0 | |
| **#204** | S/OTR KEY 지그 | **62** | **24** | B&R 서보 턴테이블 1SET |
| #205 | S/OTR 증타지그 | 32 | 14 | FESTO 1축 서보 2SET |
| #211 | S/ASSY KEY 지그 | 54 | 26 | |
| #231 | S/ASSY 증타지그 | 18 | 10 | |
| #232 | SMS 측정지그 | 2 | 0 | SMS 비전 인터록 |
| #233 | 검사지그 | 12 | 4 | 보조조작반 1SET |
| 계 | 로봇 27 (용접 14) | — | — | 인터록: 비전 1, QR랙방 2, 장비간 1, 서열 5 |

**영역 4 — PLC/통신/안전 시스템 메타 (P3~6, P59~62, P65)** ⭐⭐

- P3-5: 공장 LAYOUT, 추진일정 (24년 6월 SV 양산), UPH 46.4, 공정수 110→74
- P5: PLC 시스템 구성도 (메인 제어반, MES/설비관리 라우터, 헤밍, 셔틀, 로봇벅)
- P59-60: 안전시스템 (접점 이중화, 비상정지/라이트커튼/매트SW/도어SW)
- P61: 통신 — **Profi-bus DP** (SIDE), **RAPIENET** (3축지그), **EtherNet/IP** (B&R 서보)
- P62: 로보트 행어 계통도 — 제어반→인터록반→로봇하단BOX→ATC척→행거內 PLC

---

### 1.3 자료 C — xlsx 조립작업서

- **경로**: `F:/Git/dualsoft/secrets/KBSamples/core/4-1. SV_SIDE_조립작업서_240328.xlsx`
- **13 시트** (메타 4 + 공정별 시퀀스 9)

#### 시트 구조

| 시트 | 의미 |
|---|---|
| `1.표지` / `2.변경사항FUP` | 메타 |
| `3.장비&작업자` | **공정 × 설비 매트릭스** — 공정별 ROBOT/T-C/T-R/GUN/SEALER 수량 + AUTO/MANUAL + SPOT/SEAL/HDL'G/SPOT&SEAL'G 작업 분류 |
| `4.안전&작업설비` | 안전 설비 표 |
| **`5-1. #201`** ~ **`5-10. #233`** | **각 공정의 step 시퀀스 + 시간 차트** (9개, IO List 시트와 1:1) |

각 `5-x` 시트의 컬럼 매핑:
- `AB`=No, `AC`=Sym, `AD`=작업내역, `AJ`=시작(초), `AK`=시간(초), `AL`=누계(초)
- `AM~CU` = 10/20/30/.../70초 타임라인 (간트 시각화)
- `DG/DH` = 점수 / 등급

#### 기호 SSOT (R2-R6) — Promaker `apis:` 의 정식 명명 후보

| Sym | 의미 (한국어 → 영어) | 대응되는 Promaker device action |
|---|---|---|
| **L** | 로딩 (Loading) | Robot Work / Load |
| **UL** | 언로딩 (Unloading) | Robot Work / Unload |
| **W** | 용접 (Welding) | Gun/Tip Work |
| **SL** | 실링 (Sealing) | Sealer Work |
| **A** | 접착 (Adhering) | Adhesive Work |
| **T** | 체결 (Tightening) | Bolting/Tightening |
| **C** | 클램프 (Clamp) | Cylinder/Clamp.ADV (ON) |
| **UC** | 언클램프 (Unclamp) | Cylinder/Clamp.RET (OFF) |
| **S** | 위치결정 (Setting) | Positioning/Pin.ADV |
| **M** | 운반 (Material Handling) | Robot.Move |
| **G** | 사상 (Grinding) | Grinder Work |
| **AJ** | 조정 (Adjusting) | Manual Work |
| **CN** | 청소 (Cleaning) | Cleaning Work |
| **E** | 기타 (Etc) | — |

#### 실제 추출된 작업 시퀀스 (3 공정 사례)

**#201 — 안티스패터 정렬지그 (CT 55초)**

| step | Sym | 작업 | start | dur | cum |
|---|---|---|---|---|---|
| 1 | M | 201-1호 원위치→랙방 이동 | 0 | 4 | 4 |
| 2 | UL | S/OTR 랙방 취출 | 4 | 10 | 14 |
| 3 | M | 실러자세 이동 | 14 | 5 | 19 |
| 4 | SL | BPR실러 도포 (L=450×100mm) | 19 | 7 | 26 |
| 5 | M | 201-1호 안티스패터 지그 이동 | 26 | 4 | 30 |
| 6 | M | 201-1호 안티스패터 지그 로딩 | 30 | 10 | 40 |
| 7 | SL | 201-2호 안티스패터 도포 | 40 | 8 | 48 |
| 8 | M | 201-1/2호 원위치 | 48 | 7 | 55 |

**#204 — S/OTR KEY 지그 (CT 76초)** ⭐ IO List `S204_KEY1` zone 의 동적 흐름

| step | Sym | 작업 | start | dur | cum | 비고 |
|---|---|---|---|---|---|---|
| 1 | M | 턴테이블 터닝 | **0** | 5 | 5 | **병렬 source 1** |
| 2 | M | 204-1/2/5호 원위치→지그 이동 | **0** | 3 | 3 | **병렬 source 2** |
| 3 | UL | S/OTR, REINF, EXTN 취출 | 3 | 5 | 8 | step2 의존 |
| 4 | L | OTR COMPL → 키지그 로딩 | 8 | 10 | 18 | step3 의존 |
| 6 | C | OTR COMPL 지그 클램핑 | 18 | 4 | 22 | **← IO List `S204_WRS_*_ADV` 매칭** |
| 7 | W | OTR COMPL 키용접 | 22 | **50** | 72 | 최장 step |
| 8 | UC | OTR COMPL 지그 언클램핑 | 72 | 4 | 76 | **← IO List `S204_WRS_*_RET` 매칭** |

**#205 — S/OTR 증타지그 (CT 76초)**

| step | Sym | 작업 | start | dur | cum |
|---|---|---|---|---|---|
| 1 | M | 205-1호 → 키지그 이동 | 0 | 5 | 5 |
| 2 | M | OTR COMPL 취출 | 5 | 7 | 12 |
| 3 | M | 증타 지그 이동 및 로딩 | 12 | 10 | 22 |
| 4 | M | 205-3호 FDR MTG 이동 및 취출 | **0** | 8 | 8 | **병렬 (다른 로봇)** |
| 6 | C | OTR COMPL 증타 지그 클램핑 | 22 | 2 | 24 |
| 7 | W | OTR COMPL 증타 용접 | 24 | **45** | 69 |
| 8 | UC | OTR COMPL 증타 지그 언클램핑 | 69 | 2 | 71 |
| 9 | M | 원위치 | 71 | 5 | 76 |

→ **start=0 가 둘 이상 = 병렬 시작 (`tokenRole: Source` 가 N개), 후속 step 은 `누계 = 이전 시작 시간` 관계로 dependency 자동 추출**.

---

## 2. 3자료 합산 정보 layer 매트릭스

**매트릭스 셀의 정량 정의** (M10):
- `✅` = **자동 결정론적 추출 가능** (어댑터 또는 fixed-format SSOT 만으로 100% 추출)
- `◐` = **LLM 추론 또는 사용자 보정 필요** (자료에 신호는 있으나 ambiguous, 20~80%)
- `❌` = **자료 부재** (해당 정보의 신호 자체가 자료에 없음, 0%)

"종합 커버리지" 열의 % 는 3 자료 중 가장 강한 신호 기준이며 정확 추출률이 아님 —
즉 100% 는 "신호가 자료에 존재함" 보장이고 "정확 추출 보장" 은 아님. 정확 추출은
어댑터/LLM 의 quality 의존.

| Promaker DS 모델 요소 | xlsx IO List | PDF 제어시스템 | 조립작업서 | 종합 커버리지 |
|---|:-:|:-:|:-:|:-:|
| **물리 IO 비트 (Address)** | ✅ | — | — | 100% |
| **Tag 명명 규칙** | — | ✅ P45-50 | — | 100% |
| **공정 # ↔ 한국어 이름** | — | ✅ P89-96 | ✅ 시트명 | 100% |
| **설비 카탈로그 (유니트 수)** | — | ✅ P89-96 | ✅ R8-R26 | 100% |
| **IO 수량 sanity check** | — | ✅ P104 | — | 100% |
| **공정 간 순서 (high-level)** | — | ✅ P61 | — | 100% |
| **공정 내 step 순서** | — | — | ✅ ⭐ | 100% |
| **각 step 의 workDuration** | — | — | ✅ ⭐ | 100% |
| **병렬 vs 순차 (start 비교)** | — | — | ✅ ⭐ | 100% |
| **TokenRole=Source 식별** | — | ✅ 토큰방식 | ✅ start=0 | 100% |
| **device.action 의미 어휘** | ◐ 토큰 추정 | ✅ P47-49 | ✅ 기호 SSOT | 100% |
| **로봇 인스턴스 식별** | tag prefix | — | ✅ "n-N호" | 100% |
| **Cycle Time** | — | — | ✅ 누계 | 100% |
| **통신 protocol** | — | ✅ P5, P61 | — | 100% |
| **안전시스템** | — | ✅ P59-60 | ◐ 시트4 | 100% |
| **Opposing 의미** | ◐ 토큰 | ✅ P49 | ✅ C/UC 명시 | 100% |
| **PLC vendor / model** | — | ✅ LS XGT | — | 100% |
| **AUX/END M 신호** | — | — | — | **0%** |
| **FB call graph / rungDeps** | — | — | — | **0%** |
| **contactKind / NC vs NO** | — | — | — | **0%** |
| **callCondition (interlock 트리)** | — | ◐ P5 안전만 | — | **20%** |
| **정확한 PLC IP/port/retry** | — | — | — | **0%** |

→ **3자료 합산 커버리지: 85~90%**. 미커버 부분은 모두 PLC 내부 메모리·FB 구조·interlock 트리 (Ladder export 필요).

---

## 3. Promaker DS 모델 요소별 추출 전략 (DS 모델 초안 관점)

> 본 절은 narrative (입력→출력 흐름) 중심. 세부 cell-level 규칙은 **§4.1 매핑 규칙 표가
> SSOT** — 두 곳에 동일 규칙이 박제되어도 §4.1 이 우선.

### 3.1 Passive Systems (장치 카탈로그) — xlsx 가 주, PDF/작업서 가 보강

**입력 → 출력 흐름**:

```
xlsx IO List 시트 (Tag/Address)
   │
   ├─ Zone 추출 = 시트명 그대로
   ├─ Device 추출 = tag 토큰 (PDF P48 SSOT 사전으로 정확화)
   │                  └─ PIN/LATCH/CLP/STP/HOOK/EJECT 등
   ├─ Action 추출 = tail suffix (PDF P49 SSOT 사전으로 정확화)
   │                  └─ ADV/RET/UP/DOWN/CW/CCW/ON/OFF 등
   ├─ DeviceType 분류 = PDF P89-96 의 설비 카탈로그로 재확인
   │                       (#204 → 핀 시프트, 래치, 턴테이블 서보)
   └─ Instance 수량 = PDF "유니트 수" + 작업서 "n-N호" 와 cross-check
                       (#204: 핀 3SET, 래치 20SET)
   ↓
Passive System YAML (`kind: passive` + device sugar)
```

**검증 게이트**: 자동 추출한 zone × IO 비트 수가 PDF P104 의 예상 IO 수량
(#204 입력 62 / 출력 24) 과 큰 차이 (예: ±30% 이상) 가 나면 어댑터 오류 진단.

### 3.2 Active Systems (공정/Flow/Work) — 작업서 가 주, PDF 가 high-level 보강

**입력 → 출력 흐름**:

```
조립작업서 5-x. #공정 시트 (간트 차트)
   │
   ├─ Active System = 라인 단위 = "SIDE_OUTER_Control"
   ├─ Flow = 공정 # 단위 (#201, #202, ..., #233 → 9개 Flow)
   ├─ Work = step 단위 (작업서 행 1건 = 1 Work)
   │         └─ Work name = 작업내역 한국어 → 영문 (LLM 변환)
   ├─ tokenRole: Source = start=0 인 Work
   ├─ workDuration = 작업서 `AK` 컬럼 (초 → "Ns")
   ├─ calls: = Work 의 Sym 에 해당하는 Passive.ApiDef 참조
   │           └─ Sym 'C' → cylinder.ADV, 'UC' → cylinder.RET
   │           └─ Sym 'W' → weldGun.Weld
   │           └─ Sym 'M' → robot.WORKn
   ├─ arrows = step dependency (`start_B == cum_A` 일 때 A → B)
   │           └─ Type 추론: 일반 sequence = Start, 완료-시작 chain = StartReset
   └─ Cycle Time = 마지막 step 의 cum (#204 = 76s)
   ↓
Active System YAML (`kind: active` + flow/works/arrows)
```

**high-level 검증**: PDF P61 의 SIDE 공정 순서 ("픽킹 → 로딩 → 클램프 → 용접 →
언클램프") 가 작업서 #201 ~ #205 의 Flow 순서로 모두 관찰되는지 확인.

### 3.3 PLC Metadata (System `plc:` sub-section) — PDF 가 주

**추출 매핑**:

| Promaker YAML key | 값 | 출처 |
|---|---|---|
| `plcVendor` | `"LS XGT"` | PDF P5, P46 |
| `tagPrefix` | zone 이름 (예: `"S204"`) | xlsx 시트명 |
| `tagNamingFormat` | `"{Equipment}_{Part}_{Unit}_{State}"` | PDF P45 규칙 |
| `communicationTimeout` | `"00:00:05"` (기본) | PDF 미명시 → default |
| `enableSafetyInterlock` | `true` | PDF P59-60 |
| `emergencyStopEnabled` | `true` | PDF P59 (접점 이중화) |
| `lightCurtainCheck` | `true` | PDF P59 |
| `safetyDoorCheck` | `true` | PDF P59 |
| `enableHeartbeat` | `true` | PDF P4 (토큰방식 통신 = heartbeat 필수) |

서보·로봇 등 sub-device 의 통신:
- **B&R 서보 (#204 턴테이블)**: `EtherNet/IP` (PDF P94)
- **3축 지그**: `RAPIENET` (PDF P61)
- **로봇/SIDE 일반**: `Profi-bus DP` (PDF P61)

### 3.4 어휘 / 이름 / Description — PDF 어휘 SSOT + 작업서 한국어

| Promaker YAML key | 출처 |
|---|---|
| `system: <name>` | xlsx 시트명 + PDF 한국어 매핑 |
| `device: <DU literal>` | PDF P48 (UNIT/PIN/LATCH/SRV/RBT 등) |
| `apis: [...]` | PDF P49 어휘 + 작업서 기호 (둘 다 cross-check) |
| `apiDetails.<api>.description` | 작업서의 한국어 작업내역 + PDF P89-96 설비 설명 |
| `author` | PDF "자동화기술실 / 설비제어기술2팀" |
| `version` | PDF 작성일 `2022.12.15` |
| `iri` | (선택) — 차후 KB 색인 URI |

---

## 4. Promaker YAML 자동 생성 매핑 규칙

### 4.1 핵심 변환 규칙

| 입력 (3자료 원소) | 변환 규칙 | Promaker YAML 출력 |
|---|---|---|
| xlsx 시트명 | zone 식별자 | `system: <시트명>_<DeviceType>` (Passive) 또는 `flow Flow_<공정번호>_<공정명>` (Active — YAML 의 `#` 는 주석이므로 회피, sanitizeName 정합) |
| xlsx tag `S204_WRS_1ST_CLAMP3_ADV1` | PDF P49 어휘로 분해 → device=`CLAMP3`, action=`ADV` | `apis: [ADV, RET]` + `out.name=<tag>`, `out.addr=<%IW…>` |
| xlsx tag prefix `RB{N}` | 로봇 device | Passive `device: robot` |
| 작업서 Sym=`C` | cylinder.ADV (클램프 가동) | `calls: [<CylinderName>.ADV, ...]` |
| 작업서 Sym=`UC` | cylinder.RET | `calls: [<CylinderName>.RET, ...]` |
| 작업서 Sym=`W` | 용접건 동작 | `calls: [<WeldGun>.Weld]` |
| 작업서 Sym=`M` + "n-N호" | robot Work | `calls: [<Robot>.WORK{N}]` |
| 작업서 `start=0` | source work | `tokenRole: Source` |
| 작업서 `AK` (시간) | duration | `workDuration: <AK>s` |
| 작업서 step A `cum` == step B `start` | A → B sequential | `arrows: [A -> B : Start]` |
| 작업서 step A `start` == step B `start` == 0 | parallel sources | 양쪽 모두 `tokenRole: Source` |
| PDF P89-96 zone 설명 | 한국어 description | `apiDetails.<api>.description: "..."` |
| PDF P49 토큰방식 명시 | active token semantics | Active system 의 default tokenRole 정책 |

### 4.2 Cylinder · Clamp · Pin 의 opposing 매칭 (광명2 conventions)

| 토큰 (xlsx) | 작업서 Sym | Opposing | Promaker 표기 |
|---|---|---|---|
| `_ADV` / `_RET` | C / UC | chain | `device: cylinder`, `apis: [ADV, RET]` |
| `_CLP` / `_UNCLP` | C / UC | chain | `device: clamp`, `apis: [CLP, UNCLP]` |
| `_UP` / `_DN` | (Sym 없음) | chain | `device: custom(Lifter)`, `apis: [UP, DN]` |
| `_OPEN` / `_CLOSE` | (Sym 없음) | chain | `device: custom(Gate)`, `apis: [OPEN, CLOSE]` |
| `_CW` / `_CCW` | — | chain | `device: custom(Motor)`, `apis: [CW, CCW]` |
| `_HOME_POS` / `_WK_COMP` / `_LAST_WK_COMP` | M / L / UL | none | `device: robot`, `apis: [HOME, WORK1, ..., WORK]` |

### 4.3 ROBOT 인스턴스 매칭 (xlsx ↔ 작업서)

xlsx 시트 `S201_RB1_3-01` ↔ 작업서 #201 의 "201-1호" → **동일 로봇**.
PDF P61 의 "로봇 대차 픽킹" 도 작업서 #201 step 1-2 ("원위치→랙방 이동, 랙방 취출") 와 정합.

- xlsx `S201_RB1` → Passive `S201_RB1` (device: robot)
- 작업서 "201-1호" → Active Flow #201 의 Work 들이 `S201_RB1.WORK{N}` 호출

### 4.4 Active YAML 생성 시연 (#204 전체)

```yaml
protocol: promaker/v0
project: 광명2_SV_SIDE_OUTER
author: "설비제어기술2팀"
version: "2022.12.15"   # quoting — unquoted 2022.12.15 는 YAML 1.2 의 일부 parser 가 float/date 시도

systems:
  # ========== Passive: xlsx IO List 에서 자동 ==========
  - system: S204_KEY_Clamp1
    kind: passive
    device: cylinder
    apis: [ADV, RET]              # IO List S204_WRS_*_CLAMP1_ADV/RET
    opposing: chain
    workDuration: 4s              # 작업서 #204 step 6/8 (C: 4s, UC: 4s)

  - system: S204_KEY_Pin1
    kind: passive
    device: cylinder
    apis: [ADV, RET]
    workDuration: 4s

  # ... (래치 20SET, 핀 3SET, 파트감지 4EA 등 PDF P94 기준)

  - system: S204_RB1
    kind: passive
    device: robot
    apis: [HOME, WORK1, WORK2]
    apiDetails:
      WORK1: { description: "원위치→지그 이동" }       # 작업서 step 2
      WORK2: { description: "S/OTR 취출 + 키지그 로딩" }  # 작업서 step 3+4

  - system: S204_Turntable
    kind: passive
    device: custom(Servo)
    apis: [Turn]
    workDuration: 5s              # 작업서 #204 step 1
    plc:
      plcVendor: "B&R"            # PDF P94
      communicationTimeout: "00:00:05"
      # EtherNet/IP — PDF P94

  - system: S204_WeldGun
    kind: passive
    device: custom(WeldGun)
    apis: [Weld]
    workDuration: 50s             # 작업서 #204 step 7

  # ========== Active: 작업서 시퀀스에서 자동 ==========
  - system: SIDE_OUTER_Control
    kind: active
    plc:
      plcVendor: "LS XGT"         # PDF P5
      tagPrefix: "S204"
      enableSafetyInterlock: true
      emergencyStopEnabled: true
      lightCurtainCheck: true     # PDF P59
      enableHeartbeat: true       # PDF P4 토큰방식

    flow Flow_204_KEY_Process:           # YAML 의 # 주석 회피 — sanitizeName 정합
      works:
        TurntableTurn:
          tokenRole: Source       # 작업서 start=0 (병렬 source 1)
          workDuration: 5s
          calls:
            - S204_Turntable.Turn

        RobotMoveToJig:
          tokenRole: Source       # 작업서 start=0 (병렬 source 2)
          workDuration: 3s
          calls:
            - S204_RB1.WORK1

        UnloadFromRacks:          # 작업서 step 3 (start=3, dur=5)
          workDuration: 5s
          calls:
            - S204_RB1.WORK2

        LoadToKeyJig:             # 작업서 step 4 (start=8, dur=10)
          workDuration: 10s
          calls:
            - S204_RB1.WORK2

        ClampOtrCompl:            # 작업서 step 6 (start=18, dur=4)
          workDuration: 4s
          calls:
            - S204_KEY_Clamp1.ADV   # Sym 'C' → cylinder.ADV
            - S204_KEY_Pin1.ADV     # 동시 클램프 (여러 cylinder)

        WeldOtrCompl:             # 작업서 step 7 (start=22, dur=50)
          workDuration: 50s
          calls:
            - S204_WeldGun.Weld

        UnclampOtrCompl:          # 작업서 step 8 (start=72, dur=4)
          workDuration: 4s
          calls:
            - S204_KEY_Clamp1.RET   # Sym 'UC' → cylinder.RET
            - S204_KEY_Pin1.RET

      arrows:
        # === (a) 시간 기반 자동 도출 (start_B == cum_A 정합) ===
        - RobotMoveToJig -> UnloadFromRacks : Start    # 2→3 (cum_2=3 == start_3=3)
        - UnloadFromRacks -> LoadToKeyJig : Start      # 3→4 (cum_3=8 == start_4=8)
        - LoadToKeyJig -> ClampOtrCompl : Start        # 4→6 (cum_4=18 == start_6=18)
        - ClampOtrCompl -> WeldOtrCompl : Start        # 6→7 (cum_6=22 == start_7=22)
        - WeldOtrCompl -> UnclampOtrCompl : Start      # 7→8 (cum_7=72 == start_8=72)

        # === (b) LLM·사용자 보정 영역 (시간 정합 안 됨, 의미 추론) ===
        - TurntableTurn -> ClampOtrCompl : Start       # 1→6 — cum_1=5 < start_6=18 의 13s 갭.
                                                        # 직접 dependency 아니나 의미상 "턴테이블
                                                        # 완료 후 클램프 진행 가능". LLM 또는 사용자
                                                        # 가 의미 추론으로 추가.
```

> arrow 자동 도출 규칙은 `cum_A == start_B` 기준. 갭이 있는 dependency (위 1→6) 는
> 자동 추출 대상이 아니며 LLM 보정 (KMM 매뉴얼 FLOW CHART 같은 prior-art 가 있으면
> 참조) 또는 사용자 GUI 수동 추가가 정공.

---

## 5. DS Model 초안 생성 파이프라인 (시나리오 A+++++ 기준)

채택 시나리오 = **A+++++** (§7) — LightHouse 색인 시점 specialized 박제 + chat 시작 시
강제 주입. 본 절은 그 전제로 단계 정리.

### Step 0 — 자료 정렬 및 cross-reference 테이블 생성 (1회, 색인 전)

산출물: `광명2_xref.json` — 3자료의 핵심 식별자 매핑.

```json
{
  "zones": [
    {
      "process_no": "#201",
      "iolist_sheets": ["S201_ANTI_JIG", "S201_S_BOX1", "S201_RB1_3-01"],
      "pdf_page": 89,
      "pdf_name": "안티스패터 정렬지그",
      "workorder_sheet": "5-1. #201",
      "expected_io": { "input": 3, "output": 0 },
      "cycle_time_s": 55,
      "equipment": ["로봇 201-1호", "로봇 201-2호"]
    },
    {
      "process_no": "#204",
      "iolist_sheets": ["S204_KEY1", "S204_KEY_JIG1", "S204_KEY_JIG2", "S204_BNR_PNL", "S204_BnR_SERVO_6-11"],
      "pdf_page": 94,
      "pdf_name": "S/OTR KEY 지그",
      "workorder_sheet": "5-4. #204",
      "expected_io": { "input": 62, "output": 24 },
      "cycle_time_s": 76,
      "equipment": ["로봇 204-1호", "204-2호", "204-5호", "B&R 턴테이블 서보"]
    }
    // ... 9 zones
  ]
}
```

xref 는 LightHouse strategy 가 cross-check 용 신호로 사용 (선택). 부재해도 strategy 동작.

### Step 1 — LightHouse 폴더 등록 + 색인 (사용자 1회 행위)

```
[사용자가 KB 폴더 등록]
   ↓
KbManagerDialog → AttachmentIngestService.RunIndexerAsync
   ↓
runIndex (Indexer.fs)
   ├─ raw text dump → .lighthouse-kb/text/<docId>-<filename>.md   (기존 PR-C)
   ├─ keyword extract → meta.json                                  (기존 PR-B)
   ├─ doc summary 1줄 → .lighthouse-kb/summary.md                  (기존 PR-H1)
   └─ ★ specialized strategy dump → .lighthouse-kb/summary/<docId>-<filename>.md  (신규, §8)
       ├─ XlsxSignatureClassifier.detect(workbook)
       ├─ → IoListStrategy / WorkOrderStrategy / PdfControlSpecStrategy / Generic
       └─ → 정제 markdown 박제
   ↓
zip 패키징 → server upload
```

자료 A / B / C 가 같은 폴더에 있으면 strategy 가 각각 인식하여 specialized markdown 박제.

### Step 2 — chat 시작 (사용자 N회, 색인 후)

```
[사용자가 LLM Chat panel open]
   ↓
LlmChatViewModel.Initialize.cs
   ├─ FetchKbProfilesAsync (PR-F)
   ├─ KbDigestBuilder.Build → keyword digest                         (기존 PR-G)
   └─ ★ SpecializedDigestBuilder.Build                                (신규, §8)
       └─ 색인 폴더의 .lighthouse-kb/summary/*.md 전부 합쳐 system prompt 박제
   ↓
SystemContentBuilder (PR-G+E)
   ├─ base system prompt
   ├─ keyword digest TextContent (cache breakpoint)
   └─ ★ specialized digest TextContent (cache breakpoint)             (신규)
   ↓
첫 turn 전송 — Anthropic cache_create_input_tokens 박제
   ↓
다음 turn — cache_read_input_tokens 으로 ~10x 저렴
```

### Step 3 — DS 모델 초안 생성 (사용자 1 turn)

사용자가 chat 에 입력:
> "광명2 SIDE OUTER 의 #204 KEY 지그를 Promaker YAML 로 변환해줘"

LLM 의 입력 (자동 주입된 상태):
- (E) specialized digest = IoList 정제 markdown + WorkOrder 정제 markdown + PDF 어휘 SSOT
- (A) keyword digest
- (system base) Promaker YAML schema 지식
- (user) 위 요청

LLM 출력: 위 §4.4 시연 같은 Active + Passive YAML.

필요 시 LLM 이 `attachment_fulltext` MCP 호출하여 (B) text dump 의 raw 부분 fetch
(예: `S204_KEY1` 시트의 모든 비트 정확 dump).

### Step 4 — 사용자 검토 + iteration

GUI 로딩 → `ValidateModelDoc` 통과 → 사용자 보정 → 다음 turn 에 수정 요청.
다음 turn 도 cache hit 으로 저렴.

---

## 6. 남은 부족 정보 (PLC Ladder 만이 정답)

3자료 합산 + specialized strategy 박제 후에도 도달 못 하는 영역:

| 부족 정보 | 영향 받는 Promaker 모델 요소 | 유일한 소스 |
|---|---|---|
| **AUX / END (M 제어신호)** — 자동기동 조건 / 완료 피드백 비트 | callCondition 트리, 정확한 시퀀싱 trigger | PLC `%M` 영역 dump, FB body (rungDeps) |
| **FB call graph / pin wiring** | 정확한 cross-call Arrow, Passive `apis` 의 internal connection | LS XGT Ladder export, FB body |
| **ContactKind (NC vs NO)** — interlock 입력의 접점 타입 | `calls:` object `contactKind: NcContact` | PLC 회로도 |
| **CallCondition (interlock) 트리** | `callCondition:` recursive 구조 | PLC interlock rung |
| **정확한 PLC IP/port/timeout/retry** | System `plc:` sub-section 세부 | PLC 통신 설정 export |
| **TON 타이머 정확 값** | `workDuration` 의 미세 보정 | PLC Ladder TON instance |
| **SkipInputSensor, CallType** | Call 보강 property | PLC FB body |

→ 이 영역은 LLM 추정 또는 GUI 사용자 수동 보정으로 채우거나, 후속 단계에서
Ladder 입수 시 `광명2_draft.yaml` 을 patch 형태로 갱신.

**부분 보강 가능성** (§8.6 caption prompt strategy 도입 후): KMM 매뉴얼 (`03/04.SIDE
OTR/COMPL LINE_KMM 라인 운영 매뉴얼`) 의 **INTERLOCK 도식 (S60-65)** 과 **FLOW CHART
(S66-71)** caption 을 InterlockDiagram / FlowChart prompt 로 재추출하면:
- **callCondition 트리 ~30~50%** 보강
- **Arrow Type 결정 근거 ~70~80%** 보강

두 영역 독립이라 §0 의 "~30~80%" 표기는 두 영역의 합집합 범위 (callCondition 의
lower 30% ~ Arrow Type 의 upper 80%). 단 prior-art (멕시코 라인 2015) 기반이라
광명2 직접 매핑이 아닌 **패턴 reference** 로만 사용 — A/B test 로 noise 여부 사전
검증 (§8.9 참조).

---

## 7. 시나리오 진화 (A → A+++++)

### 7.1 진화 단계 매트릭스

| 시나리오 | xlsx 처리 위치 | Ev2.Oracle | 사용자 UX | Cache 활용 | 결정론 baseline | 평가 |
|---|---|:-:|---|:-:|---|---|
| **A** (최소) | 외부 어댑터 | ✅ | CLI 별도 | — | 어댑터 | Passive 만 |
| **A++** (3자료 합산) | 외부 어댑터 (A + C) | ✅ | CLI + GUI 조립 | — | 어댑터 | Active 까지 도달 |
| **A+++** (OOXML chat 첨부 활성) | 외부 (A) + chat raw (C) | ◐ (A 만) | 혼합 | 부분 | 어댑터 (A 만) | 정제 안 됨 |
| **A++++** (chat 적응형 어댑터) | chat 첨부 시 signature → strategy | ❌ | drag-drop 3 파일 + 1 turn | turn 안 (제한) | 어댑터 (매 첨부) | Promaker 자급자족 |
| **A+++++ (채택)** | **LightHouse 색인 시점 strategy + 자동 주입** | ❌ | **KB 등록 1회** | **system prompt cache** | **색인 박제 (idempotent golden)** | **모든 축 우월** |

### 7.2 A+++++ 가 다른 시나리오 대비 우월한 이유

1. **prompt cache hit** — Anthropic `cache_read_input_tokens` 으로 N번째 turn 부터 input 비용 ~10x 절감. 광명2 모델링은 자료 한 set 으로 수십 turn 대화하는 use case → cache 효과 결정적.

2. **결정론적 baseline** — 색인 시점에 박제된 specialized markdown 은 idempotent. 어댑터 회귀 테스트의 golden 으로 직접 사용.

3. **KB 공유** — multi-tenant T1 flat 정합. 같은 KB 사용자 모두 자동 활용. 다른 라인 (FLR / BB / CRP / ROOF / BC) 도 KB 등록 한 번이면 됨.

4. **chat 첨부 부담 0** — 사용자가 매번 drag-drop 안 해도 됨. KB collection toggle 만으로 활성/비활성 제어.

5. **retrieval 와 공존** — specialized markdown 은 system prompt 주입, raw chunk 는 `attachment_search` 로 retrieval. LLM 이 자율적으로 `attachment_fulltext` 호출하여 정확 dump 가져옴.

6. **자료 갱신 시 재색인 한 번** — KB 재업로드 (D5 swap) → 모든 사용자에게 새 strategy 결과 자동 반영.

7. **strategy 코드의 단일 위치** — `Solutions/Core/Ds2.LightHouse/` 의 lib 안. 변경 시 LightHouse Service 만 재배포.

8. **다른 라인 / 다른 산업으로 확장** — strategy 1개 더 추가하면 됨. signature 기반 분기라 기존 strategy 영향 없음.

---

## 8. LightHouse 색인 통합 — `.lighthouse-kb/summary/*.md` 설계

### 8.1 위치 — 기존 4-layer RAG 위의 확장

`todo-lighthouse-index-summary.md` (r4) 의 4-layer RAG:

| Layer | 박제 위치 | 호출 시점 | 용량 | 상태 |
|---|---|---|---|---|
| (A) keyword digest | system prompt (chat lifetime) | 자동 inline | ~150 tok / collection | ✅ PR-G `ae58e11` |
| (C) chunk excerpt | `attachment_search` hit | LLM query 시 | top-K × ≤4K | ✅ 기존 |
| (B) text dump | `attachment_fulltext` MCP | LLM 자율 (search 부족 시) | 수만 token / doc | ✅ PR-C/D + r3 `682fc812` |
| (D) doc summary 1줄 | `.lighthouse-kb/summary.md` (single file) | hybrid | ~50 tok × M doc / coll | ◐ PR-H1 완료, PR-H2 자율 진입 |

**신규 (E) layer 제안** — 본 §8 의 작업:

| Layer | 박제 위치 | 호출 시점 | 용량 | 상태 |
|---|---|---|---|---|
| **(E) specialized digest** | **`.lighthouse-kb/summary/<docId>-<filename>.md`** (per-doc directory) | **자동 inline (system prompt 박제)** | doc 당 수K~수십K token | 🆕 본 §8 |

`.lighthouse-kb/` 디렉토리 구조 (갱신):

```
<source-folder>/.lighthouse-kb/
├── index.db                       (BM25 + ANN, 기존)
├── meta.json                      (keyword digest 포함, PR-B)
├── summary.md                     (doc 1줄 요약, PR-H1)
├── text/                          (raw markdown dump, PR-C)
│   ├── <docId1>-<filename1>.md
│   ├── <docId2>-<filename2>.md
│   └── ...
└── summary/                       ★ 신규 (본 §8)
    ├── <docId1>-<filename1>.md    (specialized strategy 출력, 또는 부재)
    ├── <docId2>-<filename2>.md
    └── ...
```

`summary/` 디렉토리의 파일은 **signature 매치된 doc 만** 박제. 매치 없으면 파일 없음 (generic doc 은 기존 `text/` + `summary.md` 1줄 + keyword digest 로 충분).

### 8.2 박제 정책

**색인 시점** (`runIndex` 의 dump hook 확장):

1. doc 파일 1개씩 순회
2. `XlsxSignatureClassifier.detect(file)` 또는 `PdfSignatureClassifier.detect(file)` 호출
3. signature 매치 → 해당 strategy 의 `Transform(file)` 호출
4. 출력 markdown 을 `<source>/.lighthouse-kb/summary/<docId>-<filename>.md` 박제
5. signature 미매치 → skip (파일 안 만듦)
6. 모든 doc 처리 후 `summary/` 디렉토리도 zip 패키징 + server upload

**박제 markdown 의 머리말**:

```markdown
<!-- generated by IoListStrategy v1.0 (signature: ioListSv1) at 2026-05-26T15:00:00Z -->
<!-- source: 4-3. 광명2 SIDE OUTER SV IO LIST STD MAP.xlsx (docId: a1b2c3d4) -->

# IO List — 광명2 SIDE OUTER SV
...
```

머리말로 strategy version 박제 → 후속 strategy 개선 시 회귀 무회귀 판단.

### 8.3 주입 정책 — chat 시작 시 강제 주입

**SpecializedDigestBuilder** (신규, `KbDigestBuilder` 와 평행):

```fsharp
// Solutions/Core/Ds2.LightHouse/SpecializedDigestBuilder.fs (신규)
module SpecializedDigestBuilder

/// active collection 의 .lighthouse-kb/summary/*.md 전부 읽어 합쳐 single string 반환.
/// 빈 디렉토리 또는 부재 시 "" 반환.
/// SystemContentBuilder 가 이를 별도 TextContent 로 wrap (cache breakpoint).
let build (activeCollections: CollectionInfo seq) : string =
    activeCollections
    |> Seq.collect (fun coll ->
        let dir = Path.Combine(coll.LocalPath, ".lighthouse-kb", "summary")
        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.md")
            |> Array.map (fun f -> File.ReadAllText(f, Encoding.UTF8))
        else [||])
    |> String.concat "\n\n---\n\n"
```

**SystemContentBuilder** 갱신 (PR-G+E `ae58e11` 의 패턴 그대로 확장):

```
[base system prompt]                                          ← cache breakpoint 1
[KbDigestBuilder.Build → keyword digest]                      ← cache breakpoint 2 (기존)
[SpecializedDigestBuilder.Build → specialized markdown 합본]  ← cache breakpoint 3 (신규)
[user messages]
```

3개의 cache breakpoint → 부분 변경 시 효율적 cache 재사용:
- collection 토글 변경 → cache breakpoint 2/3 재계산, breakpoint 1 (base) 는 cache hit
- 자료 재색인 → breakpoint 3 만 재계산
- 평시 chat turn → 모두 cache hit

### 8.4 토큰 예산

광명2 3 자료 색인 결과의 `.lighthouse-kb/summary/` 파일 예상 크기 (잠정 추정 —
**PR-I1 PoC 후 실측 갱신 필요**[^token-est]):

| 자료 | strategy | summary 토큰 |
|---|---|---|
| IO List xlsx | IoListStrategy | ~30K (43 시트 long-form CSV markdown) |
| 조립작업서 xlsx | WorkOrderStrategy | ~3K (9 시트 Gantt 핵심) |
| 제어시스템 PDF | PdfControlSpecStrategy | ~5K (P45-50 어휘 + P61 순서 + P104 IO 표) |
| **합계** | — | **~38K tokens** |

[^token-est]: 추정 산식 = 43 시트 × 평균 100 비트 × 평균 30자 한국어/영문 혼합 ≈ 130K chars
≈ 40~50K tokens (raw). 정제 (헤더/빈셀/메타 제거) 후 30K 가정. 실측 시 ±20% 변동 가능 —
PR-I1 의 `.lighthouse-kb/summary/광명2_iolist.md` 산출물 크기로 갱신.

system prompt 박제. Anthropic Claude Opus 4.7 200K context 의 ~19% — 충분히 여유.
1M context 모드는 ~4% 만 차지.

cache 적용 후 N번째 turn 의 input cost:
- input ~5K (user message + base + chat history) + cache_read ~38K × 0.1 = ~5K + ~3.8K = ~9K equiv
- vs cache 없을 시: ~43K
- → ~80% 절감

### 8.5 strategy 카탈로그 (v1.0 — 광명2 라인)

| Strategy | signature 매치 조건 | 출력 | 토큰 |
|---|---|---|---|
| **IoListStrategy** | xlsx, 시트≥10, `%IW`/`%QW` 셀 ≥50, 시트명 zone 코드 ≥5, R1 "I/O LIST" | 시트별 long-form CSV markdown (Word/Dir/Name/Addr/Type/Symbol) + R2/R4 zone meta heading | ~30K (광명2 자료 A) |
| **WorkOrderStrategy** | xlsx, 시트명 `#\d{3}` ≥3, Gantt header 4종 매치, Sym 토큰 ≥10 | 시트별 step 표 markdown + Symbol SSOT + dependency 자동 추론 (start/cum) | ~3K (광명2 자료 C) |
| **PdfControlSpecStrategy** | pdf, "광명2 차체공장 표준 심벌" + "(①설비/라인/제어반)_(②부품명)_…" 패턴 | P45-50 어휘 SSOT JSON-like + P61 작업순서 + P104 IO 수량 표 | ~5K (광명2 자료 B) |
| **(fallback) None** | 위 어느 것도 매치 안 됨 | 박제 안 함 (기존 (A)+(B)+(D) layer 로 충분) | 0 |

신규 strategy 추가 시 (다른 라인 / 다른 형식):
- `XlsxStrategies/<NewStrategy>.fs` 추가
- `XlsxSignatureClassifier.detect` 의 strategy 후보 list 에 추가
- 기존 strategy 영향 0 (signature 분리)

### 8.6 이미지 caption strategy (CaptionPromptStrategy)

§8.5 의 strategy 들은 문서의 **텍스트** 영역을 정제한다. 그러나 광명2 PDF (자료 B)
의 도식 부분, KMM 매뉴얼 (`03/04.SIDE OTR/COMPL LINE_KMM 라인 운영 매뉴얼`, 76+ slides
× 118+ images) 같은 **이미지 중심 자료**의 가치는 caption quality 에 결정적으로 의존.

현 `CaptionGenerator.CaptionPrompt` (lib SSOT) 는 **단일 generic prompt + max_tokens=256**:

> "이 이미지를 한국어로 1~2문장으로 설명해주세요. 도면/표/그래프인 경우 안에 보이는
> 라벨/번호/태그(예: CV01, DI12)를 우선 인용해주세요."

→ KB 검색 hit 용으로는 충분하나, **모델 생성의 결정 근거로 쓰기엔 정보 손실 90%+**.
특히 FLOW CHART / INTERLOCK 도식 / SystemTopology 같은 **DS 모델 직결 자료**가 가장 약한 영역.

#### 8.6.1 단일 SSOT → task-specific 분기

**`CaptionPromptKind` DU 신설** + signature detector 도입:

```fsharp
// Solutions/Core/Ds2.LightHouse/CaptionPromptStrategy.fs (신규)
type CaptionPromptKind =
    | Generic                  // 현 default 유지 (legacy 호환)
    | FlowChart                // FLOW CHART, 차종 흐름도, 공정 흐름도
    | InterlockDiagram         // EMS/ROBOT/SERVO INTERLOCK, callCondition 후보
    | SystemTopology           // PLC 시스템 구성도, 통신 계통도
    | LayoutFloorPlan          // Layout 도면, 비상정지 Layout
    | HmiScreenshot            // HMI 화면 캡처
    | EquipmentPhoto           // 제어반/부품 사진 (모델 무관, generic 으로 위임)
    | TableSnapshot            // 표 이미지 (차종 LIST 등)

/// signature detector — slide 제목 + 직전·직후 텍스트 토큰 + 이미지 hash 의
/// 시각 hint (선택). pptx 의 경우 slide context, pdf 의 경우 page heading
/// + 인접 단락 200자 흡수.
val detect:
    slideContext: string -> imageHash: string -> CaptionPromptKind
```

signature 매치 keyword (잠정):
- `FlowChart`: "FLOW CHART", "흐름도", "공정 흐름", "차종 흐름", "프로세스 흐름"
- `InterlockDiagram`: "INTERLOCK", "인터록", "ROBOT INTERLOCK", "SERVO INTERLOCK", "EMS INTERLOCK"
- `SystemTopology`: "시스템 구성도", "계통도", "통신", "네트워크 구성", "PROFIBUS", "ETHERNET/IP"
- `LayoutFloorPlan`: "Layout", "레이아웃", "비상정지", "평면", "배치"
- `HmiScreenshot`: "HMI", "화면", "Screen", slide 텍스트에 GUI 버튼 명 다수
- `TableSnapshot`: 인접 텍스트가 표 형식 / "LIST" / "수량" / 차종 코드 다수
- `EquipmentPhoto` / `Generic`: 위 매치 0 (fallback)

#### 8.6.2 유형별 prompt 카탈로그 (잠정)

| Kind | prompt 핵심 지시 | max_tokens | 권장 model |
|---|---|---|---|
| **FlowChart** | "이 흐름도의 모든 노드를 나열하고, edge 를 `A → B` 형태로 모두 추출. Source (in-deg 0) 와 Sink (out-deg 0) 명시. 분기·병렬 위치 표시. JSON: `{nodes, edges, sources, sinks}`" | **2000** | **Opus 4.7** |
| **InterlockDiagram** | "이 인터록 도식의 모든 신호 source/destination 을 `[source] → [target]` 형태로 나열. NC/NO 또는 ON/OFF 표시 인용. 신호 이름 (HOME_POS, WK_COMP 등) 정확 인용. JSON: `{signals:[{source,target,name,polarity}]}`" | **2000** | **Opus 4.7** |
| **SystemTopology** | "이 시스템 구성도의 모든 박스 라벨과 연결선 나열. 통신 protocol 표시 (Profibus-DP, EtherNet/IP, RAPIENET 등) 인용. 네트워크 계층 명시 (제어반→인터록반→로봇하단BOX→행거內 PLC 같은)" | **1500** | **Opus 4.7** |
| **LayoutFloorPlan** | "이 레이아웃 도면에 표시된 모든 라벨 (zone 코드, 박스 이름, 공정 번호) 나열. 위치 좌표는 무시하고 라벨 inventory 만." | 1000 | Sonnet 4.6 |
| **HmiScreenshot** | "이 HMI 화면의 모든 버튼/필드/라벨 텍스트 인용. 화면 타이틀 명시. 표시된 상태 (램프 ON/OFF, 알람 등) 인용." | 800 | Sonnet 4.6 |
| **TableSnapshot** | "이 표의 헤더와 모든 행을 markdown table 로 재구성. 빈 셀은 `-` 로 표시." | 1500 | Sonnet 4.6 |
| **EquipmentPhoto** / **Generic** | 현 prompt 그대로 (1~2문장 설명) | 256 | Sonnet 4.6 |

→ **model 차등**: FlowChart / InterlockDiagram / SystemTopology 만 Opus 4.7 vision
(산업 도식 정확도 5~30%p 우월). 나머지는 Sonnet 4.6 (cost). 카테고리별 가중 평균
비용 증가는 ~30% 추정[^vision-cost], quality 이득은 본질적.

[^vision-cost]: KMM 매뉴얼 1 파일 (118 images) 기준 추정 — Opus 4.7 vision input ~1500
tokens/image × Opus rate ($15/1M input + cache) × 30% = $0.6~1.0 per re-index. monthly
cap 정책 = collection 당 월 $5 default (UI 에서 사용자 override), 초과 시 Sonnet 4.6
fallback 자동. 단가는 Anthropic pricing 변동 시 stale — PR-I6 진입 시 실측 갱신.

#### 8.6.3 context inject

caption 단계에서 image bytes 만 던지지 말고 **slide context 동봉**:

```
[system]
이 이미지는 다음 슬라이드/페이지에서 발췌됨:
  파일: {filename}
  슬라이드/페이지 제목: {title}
  직전 텍스트 (200자): {prevText}
  직후 텍스트 (200자): {nextText}
  자료 카테고리: {kbCategory}   <- (선택) collection 메타에서 흡수

[user]
[image]
[task-specific prompt — §8.6.2 표]
```

→ 도식의 의도가 텍스트에 있는 경우가 많아 vision LLM 의 해석 정확도 크게 향상.
KMM 매뉴얼의 S66-71 같은 **연속 5장 FLOW CHART** 도 매 장 caption 의 일관성 확보.

#### 8.6.4 SSOT 정책 갱신 (외부 문서 `todo-lighthouse-indexer-claude-caption.md` 의 두 결정 항목 patch)

해당 외부 문서의 영향받는 두 결정 항목:

1. **단일 CaptionPrompt SSOT 정책** (외부 todo §2 의 #5번 항목) — 현 박제:
   > "lib `CaptionGenerator.CaptionPrompt` (Literal) 단일 유지 — skill 은 CLI
   > `lighthouse-cli print-caption-prompt` 로 매번 fetch — 사본 박제 없음".

2. **spot-check 합격 기준** (외부 todo §2 의 #14번 항목) — 현 박제:
   > "샘플 5장 중 ≥ 4장 의미 동등 (사람 판정). 미만 시 SSOT (lib CaptionPrompt
   > literal) 재검토".

본 §8.6 진입 시 두 항목 갱신:

- 단일 CaptionPrompt SSOT 정책 → **`CaptionPromptStrategy.fs` 의 7종 prompt** 가 신 SSOT
  (lib 안). CLI `lighthouse-cli print-caption-prompt --kind <Kind>` 로 유형별 fetch
  (default `Generic` = legacy 호환). skill / Promaker / server caller 가 image 마다
  `detect` 호출 후 해당 prompt fetch.
- spot-check 합격 기준 → **유형별 평가** (각 kind 5 장 × 7 kind = 35 장, kind 마다 ≥ 4/5
  의미 동등). 미만 시 해당 kind 의 prompt 재검토 (kind 단위 부분 patch 가능).

patch 시점 = PR-I6 진입 turn 의 commit message + 외부 todo 문서의 rev 라인 stamp 추가.

#### 8.6.5 모델 생성 후 KMM 매뉴얼 등 image 자료 진입

caption prompt strategy 도입 + spot-check 통과 후:
- `KmmManualStrategy` (pptx) — 매뉴얼 signature 인식 + slide context inject 강화 + FlowChart/InterlockDiagram caption 우선 박제
- `.lighthouse-kb/summary/*.md` 에 KMM 매뉴얼의 **FlowChart / InterlockDiagram caption 만** 추출 박제
- 광명2 #204 모델 생성 시 KMM 의 prior-art FlowChart caption 을 system prompt 에 추가 inject → Arrow Type 결정 보조

진입 시점: §9 의 Phase 4 (PR-I7 이후 자율 진입).

### 8.7 구현 위치 + 코드량

```
Solutions/Core/Ds2.LightHouse/
├── TextDumper.fs                          ← summary/ dir 박제 추가 (~50 줄)
├── SpecializedDigestBuilder.fs            ← 신규 (~80 줄)
├── Extractors/
│   ├── OoxmlExtractor.fs                  ← 기존 (XlsxSheetRoles SSOT 재사용)
│   ├── PdfExtractor.fs                    ← 기존 (r3 `f88e60c4` CJK 띄어쓰기 fix 포함)
│   └── XlsxStrategies/                    ← 신규 폴더
│       ├── IXlsxStrategy.fs               ← interface (~30 줄)
│       ├── XlsxSignatureClassifier.fs     ← 진입점 (~80 줄)
│       ├── IoListStrategy.fs              ← 4-block reshape (~150 줄)
│       └── WorkOrderStrategy.fs           ← Gantt 매핑 (~80 줄, XlsxSheetRoles 재사용)
└── PdfStrategies/                         ← 신규 폴더
    └── PdfControlSpecStrategy.fs          ← 광명2 PDF (~120 줄)

Solutions/Tools/Ds2.LightHouse.Cli/
└── Program.fs                             ← runIndex dump hook 확장 (~20 줄)

Apps/Promaker/Promaker/Knowledge/
└── KbSpecializedDigestFetcher.cs          ← 신규 (~80 줄, PR-F KbProfileExtractor 패턴)

Apps/Promaker/Promaker/ViewModels/
└── LlmChatViewModel.SpecializedDigest.cs  ← 신규 partial (~80 줄, PR-G KbDigest 패턴)

Apps/Promaker/Promaker/LlmAgent/
└── SystemContentBuilder.cs                ← 갱신 (~20 줄, cache breakpoint 추가)

Tests/
├── Ds2.LightHouse.Tests/
│   └── XlsxStrategiesTests.fs             ← signature 분류 + 변환 회귀 (~300 줄)
├── Ds2.LightHouseService.Tests/
│   └── SummaryDirIntegrationTests.fs      ← upload/download e2e (~150 줄)
└── Promaker.Tests/
    └── SpecializedDigestInjectionTests.cs ← 주입 회귀 (~100 줄)
```

### 8.7.1 image caption strategy 추가 (§8.6 진행 시)

```
Solutions/Core/Ds2.LightHouse/
├── CaptionGenerator.fs               ← Generic prompt 유지 + helper 분리 (~50 줄 변경)
└── CaptionPromptStrategy.fs          ← 신규 (~200 줄)
    ├── CaptionPromptKind DU (8 case)
    ├── detect (slideContext, imageHash) → Kind
    ├── promptFor (Kind) → string + maxTokens
    └── modelFor (Kind) → "claude-opus-4-7" | "claude-sonnet-4-6"

Solutions/Tools/Ds2.LightHouse.Cli/
└── Program.fs                        ← print-caption-prompt --kind <Kind> 분기 (~20 줄)

Solutions/Tools/Ds2.LightHouseService/
└── (Indexer 흐름 변경)               ← image 처리 시 detect → promptFor → callAnthropic (~30 줄)

.claude/skills/indexer/SKILL.md       ← subagent dispatch 시 kind 별 prompt fetch (~doc only)

Tests/
└── Ds2.LightHouse.Tests/
    └── CaptionPromptStrategyTests.fs ← detect signature + prompt SSOT 회귀 (~150 줄)

추가 합계: 약 450 줄 (lib 250 + CLI 20 + service 30 + tests 150)
```

### §8.7 합계 (전체)

기존 strategy (§8.5) + image caption strategy (§8.6) 합산 breakdown:

| 영역 | §8.5 strategy | §8.6 caption | 합 |
|---|---|---|---|
| **lib** (Solutions/Core/Ds2.LightHouse/) | 590 (TextDumper 50 + SpecializedDigestBuilder 80 + interface 30 + classifier 80 + IoList 150 + WorkOrder 80 + PdfControlSpec 120) | 250 (CaptionGenerator 변경 50 + CaptionPromptStrategy 신규 200) | **840** |
| **UI** (Apps/Promaker/Promaker/) | 180 (KbFetcher 80 + ChatVM partial 80 + SystemContentBuilder 20) | 0 | **180** |
| **tests** | 550 (xlsx 300 + summary 150 + injection 100) | 150 (CaptionPromptStrategyTests) | **700** |
| **misc** (CLI / service) | 20 (runIndex hook) | 50 (CLI print-prompt 20 + Indexer flow 30) | **70** |
| **합계** | **1340** | **450** | **1790** |

- 일정: **strategy 코드 한정 2~2.5 주**. §9 의 Phase P1~P4 전체 (매퍼 + 검수 + 라인 확장
  + caption strategy) 는 별도 — §0 합산 참조.

> **모델 ID (claude-opus-4-7 / claude-sonnet-4-6) 는 코드 hardcode 회피 권고** (M14) —
> `settings.json` 또는 `LIGHTHOUSE_CAPTION_MODEL_*` env 외부화 + fallback chain
> (`opus-4-7 → sonnet-4-6 → sonnet-4-5`) 정책. 모델 deprecation cycle 12~18 개월로
> stale 빠름 — PR-I6 진입 시 함께 작업.

### 8.8 PR 분할 계획 (`light-house-summary` branch 확장)

| PR | scope | 산출물 |
|---|---|---|
| **PR-I1** | strategy interface + signature classifier + IoListStrategy 단독 | 자료 A 의 summary 박제 e2e |
| **PR-I2** | WorkOrderStrategy + PdfControlSpecStrategy | 자료 B/C summary 박제 |
| **PR-I3** | TextDumper.fs 의 summary/ dir 박제 hook + Packager.fs zip whitelist | 색인/upload e2e |
| **PR-I4** | SpecializedDigestBuilder + SystemContentBuilder cache breakpoint 3 | system prompt 주입 |
| **PR-I5** | Promaker WPF — KbSpecializedDigestFetcher + LlmChatViewModel partial + 회귀 테스트 | 사용자 e2e |
| **PR-I6** | **CaptionPromptStrategy 7 kind + detect signature + Opus model 차등** | **image caption quality 도약** |
| **PR-I7** | (자율) **KmmManualStrategy pptx + FlowChart/Interlock caption 의 summary/ 박제 통합** | KMM 매뉴얼 baseline 활용 |

**자율 진입 조건** — PR-H2 (subagent batch) 후 또는 평행. PR-H 의 (D) summary 1줄 박제 와 본 (E) specialized digest 박제 는 디렉토리 분리 (`summary.md` 단일 파일 vs `summary/` 디렉토리) 라 충돌 0. **PR-I6 는 PR-I1~I5 와 평행 가능 (signature 분리)**, **PR-I7 은 PR-I6 의 spot-check (§2 #14 갱신본) 통과 후 자율**.

### 8.9 검증 시나리오

색인 대상 = `F:/Git/dualsoft/secrets/KBSamples/core/` (사용자가 KB collection 으로
등록할 폴더). 현재 그 폴더에 다음 파일이 모여 있음:

```
F:/Git/dualsoft/secrets/KBSamples/core/
├── 4-3. 광명2 SIDE OUTER SV IO LIST STD MAP.xlsx   ← 자료 A (IoListStrategy 발동)
├── 3.광명2_전동화공장_제어시스템(HMI편집됨).pdf      ← 자료 B (PdfControlSpecStrategy 발동)
├── 4-1. SV_SIDE_조립작업서_240328.xlsx              ← 자료 C (WorkOrderStrategy 발동)
└── (후속 추가 자료 — 사양관련 기타 PDF / 작업표준서 docx / 기타 메모 .md 등은
     signature 미매치 → summary/ 박제 0, 기존 (B) text dump 와 (A) keyword digest 로 흡수)
```

색인 후 검증:
- `F:/Git/dualsoft/secrets/KBSamples/core/.lighthouse-kb/summary/` 디렉토리에 **정확히
  3개 파일** (자료 A/B/C 의 specialized markdown) 만 박제
- 각 파일 머리말의 strategy version 박제 확인
- KB collection 활성화 후 Promaker chat 시작 → first turn 의 system prompt 에 3개 markdown 합본 inject 박제 확인
- 둘째 turn 의 Anthropic response 에서 `cache_read_input_tokens` ~38K 박제 확인
- 사용자가 "S204 KEY 지그의 YAML 초안" 요청 → §4.4 시연 같은 YAML 출력 확인
- LLM 이 정확 IO 비트 dump 필요 시 `attachment_fulltext` 자율 호출 확인

#### Caption strategy 검증 (§8.6, PR-I6)

별도 검증 폴더 = `F:/Git/dualsoft/secrets/KBSamples/sisters/` (KMM 매뉴얼 1 파일만 색인,
core 와 collection 분리):

- detect 결과 분포 확인 — 76 slides × ~1.5 img 중 FlowChart 5+ / InterlockDiagram 6+ /
  SystemTopology 2+ / LayoutFloorPlan 6+ / HmiScreenshot 20+ / TableSnapshot 4+ / 나머지 Generic
- Opus 차등 — FlowChart / InterlockDiagram / SystemTopology 호출 model `claude-opus-4-7` 확인,
  나머지 Sonnet 4.6
- spot-check (§8.6.4 갱신본) — 각 kind 5장 × 7 = 35장 human eval
  - FlowChart: edge `A → B` 가 도식과 정합 4/5 이상
  - InterlockDiagram: signal source/target 라벨 정합 4/5 이상
  - SystemTopology: 통신 protocol 라벨 정합 4/5 이상
  - 나머지: legacy 동등성 4/5 이상
- 합격 후 자율 PR-I7 진입 — KmmManualStrategy 박제 + `.lighthouse-kb/summary/` 통합

#### KMM caption A/B test (PR-I7 진입 조건, M19)

KMM 매뉴얼 caption 을 광명2 모델 생성에 inject 하는 것이 noise 가 아닌지 사전 검증.
실험 설계:

- **샘플 task**: 광명2 #204 (KEY 지그) zone 의 Active YAML 생성. LLM 입력 = 자료 A/B/C
  의 `.lighthouse-kb/summary/*.md` (고정).
- **조건 A** (control): KMM caption 미주입.
- **조건 B** (treatment): KMM 매뉴얼의 FlowChart/InterlockDiagram caption 동봉 주입.
- 각 조건 5회 반복 실행 (LLM stochastic 흡수).
- **평가 지표**:
  - Arrow Type 정확도 (golden vs LLM 출력의 Start/Reset/StartReset/ResetReset 일치율)
  - callCondition 트리 정합 (도메인 전문가 검수)
  - YAML validate 통과율 (`ValidateModelDoc`)
- **합격 기준**: 조건 B 가 모든 지표에서 조건 A 보다 ≥ 5%p 향상 + 어느 지표도
  회귀 0. 미합격 시 KmmManualStrategy 박제 보류 또는 prompt 재설계.

---

## 9. 실행 권고 — PoC 진행 순서

### 단계 A — strategy 개발 (Phase 1, 1.5~2주)

1. **strategy interface + signature classifier 설계** (PR-I1)
2. **IoListStrategy 구현 + 자료 A 회귀 e2e** (PR-I1)
3. **WorkOrderStrategy + PdfControlSpecStrategy** (PR-I2)
4. **TextDumper summary/ dir 박제** (PR-I3)
5. **SpecializedDigestBuilder + SystemContentBuilder** (PR-I4)
6. **Promaker WPF 주입** (PR-I5)

### 단계 B (P2) — Promaker YAML 매퍼 (0.5~1주)

7. **YAML schema 도구화** — Promaker `ApplyModelDoc` / `ValidateModelDoc` 가 LLM 출력 수용 + 사용자 GUI 검토 path 확인
8. **회귀 baseline 박제 — 도메인 전문가 검수 의무** ⚠️ (M18) — 광명2 #204 zone YAML
   출력을 golden 으로 KB 색인하기 **전에**, 자료 B 작성처 (HKMC 설비제어기술2팀) 또는
   광명2 SV 라인 운영자의 검수 1회 통과 필수. LLM 생성물을 검수 없이 golden 박제하면
   **첫 오류가 영구화** 되어 이후 strategy 개선의 회귀 baseline 자체가 오염됨.

### 단계 C — 다른 라인 확장 (Phase 3, 라인 당 0.5주)

9. FLR / BB / CRP / ROOF / BC 자료 입수 시 signature 매치 여부만 확인 — 매치되면 strategy 재사용, 매치 안 되면 strategy 변형 1개 추가

### 단계 D (P4) — image caption strategy 도입 (1주)

10. **PR-I6** — `CaptionPromptStrategy` 7 kind + signature detector + Opus model 차등 (§8.6).
    **dependency**: 코드 영역은 PR-I1~I5 와 분리되어 평행 가능, 단 사용자 진입 priority
    는 P1~P3 검증 후 (P4) — strategy 자체 구현은 평행, 운용 진입만 순차 (M21).
11. **PR-I6 spot-check** (§8.9) — 각 kind 5장 × 7 = 35장 human eval, ≥ 4/5 합격
12. **PR-I7 자율 진입** — `KmmManualStrategy` (pptx) + KMM 매뉴얼 FlowChart/Interlock
    caption 의 `.lighthouse-kb/summary/` 통합 박제. **A/B test 의무** (M19) — 멕시코
    2015 prior-art 가 광명2 2024 모델 생성에 noise 인지 검증: 광명2 #204 YAML 생성
    실험을 (a) KMM caption 미주입 (b) KMM caption 주입 두 조건에서 각 5회씩 실행 →
    정합성 / Arrow Type 정확도 비교. 주입 조건이 비주입 조건보다 명백히 우월할 때만
    `.lighthouse-kb/summary/` 박제 활성화.

이 단계 후 KMM 매뉴얼이 광명2 모델 생성의 **patterns reference** 로 활용 가능:
- "광명2 #204 의 KEY 지그 클램프-용접-언클램프 sequence 의 Arrow Type 은?" 질문 시
- LLM 이 KMM 매뉴얼의 FlowChart caption JSON (`edges:[{from,to}...]`) 을 retrieval
- → 같은 부서·같은 라인 유형의 기존 모델 패턴을 reference 로 답변 정확도 향상

### 차후 결정 포인트

- **PLC Ladder export** 가 입수되면 시나리오 격상 — Ev2.Oracle `parse_lspous.py` 를 LightHouse strategy 로 임포트 (또는 PLC Ladder 별 strategy 추가)
- **다른 차체공장 / 다른 OEM** 자료 형식 — signature 기반이라 영향 0, strategy 1세트 추가
- **PR-H2 (subagent batch summary)** 와 본 (E) layer 의 정보 영역 분리 — (D) = doc 1줄, (E) = 정제 markdown 수십K. 충돌 0.
- **caption prompt SSOT 갱신** — `todo-lighthouse-indexer-claude-caption.md` §2 #5 (단일 SSOT) 과 §2 #14 (spot-check 합격 기준) 두 항목을 §8.6.4 갱신본으로 patch. patch 시점 = PR-I6 진입 turn 의 commit message.

---

## 10. 참고 문서

| 위치 | 내용 |
|---|---|
| `Apps/Promaker/Docs/yaml-protocol-v0.md` | Promaker LLM ↔ MCP YAML 프로토콜 v0 (DS 모델 schema SSOT) |
| `Apps/Promaker/Docs/todo-lighthouse-index-summary.md` | LightHouse KB 4-layer RAG (A keyword / B text dump / C chunk / D doc summary). 본 §8 의 (E) layer 가 위 위에 추가 |
| `Apps/Promaker/Docs/howto-connect-lighthouse-service.md` | LightHouse Service 설치 + Promaker 연결 가이드 |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.Attachments.cs` | chat 첨부 분류 (xlsx 미지원 / PDF 32MB cap / 텍스트 1MB cap) |
| `Solutions/Core/Ds2.LightHouse/Classifier.fs` | KB 색인 분류 (xlsx 지원, 거부 확장자) |
| `Solutions/Core/Ds2.LightHouse/Extractors/OoxmlExtractor.fs` | OOXML extractor + XlsxSheetRoles (Gantt SSOT, WorkOrderStrategy 재사용) |
| `Solutions/Core/Ds2.LightHouse/TextDumper.fs` | raw markdown dump 박제 (`.lighthouse-kb/text/`) — 본 §8 가 `summary/` 평행 추가 |
| `Solutions/Core/Ds2.LightHouse/SummaryBuilder.fs` | PR-H1 의 doc 1줄 summary (`.lighthouse-kb/summary.md`) |
| `Solutions/Core/Ds2.LightHouse/CaptionGenerator.fs` | 이미지 caption SSOT (`CaptionPrompt` Literal + `callAnthropic` wire). §8.6 이 task-specific 분기 도입 |
| `Apps/Promaker/Docs/todo-lighthouse-indexer-claude-caption.md` | `/indexer` skill subagent caption 위임 — §2 #5 SSOT + §2 #14 spot-check 기준 갱신 대상 (§8.6.4) |
| `solutions/Ev2.Backend/src/Ev2.Oracle/CLAUDE.md` | Ev2.Oracle 지침 (시나리오 A++ 까지의 외부 파이프라인 — A+++++ 채택 후 의존 제거) |
| `solutions/Ev2.Backend/src/Ev2.Oracle/Prompts/common-build-model-domain.md` | tags.json 스키마 + zone/device/action 분석 규칙 (strategy 설계 reference) |
| `solutions/Ev2.Backend/src/Ev2.Oracle/Doc/lse-il-info.md` | LS XGT IL export 구조 (차후 Ladder strategy 입력 형식) |
| **자료 A** — `F:/Git/dualsoft/secrets/KBSamples/core/4-3. 광명2 SIDE OUTER SV IO LIST STD MAP.xlsx` | IO List (43 sheets, 5000+ 비트) |
| **자료 B** — `F:/Git/dualsoft/secrets/KBSamples/core/3.광명2_전동화공장_제어시스템(HMI편집됨).pdf` | 제어시스템 사양 (165p, 어휘 SSOT + 설비 카탈로그 + 통신/안전 사양) |
| **자료 C** — `F:/Git/dualsoft/secrets/KBSamples/core/4-1. SV_SIDE_조립작업서_240328.xlsx` | 조립작업서 (13 sheets, 공정별 step 시퀀스 + 시간) |
| **sister 자료 — KMM 매뉴얼 (SIDE OTR)** — `F:/Git/dualsoft/secrets/KBSamples/sisters/03.SIDE OTR LINE_KMM 라인 운영 매뉴얼(20150120)_(국문).pptx` | 멕시코 차체공장 SIDE OTR LINE 운영 매뉴얼 (76 slides, 도식 중심 — §8.6 caption strategy 도입 후 prior-art baseline) |
