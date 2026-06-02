# 20. CCTV 설비 오버레이 — 데이터 모델 & API 설계 (P4)

> 상태: **설계(검증 완료)**. dspilot-ux.html P4 의 "영상 위 설비 라벨 오버레이 + 오버레이 에디터" UX 를 실제 데이터로 구현하기 위한 신규 모델 설계. 근거 조사 + 적대적 검증을 거쳐 **시뮬레이션 전용 가정/존재하지 않는 API 가정을 제거**한 보정본.

## 0. 목표 / 범위
- 영상(WHEP) 위 절대좌표 **설비 라벨 오버레이**: DeviceId + 현재 상태(색) + 시간/편차, 라벨↔설비 연결선.
- **오버레이 에디터**: 그리드에 영역을 그려 라벨을 배치하고 Call/PLC 태그에 바인딩 → 영속.
- 선택 설비 상세 패널, 채널별 상태.
- 오버레이 단위 = **Call 단위 바인딩**(라벨 1개 ↔ Call 1개). 카메라는 `CctvCamera.Name`(영숫자/`-`/`_` 제약, URL path)로 식별.

## 1. 영속 결정 — plc.db 가 아니라 JSON 파일
- **`%ProgramData%\DualSoft\Shared\cctv-overlays.json`** + 신규 `CctvOverlayService`(싱글톤, `BlueprintService` 선례 복제).
- 이유: `DatabaseLifecycleService.RebuildDatabaseAsync` 가 **plc.db 파일을 통째로 삭제**한다(doc 컨벤션 §4). 사용자가 그린 오버레이 좌표는 rebuild 로 사라지면 안 되는 **사용자 자산** → plc.db 금지. `BlueprintLayout`(layout-data.json)과 동일 판단.
- 저장 경로/`IWebHostEnvironment` 사용(WebRoot vs ContentRoot)은 구현 전 `BlueprintService._filePath` 생성 코드를 직접 확인해 맞춘다 **(선결 V2)**.

## 2. 좌표계
- 모든 좌표는 **영상 프레임 기준 정규화 0~1**(x, y, w, h). 해상도/스트림 변경에 독립.
- 렌더: `object-fit: contain`(cctv.html:68) → letterbox 발생. 정규화→px 변환 시 **displayRect**(실제 표시 사각형, letterbox offset 포함)를 거친다.
- 에디터 역변환(px→0~1)도 **반드시 displayRect 기준**: `nx = (clientX - displayRect.left) / displayRect.width`. ⚠ editor.html 의 blueprint 그리드는 viewBox stretch 라 letterbox offset 이 없다 — 그 좌표 코드를 그대로 베끼면 어긋난다. 별도 displayRect 계산 필수.

## 3. 데이터 모델 (`CctvOverlay`)
```jsonc
// cctv-overlays.json
{
  "version": 1,
  "overlays": [
    {
      "id": "ovl_<guid>",        // 클라 생성 GUID 문자열
      "cameraName": "STN1",      // CctvCamera.Name (FK, URL-safe)
      "callId": "<guid>",        // 바인딩된 Call (GetAllCallTagPairs 의 CallId)
      "callName": "Press.Down",  // 표시/조회 캐시 (rename 대비 callId 가 정본)
      "x": 0.62, "y": 0.30, "w": 0.18, "h": 0.12,  // 정규화 0~1
      "label": "STN1-Press",     // 표시 라벨 (없으면 callName)
      "anchorX": 0.71, "anchorY": 0.42  // 연결선 끝점(옵션)
    }
  ]
}
```
- `callId` 가 정본, `callName` 은 표시 캐시. Call 매핑은 `PlcToCallMapperService.GetAllCallTagPairs()`(검증됨, 싱글톤 `Program.cs:96`) → `(CallId, CallName, FlowName, WorkName, InTag, OutTag)`.
- **카메라 rename 보존**: 카메라 개명 시 `cameraName` 만 갱신(삭제로 오인 금지). 삭제(delete)와 개명(rename)을 구분하는 매핑 보존 로직 필요 — 미구분 시 좌표 유실.

## 4. REST API (`CctvController` 확장, BlueprintController 즉시영속 CRUD 선례)
| Method | Path | 동작 |
|---|---|---|
| GET | `/api/cctv/overlays?camera={name}` | 카메라별 오버레이 목록 |
| POST | `/api/cctv/overlays` | upsert (드래그 이동마다 즉시 저장 — 디바운스보다 mutation 즉시 동기 저장이 BlueprintController 선례에 충실) |
| POST | `/api/cctv/overlays/delete` | `{id}` 삭제 |
| GET | `/api/cctv/available-calls` | 바인딩 후보 Call 목록 = `GetAllCallTagPairs()` projection |
| GET | **`/api/cctv/overlay-state?camera={name}`** | 오버레이별 현재 상태색 공급 (아래 §5) |

## 5. 실시간 상태 구동 — ⚠ 검증으로 정정된 핵심
mockup 의 상태색/깜빡임을 구동하는 소스. **초기 설계의 절반이 실코드에 없어 다음과 같이 정정한다.**

### 5.1 색 소스 = `DspDbService.Snapshot.Calls` (SignalR batch 아님)
- `DspDbService.Snapshot.Calls : IReadOnlyList<CallState>` (+ `CallsByFlow`) 가 **per-Call 상태 + 통계를 이미 보유**: `DspCallEntity.State / AverageGoingTime / StdDevGoingTime / PreviousGoingTime`. `DspDbService` 는 싱글톤.
- 신규 `GET /api/cctv/overlay-state` 가 이 스냅샷을 `callName → {state, avgGoingTime, stdDevGoingTime}` 로 projection. (`/api/dashboard/snapshot` 은 **Flow 레벨**이라 Call 색을 줄 수 없음 → 별도 엔드포인트 필수.)
- **`CallStatisticsTracker` 사용 금지**: DI 미등록 + 조회 API 없음(`RecordStart/RecordFinish` 만, `TryGet` 없음).

### 5.2 트리거: 폴링 1차 + SignalR 보조
- `CallStateChangedBatch` SignalR 이벤트는 `NotifyStateChanged` 호출처가 `SimulationEngineService` 단 1곳이지만, **그 서비스 자체가 `RuntimeMode.Monitoring`(실 PLC passive 추론) 엔진이다** — 이름과 달리 시뮬 전용이 아니다. Agent→Hub→`HubSubscriberService`→엔진 passive inference 경로에서 **실 PLC 태그로도 동일하게 발화**함이 코드로 확인됨(Agent 데이터 감사, 2026-05). 따라서 폴링 1차 + batch 보조 설계는 안전 폴백으로 유효하되, "실 PLC 에서 안 온다"는 가정은 **틀림**. 단 두 런타임 전제가 충족돼야 함(§선결): broadcast 태그 주소가 AASX IOMap/InTag/OutTag 와 string-equal, 그리고 broadcast source 가 `"monitoring"` 이 아닐 것(자기 echo 무시 방지).
- 따라서 cctv.html 은 **N초 주기 `/api/cctv/overlay-state` 폴링을 1차**로, `CallStateChangedBatch` 는 "있으면 즉시 refetch, 없으면 폴백" 보조 트리거로만 사용.

### 5.3 상태 → 색 매핑 (정직 버전)
- 실제 방출 상태는 **`Ready` / `Going` / `Finish` 3종, PascalCase**. 반드시 `(state||'').toLowerCase()` 정규화(누락 시 전부 default 색).
- `error` 상태는 **실시간 상태머신에 없음**(코드 0건). → 1차에서 `error→적색/깜빡임` 제거. 향후 `DspCallEntity.ErrorText` 가 비어있지 않을 때 적색으로 조건 재정의.
- **한도초과(over-limit) 적색 보류**: `HistoryView.MaxCallGoingTimeMs`(30000)는 "한도"가 아니라 **"통계 제외 필터/idle 판정"**(AppSettingsModel:138,149). 이를 알람으로 쓰면 의미가 역전됨 → 금지. 표준(takt)이 없으므로 한도 판정은 **정직하게 보류**. 굳이 넣으면 `평균+kσ` 통계 이상치를 *참고용 경고*로만(별도 알람 임계 설정 신설 시 정식화).

## 6. 에디터 UX 흐름
1. CCTV 페이지 "영역 편집" → 편집 모드. 영상 위에 격자 오버레이.
2. 드래그로 사각형 → displayRect 기준 px → 정규화 0~1 저장값 계산.
3. 등록 폼: Call 선택(`/api/cctv/available-calls` 드롭다운), 라벨, (좌표 자동). RTSP/PLC태그는 Call 바인딩으로 대체(카메라는 이미 RTSP 보유).
4. 저장 → `POST /api/cctv/overlays` 즉시 영속.
5. 편집 종료 → 라이브 모드에서 `overlay-state` 색 구동.

## 7. 마이그레이션 안전성
- 신규 파일 JSON 1개 + 신규 서비스/컨트롤러 메서드만. 기존 스키마/테이블 무변경 → plc.db rebuild·Promaker 무관.

## 8. 단계별 구현
- **선결 V1** (필수, 착수 전 런타임 확인): 실 PLC(시뮬 OFF)에서 `DspDbService.Snapshot.Calls[].State` 가 실시간 갱신되는가? 안 되면 P4 색 기능은 데모(시뮬) 전용임을 명시하고 그 전제로만 진행.
- **선결 V2**: `BlueprintService` 실제 저장 경로/`IWebHostEnvironment` 확인.
- **Phase 1**: `CctvOverlayService` + JSON + CRUD API + 에디터(배치/바인딩/저장) + 정적 렌더(상태색 없이 라벨만).
- **Phase 2**: `/api/cctv/overlay-state` + 폴링 색 구동(Ready/Going/Finish) + 연결선 + 선택 설비 상세.
- **Phase 3**(옵션): `평균+kσ` 참고 경고, 알람 임계 설정 신설, 깜빡임.

## 9. 정직성 경계 (가짜로 채우면 안 되는 것)
- 색 구동이 **실 PLC 에서 동작하는지 미검증**(V1) — 검증 전엔 "데모 전용 가능성" 명시.
- "스트림 지연(ms)", "실시간 알람 건수" KPI 는 **수집 텔레메트리 없음** → 계속 `—`/"미수집".
- 한도초과 판정은 표준 부재로 **보류**(가짜 임계 금지).

## 10. 개선판 (2026-05) — Flow 단위 바인딩 + 영상벽 컴포저

> dspilot-ux.html P4 참고 리디자인. 페이지 높이 제한 제거(스크롤 허용), 좌측 카메라 레일 + 우측 드래그 배치형 뷰잉 그리드, 오버레이를 **대시보드 도면 레이아웃과 동일하게 Flow 단위**로도 지정.

### 10.1 바인딩 단위 확장 — Flow + Call (둘 다)
- `CctvOverlay` 에 `FlowId(Guid?)`/`FlowName(string?)` 추가, `CallId` 를 `Guid?` 로 완화. **최소 하나(Flow 또는 Call) 필수**. 둘 다 있으면 "이 Flow 안의 이 설비".
- 정본은 `FlowId`/`CallId`, 이름은 서버 재해석(rename 반영). Flow 해석 = `DsProjectService`(활성 시스템→Flow, BlueprintController 와 동일 소스). Call 해석 = `PlcToCallMapperService`(기존).
- 신규 `GET /api/cctv/available-flows` → `(flowId, flowName, systemName)` 팔레트용. `available-calls` 는 유지(Flow 안 세부 설비 선택).
- 기존 Call 단위 오버레이 저장본은 그대로 동작(`FlowId` 만 null) — 비파괴.

### 10.2 상태색 — overlay id 기준, Call 우선 / Flow 폴백
- `GET /api/cctv/overlay-state` 응답을 `(id, state, avgTimeMs)` 로 단순화(overlay id 키). 프론트는 `stateById[overlay.id]`.
- Call 바인딩이 있으면 `Snapshot.Calls`(per-Call, 더 구체적) → `State`/`AverageGoingTime`. 없으면 `Snapshot.Flows`(per-Flow) → `State`/`AvgCT ?? CT`.
- **Call 조인은 이름이 아니라 정본 `CallId`(=AASX Call.Id = `CallState.CallId`)로** 한다(FlowController 선례) — Call rename·동명(다른 Flow) 충돌에 강건. Flow 는 스냅샷에 Guid 키가 없어(`FlowState.Id`=int rowid) FlowName 조인(대시보드와 동일 한계).
- 색: Ready=초록 / Going=청록 / Finish=파랑 / **Error=적색**(Flow 상태에 Error 존재, 대시보드 flowColor 와 동일) / 그외 회색. 한도초과 등 추가 적색은 계속 보류(§5.3).
- 타일 LIVE 칩은 전역 MediaMTX sync 가 아니라 **각 스트림의 WebRTC `connectionState`**(cctv-whep `onState` 콜백)로 표시 — connected=LIVE / connecting=연결 중 / failed·disconnected=끊김(정직성).

### 10.4 검토로 보강한 정합성(2026-05 adversarial review)
- **카메라 rename → 오버레이 보존**: `SettingsController.Save` 가 (old∖new)·(new∖old) 가 각 1건인 단일 개명을 감지해 `CctvOverlayService.RenameCamera(old,new)` 호출(추가/삭제/순서변경과 구분, 모호하면 미적용). 이전엔 `RenameCamera` 가 dead code 라 개명 시 좌표가 통째로 고아가 됐다.
- **reload 구간 비파괴 upsert**: `UpsertOverlay` 가 flowId 해석 실패 시, **이미 저장된 id 의 갱신(이동/라벨)** 은 클라 캐시 FlowName 으로 허용(400 금지). 하드 400 은 신규 생성 경로에만(프로젝트 미로드/AASX 스왑 중 드래그 저장 유실 방지).
- **available-calls 에 flowId 부여**: 팔레트에서 무장한 Flow 의 세부 Call 목록을 FlowName 이 아니라 flowId 로 필터(동명 Flow 충돌 방지). flowId 는 `DsProjectService` 구조(시스템→Flow→Work→Call)에서 직접 매핑.

### 10.3 영상벽 컴포저
- `GET /api/cctv/config` 가 **전체(활성·유효) 카메라**를 내려보냄(기존 6대 캡 제거) + `maxConcurrent`. 좌측 레일 = 전체 카메라 드래그 소스, 우측 그리드 = 동시표시(최대 `maxConcurrent`=6, FHD 디테일 보존). 그리드 배치는 **localStorage**(`dspilot-cctv-wall`) 영속(브라우저별).
- HTML5 DnD: 레일→그리드(추가), 타일→타일(순서변경), 타일→레일(제거). ✕/＋ 버튼 폴백. 분할은 count-1~6 그리드 클래스.
- 오버레이는 **각 그리드 타일에 per-tile displayRect** 로 letterbox 보정 렌더(읽기전용). 편집은 별도 에디터 stage(단독 카메라)에서 Flow 팔레트 arm→드래그 그리기.

## 11. 개편 (2026-06) — 단일 "영상벽" 워크스페이스 + 종류 디자인 구분 + 호버 툴팁

> 사용자 요구: ① 더 직관적인 카메라 선택·배치(스플릿 또는 개별 보기), ② Flow/Call 오버레이 배치, ③ Flow 와 Call 을 **디자인으로 구분**, ④ 두 종류 모두 **상태=색**, **그 외 상세=툴팁**. 설계 판정은 3안 판정단 + 적대적 타당성 검증으로 수렴(Proposal 1 골격 + 2의 모양뱃지/해치 + 3의 클라조인/정직성 이식). 적대적 코드리뷰로 2건(medium) 발견·수정.

### 11.1 레이아웃 — 3섹션 → 1 워크스페이스
- 기존 컴포저+별도 에디터+보드 3단을 **하나의 영상벽 카드**로 통합. 카드 헤더에 **레이아웃 프리셋 세그먼트 컨트롤**(단독/2/2×2/2×3/자동) — 폰트 의존 회피 위해 **인라인 SVG 분할 다이어그램**(자동만 `auto_awesome` 글리프). `auto`=기존 `count-N`(표시 대수 적응), 그 외=고정 grid-template + 빈 셀 `.cctv-cell-empty` 플레이스홀더. 프리셋은 신규 localStorage `dspilot-cctv-layout`(+단독 대상 `dspilot-cctv-solo`)에 영속. `dspilot-cctv-wall` 은 불변.
- 좌측 레일은 **클릭 우선**(드래그는 보조) + 검색(`camFilter`) + per-item 단독 버튼.

### 11.2 단독(SOLO) — 추가 스트림 없음(핵심 불변식)
- 단독은 `wall[]` 을 건드리지 않고 `layout='solo'`+`soloId` 만 설정, **같은 마운트된 `<video>` 를 CSS 로만 확대**(`.cctv-grid.layout-solo .cctv-tile{display:none}` + `.is-solo{display:flex;grid-column/row:1/-1}`). `cctvWhep.start` 2회 호출 금지 — 별도 solo `<video>` 는 +1 WebRTC 스트림이라 `maxConcurrent`=6 위반(whep 은 srcObject 를 단일 element id 에 바인딩). 배경 스트림은 display:none 으로 살아있어(웜) 즉시 복귀. Esc 캐스케이드: 편집 종료 → 단독 해제 →(브라우저 fullscreen).

### 11.3 인플레이스 편집 — 별도 에디터 stage 폐지(스트림 1개 순감)
- 타일 바의 `edit_location_alt` 가 **그 타일만**(`editTileId`, 동시 1개) 편집 모드로 전환. 인라인 arm 바(Flow 드롭다운 + 옵션 Call `<select>`, flowId 매칭). 드래그 그리기/이동/리사이즈는 검증된 제스처 엔진을 **라이브 타일의 `cctv-wall-<id>` stage + `tileRects[camId]`(per-tile displayRect)** 로 **재타겟**(기존 `editRect`/별도 `cctv-edit-<id>` 스트림 제거 → 6대 캡 대비 순감). letterbox rect 는 편집 토글·레이아웃 변경·resize 마다 재계산.
- **리뷰 수정 #1(이동/리사이즈 좌표 동결)**: 좌표 추적이 타일 레이어 `@mousemove`(overflow 클립)에만 묶여 타일 밖 release 시 가장자리값으로 커밋되던 문제 → 제스처 동안 **window 레벨 mousemove**(`_armWindowMove`/`_disarmWindowMove`) 로 추적, 레이어 핸들러는 `_winMove` 활성 시 양보. `_layerXY` 는 window 추적 시 `editTileId` 로 레이어 rect 를 직접 해석.

### 11.4 종류=디자인(색과 직교), 상태=색
- **Flow = ZONE**: 실선·둥근 테두리 + 12% 채움 + 안쪽 모서리 **● 원형 뱃지(`account_tree`)** + `FLOW` 칩. **Call = POINT/설비**: 점선·각진 테두리 + **대각 해치(`repeating-linear-gradient`)** + **◆ 다이아 뱃지(`memory`)** + `CALL` 칩. `o.callId ? 'Call':'Flow'` 로 구동 — **백엔드 무변경**. 6분할 썸네일·흑백·색약에서도 실루엣으로 구분.
- **색(hue)은 상태 전용**(`--ovl-color`, `colorOf`): Ready=초록/Going=보라(브랜드)/Finish=파랑/Error=적색/미수신=회색. Going/Error 은 **인셋 링 펄스**.
  - **리뷰 수정 #2(가장자리 클립)**: 펄스 outward box-shadow(+7px)·칩(top/left 음수)이 레이어 `overflow:hidden` 에 잘리던 문제 → 펄스를 **inset 링**(`::after inset:0` + inset box-shadow + opacity)으로, 좌측 가장자리 칩은 `tagInsetLeft`(`left<6`)→`left:2px` 안쪽 플립. 뱃지는 원래 박스 안쪽(top:3/left:3)이라 안전.

### 11.5 호버 툴팁(요구사항 #4 "그 외 상세")
- 박스 `@mouseenter`/`@mouseleave` → 타일 stage 기준 절대배치 `.cctv-ovl-tip`(`pointer-events:none`, z-index 레이어 위, 박스 px rect 기준·가장자리 플립, 편집 중 타일은 억제). 헤더=종류 글리프+`FLOW/CALL`+상태칩, 본문 행은 존재 시에만(`x-show`).
- **이름은 클라 조인**(서버 미포함): `systemName`=`availableFlows`(flowId), `workName`=`availableCalls`(callId). 라이브 진단은 **`overlay-state` DTO 확장(컨트롤러 전용, 신규 쿼리 없음)**: Call=`workName/device/goingCount/progressRate/errorText`, Flow=`currentCt/movingStartName/movingEndName`. `ErrorText` 는 표시 전용 — **Call 적색 합성 금지**(Error 는 상태 구동, §5.3). 미수신 필드는 행 생략(가짜 금지). 색·툴팁이 같은 3초 폴링 소스라 불일치 없음.

### 11.6 정직성 / 미해결
- `StdDevGoingTime`/`PreviousGoingTime` 은 `CallState` 스냅샷에 없음(DspCallEntity 에만) → 툴팁에서 **생략**(Tier 2: DspDbService 스냅샷 빌더에 먼저 추가해야 함, 보류).
- Flow 상태는 여전히 `FlowName` 조인(FlowState 에 Guid 키 없음) — 동명 Flow 모호성 잔존(대시보드와 동일 한계).
- 실 PLC 에서 Call `State=='Error'` 실제 방출 여부는 런타임 검증 대상.

### 관련 파일
`Services/DspDbService.cs`(Snapshot 싱글톤·per-Call 소스), `Models/DspDbModels.cs`(CallState/FlowState·per-Call/Flow 필드), `Models/Dsp/DspCallEntity.cs`(State/Average/StdDev/ErrorText), `Services/PlcToCallMapperService.cs:254`(GetAllCallTagPairs), `Controllers/BlueprintController.cs`(즉시영속 CRUD 선례), `Services/BlueprintService.cs`(JSON 영속 선례), `Controllers/CctvController.cs`(`CctvOverlayStateDto` 확장 + `GetOverlayState` projection), `Models/AppSettingsModel.cs`(CctvCamera, MaxCallGoingTimeMs 의미), `wwwroot/app/cctv.html`(영상벽 워크스페이스·종류 디자인·툴팁·인플레이스 편집), `wwwroot/js/cctv-whep.js`(단일 element 바인딩 — 단독 +1 스트림 금지 근거).
