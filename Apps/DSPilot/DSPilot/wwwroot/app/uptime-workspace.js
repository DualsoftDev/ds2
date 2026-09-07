        // 정지 구분: 고장(isFailure=true) / 유지보수(isFailure=false) / 비생산(isNonProd=true, A 분모 밖).
        const FAULT_DEF = { label: '고장', color: 'var(--oee-fault)', cls: 'hatch-fault', pat: 'up-pat-fault' };
        const MAINT_DEF = { label: '유지보수', color: 'var(--oee-maint)', cls: 'hatch-maint', pat: 'up-pat-maint' };
        const NONPROD_DEF = { label: '비생산', color: 'var(--nonprod)', cls: 'hatch-nonprod', pat: 'up-pat-nonprod' };
        // 대기(고장 여파, doc/25) — 라인 내 다른 설비 고장으로 서 있던 시간(기준 이상 → 분모 밖). 비생산과 분리 표기.
        const WAIT_DEF = { label: '대기(고장 여파)', color: 'var(--oee-slack, #7dd3fc)', cls: 'hatch-wait', pat: 'up-pat-wait' };

        // 일자별 차트 인스턴스 — Alpine 반응형 밖에 보관(Proxy 크래시 방지)
        let _dailyChart = null;
        let _nightPattern = null; // 비생산(제외) 세그먼트용 밤하늘 빗금 패턴 (캐시)

        // 밤하늘 같은 어두운 푸른 빗금 CanvasPattern — 비생산(A 분모 밖) 시간 표시용.
        function _nightHatchPattern(canvas) {
            if (_nightPattern) return _nightPattern;
            const size = 8;
            const tile = document.createElement('canvas');
            tile.width = size; tile.height = size;
            const t = tile.getContext('2d');
            t.fillStyle = '#0b1b38';                     // 밤하늘 바탕(어두운 남색)
            t.fillRect(0, 0, size, size);
            t.strokeStyle = 'rgba(96,140,210,0.55)';     // 어두운 푸른 빗금
            t.lineWidth = 1.3;
            t.beginPath();                               // 대각선(코너-투-코너 → 이음새 없이 타일링)
            t.moveTo(0, size); t.lineTo(size, 0);
            t.moveTo(-1, 1); t.lineTo(1, -1);
            t.moveTo(size - 1, size + 1); t.lineTo(size + 1, size - 1);
            t.stroke();
            _nightPattern = canvas.getContext('2d').createPattern(tile, 'repeat');
            return _nightPattern;
        }

        // 정지 유형 대각선 빗금 CanvasPattern (색 위 흰 빗금) — 고장/유지보수용. 가동(솔리드)과 대비.
        // 매 렌더마다 새로 생성(캔버스 재생성 시 stale context 방지) — 8×8 타일이라 비용 무시 수준.
        function _stripePattern(canvas, base) {
            const size = 8;
            const tile = document.createElement('canvas');
            tile.width = size; tile.height = size;
            const t = tile.getContext('2d');
            t.fillStyle = base;
            t.fillRect(0, 0, size, size);
            t.strokeStyle = 'rgba(255,255,255,0.55)';
            t.lineWidth = 1.3;
            t.beginPath();
            t.moveTo(0, size); t.lineTo(size, 0);
            t.moveTo(-1, 1); t.lineTo(1, -1);
            t.moveTo(size - 1, size + 1); t.lineTo(size + 1, size - 1);
            t.stroke();
            return canvas.getContext('2d').createPattern(tile, 'repeat');
        }

        // ── 생산효율 매트릭스(P6 L0) SVG 헬퍼 — Alpine 반응형 밖(임퍼러티브 렌더, Proxy 크래시 방지) ──
        // 설비 합산(Σ_flow) 값의 단위 전환 경계 — 조회 범위가 이 길이 이하면 '시간' 으로 고정한다.
        //   짧은 범위(오늘/24h)에서 '2일 20시간' 은 달력 초과로 오독되고, 긴 범위(30·60일)에서 시간 고정은
        //   '10080시간' 이 되어 못 읽는다. 값 크기가 아니라 <b>범위 길이</b> 기준이라 한 화면이 같은 단위로 묶인다.
        const SUM_HOUR_UNIT_MAX_MS = 48 * 3600 * 1000;

        const TM_NS = 'http://www.w3.org/2000/svg';
        function _tmEl(tag, attrs, parent) {
            const e = document.createElementNS(TM_NS, tag);
            for (const k in attrs) e.setAttribute(k, attrs[k]);
            if (parent) parent.appendChild(e);
            return e;
        }
        // 정지 캡(빨강) — 시간분해 막대의 '정지' 와 같은 의미(빗금 대신 3D 캡).
        const TM_DOWN_FACES = { top: '#B71C1C', right: '#C62828', front: '#D32F2F' };
        // 가동(초록) 단일색 — OEE 등급별 색 스케일(옐로/그린 4단계 + 산출불가 회색)은 2026-07-08 단순화로 제거,
        // 색은 가동/정지 두 가지만 구분. OEE 값 자체는 툴팁에 유지.
        const TM_RUN_FACES = { top: '#66BB6A', right: '#4CAF50', front: '#43A047' };

        function uptimeApp() {
            return {
                // 기존 UserTag 상태 (+ 구 이상발생 관리 흡수: 필터/페이지/정의/차트)
                ut: null, loading: true, error: null, dark: false,
                tab: 'oee', // 'oee' | 'anomaly' — URL ?tab= 와 동기화
                // 물리 페이지 뷰: 'oee'(=/uptime-oee 설비효율) | 'teep'(=/uptime-teep 생산효율) | 'alarm'(=/uptime-alarm) | 'both'(구 통합, 폐기).
                // window.DSP_UPTIME_VIEW 로 각 HTML 이 주입. view 가 곧 표시할 도메인(탭바 없음).
                // 구 내부 탭(oeeTab ?section=)은 2026-07-03 물리 분리로 폐지 — ?section=teep 딥링크는 init 이 /uptime-teep 로 보냄.
                view: (window.DSP_UPTIME_VIEW || 'both'),
                period: 'today',
                curFlow: '', // '' = 라인 전체, 그 외 = 특정 Flow (OEE/정지/도넛/계획시간을 그 설비로 필터)
                curSystem: '', // '' = 스코프 없음, 그 외 = 시스템 단위 묶음(?system=, 좌측 나브 '○○ 관리' 헤더). curFlow 가 우선.
                rt: { connected: false },
                _conn: null, _dt: null, _pollTimer: null,
                // stale 응답 가드 — 폴링/기간변경/페이지이동 응답이 뒤늦게 도착해 최신 상태를 덮어쓰는 경합 방지
                _utSeq: 0, _oeeSeq: 0, _anpSeq: 0,
                // 사용자 로드(기간변경·페이지·정렬 등 비무음) 진행 중 카운트 — >0 이면 폴링/SignalR 무음 재로드를 건너뜀.
                // 무음 로드가 seq 를 선점하면 사용자 로드 응답이 stale 폐기되어, 로딩 인디케이터가 끝나고도
                // (뒤늦은 무음 응답 도착까지) 화면이 안 채워지는 가로채기가 생긴다. OEE 요약처럼 느린 조회일수록 잦음.
                _userBusy: 0,
                // utCategory = 구분 필터 ('' 전체 | 'abnormal' | 'usertag'). 레벨은 서버가 Error 로 통일(클라 미노출).
                utPage: 0, utSearch: '', utCategory: '', utSystem: '', actionOverHint: [],
                // 알람 이력 테이블 — 페이지 크기 / 정렬(서버 처리). sort 키는 서버 화이트리스트와 일치.
                utPageSize: 10, utSort: 'occurredAt', utSortDir: 'desc', _utSearchTimer: null,
                _focusAt: null, // 피드에서 at 으로 진입 시 스크롤·하이라이트할 알람 행 키(occurredAtLocal 초단위)
                _charts: null,
                dailyData: null,

                // 기간 직접 선택
                customFrom: '', customTo: '',

                // 신규 OEE 상태
                oee: null, oeeError: null,
                // 생산효율(TEEP) — /api/oee/teep. teepPct() 가 시간분해 막대 %. _teepSeq=stale 가드.
                teep: null, teepError: null, _teepSeq: 0,
                // 생산효율 매트릭스(P6 L0) — /api/oee/teep/matrix (flow×시간버킷 TEEP·OEE).
                // 라인 전체=3D 아이소(설비×시간), 설비 선택=2D 막대(TEEP·OEE/시간). _teepMxAt=무음(폴링) 갱신 스로틀 기준 시각.
                teepMatrix: null, teepMatrixError: null, _teepMxSeq: 0, _teepMxAt: 0,
                // 3D 아이소 뷰 옵션 — teepIsoRot: 수직축 4시점 스텝 회전(0~3, 가림을 시점 전환으로 회피).
                // 색은 OEE 등급 고정 — 높이가 이미 TEEP 이라 색=TEEP 은 중복(높이=TEEP · 색=OEE 로 두 지표 동시 표현).
                teepIsoRot: 0,
                // 날짜별 비생산 패턴 — /api/oee/planned-stops/actual 의 days(로컬 날짜별 접기, 기간에 맞춰 행 수 변동).
                // 비생산은 시스템(전역) 단위라 curFlow 필터 없이 조회(설정 타임라인과 동일 규칙). _teepNpSeq=stale 가드.
                teepNonProd: null, teepNonProdError: null, _teepNpSeq: 0,
                oeeExporting: false,   // OEE Excel 내보내기 진행 중
                // 계측 품질 — /api/oee/measurement-quality (사이클 제외·누락률). OEE 지표와 별개 축이라
                // A/P/Q 에 섞지 않고 페이지 맨 아래 독립 카드로만 보고한다. mqOpen=설비별 상세 펼침.
                mq: null, mqOpen: false, _mqSeq: 0,
                downtime: [], ranking: [],
                dtTab: 'down', // 정지 로그 구분 탭: 'down'(비가동=고장/유지보수) | 'nonprod'(비생산) — 보내기 후 대상 탭 자동 이동
                dtFilterStatus: 'all', dtFilterFault: 'all', // 'all'|'fault'|'maintenance' (비가동 탭 전용 하위 필터)
                dtMsg: '', _dtMsgTimer: null, _prodMsgTimer: null, _ctMsgTimer: null,
                dtReclassBusy: false, // 비생산↔비가동 보내기 진행 중(이중 클릭 가드)
                // 일괄 선택 상태 — bulkProgress: 순차 처리(합성 행 확정/일괄 이동) 진행 표시("이동 중 3/12")
                selectedIds: {}, bulkBusy: false, bulkProgress: '',
                // 일자 기본값은 로컬 날짜 — toISOString() 은 UTC 라 KST 오전 9시 전엔 어제로 채워짐
                // 품질(양품률) 직접 입력 다이얼로그 — 전반 품질% 를 직접 설정(POST /api/oee/quality, 전역). 품질 Q 카드 클릭으로 염.
                qDialog: { show: false, qualityPct: 100, busy: false, msg: '', err: '' },
                // 오버레이 닫힘 가드 — mousedown 이 오버레이(백드롭)에서 시작했을 때만 닫는다(모달 안에서 시작→백드롭 release 드래그로 오닫힘 방지).
                _qDown: false,
                _dtDown: false, // 정지 이벤트 로그 다이얼로그 백드롭 닫힘 가드
                // 비생산 시간대 (doc/22 §3.3, 2026-07-08 병행 모델) — 당일 자동 판정(10×장시간정지)은 항상 켜져 있고,
                // windows=수동 지정 창(추가로 무조건 비생산, 매일 반복). source: auto(지정 없음)/both. editing=[수동 편집] 모드(addMode=[시간 추가] 드래그 무장).
                // actualNonProd=이번 기간 실제 제외된 비생산(자동+지정 합산 실측 — 통합 타임라인의 자동(점선) 소스).
                // (구 auto/pendingManual 배타 토글, excludedWeekdays/xw*[생산 요일] 는 병행 모델 전환으로 제거.)
                ps: { source: 'auto', ctMultiplier: 10, windows: [], selected: -1, addMode: false, editing: false, msg: '', err: '', busy: false, actualNonProd: null, dirty: false },
                _psDrag: null, // 진행 중 드래그 상태 { mode:'create'|'resize-l'|'resize-r', index, anchor } (비반응형)
                // 정지·비생산 판정 기준 (doc/22 §3/§3.3, 2026-07-13 사용자 설정화) — 비가동=평균CT×idle 초과 사이클,
                // 비생산=평균CT×nonProd 이상 무변화 정지(분모 밖). preview=저장 전 what-if 재분류(서버 오버라이드 계산, 저장·기록 없음).
                cm: { idle: 2.5, nonProd: 15, fault: 2.5, origIdle: 2.5, origNonProd: 15, origFault: 2.5, flows: [], busy: false, msg: '', err: '', preview: null, previewBusy: false },
                _cmSeq: 0, _cmPrevTimer: null, _cmMultMsgTimer: null,
                // 정지 이벤트 로그 토글 — 기본 숨김, 정지 원인 구성(도넛)의 [로그 보기 및 설정] 버튼으로 토글
                showDowntimeLog: false,
                // 날짜별 비가동 패턴 (드릴다운, 2026-07-13) — 가용성 누적 정산의 빨간(비가동) 부분·정지 구성
                // 도넛/범례(고장·유지보수) 클릭으로 열림. '날짜별 비생산 패턴'(생산효율)과 같은 up-npd 골격을
                // 이미 로드된 this.downtime(기간·설비 필터 반영)에서 클라 접기로 그린다. filter='all'|'fault'|'maintenance'.
                dtPat: { show: false, filter: 'all' },
                _dtPatMemo: null, // dtPatDays() 메모 — downtime 재로드(배열 교체)/필터/기간 변경 시 무효
                // 표준 가동시간(idealCT) Flow 일괄 편집 테이블 (편집값은 각 행 객체 draft 에 보관 — Alpine x-for 양방향 바인딩 안정)
                ctTable: [], ctMsg: '', ctError: null, ctLoading: false, ctApplying: false,
                // 알람 차단 관리 모달 — 탭 2개(auto=자동알람/디바이스, user=사용자지정/UserTag).
                //   자동알람: selKinds=적용할 유형 int[], selected=디바이스 체크 맵.
                //   사용자지정(ut): tags=UserTag 정의 목록, selected=주소 체크 맵.
                blockMgr: {
                    show: false, tab: 'auto', busy: false,
                    loading: false, devices: [], kindOptions: [], selKinds: [], selected: {}, msg: '', err: '', filter: '', showBlockedOnly: false, sortCol: 'device', sortDir: 'asc',
                    ut: { loading: false, tags: [], selected: {}, msg: '', err: '', filter: '', showBlockedOnly: false }
                },

                async init() {
                    // 구 내부 탭 딥링크(/uptime-oee?section=teep) → 물리 분리된 생산효율 페이지로 이동(그 외 쿼리 보존).
                    if (this.view === 'oee') {
                        const legacyQp = new URLSearchParams(location.search);
                        if (legacyQp.get('section') === 'teep') {
                            legacyQp.delete('section');
                            const legacyQs = legacyQp.toString();
                            location.replace('/uptime-teep' + (legacyQs ? '?' + legacyQs : '') + location.hash);
                            return;
                        }
                    }
                    this.dark = localStorage.getItem('dspilot-theme') === 'dark';
                    window.addEventListener('storage', (e) => { if (e.key === 'dspilot-theme') { this.dark = e.newValue === 'dark'; this.redrawForTheme(); } });
                    // 사이드바 이상코드 피드에서 진입 시 필터 시드(/uptime?utSystem=&category=&utSearch=).
                    const qp = new URLSearchParams(location.search);
                    // 설비(Flow) 필터는 URL(?flow=)에서만 온다(좌측 나브 '이상·알람' 트리의 FLOW 행 / OEE·TEEP 시스템 그룹). 없으면 라인 전체.
                    if (qp.has('flow')) this.curFlow = qp.get('flow') || '';
                    // 시스템 스코프(?system=) — 좌측 나브 시스템 그룹 헤더(OEE/TEEP) 또는 '이상·알람' 트리 시스템 행 진입.
                    // 설비(?flow=)가 있으면 무시(설비 우선). 알람 스냅샷은 utQs() 가 이 값을 system 파라미터로 보낸다.
                    if (!this.curFlow && qp.has('system')) this.curSystem = qp.get('system') || '';
                    if (qp.has('utSystem')) this.utSystem = qp.get('utSystem') || '';
                    if (qp.has('category')) this.utCategory = qp.get('category') || '';
                    if (qp.has('utSearch')) this.utSearch = qp.get('utSearch') || '';
                    // 기간 복원(?period, custom 이면 +from/to) — 같은 페이지에서 전체/FLOW 를 바꿔 이동할 때
                    // shell 나브가 현재 URL 의 기간 선택을 같이 실어 보낸다(아래 syncPeriodUrl 참조). ?at= 딥링크가
                    // 있으면 그 알람의 '하루'가 우선(아래 블록이 덮어씀).
                    const perQ = qp.get('period');
                    if (perQ === '7d' || perQ === '30d' || perQ === '60d') this.period = perQ;
                    else if (perQ === 'custom' && qp.get('from') && qp.get('to')) {
                        this.period = 'custom';
                        this.customFrom = qp.get('from').slice(0, 16);
                        this.customTo = qp.get('to').slice(0, 16);
                        this.clampCustomRange(); // 북마크/URL 로 상한(2개월) 우회 방지
                    }
                    // 피드에서 발생시각(at)을 받으면 그 '날' 하루를 custom 기간으로 맞춰 클릭한 알람이 조회 범위에 들어오게 한다.
                    // (기본 '오늘'이라 과거 알람이면 0건이 되던 문제 해결.) _focusAt 은 로드 후 그 행을 스크롤·하이라이트하는 키.
                    const at = qp.get('at'); // "yyyy-MM-dd HH:mm:ss" (초 단위) — 알림 이력 행의 occurredAtLocal.slice(0,19) 와 동일 형식
                    if (at && at.length >= 19) {
                        const day = at.slice(0, 10); // yyyy-MM-dd
                        this.period = 'custom';
                        this.customFrom = day + 'T00:00';
                        this.customTo = day + 'T23:59';
                        this._focusAt = at.slice(0, 19);
                        this.syncPeriodUrl(); // 이후 나브 전체/FLOW 이동에도 이 '하루' 기간이 유지되게 URL 에 반영
                    }
                    const seeded = qp.has('utSystem') || qp.has('category') || qp.has('utSearch') || qp.has('at');
                    // 활성 탭: 물리 분리 페이지는 view 가 결정(탭바 없음). 구 통합(both)만 URL ?tab=/시드로 분기.
                    this.tab = (this.view === 'oee' || this.view === 'teep') ? 'oee'
                        : this.view === 'alarm' ? 'anomaly'
                        : ((qp.get('tab') === 'anomaly' || seeded || qp.has('blockMgr')) ? 'anomaly' : 'oee');
                    this.syncTabUrl();
                    // 뒤로/앞으로 가기 시 탭 동기화
                    window.addEventListener('popstate', () => { this.applyTabFromUrl(); });
                    // 이상·알람 추이/Top 차트 모듈 — 알람 도메인에서만 사용(OEE/TEEP 페이지는 해당 캔버스 없음).
                    if (this.view !== 'oee' && this.view !== 'teep') {
                        try { this._charts = await import('/js/user-tag-trend-chart.js'); } catch (e) { console.warn('chart module load failed', e); }
                    }
                    // 최초 진입 로드도 로딩 인디케이터로 감싼다(기간 변경 setPeriod 와 동일 UX). 폴링(load(true))은 무표시.
                    const initLoad = async () => {
                        await this.load();
                        // OEE 도메인 로드 — CT 표(수동 오버라이드 UI)는 설비효율 페이지 전용,
                        // 비생산 시간대(ps.ctMultiplier 등)는 설비효율·생산효율 둘 다 사용.
                        if (this.view === 'oee' || this.view === 'both') await this.loadCtTable();
                        if (this.view !== 'alarm') await this.loadPlannedStops();
                        // 판정 기준 카드(비가동/비생산 배수)는 설비효율 페이지 전용.
                        if (this.view === 'oee' || this.view === 'both') await this.loadCtMultipliers();
                    };
                    if (window.dspLoading) await window.dspLoading.wrap(initLoad, '불러오는 중…');
                    else await initLoad();
                    this.connectSignalR();
                    // '실제 제외 비생산' 타임라인은 설비효율 페이지에만 있음 — 생산효율/알람 페이지는 폴링 생략.
                    // 숨긴 탭(다른 탭 뒤/최소화)은 폴링을 정지 — 방치 탭이 무거운 OEE 엔드포인트를 계속
                    // 때리는 것을 차단하고, 다시 보이는 순간 1회 즉시 재로드로 따라잡는다.
                    // P2: OEE/TEEP 는 서버 push(OeePrecomputed)가 주 갱신 경로 — 폴링은 60초 안전망으로 감속
                    // (push 유실/미연결 대비). 알람 페이지는 사전계산 대상이 아니라 10초 유지.
                    const pollMs = this.view === 'alarm' ? 10000 : 60000;
                    this._pollTimer = setInterval(() => { if (document.hidden) return; this.load(true); if ((this.view === 'oee' || this.view === 'both') && !this._userBusy) this.refreshActualNonProd(); }, pollMs);
                    document.addEventListener('visibilitychange', () => { if (!document.hidden) this.load(true); });
                    // 알람 페이지 진입 시드(필터 스크롤·포커스·차단 모달) — OEE/TEEP 전용 페이지에서는 무의미하므로 스킵.
                    if (this.view !== 'oee' && this.view !== 'teep') {
                        // 필터 시드로 진입했으면 UserTag 카드로 스크롤(특정 알람 포커스가 있으면 그 행으로 직접 스크롤하므로 생략).
                        if (seeded && !this._focusAt) this.$nextTick(() => document.getElementById('ut-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' }));
                        if (this._focusAt) this.$nextTick(() => this.focusAlertRow());
                        // 차단 상태는 항상 로드(툴바 버튼의 차단 수 배지) — ?blockMgr=1 진입(설정 페이지 링크)이면 모달 자동 열기.
                        if (qp.has('blockMgr')) this.openBlockMgr();
                        else { this.loadBlockState(); this.loadUserTagBlockState(); }
                    }
                    // 더티 가드 등록 — 비생산 시간대 수동 편집(ps.dirty) 중 이탈 방지(OEE 페이지만)
                    if (this.view !== 'alarm') {
                        window.dspDirtyRegister(() => this.ps.editing && this.ps.dirty);
                    }
                },

                destroy() {
                    clearInterval(this._pollTimer);
                    clearTimeout(this._dt);
                    this._conn?.stop();
                    if (_dailyChart) { try { _dailyChart.destroy(); } catch (e) {} _dailyChart = null; }
                },
                toggleTheme() { this.dark = !this.dark; localStorage.setItem('dspilot-theme', this.dark ? 'dark' : 'light'); this.redrawForTheme(); },
                // 테마 전환 직후 차트 색 재계산 — 없으면 다음 폴링(최대 10초)까지 이전 테마 색이 남음
                redrawForTheme() { this.$nextTick(() => { this.drawCharts(); this.drawDailyChart(); }); },

                // ── 탭 전환 (종합효율 현황 ⇆ 이상·알람) + URL ?tab= 동기화 ──
                setTab(t) {
                    if (this.view !== 'both') return; // 물리 분리 페이지는 탭 전환 없음(단일 도메인)
                    if (this.tab === t) return;
                    this.tab = t;
                    this.syncTabUrl();
                    // x-show(display:none) 으로 숨겨졌던 캔버스는 0 크기로 그려지므로, 표시되는 탭의 차트를 다시 렌더.
                    this.$nextTick(() => { if (t === 'anomaly') this.drawCharts(); else this.drawDailyChart(); });
                },
                // 활성 탭을 URL 쿼리(?tab=)에 반영 — 새로고침/공유 시 같은 탭으로 진입. 기본(oee)은 파라미터 생략.
                syncTabUrl() {
                    if (this.view !== 'both') return; // 물리 분리 페이지는 URL ?tab= 미사용
                    const qp = new URLSearchParams(location.search);
                    if (this.tab === 'oee') qp.delete('tab'); else qp.set('tab', this.tab);
                    const qs = qp.toString();
                    history.replaceState(null, '', location.pathname + (qs ? '?' + qs : '') + location.hash);
                },
                applyTabFromUrl() {
                    if (this.view !== 'both') return; // 물리 분리 페이지는 뒤로가기 탭 동기화 없음
                    const t = new URLSearchParams(location.search).get('tab') === 'anomaly' ? 'anomaly' : 'oee';
                    if (t === this.tab) return;
                    this.tab = t;
                    this.$nextTick(() => { if (t === 'anomaly') this.drawCharts(); else this.drawDailyChart(); });
                },

                // 현재 기간 선택을 URL(?period=, custom 이면 +from/to)에 반영 — 새로고침 유지 +
                // shell 나브의 같은 페이지 전체/FLOW 이동이 이 파라미터를 실어 가 기간이 유지된다(init 에서 복원).
                // 기본(오늘)은 파라미터 생략. custom 인데 범위 미입력(적용 전)이면 기간 파라미터를 남기지 않는다.
                syncPeriodUrl() {
                    const qp = new URLSearchParams(location.search);
                    qp.delete('period'); qp.delete('from'); qp.delete('to');
                    if (this.period === 'custom') {
                        if (this.customFrom && this.customTo) { qp.set('period', 'custom'); qp.set('from', this.customFrom); qp.set('to', this.customTo); }
                    } else if (this.period !== 'today') qp.set('period', this.period);
                    const qs = qp.toString();
                    history.replaceState(null, '', location.pathname + (qs ? '?' + qs : '') + location.hash);
                },
                // ── 스코프 헬퍼 — 설비(?flow=)가 시스템(?system=)보다 우선(나브 딥링크 규약과 동일) ──
                scopeQs() {
                    if (this.curFlow) return '&flow=' + encodeURIComponent(this.curFlow);
                    if (this.curSystem) return '&system=' + encodeURIComponent(this.curSystem);
                    return '';
                },
                scopeLabel() {
                    return this.curFlow ? ('설비: ' + this.curFlow)
                        : this.curSystem ? ('시스템: ' + this.curSystem) : '라인 전체';
                },
                // ── fetch 헬퍼 ──
                async apiGet(url) {
                    const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
                    if (!res.ok) throw new Error('HTTP ' + res.status);
                    return await res.json();
                },
                async apiPost(url, body) {
                    const res = await fetch(url, {
                        method: 'POST',
                        headers: { 'Accept': 'application/json', 'Content-Type': 'application/json' },
                        body: body == null ? null : JSON.stringify(body)
                    });
                    if (!res.ok) {
                        let msg = 'HTTP ' + res.status;
                        try { const j = await res.json(); if (j && j.error) msg = j.error; } catch (e) {}
                        throw new Error(msg);
                    }
                    return await res.json();
                },
                async apiPut(url, body) {
                    const res = await fetch(url, {
                        method: 'PUT',
                        headers: { 'Accept': 'application/json', 'Content-Type': 'application/json' },
                        body: body == null ? null : JSON.stringify(body)
                    });
                    if (!res.ok) {
                        let msg = 'HTTP ' + res.status;
                        try { const j = await res.json(); if (j && j.error) msg = j.error; } catch (e) {}
                        throw new Error(msg);
                    }
                    return await res.json();
                },

                // ── 비생산 시간대 (doc/22): 5일 자동감지 ▸ 사용자 설정 시 수동 전환. UI=24시간 연표 에디터 ──
                minToHHMM(m) { const p = x => String(x).padStart(2, '0'); m = Math.max(0, Math.min(1440, Math.round(m || 0))); return p(Math.floor(m / 60)) + ':' + p(m % 60); },
                // type=time 입력용 — 24:00(1440)은 표현 불가라 23:59 로 클램프(라벨/타이틀은 minToHHMM 의 '24:00' 유지)
                minToTimeInput(m) { return this.minToHHMM(Math.min(1439, Math.max(0, Math.round(m || 0)))); },
                hhmmToMin(s) { if (!s) return null; const a = s.split(':'); const h = +a[0], m = +a[1]; return (isNaN(h) || isNaN(m)) ? null : h * 60 + m; },
                // 현재 시각의 분(0~1439) — 연표 'now' 라인. 10초 폴링이 oee 를 갱신할 때 함께 재평가됨.
                nowMin() { const d = new Date(); return d.getHours() * 60 + d.getMinutes(); },
                // 윈도 → 트랙 위 절대 위치(left/width %). end<start(편집 중) 면 width 0 으로 클램프.
                psWinStyle(w) {
                    const s = Math.max(0, Math.min(1440, w.startMinutes || 0));
                    const e = Math.max(0, Math.min(1440, w.endMinutes || 0));
                    return `left:${s / 1440 * 100}%; width:${Math.max(0, (e - s) / 1440 * 100)}%;`;
                },
                async loadPlannedStops() {
                    try {
                        const r = await this.apiGet('/api/oee/planned-stops');
                        this.ps.source = r.source || 'auto';
                        this.ps.ctMultiplier = r.ctMultiplier || 10;
                        this.ps.windows = (r.windows || []).map(w => ({ startMinutes: w.startMinutes, endMinutes: w.endMinutes, label: w.label || '' }));
                        this.ps.selected = -1; this.ps.addMode = false; this.ps.dirty = false; this.ps.editing = false;
                    } catch (e) { this.ps.err = '비생산 시간대를 불러오지 못했습니다: ' + e.message; }
                    // 이번 기간 실제 제외 비생산(자동+지정 합산 — 통합 타임라인의 자동(점선) 소스). 비생산 시간대는
                    // 시스템(전역) 단위 — flow별 페이지에서도 curFlow 필터 없이 항상 시스템 전체로 표시.
                    const seq = ++this._anpSeq; // 진행 중인 refreshActualNonProd 의 stale 응답이 이 결과를 덮지 않도록
                    try {
                        const r = this.rangeForPeriod();
                        const aqs = `from=${encodeURIComponent(r.from)}&to=${encodeURIComponent(r.to)}`;
                        const dto = await this.apiGet('/api/oee/planned-stops/actual?' + aqs);
                        if (seq === this._anpSeq) this.ps.actualNonProd = dto;
                    } catch (e) { if (seq === this._anpSeq) this.ps.actualNonProd = null; }
                },
                // 폴링·기간변경 경량 갱신 — '실제 제외 비생산'(+현재 상태 배지)만 다시 읽는다.
                // 지정 창 편집 상태(ps.windows/selected/addMode)는 건드리지 않아 편집 중 클로버 방지.
                async refreshActualNonProd() {
                    const seq = ++this._anpSeq;
                    try {
                        const r = this.rangeForPeriod();
                        const dto = await this.apiGet(
                            `/api/oee/planned-stops/actual?from=${encodeURIComponent(r.from)}&to=${encodeURIComponent(r.to)}`);
                        if (seq === this._anpSeq) this.ps.actualNonProd = dto; // stale 응답(이후 기간변경/폴링이 이미 시작) 폐기
                    } catch (e) { /* 이전 값 유지 */ }
                },
                // [수동 편집] — 지정 창 편집 모드 진입(자동 판정은 서버에서 계속 켜져 있음).
                // 추가 드래그는 무장하지 않음 — 기존 창 이동/조절/삭제가 기본, 새 창은 [시간 추가]로만.
                psBeginEdit() {
                    this.ps.editing = true; this.ps.selected = -1; this.ps.addMode = false; this.ps.err = ''; this.ps.msg = '';
                },
                // 보기 모드에서 수동 막대 클릭 → 편집 모드 진입 + 그 막대 선택(추가 무장 없음).
                psEditWindow(i) {
                    this.psBeginEdit();
                    this.ps.selected = i;
                },
                // 편집 취소 — 저장 안 한 편집 폐기, 서버 truth 재로드.
                async psCancelEdit() {
                    this.ps.editing = false; this.ps.dirty = false; this.ps.addMode = false; this.ps.selected = -1;
                    this.ps.msg = ''; this.ps.err = '';
                    await this.loadPlannedStops();
                },
                // 자동(점선) 표시용 — 실측 제외 비생산에서 수동 지정 창과 겹친 부분을 차집합(수동 남색이 정체를 대변).
                get psAutoWindows() {
                    const act = (this.ps.actualNonProd && this.ps.actualNonProd.windows) || [];
                    const man = this.ps.windows || [];
                    const res = [];
                    for (const a of act) {
                        let segs = [[a.startMinutes, a.endMinutes]];
                        for (const m of man) {
                            const next = [];
                            for (const [s, e] of segs) {
                                if (m.endMinutes <= s || m.startMinutes >= e) { next.push([s, e]); continue; }
                                if (m.startMinutes > s) next.push([s, Math.min(m.startMinutes, e)]);
                                if (m.endMinutes < e) next.push([Math.max(m.endMinutes, s), e]);
                            }
                            segs = next;
                        }
                        for (const [s, e] of segs) if (e - s > 0) res.push({ startMinutes: s, endMinutes: e });
                    }
                    return res;
                },
                // (구 psBeginManual/psCancelManual/psSetAuto[자동/수동 배타 토글], 생산 요일(xw*) 함수들은 병행 모델로 제거.)
                psSelect(i) { if (this.ps.addMode) return; this.ps.selected = i; this.ps.err = ''; },
                psDeselect() { this.ps.selected = -1; },
                // [시간 추가] 토글 — 무장 시 트랙 드래그로 새 시간대 생성. 다시 누르면 취소.
                psBeginAdd() { this.ps.addMode = true; this.ps.selected = -1; this.ps.err = ''; this.ps.msg = ''; },
                psCancelAdd() { this.ps.addMode = false; },
                // clientX → 분(0~1440, 5분 스냅) — $refs.ptltrack 기준
                _psMinFromEvent(ev) {
                    const el = this.$refs.ptltrack; if (!el) return 0;
                    const rect = el.getBoundingClientRect(); if (rect.width <= 0) return 0;
                    let f = (ev.clientX - rect.left) / rect.width;
                    f = Math.max(0, Math.min(1, f));
                    return Math.round(f * 1440 / 5) * 5; // 5분 단위 스냅
                },
                // 트랙 pointerdown — 추가 모드: 드래그 생성 시작 / 평소: 빈 곳이면 선택 해제(막대는 @click.stop)
                psTrackPointerDown(ev) {
                    if (!this.ps.editing) return;   // 보기 모드 — 통합 타임라인은 읽기 전용
                    if (this.ps.addMode) {
                        const m = this._psMinFromEvent(ev);
                        this.ps.windows = [...this.ps.windows, { startMinutes: m, endMinutes: m, label: '' }];
                        this._psDrag = { mode: 'create', index: this.ps.windows.length - 1, anchor: m };
                        this._psStartDrag();
                        ev.preventDefault();
                    } else if (ev.target === ev.currentTarget) {
                        this.ps.selected = -1;
                    }
                },
                // 막대 본체 pointerdown → 선택 + 이동(전체 시간대 평행이동) 드래그 시작. 거의 안 움직이면 단순 선택(클릭)으로 처리.
                psMoveStart(ev, i) {
                    if (!this.ps.editing) { this.psEditWindow(i); return; }   // 보기 모드 클릭 → 편집 모드로 바로 진입
                    if (this.ps.addMode) return;
                    this.ps.selected = i; this.ps.err = '';
                    const w = this.ps.windows[i];
                    this._psDrag = { mode: 'move', index: i, anchor: this._psMinFromEvent(ev), downX: ev.clientX, dur: w.endMinutes - w.startMinutes, origStart: w.startMinutes, moved: false };
                    this._psStartDrag();
                },
                // 막대 양끝 핸들 pointerdown → 리사이즈 드래그 시작
                psResizeStart(ev, i, side) {
                    if (!this.ps.editing || this.ps.addMode) return;
                    this.ps.selected = i; this.ps.err = '';
                    this._psDrag = { mode: side === 'l' ? 'resize-l' : 'resize-r', index: i, anchor: 0 };
                    this._psStartDrag();
                },
                // 문서 레벨 move/up 바인딩 — 포인터가 트랙을 벗어나도 드래그 유지. 드래그 동안 커서 고정.
                _psStartDrag() {
                    const cur = this._psDrag.mode === 'move' ? 'grabbing' : (this._psDrag.mode === 'create' ? 'crosshair' : 'ew-resize');
                    document.body.style.cursor = cur;
                    this._psMoveBound = (e) => this._psDragMove(e);
                    this._psUpBound = (e) => this._psDragEnd(e);
                    document.addEventListener('pointermove', this._psMoveBound);
                    document.addEventListener('pointerup', this._psUpBound);
                    document.addEventListener('pointercancel', this._psUpBound);
                },
                _psDragMove(ev) {
                    const d = this._psDrag; if (!d) return;
                    const w = this.ps.windows[d.index]; if (!w) return;
                    const m = this._psMinFromEvent(ev);
                    if (d.mode === 'create') { w.startMinutes = Math.min(d.anchor, m); w.endMinutes = Math.max(d.anchor, m); }
                    else if (d.mode === 'resize-l') { w.startMinutes = Math.max(0, Math.min(m, w.endMinutes - 5)); }
                    else if (d.mode === 'resize-r') { w.endMinutes = Math.min(1440, Math.max(m, w.startMinutes + 5)); }
                    else if (d.mode === 'move') {
                        let ns = d.origStart + (m - d.anchor);           // 길이 유지한 채 평행이동
                        ns = Math.max(0, Math.min(1440 - d.dur, ns));    // 0~24시 경계 클램프
                        w.startMinutes = ns; w.endMinutes = ns + d.dur;
                        if (Math.abs(ev.clientX - d.downX) >= 4) d.moved = true; // 픽셀 기준 드래그 판정(클릭과 구분)
                    }
                    ev.preventDefault();
                },
                _psDragEnd() {
                    document.removeEventListener('pointermove', this._psMoveBound);
                    document.removeEventListener('pointerup', this._psUpBound);
                    document.removeEventListener('pointercancel', this._psUpBound);
                    document.body.style.cursor = '';
                    const d = this._psDrag; this._psDrag = null;
                    if (!d) return;
                    const w = this.ps.windows[d.index];
                    // 거의 안 움직인 생성(<10분 ≈ 클릭/손떨림)은 폐기 — 추가 모드는 유지(다시 드래그 가능)
                    if (d.mode === 'create' && (!w || (w.endMinutes - w.startMinutes) < 10)) {
                        if (w) this.ps.windows = this.ps.windows.filter((_, i) => i !== d.index);
                        return;
                    }
                    // 이동인데 거의 안 움직였으면 클릭으로 간주 → 원위치 복원(선택 상태는 유지)
                    if (d.mode === 'move' && !d.moved) {
                        if (w) { w.startMinutes = d.origStart; w.endMinutes = d.origStart + d.dur; }
                        return;
                    }
                    const ref = w;                                       // 정렬 후 동일 객체로 선택 복원
                    this.ps.windows = this.ps.windows.slice().sort((a, b) => a.startMinutes - b.startMinutes);
                    this.ps.selected = this.ps.windows.indexOf(ref);
                    if (d.mode === 'create') this.ps.addMode = false;    // 생성 완료 → 추가 모드 종료
                    this.ps.dirty = true;
                },
                // 선택된 윈도의 시작/끝 시각 인라인 수정(type=time → 분). 라이브 재배치(재정렬은 적용 시).
                psSetTime(which, val) {
                    const m = this.hhmmToMin(val);
                    if (m == null || this.ps.selected < 0) return;
                    const w = this.ps.windows[this.ps.selected];
                    if (!w) return;
                    if (which === 'start') w.startMinutes = m; else w.endMinutes = m;
                    this.ps.err = '';
                    this.ps.dirty = true;
                },
                psRemove(i) {
                    this.ps.windows = this.ps.windows.filter((_, idx) => idx !== i);
                    this.ps.dirty = true;
                    if (this.ps.selected === i) this.ps.selected = -1;
                    else if (this.ps.selected > i) this.ps.selected -= 1;
                },
                async psApply() {
                    // 검증: 모든 윈도 0~24시 범위 + 끝>시작(드래그 생성/리사이즈 결과를 적용 직전 재확인)
                    for (const w of this.ps.windows) {
                        if (w.startMinutes == null || w.endMinutes == null || w.endMinutes <= w.startMinutes || w.startMinutes < 0 || w.endMinutes > 1440) {
                            this.ps.err = '시간대가 올바르지 않습니다 (끝 > 시작, 00:00~24:00). 자정을 넘기는 정지는 두 칸으로 분리하세요.';
                            return;
                        }
                    }
                    this.ps.busy = true; this.ps.msg = ''; this.ps.err = '';
                    try {
                        const windows = this.ps.windows.slice()
                            .sort((a, b) => a.startMinutes - b.startMinutes)
                            .map(w => ({ startMinutes: w.startMinutes, endMinutes: w.endMinutes, label: (w.label || '').trim() || null }));
                        await this.apiPut('/api/oee/planned-stops', { windows });
                        const okMsg = windows.length
                            ? `수동 지정 ${windows.length}개 적용 — 매일 이 시간대는 무조건 비생산 (자동 판정은 계속 동작)`
                            : '수동 지정 없음으로 저장 — 당일 자동 판정만 적용';
                        await this.loadPlannedStops();   // editing/dirty 리셋 + 서버 truth 재로드
                        this.ps.msg = okMsg;
                        await this.loadOee();
                    } catch (e) { this.ps.err = '적용 실패: ' + e.message; }
                    finally { this.ps.busy = false; setTimeout(() => { this.ps.msg = ''; }, 5000); }
                },

                // ── 정지·비생산 판정 기준 (GET/PUT /api/oee/ct-multipliers) — 슬라이더 + flow 임계 환산 + 저장 전 재분류 미리보기 ──
                cmDirty() {
                    return this.cm.idle !== this.cm.origIdle || this.cm.nonProd !== this.cm.origNonProd
                        || this.cm.fault !== this.cm.origFault;
                },
                async loadCtMultipliers() {
                    try {
                        const r = await this.apiGet('/api/oee/ct-multipliers');
                        this.cm.idle = this.cm.origIdle = Math.round((r.idleCtMultiplier || 2.5) * 10) / 10;
                        this.cm.nonProd = this.cm.origNonProd = Math.round((r.nonProdCtMultiplier || 15) * 10) / 10;
                        this.cm.fault = this.cm.origFault = Math.round((r.faultMtMultiplier || 2.5) * 10) / 10;
                        this.cm.flows = r.flows || [];
                        this.cm.preview = null;
                    } catch (e) { this.cm.err = '판정 기준을 불러오지 못했습니다: ' + e.message; }
                },
                // 배수 × flow 평균 CT → 임계 환산 표시 (10s 미만은 소수 1자리, 90s↑ 분, 90분↑ 시간)
                cmSecs(avgMs, mult) {
                    const s = ((avgMs || 0) * mult) / 1000;
                    if (s >= 5400) return (s / 3600).toFixed(1) + '시간';
                    if (s >= 90) return (s / 60).toFixed(1) + '분';
                    return (s >= 10 ? String(Math.round(s)) : s.toFixed(1)) + '초';
                },
                cmFmtH(ms) { const h = (ms || 0) / 3600000; return (h >= 10 ? Math.round(h) : h.toFixed(1)) + '시간'; },
                cmFmtPct(v) { return (v === null || v === undefined) ? '—' : (v * 100).toFixed(1) + '%'; },
                // 슬라이더 입력 — 역전 차단(비가동 + 0.5 ≤ 비생산 유지) 후 미리보기 디바운스
                cmSetIdle(v) {
                    const x = Math.round(parseFloat(v) * 10) / 10;
                    if (!isFinite(x)) return;
                    this.cm.idle = Math.max(1, Math.min(x, this.cm.nonProd - 0.5));
                    this.cmQueuePreview();
                },
                cmSetNonProd(v) {
                    const x = Math.round(parseFloat(v) * 10) / 10;
                    if (!isFinite(x)) return;
                    this.cm.nonProd = Math.min(100, Math.max(x, this.cm.idle + 0.5));
                    this.cmQueuePreview();
                },
                // 고장(MT축) 배수 — CT축 두 배수와 축이 달라 역전 제약이 없다(비교 자체가 무의미).
                cmSetFault(v) {
                    const x = Math.round(parseFloat(v) * 10) / 10;
                    if (!isFinite(x)) return;
                    this.cm.fault = Math.min(10, Math.max(1, x));
                    this.cmQueuePreview();
                },
                cmQueuePreview() {
                    this.cm.msg = ''; this.cm.err = '';
                    if (!this.cmDirty()) { this.cm.preview = null; return; }
                    clearTimeout(this._cmPrevTimer);
                    this._cmPrevTimer = setTimeout(() => this.cmPreview(), 600);
                },
                async cmPreview() {
                    if (!this.cmDirty()) return;
                    const seq = ++this._cmSeq;
                    this.cm.previewBusy = true;
                    try {
                        const r = this.rangeForPeriod();
                        const qs = `idle=${this.cm.idle}&nonProd=${this.cm.nonProd}&fault=${this.cm.fault}&from=${encodeURIComponent(r.from)}&to=${encodeURIComponent(r.to)}`;
                        const dto = await this.apiGet('/api/oee/ct-multipliers/preview?' + qs);
                        if (seq === this._cmSeq) this.cm.preview = dto;
                    } catch (e) { if (seq === this._cmSeq) { this.cm.preview = null; this.cm.err = '미리보기 실패: ' + e.message; } }
                    finally { if (seq === this._cmSeq) this.cm.previewBusy = false; }
                },
                cmReset() { this.cm.idle = 2.5; this.cm.nonProd = 15; this.cm.fault = 2.5; this.cmQueuePreview(); },
                async cmApply() {
                    if (this.cm.idle >= this.cm.nonProd) { this.cm.err = '비가동 배수는 비생산 배수보다 작아야 합니다.'; return; }
                    this.cm.busy = true; this.cm.msg = ''; this.cm.err = '';
                    try {
                        const r = await this.apiPut('/api/oee/ct-multipliers',
                            { idleCtMultiplier: this.cm.idle, nonProdCtMultiplier: this.cm.nonProd, faultMtMultiplier: this.cm.fault });
                        this.cm.idle = this.cm.origIdle = Math.round(r.idleCtMultiplier * 10) / 10;
                        this.cm.nonProd = this.cm.origNonProd = Math.round(r.nonProdCtMultiplier * 10) / 10;
                        this.cm.fault = this.cm.origFault = Math.round((r.faultMtMultiplier || 2.5) * 10) / 10;
                        this.cm.flows = r.flows || [];
                        this.cm.preview = null;
                        this.cm.msg = `판정 기준 적용 — 비가동 ${this.cm.idle}×CT / 비생산 ${this.cm.nonProd}×CT / 고장 ${this.cm.fault}×MT (조회 시 재계산이라 과거 기간에도 즉시 반영)`;
                        // KPI + 비생산 카드의 자동 칩(배수 표기) + 실측 타임라인 갱신
                        await Promise.all([this.loadOee(), this.loadPlannedStops()]);
                    } catch (e) { this.cm.err = '적용 실패: ' + e.message; }
                    finally {
                        this.cm.busy = false;
                        clearTimeout(this._cmMultMsgTimer);
                        this._cmMultMsgTimer = setTimeout(() => { this.cm.msg = ''; }, 6000);
                    }
                },
                // 분류 밴드 세그먼트 폭(%) — 0 ~ 비생산×1.15 선형 스케일(정상 | 비가동 | 비생산)
                cmBandW(kind) {
                    const max = this.cm.nonProd * 1.15;
                    if (kind === 'normal') return (this.cm.idle / max * 100) + '%';
                    if (kind === 'idle') return ((this.cm.nonProd - this.cm.idle) / max * 100) + '%';
                    return (100 - this.cm.nonProd / max * 100) + '%';
                },

                // 현재 기간 → OEE 엔드포인트용 from/to (로컬 ISO, UserTagsController.ResolvePeriod 와 동일 의미)
                rangeForPeriod() {
                    if (this.period === 'custom' && this.customFrom && this.customTo)
                        return { from: this.customFrom + ':00', to: this.customTo + ':00' };
                    const now = new Date();
                    const startOfDay = (d) => { const x = new Date(d); x.setHours(0, 0, 0, 0); return x; };
                    let from;
                    switch (this.period) {
                        case '7d': from = startOfDay(now); from.setDate(from.getDate() - 6); break;
                        case '30d': from = startOfDay(now); from.setDate(from.getDate() - 29); break;
                        case '60d': from = startOfDay(now); from.setDate(from.getDate() - 59); break;
                        default: from = startOfDay(now); break;
                    }
                    const iso = (d) => {
                        const p = (x) => String(x).padStart(2, '0');
                        return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
                    };
                    return { from: iso(from), to: iso(now) };
                },

                // 이상발생 스냅샷 쿼리 (기간 + 페이지 + 검색/레벨/System 필터)
                utQs() {
                    const p = new URLSearchParams({ period: this.period, page: this.utPage, pageSize: this.utPageSize, sort: this.utSort, sortDir: this.utSortDir });
                    if (this.period === 'custom' && this.customFrom && this.customTo) {
                        p.set('from', this.customFrom + ':00');
                        p.set('to', this.customTo + ':00');
                    }
                    if (this.utSearch.trim()) p.set('search', this.utSearch.trim());
                    // 설비(Flow)별 보기(?flow=)면 자동감지(Abnormal)만 남으므로 구분 필터는 보내지 않는다
                    // (서버에서 flow+usertag 는 0건이 되는 모순 방지 — flow 가 곧 abnormal-only 를 함의).
                    if (this.curFlow) p.set('flow', this.curFlow);
                    else if (this.utCategory) p.set('category', this.utCategory);
                    // 시스템 필터 = 알람 행 systemName 등식(서버). utSystem(대시보드 이상코드 피드 시드)이 우선,
                    // 없으면 나브 시스템 스코프(?system=, curSystem). 설비(?flow=)가 있으면 curSystem 은 init 에서
                    // 이미 비어 있다(설비 우선). 자동감지 행도 flow→System 해석으로 systemName 이 채워져 둘 다 걸린다.
                    const sysFilter = this.utSystem || this.curSystem;
                    if (sysFilter) p.set('system', sysFilter);
                    return p.toString();
                },

                async load(silent) {
                    // 사용자 로드 진행 중이면 무음(폴링/SignalR) 재로드는 건너뜀 — seq 선점 가로채기 방지(_userBusy 주석 참조).
                    if (silent && this._userBusy) return;
                    if (!silent) { this.loading = true; this._userBusy++; }
                    try {
                        // OEE 데이터는 스냅샷과 독립적이므로 여기서 즉시(동기) dispatch 해 스냅샷 fetch 뒤로 미루지 않는다.
                        // 뒤로 미루면(특히 스냅샷이 느릴 때) 이미 dispatch 된 옛 기간의 loadOee 가 최신 _oeeSeq 를 차지한 채
                        // 먼저 도착해, 방금 선택한 기간과 화면이 어긋나거나(범위 불일치) 새 기간이 스냅샷 완료 후에야
                        // 뒤늦게 적용되던 경합이 생긴다. 동기 호출하면 rangeForPeriod()·++_oeeSeq 가 방금 바뀐 period 로
                        // 즉시 실행돼 loadOee 의 dispatch 순서가 기간 선택 순서와 정확히 일치한다(최신 선택이 항상 승리).
                        const oeePromise = this.loadOee(silent);
                        // 생산효율 페이지 — 알람(UserTag) UI 가 없어 스냅샷 조회 생략(OEE 요약 + TEEP + 매트릭스만).
                        if (this.view === 'teep') {
                            this.loading = false;
                            await oeePromise;
                            return;
                        }
                        const seq = ++this._utSeq;
                        // 이상발생(UserTag) — 구 관리페이지 흡수(필터/페이지/차트)
                        try {
                            const snap = await this.apiGet('/api/user-tags/snapshot?' + this.utQs());
                            if (seq === this._utSeq) { // stale 응답(이후 요청이 이미 시작됨)은 폐기 — 페이지/기간 덮어쓰기 경합 방지
                                this.ut = snap;
                                this.utPage = snap.page; // 서버가 page 클램프할 수 있음
                                // ActionOver 반복 디바이스 집계 — 같은 tagAddress 에서 2건 이상이면 힌트 표시
                                const _aoCounts = {};
                                for (const a of (snap.alerts || []))
                                    if (a.matchOp === 'AbnormalDetect' && a.matchValue === 'ActionOver' && a.tagAddress)
                                        _aoCounts[a.tagAddress] = (_aoCounts[a.tagAddress] || 0) + 1;
                                this.actionOverHint = Object.entries(_aoCounts).filter(([,n]) => n >= 2).map(([k]) => k);
                                this.error = null;
                                this.$nextTick(() => this.drawCharts());
                            }
                        } catch (e) {
                            if (seq === this._utSeq) this.error = '이상발생 데이터를 불러오지 못했습니다: ' + e.message;
                        } finally { this.loading = false; }

                        // 신규 OEE 데이터 — 위에서 이미 dispatch 됨(스냅샷과 병렬). 완료만 대기.
                        await oeePromise;
                    } finally { if (!silent) this._userBusy--; }
                },

                // 피드에서 at 으로 진입했을 때 해당 알람 행(data-at=occurredAtLocal 초단위)을 찾아 스크롤 + 잠깐 하이라이트.
                // x-for 렌더가 끝난 직후 타이밍을 짧게 폴링(최대 ~2초)하고, 못 찾으면 목록 카드로라도 스크롤한다.
                focusAlertRow() {
                    const at = this._focusAt;
                    if (!at) return;
                    let tries = 0;
                    const tick = () => {
                        let hit = null;
                        document.querySelectorAll('#ut-section tr[data-at]').forEach(r => {
                            if (!hit && r.getAttribute('data-at') === at) hit = r;
                        });
                        if (hit) {
                            hit.scrollIntoView({ behavior: 'smooth', block: 'center' });
                            hit.classList.add('up-row-flash');
                            setTimeout(() => hit.classList.remove('up-row-flash'), 3200);
                            this._focusAt = null;
                        } else if (++tries < 12) {
                            setTimeout(tick, 180);
                        } else {
                            // 못 찾음(다른 페이지·필터 등) — 목록 카드로 폴백 스크롤.
                            document.getElementById('ut-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
                            this._focusAt = null;
                        }
                    };
                    tick();
                },

                drawCharts() {
                    if (this.view === 'oee' || this.view === 'teep') return; // 이상·알람 차트는 OEE/TEEP 전용 페이지에 캔버스 없음
                    if (!this._charts || !this.ut) return;
                    try {
                        // 설비별 보기는 자동감지만(USERTAG 는 Flow 에 속하지 않음) → 트렌드도 자동감지 단일 시리즈.
                        // 요약 카드로 구분을 골랐을 때도 같은 규칙 — 0-채움 버킷만 남는 시리즈를 범례에서 지운다.
                        const trendCats = (this.curFlow || this.utCategory === 'abnormal') ? ['ABNORMAL']
                            : (this.utCategory === 'usertag' ? ['USERTAG'] : ['ABNORMAL', 'USERTAG']);
                        this._charts.renderTrendChart('ut-trend-chart', this.ut.buckets || [], this.ut.granularity, trendCats);
                        // 태그별 Top 10 은 경로(FLOW / WORK / CALL)별 집계로 고정.
                        this._charts.renderTopChart('ut-top-chart', (this.ut.topRowsByPath || []).slice(0, 10));
                    } catch (e) { console.warn('chart draw failed', e); }
                },

                // ── 이상발생 필터/페이지 (구 관리페이지) ──
                applyUtFilters() { this.utPage = 0; this.load(); },
                // 검색어 입력 — 키 입력마다 서버 재조회를 피하려 300ms 디바운스 후 첫 페이지부터 재조회.
                onUtSearchInput() {
                    if (this._utSearchTimer) clearTimeout(this._utSearchTimer);
                    this._utSearchTimer = setTimeout(() => { this.utPage = 0; this.load(); }, 300);
                },
                clearUtSearch() { this.utSearch = ''; this.utPage = 0; this.load(); },
                // 페이지 크기 변경 — 첫 페이지로 리셋 후 재조회.
                setUtPageSize(n) { this.utPageSize = +n || 10; this.utPage = 0; this.load(); },
                // 정렬 헤더 클릭 — 같은 컬럼이면 방향 토글, 다른 컬럼이면 내림차순 시작. 서버 정렬이라 재조회.
                setUtSort(col) {
                    if (this.utSort === col) this.utSortDir = this.utSortDir === 'asc' ? 'desc' : 'asc';
                    else { this.utSort = col; this.utSortDir = 'desc'; }
                    this.utPage = 0; this.load();
                },
                utSortIcon(col) {
                    if (this.utSort !== col) return 'unfold_more';
                    return this.utSortDir === 'asc' ? 'arrow_upward' : 'arrow_downward';
                },
                prevUtPage() { if (this.utPage === 0) return; this.utPage--; this.load(); },
                nextUtPage() { if (!this.ut || this.utPage + 1 >= this.ut.maxPage) return; this.utPage++; this.load(); },
                firstUtPage() { if (this.utPage === 0) return; this.utPage = 0; this.load(); },
                lastUtPage() { if (!this.ut || this.utPage + 1 >= this.ut.maxPage) return; this.utPage = this.ut.maxPage - 1; this.load(); },
                // Excel(.xlsx) 다운로드 — 서버(/api/user-tags/excel)가 현재 필터로 조회해 xlsx 를 반환(Content-Disposition attachment).
                // utQs() 가 기간·검색·System·구분·설비(flow) 필터를 그대로 담는다.
                exportUtExcel() {
                    if (!this.ut || !this.ut.alerts || this.ut.alerts.length === 0) return;
                    const url = '/api/user-tags/excel?' + this.utQs() + '&limit=100000';
                    const a = document.createElement('a');
                    a.href = url;
                    document.body.appendChild(a);
                    a.click();
                    document.body.removeChild(a);
                },
                // ── 생산효율(TEEP) 로드 (/api/oee/teep) — 생산효율 페이지(/uptime-teep) 전용 ──
                async loadTeep() {
                    if (this.view !== 'teep') return;
                    const r = this.rangeForPeriod();
                    const qs = `from=${encodeURIComponent(r.from)}&to=${encodeURIComponent(r.to)}` + this.scopeQs();
                    const seq = ++this._teepSeq;
                    try {
                        const dto = await this.apiGet('/api/oee/teep?' + qs);
                        if (seq !== this._teepSeq) return; // stale 응답 폐기
                        this.teep = dto;
                        this.teepError = null;
                    } catch (e) {
                        if (seq !== this._teepSeq) return;
                        this.teepError = 'TEEP 데이터를 불러오지 못했습니다: ' + e.message;
                    }
                },
                // 시간 분해 막대 — 해당 조각이 캘린더에서 차지하는 % (0~100, 1자리).
                teepPct(field) {
                    if (!this.teep || !this.teep.calendarMs) return '0.0';
                    return Math.max(0, Math.min(100, this.teep[field] / this.teep.calendarMs * 100)).toFixed(1);
                },
                // ── 날짜별 비생산 패턴 (/api/oee/planned-stops/actual · days) — 생산효율 페이지 전용 ──
                // ps.actualNonProd(설비효율 설정 타임라인, 자동 모드 전용)와 별도 상태 — 여기는 자동/수동 설정과
                // 무관하게 그 범위의 "실측" 비생산 패턴을 조회한다(detected=true 로 10×CT 감지 강제 — 수동 지정이
                // 걸려 있어도 실제 패턴이 드러나게. 수동 시간대도 union 으로 함께 표시).
                async loadTeepNonProd() {
                    if (this.view !== 'teep') return;
                    const r = this.rangeForPeriod();
                    const seq = ++this._teepNpSeq;
                    try {
                        const dto = await this.apiGet(
                            `/api/oee/planned-stops/actual?detected=true&from=${encodeURIComponent(r.from)}&to=${encodeURIComponent(r.to)}`);
                        if (seq !== this._teepNpSeq) return; // stale 응답 폐기
                        this.teepNonProd = dto;
                        this.teepNonProdError = null;
                    } catch (e) {
                        if (seq !== this._teepNpSeq) return;
                        this.teepNonProdError = '날짜별 비생산 패턴을 불러오지 못했습니다: ' + e.message;
                    }
                },
                // 행 배열 — 최근이 위(오늘이 첫 행, 스크롤 없이 보이게). 서버 days 는 날짜 오름차순.
                teepNpDays() { return ((this.teepNonProd && this.teepNonProd.days) || []).slice().reverse(); },
                teepNpDense() { return this.teepNpDays().length > 21; }, // 30·60일 — 행 높이 압축(히트맵 톤)
                // 'YYYY-MM-DDT00:00:00'(로컬 자정) → 라벨/요일/오늘 여부. 파싱은 date-only 슬라이스(타임존 안전).
                teepNpDayInfo(d) {
                    const s = String(d.date).slice(0, 10);
                    const dt = new Date(s + 'T00:00:00');
                    const dow = dt.getDay();
                    const today = new Date(); today.setHours(0, 0, 0, 0);
                    return { label: s.slice(5).replace('-', '/') + ' (' + '일월화수목금토'[dow] + ')', dow, isToday: dt.getTime() === today.getTime() };
                },
                teepNpDayMs(d) { return (d.windows || []).reduce((a, w) => a + Math.max(0, w.endMinutes - w.startMinutes), 0) * 60000; },
                teepNpWinTitle(d, w, kind) {
                    return this.teepNpDayInfo(d).label + ' ' + this.minToHHMM(w.startMinutes) + '–' + this.minToHHMM(w.endMinutes)
                        + ' ' + kind + ' (' + this.durShort(Math.max(0, w.endMinutes - w.startMinutes) * 60000) + ')';
                },
                teepNpSub() {
                    const n = this.teepNpDays().length;
                    if (!n) return '';
                    const base = n === 1 ? '1일 × 24시간' : `${n}일 × 24시간 · 최근이 위`;
                    return base + (this.teepNonProd && this.teepNonProd.daysClipped ? ' · 최근 92일만 표시' : '');
                },

                // ── 생산효율 매트릭스 (P6 L0, /api/oee/teep/matrix) ──────────────────
                // flow×버킷 재집계라 KPI 보다 비싸다 — 무음(10초 폴링) 갱신은 60초에 한 번만(기간/설비 변경·수동 적용은 즉시).
                async loadTeepMatrix(silent) {
                    if (this.view !== 'teep') return;
                    if (silent && Date.now() - this._teepMxAt < 60000) return;
                    const r = this.rangeForPeriod();
                    const qs = `from=${encodeURIComponent(r.from)}&to=${encodeURIComponent(r.to)}` + this.scopeQs();
                    const seq = ++this._teepMxSeq;
                    try {
                        const dto = await this.apiGet('/api/oee/teep/matrix?' + qs);
                        if (seq !== this._teepMxSeq) return; // stale 응답 폐기
                        this.teepMatrix = dto;
                        this.teepMatrixError = null;
                        this._teepMxAt = Date.now();
                        this.$nextTick(() => this.renderTeepMatrix());
                    } catch (e) {
                        if (seq !== this._teepMxSeq) return;
                        this.teepMatrixError = '매트릭스 데이터를 불러오지 못했습니다: ' + e.message;
                    }
                },
                // 표시 모드 — 라인 전체(설비 ≥2)=3D 아이소 'iso', 설비 선택(또는 설비 1개 라인)=2D 막대 'bars', 데이터 없음=null.
                teepMatrixMode() {
                    const m = this.teepMatrix;
                    if (!m || !m.flows || m.flows.length === 0 || !m.buckets || m.buckets.length === 0) return null;
                    return (this.curFlow || m.flows.length === 1) ? 'bars' : 'iso';
                },
                teepMatrixSub() {
                    const m = this.teepMatrix, mode = this.teepMatrixMode();
                    if (!mode) return '';
                    const g = m.granularity === 'hour' ? '시간별' : '일별';
                    if (mode === 'iso')
                        return `설비 ${m.flows.length}개 × ${g} ${m.buckets.length}구간 · 클릭=설비 상세`;
                    const f = this.curFlow || m.flows[0].flowName;
                    return `${f} · ${g} ${m.buckets.length}구간 · 클릭=설비효율(A·P·Q) 상세`;
                },
                // 계획 기준선(골드 점선)이 실제로 그려지는가 — 렌더러 showPlanned 와 동일 조건(범례/서브헤더 게이트 SSOT).
                teepShowPlanned() {
                    const m = this.teepMatrix;
                    return !!m && m.plannedFraction != null && m.plannedSource !== 'calendar'
                        && m.plannedFraction > 0.02 && m.plannedFraction < 0.995;
                },
                // 3D 시점 스텝 회전(±90°) — 상태만 바꾸고 전체 재렌더(임퍼러티브 SVG, 체감 즉시).
                teepIsoRotate(dir) { this.teepIsoRot = (this.teepIsoRot + dir + 4) % 4; this.renderTeepMatrix(); },
                // 룰 기반 한 줄 인사이트 — 라인 뷰=기간 최저/최고 설비, 설비 뷰=최저 버킷과 그 원인 분해.
                teepMatrixInsight() {
                    const m = this.teepMatrix;
                    if (!this.teepMatrixMode()) return '';
                    if (this.teepMatrixMode() === 'iso') {
                        const per = m.flows.map(f => {
                            let run = 0, cal = 0, down = 0;
                            for (const c of f.cells) { run += c.runningMs; cal += c.calendarMs; down += c.downMs; }
                            return { name: f.flowName, teep: cal > 0 ? run / cal : null, down };
                        }).filter(x => x.teep != null);
                        if (per.length < 2) return '';
                        const worst = per.reduce((a, b) => (b.teep < a.teep ? b : a));
                        const best = per.reduce((a, b) => (b.teep > a.teep ? b : a));
                        return `기간 생산효율 최저 설비: ${worst.name} ${this.pct(worst.teep)} (정지 ${this.durShort(worst.down)}) · 최고: ${best.name} ${this.pct(best.teep)}`;
                    }
                    const f = this.curFlow ? m.flows.find(x => x.flowName === this.curFlow) : m.flows[0];
                    if (!f) return '';
                    let wi = -1;
                    f.cells.forEach((c, i) => {
                        if (c.teep == null || c.runningMs + c.downMs <= 0) return; // 무활동 버킷(야간 등)은 최저 후보 제외
                        if (wi < 0 || c.teep < f.cells[wi].teep) wi = i;
                    });
                    if (wi < 0) return '';
                    const c = f.cells[wi];
                    return `최저 구간: ${this._tmShortLabel(m.buckets[wi].label, m.granularity)} — TEEP ${this.pct(c.teep)} · 가동 ${this.durShort(c.runningMs)} · 정지 ${this.durShort(c.downMs)} · 비생산 ${this.durShort(c.nonProdMs)}`;
                },
                _tmShortLabel(lbl, gran) {
                    return gran === 'hour' ? lbl.slice(11, 16) : lbl.slice(5); // "yyyy-MM-dd HH:00"→"HH:mm" / "yyyy-MM-dd"→"MM-DD" (ISO 숫자형 통일)
                },
                // 큐브 클릭 → 이 페이지의 설비(2D) 뷰. location.search 가 기간(?period 등)을 이미 담고 있어(syncPeriodUrl) 그대로 유지.
                _tmDrillFlow(flowName) {
                    const qp = new URLSearchParams(location.search);
                    qp.set('flow', flowName);
                    location.href = '/uptime-teep?' + qp.toString();
                },
                // 2D 막대 클릭 → 설비효율(OEE) 페이지 L1 드릴(A·P·Q 분해). 기간/설비 유지.
                _tmDrillOee(flowName) {
                    const qp = new URLSearchParams(location.search);
                    qp.set('flow', flowName);
                    location.href = '/uptime-oee?' + qp.toString();
                },
                // 호스트 div 는 x-show 로 상시 존재(테어다운 없음) — 드물게 첫 마운트 직후 못 찾으면 1프레임 재시도
                // (중첩 x-if 캔버스 레이스와 같은 계열 방어).
                renderTeepMatrix(retried) {
                    const host = document.getElementById('teep-matrix-host');
                    if (!host) { if (!retried) requestAnimationFrame(() => this.renderTeepMatrix(true)); return; }
                    host.innerHTML = '';
                    const mode = this.teepMatrixMode();
                    if (!mode) return;
                    if (mode === 'iso') this._renderTeepIso(host, this.teepMatrix);
                    else this._renderTeepBars(host, this.teepMatrix);
                },
                // 3D 아이소(설비 × 시간 × 가동) — P6 목업 renderL0 이식 + 4시점 스텝 회전 + 행 하이라이트.
                // 높이=가동/캘린더=TEEP, 색=가동(초록)/정지(빨간 캡) 2색. 회전(teepIsoRot)은 데이터 (버킷,설비)를
                // 프레임 (x,z)로 재매핑만 한다 — 투영·면 구성(앞/우/윗면)·페인터 정렬은 불변이라 면 가시성 계산이 필요 없고,
                // 앞쪽 벽에 가린 셀은 시점을 90° 돌려서 본다(데이터 의존 가림의 회피 수단).
                _renderTeepIso(host, m) {
                    const flows = m.flows, B = m.buckets.length, L = flows.length;
                    const r = ((this.teepIsoRot % 4) + 4) % 4;
                    const X = r % 2 === 0 ? B : L, Z = r % 2 === 0 ? L : B; // 회전 후 프레임 치수
                    const map = (d, l) => r === 0 ? [d, l] : r === 1 ? [l, B - 1 - d] : r === 2 ? [B - 1 - d, L - 1 - l] : [L - 1 - l, d];
                    const CELL = Math.max(9, Math.min(24, Math.floor(720 / (B + L + 4)))); // 60일(60버킷)까지 자동 축소
                    const COS30 = 0.866, SIN30 = 0.5;
                    const H_UNITS = 5; // 가동 100% = 5셀 높이
                    const PAD_L = 92, PAD_R = 34, PAD_T = 14, PAD_B = 34; // 좌측=설비 라벨 gutter, 하단=회전 시 설비 라벨
                    const OX = PAD_L + Z * CELL * COS30;
                    const OY = PAD_T + H_UNITS * CELL;
                    const W = Math.ceil(OX + X * CELL * COS30 + PAD_R);
                    const H = Math.ceil(OY + (X + Z) * CELL * SIN30 + PAD_B);
                    const iso = (x, y, z) => [OX + (x - z) * CELL * COS30, OY + (x + z) * CELL * SIN30 - y * CELL];
                    const pts = (arr) => arr.map(p => p[0].toFixed(1) + ',' + p[1].toFixed(1)).join(' ');
                    const svg = _tmEl('svg', { viewBox: `0 0 ${W} ${H}`, class: 'up-tm-svg', role: 'img' });
                    host.appendChild(svg);

                    // 바닥 그리드(체커보드) — 크롬 색은 CSS 토큰(라이트/다크 자동 대응).
                    for (let z = 0; z < Z; z++)
                        for (let x = 0; x < X; x++)
                            _tmEl('polygon', {
                                points: pts([iso(x, 0, z), iso(x + 1, 0, z), iso(x + 1, 0, z + 1), iso(x, 0, z + 1)]),
                                class: (x + z) % 2 === 0 ? 'up-tm-floor-a' : 'up-tm-floor-b'
                            }, svg);

                    // 계획 기준선 평면(P6 목업 "계획 Nh/day" 복원) — 캘린더 대비 "가동하기로 한" 높이에 점선 평면.
                    // 큐브 총높이(가동+정지)가 이 평면에 닿으면 계획시간을 다 쓴 것, 아래면 초과 유휴(Idle). calendar 폴백
                    // (계획 미설정=비율 1.0)이면 기준선이 천장과 겹쳐 무의미 → 생략. 채움은 큐브 아래, 점선·라벨은 위(항상 노출).
                    const pf = m.plannedFraction;
                    const showPlanned = pf != null && m.plannedSource !== 'calendar' && pf > 0.02 && pf < 0.995;
                    const hPlan = showPlanned ? pf * H_UNITS : 0;
                    const planCorners = showPlanned ? [iso(0, hPlan, 0), iso(X, hPlan, 0), iso(X, hPlan, Z), iso(0, hPlan, Z)] : null;
                    if (showPlanned)
                        _tmEl('polygon', { points: pts(planCorners), class: 'up-tm-plan-fill' }, svg);

                    // 큐브 — 프레임 좌표 기준 뒤(x+z 작음)→앞 페인터 정렬. 색=가동 단일색(초록), 정지만 빨간 캡.
                    const order = [];
                    for (let l = 0; l < L; l++) for (let d = 0; d < B; d++) { const [x, z] = map(d, l); order.push([l, d, x, z]); }
                    order.sort((a, b) => (a[2] + a[3]) - (b[2] + b[3]));
                    const cubes = [];              // 행(설비) 호버 하이라이트용 — enter 시 같은 flow 만 남기고 dim
                    const flowLabelEls = {};       // flowName → 축 라벨 <text>(호버 시 함께 강조). 축 라벨부에서 채움.
                    for (const [l, d, x, z] of order) {
                        const c = flows[l].cells[d];
                        if (!c || c.calendarMs <= 0) continue;
                        const hRun = Math.min(H_UNITS, c.runningMs / c.calendarMs * H_UNITS);
                        const hTot = Math.min(H_UNITS, (c.runningMs + c.downMs) / c.calendarMs * H_UNITS);
                        if (hTot < 0.03) continue;
                        const g = _tmEl('g', { class: 'up-tm-cube' }, svg);
                        // 앞(z+1)/우(x+1)/윗면 3면 박스 [y0,y1]
                        const box = (y0, y1, f) => {
                            const b0 = iso(x + 1, y0, z), c0 = iso(x + 1, y0, z + 1), e0 = iso(x, y0, z + 1);
                            const a1 = iso(x, y1, z), b1 = iso(x + 1, y1, z), c1 = iso(x + 1, y1, z + 1), e1 = iso(x, y1, z + 1);
                            _tmEl('polygon', { points: pts([e0, c0, c1, e1]), fill: f.front, class: 'up-tm-face' }, g);
                            _tmEl('polygon', { points: pts([b0, c0, c1, b1]), fill: f.right, class: 'up-tm-face' }, g);
                            _tmEl('polygon', { points: pts([a1, b1, c1, e1]), fill: f.top, class: 'up-tm-face' }, g);
                        };
                        if (hRun > 0.03) box(0, hRun, TM_RUN_FACES);
                        if (hTot - hRun > 0.03) box(hRun, hTot, TM_DOWN_FACES); // 정지 캡
                        const t = _tmEl('title', {}, g);
                        t.textContent = `${flows[l].flowName} · ${this._tmShortLabel(m.buckets[d].label, m.granularity)}`
                            + ` — TEEP ${this.pct(c.teep)} · OEE ${this.pct(c.oee)}`
                            + ` · 가동 ${this.durShort(c.runningMs)} · 정지 ${this.durShort(c.downMs)} · 비생산 ${this.durShort(c.nonProdMs)}`;
                        g.dataset.flow = flows[l].flowName;
                        cubes.push(g);
                        g.addEventListener('click', () => this._tmDrillFlow(flows[l].flowName));
                        g.addEventListener('pointerenter', () => {
                            svg.classList.add('tm-dim');
                            for (const q of cubes) q.classList.toggle('is-hl', q.dataset.flow === g.dataset.flow);
                            for (const fn in flowLabelEls) flowLabelEls[fn].classList.toggle('is-hl', fn === g.dataset.flow);
                        });
                    }
                    svg.addEventListener('pointerleave', () => {
                        svg.classList.remove('tm-dim');
                        for (const q of cubes) q.classList.remove('is-hl');
                        for (const fn in flowLabelEls) flowLabelEls[fn].classList.remove('is-hl');
                    });

                    // 계획 기준선 점선 윤곽 + 라벨 — 큐브 위에 그려 항상 보이게(가려진 셀 위로도 기준 노출).
                    if (showPlanned) {
                        _tmEl('polygon', { points: pts(planCorners), class: 'up-tm-plan-line' }, svg);
                        const hpd = pf * 24;                                  // 캘린더 대비 비율 → 하루 계획가동 시간(h)
                        const lp = iso(X, hPlan, Z);                          // 우측(앞) 상단 모서리 — 좌측 설비 라벨 gutter 와 분리
                        _tmEl('text', { x: (lp[0] - 4).toFixed(1), y: (lp[1] - 4).toFixed(1), class: 'up-tm-plan-label', 'text-anchor': 'end' }, svg)
                            .textContent = `계획 ${hpd % 1 === 0 ? hpd.toFixed(0) : hpd.toFixed(1)}h/day`;
                    }

                    // 축 라벨 — 시간(bucket)은 축 위 스텝 솎음. 설비(flow)는 플롯 밖 전용 여백(gutter)에 정렬 + 리더선으로
                    // 각 행에 연결한다: 압축된 깊이축 위에 직접 그리면 서로/그리드/계획선과 겹치고 영역을 벗어나기 때문(사용자 지적).
                    // 세로 깊이축(z, r=0·2)=좌측 여백 세로 스택 / 가로 깊이축(x, r=1·3)=하단 여백 가로 스택.
                    const fullName = (i) => { const n = flows[i].flowName; return n.length > 10 ? n.slice(0, 9) + '…' : n; };
                    const zItem = (zi) => r === 0 ? { flow: zi } : r === 1 ? { bucket: B - 1 - zi } : r === 2 ? { flow: L - 1 - zi } : { bucket: zi };
                    const xItem = (xi) => r === 0 ? { bucket: xi } : r === 1 ? { flow: xi } : r === 2 ? { bucket: B - 1 - xi } : { flow: L - 1 - xi };
                    const zStep = Math.max(1, Math.ceil(Z / 14)), xStep = Math.max(1, Math.ceil(X / 14));
                    const flowAnchors = []; // { fi, ax, ay } — 행의 축 위 기준점(리더선 끝)
                    for (let zi = 0; zi < Z; zi++) {
                        const it = zItem(zi);
                        if (it.flow != null) { const p = iso(-0.1, 0, zi + 0.5); flowAnchors.push({ fi: it.flow, ax: p[0], ay: p[1] }); }
                        else if (zi % zStep === 0) { const p = iso(-0.35, 0, zi + 0.55); _tmEl('text', { x: p[0].toFixed(1), y: (p[1] + 4).toFixed(1), class: 'up-tm-axis', 'text-anchor': 'end' }, svg).textContent = this._tmShortLabel(m.buckets[it.bucket].label, m.granularity); }
                    }
                    for (let xi = 0; xi < X; xi++) {
                        const it = xItem(xi);
                        if (it.flow != null) { const p = iso(xi + 0.5, 0, Z + 0.1); flowAnchors.push({ fi: it.flow, ax: p[0], ay: p[1] }); }
                        else if (xi % xStep === 0) { const p = iso(xi + 0.5, 0, Z + 0.45); _tmEl('text', { x: p[0].toFixed(1), y: (p[1] + 11).toFixed(1), class: 'up-tm-axis', 'text-anchor': 'middle' }, svg).textContent = this._tmShortLabel(m.buckets[it.bucket].label, m.granularity); }
                    }
                    // gutter 배치 — 앵커 순서대로 최소간격 유지하며 여백에 정렬, 밀린 만큼 리더선으로 행과 연결.
                    const addFlowLabel = (fi, lx, ly, anchor, ax, ay) => {
                        _tmEl('line', { x1: lx.toFixed(1), y1: ly.toFixed(1), x2: ax.toFixed(1), y2: ay.toFixed(1), class: 'up-tm-axis-leader' }, svg);
                        const el = _tmEl('text', { x: lx.toFixed(1), y: (ly + 3.5).toFixed(1), class: 'up-tm-axis-flow', 'text-anchor': anchor }, svg);
                        el.textContent = fullName(fi); el.dataset.flow = flows[fi].flowName; flowLabelEls[flows[fi].flowName] = el;
                    };
                    if (r === 0 || r === 2) { // 좌측 여백 세로 스택
                        flowAnchors.sort((a, b) => a.ay - b.ay);
                        let prev = -1e9; const gap = 15, lx = PAD_L - 10;
                        for (const a of flowAnchors) { const ly = Math.max(a.ay, prev + gap); prev = ly; addFlowLabel(a.fi, lx, ly, 'end', a.ax, a.ay); }
                    } else {                  // 하단 여백 가로 스택
                        flowAnchors.sort((a, b) => a.ax - b.ax);
                        let prev = -1e9; const gap = 44, ly = H - 6;
                        for (const a of flowAnchors) { const lx = Math.max(a.ax, prev + gap); prev = lx; addFlowLabel(a.fi, lx, ly - 3.5, 'middle', a.ax, a.ay); }
                    }
                },
                // 2D 막대(설비 뷰) — 3D 아이소의 단일 flow 시간열을 평면으로 편 것(같은 정보): 막대 높이=TEEP(가동),
                // 색=가동(초록)/정지(빨간 캡) 2색, 골드 점선=계획 기준선. 한 flow 대상이라 2D 로 충분.
                _renderTeepBars(host, m) {
                    const fr = this.curFlow ? m.flows.find(f => f.flowName === this.curFlow) : m.flows[0];
                    if (!fr) return;
                    const B = m.buckets.length;
                    const W = 900, H = 300;
                    const mg = { l: 46, r: 14, t: 20, b: 42 };
                    const pw = W - mg.l - mg.r, ph = H - mg.t - mg.b;
                    const svg = _tmEl('svg', { viewBox: `0 0 ${W} ${H}`, class: 'up-tm-svg', role: 'img' });
                    host.appendChild(svg);
                    const yOf = v => mg.t + ph * (1 - Math.min(1, Math.max(0, v)));

                    for (const v of [0, 25, 50, 75, 100]) {
                        const y = yOf(v / 100).toFixed(1);
                        _tmEl('line', { x1: mg.l, y1: y, x2: mg.l + pw, y2: y, class: v === 0 ? 'up-tm-grid-strong' : 'up-tm-grid' }, svg);
                        _tmEl('text', { x: mg.l - 7, y: (+y + 3.5).toFixed(1), class: 'up-tm-axis', 'text-anchor': 'end' }, svg).textContent = v + '%';
                    }

                    // 계획 기준선 — 3D 와 동일 소스(가용성 폴백 체인). calendar 폴백(비율≈1)이면 생략.
                    if (this.teepShowPlanned()) {
                        const yP = yOf(m.plannedFraction);
                        _tmEl('line', { x1: mg.l, y1: yP.toFixed(1), x2: mg.l + pw, y2: yP.toFixed(1), class: 'up-tm-plan-line' }, svg);
                        const hpd = m.plannedFraction * 24;
                        _tmEl('text', { x: (mg.l + pw).toFixed(1), y: (yP - 3).toFixed(1), class: 'up-tm-plan-label', 'text-anchor': 'end' }, svg)
                            .textContent = `계획 ${hpd % 1 === 0 ? hpd.toFixed(0) : hpd.toFixed(1)}h/day`;
                    }

                    const gw = pw / B;
                    const bw = Math.max(4, Math.min(30, gw * 0.62));
                    const showVal = B <= 16; // 버킷 적을 때만 막대 위 TEEP% (시간별 24개 등은 툴팁만)
                    const step = Math.max(1, Math.ceil(B / 12));
                    for (let i = 0; i < B; i++) {
                        const c = fr.cells[i];
                        const cx = mg.l + i * gw + gw / 2;
                        const g = _tmEl('g', { class: 'up-tm-bar-g' }, svg);
                        // 투명 히트영역 — 막대가 없는(무활동) 버킷도 툴팁·클릭 가능
                        _tmEl('rect', { x: (cx - gw / 2).toFixed(1), y: mg.t, width: gw.toFixed(1), height: ph, fill: 'transparent' }, g);
                        if (c && c.calendarMs > 0) {
                            const teepF = c.teep ?? 0;                                     // 가동/캘린더 = 막대 높이
                            const totF = Math.min(1, (c.runningMs + c.downMs) / c.calendarMs); // 가동+정지 = 캡 상단
                            const x = (cx - bw / 2).toFixed(1), w = bw.toFixed(1);
                            // TEEP 막대 — 가동 단일색(3D 팔레트 .right 톤)
                            if (teepF > 0.001) {
                                const yT = yOf(teepF);
                                _tmEl('rect', { x, y: yT.toFixed(1), width: w, height: (mg.t + ph - yT).toFixed(1), fill: TM_RUN_FACES.right, rx: 1.5 }, g);
                            }
                            // 정지 캡 — TEEP 위에 쌓음(3D 빨간 캡과 동일 의미)
                            if (totF - teepF > 0.002)
                                _tmEl('rect', { x, y: yOf(totF).toFixed(1), width: w, height: (yOf(teepF) - yOf(totF)).toFixed(1), fill: TM_DOWN_FACES.right }, g);
                            if (showVal && teepF > 0.001)
                                _tmEl('text', { x: cx.toFixed(1), y: (yOf(totF) - 3).toFixed(1), class: 'up-tm-val', 'text-anchor': 'middle' }, g)
                                    .textContent = Math.round(teepF * 100);
                            const t = _tmEl('title', {}, g);
                            t.textContent = `${m.buckets[i].label} — TEEP ${this.pct(c.teep)} · OEE ${this.pct(c.oee)}`
                                + ` (A ${this.pct(c.availability)} · P ${this.pct(c.performance)} · Q ${this.pct(m.quality)})`
                                + ` · 가동 ${this.durShort(c.runningMs)} · 정지 ${this.durShort(c.downMs)} · 비생산 ${this.durShort(c.nonProdMs)}`;
                        }
                        g.addEventListener('click', () => this._tmDrillOee(fr.flowName));
                        if (i % step === 0)
                            _tmEl('text', { x: cx.toFixed(1), y: (mg.t + ph + 15).toFixed(1), class: 'up-tm-axis', 'text-anchor': 'middle' }, svg)
                                .textContent = this._tmShortLabel(m.buckets[i].label, m.granularity);
                    }
                },

                async loadOee(silent) {
                    if (this.view === 'alarm') return; // OEE 지표는 알람 전용 페이지에서 미사용
                    const r = this.rangeForPeriod();
                    const qs = `from=${encodeURIComponent(r.from)}&to=${encodeURIComponent(r.to)}`;
                    // fqs = 기간 + 스코프(설비 ▸ 시스템) 필터. 순위(ranking)는 설비 비교용이라 설비 필터는 안 걸되,
                    // 시스템 스코프는 그 시스템 설비끼리의 비교가 목적이므로 함께 좁힌다.
                    const fqs = qs + this.scopeQs();
                    const rqs = qs + (this.curSystem ? '&system=' + encodeURIComponent(this.curSystem) : '');
                    const seq = ++this._oeeSeq;
                    // 생산효율 페이지 — OEE 는 참조 KPI(요약)만 필요. 정지/순위/추이/계획시간(무거운 4조회)은
                    // 설비효율 페이지 전용이라 10초 폴링 낭비를 막기 위해 요약 + TEEP + 매트릭스만 로드.
                    if (this.view === 'teep') {
                        try {
                            const summary = await this.apiGet('/api/oee/summary?' + fqs);
                            if (seq !== this._oeeSeq) return; // stale 응답 폐기
                            this.oee = summary;
                            this.oeeError = null;
                            await Promise.all([this.loadTeep(), this.loadTeepMatrix(silent), this.loadTeepNonProd()]);
                            this.loadMeasureQuality();   // 생산효율 페이지에도 같은 무결성 카드(별개 축, 실패해도 무해)
                        } catch (e) {
                            if (seq !== this._oeeSeq) return;
                            this.oeeError = 'OEE 데이터를 불러오지 못했습니다: ' + e.message;
                        }
                        return;
                    }
                    try {
                        // plan-time 은 더 이상 호출하지 않는다 — 시간기반 폴백(시프트/자동추정/달력)을 없애면서
                        // 화면 소비처가 사라졌는데 10초 폴링만 남아 있었다(2026-08-21). 엔드포인트 자체는
                        // TEEP 매트릭스·사전계산이 계속 쓰므로 서버에는 유지.
                        const [summary, downtime, ranking, daily] = await Promise.all([
                            this.apiGet('/api/oee/summary?' + fqs),
                            this.apiGet('/api/oee/downtime?' + fqs),
                            this.apiGet('/api/oee/ranking?' + rqs),
                            this.apiGet('/api/oee/daily?' + fqs),
                        ]);
                        if (seq !== this._oeeSeq) return; // stale 응답 폐기
                        this.oee = summary;
                        this.downtime = Array.isArray(downtime) ? downtime : [];
                        this.ranking = Array.isArray(ranking) ? ranking : [];
                        this.dailyData = daily;
                        this.oeeError = null;
                    } catch (e) {
                        if (seq !== this._oeeSeq) return;
                        this.oeeError = 'OEE 데이터를 불러오지 못했습니다: ' + e.message;
                    }
                    this.loadMeasureQuality();   // 별개 축 — OEE 실패와 독립(await 안 함, 카드가 따로 비어 있을 뿐)
                    this.$nextTick(() => this.drawDailyChart());
                },

                // ── 계측 품질 (/api/oee/measurement-quality) ──────────────────────────
                // 설비별 사이클 제외·누락률. OEE 카드와 달리 curFlow 필터를 걸지 않는다 — 설비 하나를 보는
                // 중에도 "다른 설비 IO 가 나빠지고 있다"를 놓치면 안 되고, 개선 주체(수집 인프라)가 라인 단위다.
                async loadMeasureQuality() {
                    if (this.view === 'alarm') return;
                    const r = this.rangeForPeriod();
                    const seq = ++this._mqSeq;
                    try {
                        // 설비 선택 중에도 라인 전체 유지(다른 설비 IO 악화 감시) — 시스템 스코프만 함께 좁힌다.
                        const dto = await this.apiGet(
                            `/api/oee/measurement-quality?from=${encodeURIComponent(r.from)}&to=${encodeURIComponent(r.to)}`
                            + (this.curSystem ? '&system=' + encodeURIComponent(this.curSystem) : ''));
                        if (seq !== this._mqSeq) return;
                        this.mq = dto;
                    } catch (e) {
                        if (seq !== this._mqSeq) return;
                        this.mq = null;   // 조용히 비움 — 계측 품질 실패가 OEE 화면을 막지 않는다
                    }
                },
                // ── 경계(Head/Tail) 진단 (2026-08-24) ────────────────────────────
                //   경계가 실제 동작과 어긋나면 지표가 조용히 틀린다. 실측 두 사례:
                //     배출  — head 9회·tail 10회 도는데 사이클 0건(후보 모호로 래치 비활성)
                //     xgk103 — head 978 vs tail 440(격사이클) → CT 2배로 계상, IO 누락으로 오해
                //   그래서 "지표가 비어 있음/이상함"을 침묵시키지 않고 경계 지정으로 유도한다.
                mqIssueText(f) {
                    if (!f || !f.boundaryIssue) return '';
                    const h = f.headCall || '?', t = f.tailCall || '?';
                    if (f.boundaryIssue === 'no-signal')
                        return `경계 Call 이 동작하지 않습니다 (시작 ${h} ${f.headGoingCount}회 · 완료 ${t} ${f.tailGoingCount}회) — 경계가 실제 동작과 맞지 않습니다.`;
                    if (f.boundaryIssue === 'no-cycle')
                        return `동작은 감지되는데(시작 ${f.headGoingCount}회 · 완료 ${f.tailGoingCount}회) 가동이 0건입니다 — 경계 후보가 모호하거나 순서가 반대입니다.`;
                    if (f.boundaryIssue === 'skip-cycle') {
                        const r = f.tailGoingCount > 0 ? Math.round(f.headGoingCount / f.tailGoingCount * 100) : 0;
                        return `완료 신호가 시작의 ${r}% 만 발화합니다 — 격사이클로 잡혀 가동시간이 배로 계상될 수 있습니다.`;
                    }
                    return '';
                },
                mqIssueLabel(f) {
                    return { 'no-signal': '경계 미동작', 'no-cycle': '경계 확인 필요', 'skip-cycle': '격사이클 의심' }[f && f.boundaryIssue] || '';
                },
                // 지금 보고 있는 대상의 경계 문제 — 페이지 <b>상단</b> 배너용.
                //   설비를 고른 상태면 그 설비만, 라인 전체면 문제 설비 중 첫 번째를 대표로 보여준다.
                //   맨 아래 무결성 카드까지 내려가지 않아도 "A 가 왜 비었는지"를 그 자리에서 알 수 있게 한다.
                get mqCurrentIssue() {
                    const rows = this.mqIssueRows;
                    if (!rows.length) return null;
                    if (this.curFlow) return rows.find(f => f.flowName === this.curFlow) || null;
                    return rows[0];
                },
                // 경계 문제가 있는 설비 목록 — 카드 상단 배너용.
                get mqIssueRows() { return ((this.mq && this.mq.flows) || []).filter(f => !!f.boundaryIssue); },

                // ── 사이클 분기 미분류 (2026-08-27) ──────────────────────────────
                //   미분류 = 어느 분기의 제외 필터도 통과 못한 사이클(통계 제외·여기서만 계수).
                //   급등 = 분기 정의 오류(제외 call 이 실제 IO 패턴과 불일치) 또는 센서 오감지 조기 경보.
                get mqHasBranched() { return ((this.mq && this.mq.flows) || []).some(f => f.branched); },
                get mqUnclassifiedRows() {
                    return ((this.mq && this.mq.flows) || [])
                        .filter(f => f.branched && f.unclassifiedRate != null && f.unclassifiedRate >= 0.05);
                },
                mqUnclassifiedText(f) {
                    if (!f) return '';
                    return `가동 ${f.totalCycles.toLocaleString()}회 중 ${f.unclassifiedCycles.toLocaleString()}회(${this.mqPct(f.unclassifiedRate)})가 어느 분기에도 속하지 않습니다 — 분기 정의(제외 call)가 실제 IO 패턴과 맞는지, 제외 call 의 센서 오감지가 없는지 확인하세요.`;
                },

                // 수집률 = 정상 CT / 전체 CT — 이 카드의 주 수치(2026-08-21).
                //   제외율(나쁜 비율)이 아니라 "얼마나 제대로 수집했나"를 앞세운다. OEE 가 CT축으로 바뀌어
                //   수집된 정상 CT 가 곧 지표의 근거이므로, 그 근거의 양을 보여주는 게 이 카드의 역할이다.
                mqCollectRate(f) {
                    if (!f || !f.totalCycles) return null;
                    return Math.max(0, (f.totalCycles - f.excludedCycles) / f.totalCycles);
                },
                get mqLineCollectRate() {
                    const m = this.mq;
                    if (!m || !m.totalCycles) return null;
                    return Math.max(0, m.normalCycles / m.totalCycles);
                },
                // 비율 표기 — null(사이클 0건)은 '—'. "제외 0%"로 보이면 수집 정지와 정상 가동이 같은 화면이 된다.
                mqPct(v) { return v == null ? '—' : (v * 100 < 0.05 && v > 0 ? '<0.1%' : (v * 100).toFixed(1) + '%'); },
                // 설비별 바 폭 — 임계선 없이 "이 기간 최댓값 대비"로 그린다(상대 비교 전용 축).
                //   ① 비례 막대(제외/전체)는 0.5% 에서 1px 미만이라 건강한 평시엔 아무 정보도 못 준다.
                //   ② 고정 임계선 대비는 "몇 %부터 나쁘다"는 없는 기준을 만들어낸다.
                //   최댓값 기준이면 기준을 발명하지 않고도 설비 간 편중이 바로 읽힌다(원수치는 옆 열).
                // 바 = 수집률(0~100% 고정 축). 상대 비교가 아니라 "100% 중 얼마"라는 절대 충실도라
                // 최댓값 정규화가 필요 없다 — 설비 간 비교도 같은 축에서 그대로 된다.
                mqBarW(rate) {
                    if (rate == null) return '0%';
                    return Math.max(0, Math.min(100, rate * 100)).toFixed(2) + '%';
                },
                // 측정 불가 먼저, 그다음 제외율 내림차순 — 편중된 설비가 위로("어느 설비 IO 부터"를 바로 읽게).
                // 측정 불가를 맨 위로 올리는 이유: 제외율이 null 이라 정렬 키가 없는데 그게 가장 나쁜 상태다.
                get mqRows() {
                    const f = (this.mq && this.mq.flows) || [];
                    return [...f].sort((a, b) =>
                        (a.measurable === false ? 0 : 1) - (b.measurable === false ? 0 : 1)
                        || (b.exclusionRate ?? -1) - (a.exclusionRate ?? -1));
                },

                // ── 내보내기 (종합효율 현황) ─────────────────────────────────────────────
                // Excel = 화면 상태(요약·순위·정지) + 일자별 추이 차트(캔버스 캡처)를 서버(OeeExcelExporter)가 렌더 → WYSIWYG.
                oeeExportName() { return this.curFlow ? this.curFlow : this.curSystem ? this.curSystem : '라인전체'; },
                _stamp() { const t = new Date(); const p = (x) => String(x).padStart(2, '0'); return `${t.getFullYear()}${p(t.getMonth() + 1)}${p(t.getDate())}_${p(t.getHours())}${p(t.getMinutes())}${p(t.getSeconds())}`; },
                _downloadBlob(filename, blob) {
                    const url = URL.createObjectURL(blob);
                    const a = document.createElement('a'); a.href = url; a.download = filename;
                    document.body.appendChild(a); a.click(); document.body.removeChild(a); URL.revokeObjectURL(url);
                },

                async exportOeeExcel() {
                    if (!this.oee || this.oeeExporting) return;
                    this.oeeExporting = true; this.oeeError = null;
                    try {
                        const o = this.oee;
                        const r = this.rangeForPeriod();
                        // 일자별/시간별 추이 차트(캔버스 캡처) — WYSIWYG.
                        const images = [];
                        const cv = document.getElementById('up-daily-chart');
                        if (cv) {
                            try {
                                const rc = cv.getBoundingClientRect();
                                images.push({
                                    name: (this.dailyData && this.dailyData.granularity === 'hour' ? '시간별 추이' : '일자별 추이') + ' (가동·고장·유지보수·비생산)',
                                    dataUrl: cv.toDataURL('image/png'), width: Math.round(rc.width), height: Math.round(rc.height),
                                });
                            } catch (e) { /* 캡처 실패는 무시(데이터 시트는 정상 생성) */ }
                        }
                        const ac = this.availComp;
                        const model = {
                            title: this.curFlow || this.curSystem || '라인 전체',
                            systemName: this.curSystem || null,
                            flowName: this.curFlow || null,
                            periodStart: r.from, periodEnd: r.to,
                            kpi: {
                                oee: o.oee, availability: o.availability, performance: o.performance, quality: o.quality,
                                mtbf: o.mtbf, mttr: o.mttr,
                                availabilitySource: o.availabilitySource, qualitySource: o.qualitySource,
                                downtimeCount: o.downtimeCount, downtimeMs: o.downtimeMs, ctThresholdMs: o.ctThresholdMs,
                                normalCycleCount: o.normalCycleCount, failureCount: o.failureCount,
                                goodCount: o.goodCount, totalCount: o.totalCount,
                            },
                            availComp: (ac && ac.hasData) ? { runLabel: ac.runLabel, runMs: ac.runMs, runPct: ac.runPct, stopLabel: ac.stopLabel, stopMs: ac.stopMs, stopPct: ac.stopPct, maintMs: ac.maintMs, maintPct: ac.maintPct, waitMs: ac.waitMs, waitPct: ac.waitPct } : null,
                            // 무결성 — 내보낸 표만 봐도 "얼마나 수집된 근거 위의 수치인지" 알 수 있게 동봉.
                            integrity: this.mq ? {
                                totalCycles: this.mq.totalCycles, normalCycles: this.mq.normalCycles,
                                excludedCycles: this.mq.excludedCycles, incompleteCycles: this.mq.incompleteCycles,
                                unmeasurableFlowCount: this.mq.unmeasurableFlowCount || 0, collectRate: this.mqLineCollectRate,
                            } : null,
                            faultSegs: this.faultDist.segs.map(s => ({ label: s.label, ms: s.ms, share: s.share })),
                            ranking: this.ranking.map(rk => ({ flowName: rk.flowName, oee: rk.oee, availability: rk.availability, performance: rk.performance, quality: rk.quality, downtimeCount: rk.downtimeCount, downtimeMs: rk.downtimeMs, totalCount: rk.totalCount })),
                            downtime: this.downtime.map(d => ({ startAt: d.startAt, endAt: d.endAt, durationMs: d.durationMs, flowName: d.flowName || d.systemName, deviceName: d.deviceName, isFailure: !!d.isFailure, isNonProd: !!d.isNonProd, detectSource: d.detectSource, status: d.status })),
                            images,
                        };
                        const res = await fetch('/api/oee/export-excel', {
                            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(model),
                        });
                        if (!res.ok) throw new Error('HTTP ' + res.status);
                        let fn = `OEE_${this.oeeExportName()}_${this._stamp()}.xlsx`;
                        const cd = res.headers.get('Content-Disposition');
                        if (cd) {
                            const star = cd.match(/filename\*=(?:UTF-8'')?([^;]+)/i);
                            const plain = cd.match(/filename="?([^";]+)"?/i);
                            if (star) { try { fn = decodeURIComponent(star[1].trim()); } catch (_) {} }
                            else if (plain) { fn = plain[1].trim(); }
                        }
                        this._downloadBlob(fn, await res.blob());
                    } catch (e) {
                        this.oeeError = 'Excel 내보내기 실패: ' + e.message;
                    } finally { this.oeeExporting = false; }
                },

                drawDailyChart() {
                    if (this.view === 'alarm') return; // 일자별 차트는 알람 전용 페이지에 캔버스 없음
                    const d = this.dailyData;
                    if (!d || !d.slots || d.slots.length === 0) return;
                    const canvas = document.getElementById('up-daily-chart');
                    if (!canvas) return;
                    // x-if 가 canvas 를 제거했다 다시 만들면 기존 차트는 분리된 옛 canvas 에 묶여
                    // update 가 화면에 안 그려짐(영구 공백) — canvas 가 바뀌었으면 파기 후 재생성
                    if (_dailyChart && _dailyChart.canvas !== canvas) {
                        try { _dailyChart.destroy(); } catch (e) {}
                        _dailyChart = null;
                    }

                    const MS = 3600000; // ms → 시간
                    const labels = d.slots.map(s => {
                        if (d.granularity === 'hour') return s.slot.slice(11, 16); // "HH:mm"(=HH:00)
                        return s.slot.length >= 10 ? s.slot.slice(5, 10) : s.slot;  // "yyyy-MM-dd" → "MM-DD" (ISO 숫자형 통일)
                    });
                    // 가동·고장·유지보수·비생산 4분해 — 고장/유지보수 구분은 '정지 구성' 도넛과 동일한 isFailure 2-상태로 정렬:
                    //   고장 = failureMs(isFailure=1) + unclassifiedMs(미분류, 기본 isFailure=1)
                    //   유지보수 = plannedMs(category='planned') + otherMs(계획외지만 isFailure=0 — 자재대기 등, 도넛도 유지보수로 집계)
                    //   nonProdMs=비생산(A 분모 밖 — 가동에서 카빙), 나머지=가동근사.
                    // 가동간 공백(임계 미만 사이클 간 미세 슬랙, 2026-07-14): 서버 daily 가 고장에 감지 정지 귀속분만
                    //   적재하므로 공백은 슬롯 잔여 = 가동에 포함돼 그려진다(사용자 결정 — 추이에선 가동으로 인정,
                    //   가용성 정산 바가 밝은 하늘색 '가동간 공백' 세그먼트로 따로 보여준다).
                    const failureData = d.slots.map(s => ((s.failureMs || 0) + (s.unclassifiedMs || 0)) / MS); // 고장(isFailure=1 계열)
                    const plannedData = d.slots.map(s => ((s.plannedMs || 0) + (s.otherMs || 0)) / MS); // 유지보수(isFailure=0 계열)
                    // 비생산(제외) — A 분모 밖. 미계측(수신 공백, §3.4)은 어떤 스택에도 채우지 않는다(2026-07-06 결정):
                    // 비생산·가동 어디에도 안 넣어 스택 합 < slotMs → 그만큼 흰 여백으로 남아 "데이터 없음"이 시각 구분된다.
                    //   2026-08-21: 그 규칙을 미수집 전체로 확장 — 가동을 잔여로 재구성하지 않고 서버 실측(runMs)만 그린다.
                    // (범례에도 미계측 항목 없음 — 별도 데이터셋을 만들지 않으므로.)
                    const nonProdData = d.slots.map(s => (s.nonProdMs || 0) / MS);
                    // 가동 = 서버 실측(정상 사이클 구간 ∩ 슬롯). 잔여 계산 금지 — 감지 실패가 가동으로 둔갑한다.
                    //   스택 합이 slotMs 에 못 미치는 만큼이 미수집이고, 그게 곧 데이터 무결성 카드의 수집률과 이어진다.
                    const runData = d.slots.map(s => Math.max(0, s.runMs || 0) / MS);

                    // 평균 가동시간 선 (비생산 카빙 후 실가동 기준)
                    const avgRun = runData.length > 0 ? runData.reduce((a, b) => a + b, 0) / runData.length : 0;

                    // y축 고정 상한: 오늘(시간별)=1h, 그 이상(일별)=24h. 전체(시스템)면 ×설비수(합산이라 슬롯이 그만큼 참).
                    //   부분 슬롯(오늘 진행 중 시각/기간 양끝 날)이 있어도 축은 항상 꽉 찬 슬롯 기준으로 고정 → 막대가
                    //   "1h/24h 를 넘는 것처럼" 보이던 착시(평균 참조선 대비) 제거.
                    const flowCount = Math.max(1, d.flowCount || 1);
                    const yMax = (d.granularity === 'hour' ? 1 : 24) * flowCount;

                    const cs = getComputedStyle(document.documentElement);
                    // 가동=밝은 파랑(눈에 띄게) / 정지 3종=어둡게 대비. 유지보수=노란(앰버) 계열. SSOT=uptime-workspace.css --oee-*
                    const cMaint = cs.getPropertyValue('--oee-maint').trim() || '#9A8500';
                    const cFault = cs.getPropertyValue('--oee-fault').trim() || '#B22F22';
                    const cRun   = cs.getPropertyValue('--oee-run').trim()   || '#1E9BE8'; // 가동 = 밝은 애저
                    const cGray  = cs.getPropertyValue('--color-text-secondary').trim() || '#888';
                    const nightHatch = _nightHatchPattern(canvas); // 비생산: 밤하늘 어두운 파랑 빗금
                    const faultHatch = _stripePattern(canvas, cFault);   // 고장: 어두운 빨강 위 빗금
                    const maintHatch = _stripePattern(canvas, cMaint);   // 유지보수: 노란(앰버) 위 빗금

                    const datasets = [
                        // 가동 = 솔리드(파랑) / 정지 3종(고장·유지보수·비생산) = 빗금 → "가동이 아님"을 직관적으로 표시
                        { label: '가동(실측)', data: runData, backgroundColor: cRun, stack: 's', order: 2 },   // 잔여 재구성이 아니라 서버 실측(runMs)
                        { label: '고장', data: failureData, backgroundColor: faultHatch, stack: 's', order: 2 },
                        { label: '유지보수', data: plannedData, backgroundColor: maintHatch, stack: 's', order: 2 },
                        // 기본 숨김(2026-07-08 사용자 결정) — 비생산은 A 분모 밖이라 추이에선 기본으로 감추고,
                        //   보고 싶으면 범례 클릭으로 켠다. hidden 은 생성 시에만 지정 — update-in-place 루프가
                        //   hidden 을 건드리지 않으므로 사용자의 범례 토글이 라이브 갱신에도 유지된다.
                        { label: '비생산(제외)', data: nonProdData, backgroundColor: nightHatch, stack: 's', order: 2, hidden: true },
                        {
                            label: `평균 ${avgRun.toFixed(1)}시간`,
                            type: 'line',
                            data: d.slots.map(() => avgRun),
                            borderColor: cGray,
                            borderWidth: 1.5,
                            borderDash: [6, 4],
                            pointRadius: 0,
                            fill: false,
                            stack: undefined,
                            order: 1,
                        },
                    ];

                    if (!_dailyChart) {
                        _dailyChart = new Chart(canvas, {
                            type: 'bar',
                            data: { labels, datasets },
                            options: _dailyChartOptions(d.granularity, yMax),
                        });
                    } else {
                        _dailyChart.data.labels = labels;
                        for (let i = 0; i < datasets.length; i++) {
                            if (_dailyChart.data.datasets[i]) {
                                _dailyChart.data.datasets[i].data = datasets[i].data;
                                if (datasets[i].label) _dailyChart.data.datasets[i].label = datasets[i].label;
                                if (datasets[i].backgroundColor) _dailyChart.data.datasets[i].backgroundColor = datasets[i].backgroundColor;
                                if (datasets[i].borderColor) _dailyChart.data.datasets[i].borderColor = datasets[i].borderColor;
                            } else {
                                _dailyChart.data.datasets.push(datasets[i]);
                            }
                        }
                        _dailyChart.options = _dailyChartOptions(d.granularity, yMax);
                        _dailyChart.update('none');
                    }
                },

                connectSignalR() {
                    if (!window.signalR) return;
                    const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/monitoring').withAutomaticReconnect([0, 0, 1000, 3000, 5000, 10000]).build();
                    // 이벤트-트리거 재조회 스로틀(trailing 1회). 숨긴 탭은 스킵.
                    // P2 이후 OEE/TEEP 의 트리거 소스는 서버 push(OeePrecomputed — 서버에서 이미 ≥5초
                    // 간격으로 율제한됨)라 2초 상한이면 충분하다. 재조회 응답은 사전계산 저장본(~2ms).
                    const trigger = () => {
                        if (document.hidden) return;
                        const since = Date.now() - (this._lastTrigLoad || 0);
                        if (since >= 2000) {
                            this._lastTrigLoad = Date.now();
                            this.load(true);
                        } else if (!this._dt) {
                            this._dt = setTimeout(() => {
                                this._dt = null;
                                this._lastTrigLoad = Date.now();
                                this.load(true);
                            }, 2000 - since);
                        }
                    };
                    // P2 push 구독 — 서버 사전계산이 갱신될 때만 재조회. 구 CallStateChanged(Batch) 구독은
                    // 제거: 사이클마다 오는 최다 빈도 이벤트로 무거운 전체 재조회를 유발하던 증폭원이며,
                    // OeePrecomputed(변화 후 ≤5초 내 도착)가 실시간성을 대체한다. 알람 페이지는 무관.
                    if (this.view !== 'alarm') {
                        // custom 기간은 사전계산 대상이 아니라 push 반응 시 라이브 재계산이 됨 — 표준 프리셋만 반응
                        // (custom 은 60초 안전망 폴링 + 수동 '적용'으로 갱신).
                        conn.on('OeePrecomputed', () => { if (this.period !== 'custom') trigger(); });
                    }
                    conn.on('DatabaseRebuilt', () => { this.load(true); });
                    conn.on('FlowHistoryCleared', trigger);
                    // 신규 UserTag 알림 — 총알림·시계열 추이를 상단바 배지와 동일하게 실시간 갱신 (issue #176).
                    conn.on('UserTagAlertsChanged', trigger);
                    // 신규 이상감지도 알림 이력/추이를 즉시 갱신.
                    conn.on('AbnormalDetected', trigger);
                    conn.onreconnected(() => { this.rt.connected = true; this.load(true); });
                    conn.onreconnecting(() => { this.rt.connected = false; });
                    conn.onclose(() => { this.rt.connected = false; });
                    conn.start().then(() => { this.rt.connected = true; }).catch(() => { this.rt.connected = false; });
                    this._conn = conn;
                },

                // 기간 변경 재로드 — load()(스냅샷+OEE) 외에 기간 의존 데이터('실제 제외 비생산' 타임라인)도
                // 함께 기다린다. 빠뜨리면 로딩 인디케이터가 끝난 뒤(다음 10초 폴링에서야) 타임라인이 뒤늦게 갱신됨.
                async reloadForPeriod() {
                    this._userBusy++; // load() 자체 카운트 외에 refreshActualNonProd 완주까지 무음 재로드 차단
                    try {
                        const jobs = [this.load()];
                        // '실제 제외 비생산' 타임라인은 설비효율 페이지 전용(생산효율/알람 페이지엔 UI 없음).
                        if (this.view === 'oee' || this.view === 'both') jobs.push(this.refreshActualNonProd());
                        await Promise.all(jobs);
                    } finally { this._userBusy--; }
                },
                setPeriod(p) { if (this.period === p) return; this.period = p; this.utPage = 0; this.syncPeriodUrl(); if (window.dspLoading) window.dspLoading.wrap(() => this.reloadForPeriod(), '기간 데이터 불러오는 중…'); else this.reloadForPeriod(); },

                toggleCustomPeriod() {
                    if (this.period === 'custom') { this.setPeriod('today'); return; }
                    // 현재 기간 범위를 초기값으로 세팅
                    const fmt = (d) => {
                        const p = (x) => String(x).padStart(2, '0');
                        return `${d.getFullYear()}-${p(d.getMonth()+1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
                    };
                    const r = this.rangeForPeriod();
                    this.customFrom = r.from.slice(0, 16);
                    this.customTo = r.to.slice(0, 16);
                    this.period = 'custom';
                    this.syncPeriodUrl();
                },
                // 커스텀 기간 상한(2개월) — 초과 시 종료 시각을 기준으로 시작을 당기고 토스트로 안내.
                // 서버 인메모리 미러 창(63일) 안에 사용자 조회가 항상 들어오게 하는 UX 규약(shell.js SSOT).
                clampCustomRange() {
                    if (!window.dspClampRange || !this.customFrom || !this.customTo) return;
                    const s = new Date(this.customFrom), e = new Date(this.customTo);
                    const r = window.dspClampRange(s, e, 'end');
                    if (!r.clamped) return;
                    const p = (x) => String(x).padStart(2, '0');
                    const fmt = (d) => `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
                    this.customFrom = fmt(r.start);
                    this.customTo = fmt(r.end);
                    if (window.dspToast) window.dspToast(window.dspRangeClampMsg, 'warning');
                },
                applyCustomPeriod() {
                    if (!this.customFrom || !this.customTo) return;
                    this.clampCustomRange();
                    this.utPage = 0;
                    this.syncPeriodUrl();
                    if (window.dspLoading) window.dspLoading.wrap(() => this.reloadForPeriod(), '기간 데이터 불러오는 중…'); else this.reloadForPeriod();
                },

                // ── 구분(ABNORMAL/USERTAG) 도넛/배지 ──
                // 구분 판별 SSOT(클라) — abnormal 행은 matchOp='AbnormalDetect'(서버 valueType='Abnormal' 과 대응).
                categoryOf(a) { return (a && a.matchOp === 'AbnormalDetect') ? 'ABNORMAL' : 'USERTAG'; },
                // 표시 라벨: ABNORMAL=자동감지(엔진 자동), USERTAG=수동등록TAG(사용자 정의 태그).
                categoryLabel(a) { return this.categoryOf(a) === 'ABNORMAL' ? '자동감지' : '수동등록TAG'; },
                // ds-status 톤: 둘 다 Error 알람이라 bad(빨강)로 통일, 구분은 라벨로만.
                categoryStatus(a) { return 'bad'; },
                cc(cat) { return (this.ut && this.ut.categoryCounts && this.ut.categoryCounts[cat]) || 0; },
                get categoryTotal() { return this.cc('ABNORMAL') + this.cc('USERTAG'); },
                // 상단 요약 카드에서 구분 필터를 쓸 수 있는지 — 설비별 보기(?flow=)면 서버가 자동감지만 남기고
                // category 파라미터를 무시하므로(utQs 주석 참조) 카드 클릭도 비활성한다.
                get utCatFilterOn() { return !this.curFlow; },
                // 요약 카드 클릭 = 구분 필터 토글('' 전체 | 'abnormal' | 'usertag'). 같은 카드 재클릭 시 해제.
                // 서버 재조회(첫 페이지부터)라 표·시계열·Top10 이 함께 좁혀지고, 카드 자체 수치(categoryCounts)는
                // 구분 필터를 무시하는 집계라 필터 중에도 전체 비율이 유지된다.
                setUtCategory(cat) {
                    if (!this.utCatFilterOn) return;
                    const next = (this.utCategory === cat) ? '' : cat;
                    if (this.utCategory === next) return;
                    this.utCategory = next;
                    this.utPage = 0;
                    this.load();
                },
                // 현재 조회 중인 구분 문구 — 시계열/Top10 부제가 요약 카드 필터와 어긋나지 않게 한다.
                get utCategoryLabel() {
                    if (this.curFlow || this.utCategory === 'abnormal') return '자동감지';
                    if (this.utCategory === 'usertag') return '수동등록TAG';
                    return '자동감지 + 수동등록TAG';
                },
                // 최다 발생 카드 — '태그별 Top 10' 차트와 동일 소스(경로 기준 1위).
                get utTopPath() {
                    const rows = (this.ut && this.ut.topRowsByPath) || [];
                    return rows.length ? rows[0] : null;
                },
                categoryShare(cat) {
                    const total = this.categoryTotal;
                    if (total <= 0) return 0;
                    return Math.round(this.cc(cat) * 100 / total);
                },
                catDonutSeg(cat) {
                    const C = 2 * Math.PI * 38;
                    const total = this.categoryTotal;
                    if (total <= 0) return { dash: '0 ' + C.toFixed(2), offset: 0 };
                    const order = ['ABNORMAL', 'USERTAG'];
                    let prior = 0;
                    for (const c of order) { if (c === cat) break; prior += this.cc(c); }
                    const len = this.cc(cat) / total * C;
                    const gap = C - len;
                    const offset = -(prior / total * C);
                    return { dash: len.toFixed(2) + ' ' + gap.toFixed(2), offset: offset.toFixed(2) };
                },
                get recentAlerts() {
                    if (!this.ut || !this.ut.alerts) return [];
                    return [...this.ut.alerts]
                        .sort((a, b) => String(b.occurredAtLocal || '').localeCompare(String(a.occurredAtLocal || '')))
                        .slice(0, 10);
                },
                describeOp(op, mv) {
                    const v = (mv == null || mv === '') ? '?' : mv;
                    switch (op) {
                        case 'RisingEdge': return '0 → 1'; case 'FallingEdge': return '1 → 0'; case 'Changed': return '값 변경';
                        case 'Eq': return '= ' + v; case 'Neq': return '≠ ' + v; case 'Gt': return '> ' + v; case 'Gte': return '≥ ' + v;
                        case 'Lt': return '< ' + v; case 'Lte': return '≤ ' + v;
                        case 'AbnormalDetect': {
                            const m = { SensorShort: '예상치 않은 시점에 완료 신호 감지', SensorOpen: '유지돼야 할 센서 신호 끊김', ActionOver: '허용 시간 초과', ActionUnder: '허용 시간 미만' };
                            return (m[mv] || '이상 감지') + (mv ? ' (' + mv + ')' : '');
                        }
                        default: return op || '?';
                    }
                },
                // 경로 셀: AbnormalDetect → tagAddress = "FLOW / WORK / CALL" 경로(서버 BuildPath),
                //          나머지(UserTag) → SystemName. (구 이력은 tagAddress=FlowName 만 → 그대로 폴백 표시)
                abnPath(a) {
                    if (a.matchOp === 'AbnormalDetect') {
                        const p = a.tagAddress || '-';
                        const idx = p.indexOf(' / ');
                        return idx >= 0 ? p.slice(idx + 3) : p;
                    }
                    return a.systemName || '-';
                },
                // 주소 셀: Sensor* → InTag 주소(actualValue), Action* → '-', 나머지 → tagAddress
                abnAddr(a) {
                    if (a.matchOp !== 'AbnormalDetect') return a.tagAddress || '-';
                    if (a.matchValue && a.matchValue.startsWith('Sensor'))
                        return (a.actualValue && a.actualValue !== a.tagAddress) ? a.actualValue : '-';
                    return '-';
                },
                abnAddrClass(a) {
                    if (a.matchOp !== 'AbnormalDetect') return 'up-codeish';
                    if (a.matchValue && a.matchValue.startsWith('Sensor'))
                        return (a.actualValue && a.actualValue !== a.tagAddress) ? 'up-codeish' : '';
                    return '';
                },
                // 값 셀: AbnormalDetect → '-'(시간·주소 이동/숨김), 나머지 기존
                abnVal(a) {
                    if (a.matchOp !== 'AbnormalDetect') return a.actualValue;
                    return '-';
                },

                // ── 알람 차단 관리 (모달) ──
                blkKindLabel(kind) { const o = this.blockMgr.kindOptions.find(k => k.kind === kind); return o ? o.label : String(kind); },
                get blkSelectedCount() { return Object.values(this.blockMgr.selected).filter(Boolean).length; },
                get blkAllSelected() { const fd = this.blkFilteredDevices; return fd.length > 0 && fd.every(d => this.blockMgr.selected[d.device]); },
                get blockedDeviceCount() { return this.blockMgr.devices.filter(d => (d.blockedKinds || []).length > 0).length; },
                // 툴바 버튼 배지 = 자동알람(디바이스) + 사용자지정(UserTag) 차단 수 합계.
                get blockedUserTagCount() { return this.blockMgr.ut.tags.filter(t => t.blocked).length; },
                get blockedTotalCount() { return this.blockedDeviceCount + this.blockedUserTagCount; },
                get blkFilteredDevices() {
                    let list = this.blockMgr.devices;
                    const q = (this.blockMgr.filter || '').toLowerCase().trim();
                    if (q) list = list.filter(d => d.device.toLowerCase().includes(q) || (d.paths || []).some(p => p.toLowerCase().includes(q)));
                    if (this.blockMgr.showBlockedOnly) list = list.filter(d => (d.blockedKinds || []).length > 0);
                    const col = this.blockMgr.sortCol, dir = this.blockMgr.sortDir === 'asc' ? 1 : -1;
                    return [...list].sort((a, b) => {
                        if (col === 'device') return dir * a.device.localeCompare(b.device);
                        if (col === 'path') return dir * ((a.paths[0] || '').localeCompare(b.paths[0] || ''));
                        if (col === 'blocked') return dir * ((a.blockedKinds || []).length - (b.blockedKinds || []).length);
                        return 0;
                    });
                },
                blkSort(col) {
                    if (this.blockMgr.sortCol === col) this.blockMgr.sortDir = this.blockMgr.sortDir === 'asc' ? 'desc' : 'asc';
                    else { this.blockMgr.sortCol = col; this.blockMgr.sortDir = 'asc'; }
                },
                blkSelectAll(on) {
                    const sel = { ...this.blockMgr.selected };
                    for (const d of this.blkFilteredDevices) sel[d.device] = on;
                    this.blockMgr.selected = sel;
                },
                blkToggleKind(kind, on) {
                    const i = this.blockMgr.selKinds.indexOf(kind);
                    if (on && i < 0) this.blockMgr.selKinds.push(kind);
                    else if (!on && i >= 0) this.blockMgr.selKinds.splice(i, 1);
                },
                // 알림 이력 Abnormal 행 → 디바이스 추출 (tagAddress = "WORK / DEVICE.API" 의 마지막 세그먼트)
                rowDevice(a) {
                    if (a.matchOp !== 'AbnormalDetect') return '';
                    const seg = (a.tagAddress || '').split(' / ').pop() || '';
                    const i = seg.lastIndexOf('.');
                    return i > 0 ? seg.slice(0, i) : '';
                },
                // presetDevice/presetKindName: 알림 행 바로가기 — 해당 디바이스+유형이 선택된 채 자동알람 탭으로 열림.
                async openBlockMgr(presetDevice, presetKindName) {
                    const m = this.blockMgr;
                    m.show = true; m.tab = 'auto'; m.msg = ''; m.err = '';
                    await Promise.all([this.loadBlockState(), this.loadUserTagBlockState()]);
                    if (presetDevice) {
                        m.selected = { [presetDevice]: true };
                        const opt = m.kindOptions.find(k => k.name === presetKindName);
                        m.selKinds = opt ? [opt.kind] : m.kindOptions.map(k => k.kind);
                    } else if (!m.selKinds.length) {
                        m.selKinds = m.kindOptions.map(k => k.kind); // 기본 = 전체 유형
                    }
                },
                // presetTagAddress: UserTag 알림 행 바로가기 — 해당 태그가 선택된 채 사용자지정 탭으로 열림.
                async openUserTagBlockMgr(presetTagAddress) {
                    const m = this.blockMgr;
                    m.show = true; m.tab = 'user'; m.ut.msg = ''; m.ut.err = '';
                    await Promise.all([this.loadBlockState(), this.loadUserTagBlockState()]);
                    if (presetTagAddress) m.ut.selected = { [presetTagAddress]: true };
                },
                async loadBlockState() {
                    const m = this.blockMgr;
                    m.loading = true;
                    try {
                        const st = await this.apiGet('/api/settings/abnormal-device-filters');
                        m.devices = st.devices || [];
                        m.kindOptions = st.kindOptions || [];
                        m.err = '';
                    } catch (e) { m.err = '차단 상태를 불러오지 못했습니다: ' + e.message; }
                    finally { m.loading = false; }
                },
                // 일괄 적용: 선택 디바이스의 차단 유형에 selKinds 를 추가(add)/제거 후 전체 규칙을 교체 저장.
                // 저장 즉시 알림 이력/통계를 재조회해 숨김·해제가 바로 반영된다(다른 화면은 AbnormalDetected 트리거로 갱신).
                async applyBlock(add) {
                    const m = this.blockMgr;
                    if (m.busy) return;
                    m.busy = true; m.msg = ''; m.err = '';
                    try {
                        const sel = new Set(Object.keys(m.selected).filter(d => m.selected[d]));
                        const filters = m.devices.map(d => {
                            let kinds = [...(d.blockedKinds || [])];
                            if (sel.has(d.device)) {
                                if (add) kinds = [...new Set([...kinds, ...m.selKinds])];
                                else kinds = kinds.filter(k => !m.selKinds.includes(k));
                            }
                            return { device: d.device, kinds };
                        }).filter(f => f.kinds.length > 0);
                        const r = await this.apiPost('/api/settings/abnormal-device-filters', { filters });
                        if (!r.ok) throw new Error(r.message || '저장 실패');
                        m.msg = r.message;
                        await this.loadBlockState();
                        await this.load(true);
                    } catch (e) { m.err = '적용 실패: ' + e.message; }
                    finally { m.busy = false; }
                },

                // ── 사용자지정(UserTag) 알람 차단 ──
                get utSelectedCount() { return Object.values(this.blockMgr.ut.selected).filter(Boolean).length; },
                get utFilteredTags() {
                    const u = this.blockMgr.ut;
                    let list = u.tags;
                    const q = (u.filter || '').toLowerCase().trim();
                    if (q) list = list.filter(t => (t.name || '').toLowerCase().includes(q) || (t.tagAddress || '').toLowerCase().includes(q) || (t.systemName || '').toLowerCase().includes(q));
                    if (u.showBlockedOnly) list = list.filter(t => t.blocked);
                    return list;
                },
                get utAllSelected() { const ft = this.utFilteredTags; return ft.length > 0 && ft.every(t => this.blockMgr.ut.selected[t.tagAddress]); },
                utSelectAll(on) {
                    const sel = { ...this.blockMgr.ut.selected };
                    for (const t of this.utFilteredTags) sel[t.tagAddress] = on;
                    this.blockMgr.ut.selected = sel;
                },
                async loadUserTagBlockState() {
                    const u = this.blockMgr.ut;
                    u.loading = true;
                    try {
                        const st = await this.apiGet('/api/settings/usertag-filters');
                        u.tags = st.tags || [];
                        u.err = '';
                    } catch (e) { u.err = '차단 상태를 불러오지 못했습니다: ' + e.message; }
                    finally { u.loading = false; }
                },
                // 일괄 적용: 선택 UserTag 를 차단(add)/해제 후 전체 차단 주소 목록을 교체 저장.
                async applyUserTagBlock(add) {
                    const m = this.blockMgr, u = m.ut;
                    if (m.busy) return;
                    m.busy = true; u.msg = ''; u.err = '';
                    try {
                        const sel = new Set(Object.keys(u.selected).filter(a => u.selected[a]));
                        // 현재 차단된 주소로 시작 → 선택분을 추가/제거.
                        const blocked = new Set(u.tags.filter(t => t.blocked).map(t => t.tagAddress));
                        for (const a of sel) { if (add) blocked.add(a); else blocked.delete(a); }
                        const r = await this.apiPost('/api/settings/usertag-filters', { tagAddresses: [...blocked] });
                        if (!r.ok) throw new Error(r.message || '저장 실패');
                        u.msg = r.message;
                        await this.loadUserTagBlockState();
                        await this.load(true);
                    } catch (e) { u.err = '적용 실패: ' + e.message; }
                    finally { m.busy = false; }
                },

                // ── OEE 포맷터/톤 ──
                pct(v) { return (v == null) ? '—' : (v * 100).toFixed(1) + '%'; },
                // 표기 SSOT = shell.js window.dspFmt.dur (한국식 일/시간/분/초).
                durShort(ms) { return window.dspFmt.dur(ms); },

                // ── 설비 합산(Σ_flow) 표기 (2026-08-27) ──────────────────────────────
                // 라인(전체) 스코프의 시간값은 설비별 실측을 합산한 값이라 기간 길이를 넘는다
                // (설비 7대면 하루 최대 24h×7=168h). 이때 일(日) 단위로 올리면 '2일 20시간'이
                // "오늘 범위인데 이틀?" 로 오독되므로 시간 단위로 고정하고, 설비수·설비당 평균을
                // 함께 노출한다. 계산은 이미 [from,to] 로 클립돼 있다(OeeControllerBase npClipped).
                sumFlowCount() {
                    if (this.curFlow) return 1;
                    const o = this.oee || {}, t = this.teep || {};
                    return Math.max(0, o.cycleFlowCount || t.flowCount || 0);
                },
                // 조회 범위 스팬(ms) — 단위 전환 기준. rangeForPeriod() 는 로컬 ISO 문자열 쌍.
                _rangeSpanMs() {
                    const r = this.rangeForPeriod();
                    const s = new Date(r.from).getTime(), e = new Date(r.to).getTime();
                    return (isFinite(s) && isFinite(e) && e > s) ? (e - s) : 0;
                },
                durSum(ms) {
                    const span = this._rangeSpanMs();
                    const capHours = this.sumFlowCount() > 1 && span > 0 && span <= SUM_HOUR_UNIT_MAX_MS;
                    return window.dspFmt.dur(ms, undefined, capHours ? { maxUnit: 'h' } : undefined);
                },
                // 합산값 → 설비당 평균. 설비 1대/미상이면 null(표시 생략).
                durPerFlow(ms) {
                    const n = this.sumFlowCount();
                    return (n > 1 && ms > 0) ? window.dspFmt.dur(ms / n) : null;
                },
                // 합산값 툴팁 문구 — '설비 합산 ×7 · 설비당 평균 9시간 42분'
                sumFlowNote(ms) {
                    const per = this.durPerFlow(ms);
                    return per ? ('설비 합산 ×' + this.sumFlowCount() + ' · 설비당 평균 ' + per) : '';
                },
                dur(ms, d) {
                    // open(진행중)인데 durationMs 없으면 시작→현재 경과 근사 표기.
                    //   기간 클립(inRangeMs)이 들어오면 마감 여부와 무관하게 값이 있으므로 open 표시('~')는 상태로 판정한다.
                    if (ms != null && ms > 0) return this.durShort(ms) + (d && d.status === 'open' ? '~' : '');
                    if (d && d.status === 'open' && d.startAt) {
                        const el = Date.now() - new Date(d.startAt).getTime();
                        return el > 0 ? this.durShort(el) + '~' : '진행중';
                    }
                    return '—';
                },
                // 기간 경계를 걸친 정지인가 — 사건 전체(durationMs)와 기간 내 몫(inRangeMs)이 1분 이상 다르면 병기.
                dtClipped(d) {
                    return !!d && d.durationMs > 0 && d.inRangeMs != null && (d.durationMs - d.inRangeMs) > 60000;
                },
                dtTime(iso) {
                    if (!iso) return '—';
                    const d = new Date(iso); if (isNaN(d)) return '—';
                    const p = (x) => String(x).padStart(2, '0');
                    return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
                },
                // null 지표는 'is-na'(흐림), 산출되면 값에 따라 good/warn/bad 톤.
                // 품질 가정값(qualitySource='assumed')은 톤 미적용(중립) — 실측처럼 초록으로 보이지 않게.
                oeeTone(key) {
                    if (!this.oee) return 'is-na';
                    const v = this.oee[key];
                    if (v == null) return 'is-na';
                    if (key === 'quality' && this.oee.qualitySource === 'assumed') return '';
                    if (v >= 0.85) return 'is-good';
                    if (v >= 0.60) return 'is-warn';
                    return 'is-bad';
                },

                // ── 정지 필터 ── 상위 = 탭(비가동/비생산, isNonProd), 하위 = 고장/유지보수(비가동 탭 전용).
                //    대기(공백) 행(isWait && !isNonProd, doc/25)은 고장도 유지보수도 아니므로 하위 필터에 걸리지 않게 통과.
                get filteredDowntime() {
                    return this.downtime.filter(d => {
                        if (this.dtFilterStatus !== 'all' && d.status !== this.dtFilterStatus) return false;
                        if (this.dtTab === 'nonprod') return !!d.isNonProd;
                        if (d.isNonProd) return false;
                        if (d.isWait) return this.dtFilterFault !== 'maintenance';   // 대기(공백) — 고장 필터엔 노출(여파 추적), 유지보수 필터엔 제외
                        if (this.dtFilterFault === 'fault' && !d.isFailure) return false;
                        if (this.dtFilterFault === 'maintenance' && d.isFailure) return false;
                        return true;
                    });
                },
                // 탭 배지 건수 — 상태 필터만 반영(하위 고장/유지보수 필터와 무관, 탭 간 총량 비교용).
                get dtDownCount() {
                    return this.downtime.filter(d => !d.isNonProd
                        && (this.dtFilterStatus === 'all' || d.status === this.dtFilterStatus)).length;
                },
                get dtNonProdCount() {
                    return this.downtime.filter(d => !!d.isNonProd
                        && (this.dtFilterStatus === 'all' || d.status === this.dtFilterStatus)).length;
                },

                // ── 일괄 선택 computed ──
                // 선택/일괄 작업 범위 = 현재 필터에 보이는 행만. 필터 변경으로 화면에서 사라진 행의
                // 체크 상태는 메모리에 남지만 카운트/일괄 처리 대상에서 제외 — "N건 선택됨"이 항상 화면과 일치.
                get selectedVisibleRows() { return this.filteredDowntime.filter(d => this.selectedIds[d.id]); },
                get selectedVisibleIds() { return this.selectedVisibleRows.map(d => d.id); },
                get selectedCount() { return this.selectedVisibleRows.length; },
                // 고장/유지보수 일괄 지정 대상 = 선택 행 중 비가동 실정지만.
                // 대기(공백)·비생산 행은 '고장/유지보수' 개념 자체가 없어(건수·MTBF 미반영) 대상에서 뺀다 —
                // 버튼에 이 수를 병기해 "6건 선택했는데 4건만 바뀜"이 사후 놀람이 되지 않게 한다.
                get bulkFaultTargets() { return this.selectedVisibleRows.filter(d => !d.isNonProd && !d.isWait); },
                get allFilteredSelected() {
                    const fd = this.filteredDowntime;
                    return fd.length > 0 && fd.every(d => this.selectedIds[d.id]);
                },
                get someFilteredSelected() { return this.filteredDowntime.some(d => this.selectedIds[d.id]); },
                get anySelectedOpen() {
                    return this.filteredDowntime.some(d => this.selectedIds[d.id] && d.status === 'open');
                },
                toggleSel(id, checked) {
                    this.selectedIds = { ...this.selectedIds, [id]: checked };
                },
                toggleAll(checked) {
                    const patch = {};
                    for (const d of this.filteredDowntime) patch[d.id] = checked;
                    this.selectedIds = { ...this.selectedIds, ...patch };
                },
                clearSel() { this.selectedIds = {}; },

                // ── P5 이식: 칩/단서/파이프라인/계획시간 체인/히스토그램 ──
                esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); },
                // 감지 출처 칩 (정지 구간 소스)
                detectChipHtml(s) {
                    const m = { 'nocycle': '무가동', 'fault-bit': '고장비트', 'usertag': '고장비트', 'manual': '수동', 'over-cycle': '이상치초과' };
                    if (typeof s === 'string' && s.includes('+'))   // 같은 정지 이중 감지 병합(무가동+이상치초과, doc/25)
                        return `<span class="src-chip detect" title="무가동 이벤트와 이상치 초과 사이클이 같은 정지를 동시 감지 — 한 줄로 병합">${s.split('+').map(x => m[x] || this.esc(x)).join('+')}</span>`;
                    return `<span class="src-chip detect">${m[s] || this.esc(s) || '—'}</span>`;
                },
                // 합성(사이클 유래) 행 = DB 이벤트가 아니라 분류/마감 불가. id 음수로 표식.
                isSyntheticDt(d) { return !d || d.id <= 0 || d.detectSource === 'over-cycle'; },
                // 단서 칩 (abnormal/usertag 시간겹침 — 표시 전용)
                clueHtml(c) {
                    if (!c) return '<span class="clue-none">—</span>';
                    const cls = c.src === 'abnormal' ? 'abn' : 'ut';
                    const tag = c.src === 'abnormal' ? 'ABN' : 'UT';
                    return `<span class="clue-chip ${cls}"><span class="material-icons">troubleshoot</span>${this.esc(c.label)}<span class="csrc">${tag}</span></span>`;
                },
                // 표준 가동시간(가동시간 이상치) 출처 칩 — 수동 고정 vs 14일 평균(자동). p10/중앙값 자동기입은 사이클 OEE 미사용.
                ctSrcChip(row) {
                    return row.isManual
                        ? '<span class="src-chip manual">수동 고정</span>'
                        : '<span class="src-chip auto">14일 평균</span>';
                },
                // 성능 P 손실 분해 — P 값은 그대로 두고 "무엇이 깎았는지"만 병기(2026-08-21).
                //   L      = Σ실측CT − N × 표준CT
                //   L_MT   = Σ실측MT − N × (표준CT × 동작비중)
                //   L_WT   = Σ실측WT − N × (표준CT × (1−동작비중))
                //   ct = mt + wt 가 행마다 성립하고 표준MT+표준WT=표준CT 이므로 L = L_MT + L_WT 가 정확히 성립한다.
                //   부호가 반대면(한쪽 +, 한쪽 −) 상쇄 — P 가 100% 여도 동작이 열화 중일 수 있어 따로 알린다.
                get perfLoss() {
                    const o = this.oee || {};
                    const n = Math.max(0, o.normalCycleCount || 0);
                    const stdCt = Math.max(0, o.ctThresholdMs || 0);
                    const ratio = (typeof o.mtRatio === 'number') ? Math.min(1, Math.max(0, o.mtRatio)) : null;
                    const mt = Math.max(0, o.normalMtMs || 0), wt = Math.max(0, o.normalWtMs || 0);
                    if (!n || !stdCt || ratio == null || (mt + wt) <= 0) return { has: false };
                    const lossMt = mt - n * stdCt * ratio;
                    const lossWt = wt - n * stdCt * (1 - ratio);
                    const loss = lossMt + lossWt;
                    const offset = (lossMt > 0 && lossWt < 0) || (lossMt < 0 && lossWt > 0);
                    return {
                        has: true, loss, lossMt, lossWt, offset,
                        // 상쇄가 아닐 때만 "손실 중 몇 %가 동작 탓인지"가 의미를 가진다.
                        mtShare: (!offset && Math.abs(loss) > 0) ? Math.round(Math.abs(lossMt) / Math.abs(loss) * 100) : null,
                    };
                },
                // 부호 있는 시간 표기 — +38분 / −12분
                signedDur(ms) { return (ms >= 0 ? '+' : '−') + this.durShort(Math.abs(ms)); },

                // 가용성 분해 — 상단 A KPI·정지 도넛과 항상 일치(같은 입력).
                get availComp() {
                    // CT축 누적 정산(2026-08-21) — 분모 = Σ정상CT + Σ비가동CT + Σ대기CT. 상단 A KPI 와 동일 SSOT.
                    //   벽시계 모델을 걷어낸 이유: 분자(사이클)와 분모(달력)가 다른 축이라 미분류 잔여가 생기고,
                    //   사이클 0건이면 달력근사가 A=100% 를 만들어냈다. 같은 축이면 잔여가 정의상 0이고
                    //   0건이면 그냥 산출 불가다. "얼마나 수집했나"는 아래 데이터 무결성 카드가 따로 보고한다.
                    const o = this.oee || {};
                    const r1 = (x) => Math.round(x * 10) / 10;
                    // 구간 union 총량 — CT 단순 합은 오염 시 서로 겹쳐 달력을 넘는다(A 산출과 동일 입력).
                    const run = Math.max(0, o.runWallMs || 0);
                    const idle = Math.max(0, o.idleCalendarMs || 0);
                    const wait = Math.max(0, o.waitSlackWallMs || 0);   // 대기(고장 여파)
                    const maint = Math.min(idle, Math.max(0, o.idleMaintCtMs || 0));
                    const fault = Math.max(0, idle - maint);
                    const denom = run + idle + wait;
                    const pct = (x) => denom > 0 ? r1(x / denom * 100) : 0;
                    const failCount = Math.max(0, o.failureCount || 0);
                    const cycles = Math.max(0, o.normalCycleCount || 0);
                    return {
                        hasData: denom > 0,
                        runMs: run, runPct: pct(run),
                        faultMs: fault, faultPct: pct(fault),
                        maintMs: maint, maintPct: pct(maint),
                        waitMs: wait, waitPct: pct(wait),
                        stopMs: fault + maint, stopPct: r1(pct(fault) + pct(maint)),
                        denomMs: denom,
                        runLabel: '가동 (정상 가동시간 합)',
                        stopLabel: '비가동 · 고장',
                        runNote: cycles + '회', stopNote: failCount + '건',
                        subtitle: 'Σ정상 가동시간 ÷ (Σ정상 + Σ비가동 + Σ대기) — 수집된 가동만 근거',
                    };
                },
                // 계획시간 폴백 체인 3단계 (활성/건너뜀/대기)
                // 계획시간 폴백 체인 UI(planChainSteps/histBars/planChainFoot)는 2026-08-21 제거 —
                // A 가 CT축 단일모델이 되며 시프트·자동추정·달력 폴백 자체가 없어졌고, 소비하는 마크업도 0건이었다.

                // ── 정지 이벤트 로그 (도넛 [로그 보기 및 설정]) — 팝업 다이얼로그(showDowntimeLog) ──
                // 버튼에서 직접 show=true; 로 여는 것이 정본이나 하위호환용 토글 유지.
                toggleDowntimeLog() {
                    this.showDowntimeLog = !this.showDowntimeLog;
                },

                // ── 품질(양품률) 직접 입력 다이얼로그 (품질 Q 카드 클릭) — 전반 품질% 직접 설정(전역) ──
                openQDialog() {
                    const d = this.qDialog;
                    d.show = true; d.msg = ''; d.err = '';
                    // 현재 적용 중인 품질을 입력칸 기본값으로(없으면 100).
                    d.qualityPct = (this.oee && this.oee.quality != null) ? +(this.oee.quality * 100).toFixed(1) : 100;
                },
                get qDialogHint() {
                    const v = Math.min(100, Math.max(0, Number(this.qDialog.qualityPct) || 0));
                    return `불량률 약 ${(100 - v).toFixed(1)}% · OEE = 가용성 × 성능 × ${v.toFixed(1)}%`;
                },
                async submitQuality() {
                    const d = this.qDialog;
                    d.msg = ''; d.err = '';
                    const pct = Math.min(100, Math.max(0, Number(d.qualityPct) || 0));
                    d.busy = true;
                    try {
                        await this.apiPost('/api/oee/quality', { qualityPercent: pct });
                        d.msg = `적용됨 — 전반 품질 ${pct.toFixed(1)}%`;
                        clearTimeout(this._prodMsgTimer);
                        this._prodMsgTimer = setTimeout(() => { d.msg = ''; }, 5000);
                        await this.loadOee();
                    } catch (ex) { d.err = '저장 실패: ' + ex.message; }
                    finally { d.busy = false; }
                },
                async clearManualQuality() {
                    const d = this.qDialog;
                    d.msg = ''; d.err = '';
                    d.busy = true;
                    try {
                        await this.apiPost('/api/oee/quality', { qualityPercent: null });
                        d.msg = '해제됨 — 불량 입력 기반(미입력 시 100% 가정)으로 복귀';
                        clearTimeout(this._prodMsgTimer);
                        this._prodMsgTimer = setTimeout(() => { d.msg = ''; }, 5000);
                        await this.loadOee();
                        d.qualityPct = (this.oee && this.oee.quality != null) ? +(this.oee.quality * 100).toFixed(1) : 100;
                    } catch (ex) { d.err = '해제 실패: ' + ex.message; }
                    finally { d.busy = false; }
                },
                // 분류/마감 성공 피드백 — 잠깐 표시 후 자동 소거 (실패는 기존 oeeError 경로)
                flashDtMsg(msg) {
                    this.dtMsg = msg;
                    clearTimeout(this._dtMsgTimer);
                    this._dtMsgTimer = setTimeout(() => { this.dtMsg = ''; }, 4000);
                },
                // 고장/유지보수 일괄 지정 — 실 이벤트 행은 서버 벌크 엔드포인트 1회, 합성 행(id<0, 이상치 초과
                // 사이클)은 벌크가 id 만 받아 처리 못 하므로 단건 set-fault 로 materialize 하며 순차 처리(doc/25).
                async bulkSetFault(isFault) {
                    const rows = this.bulkFaultTargets;
                    if (!rows.length || this.bulkBusy) return;
                    const ids = rows.filter(d => d.id > 0).map(d => d.id);
                    const synth = rows.filter(d => d.id <= 0);
                    this.bulkBusy = true; this.bulkProgress = '';
                    try {
                        if (ids.length) await this.apiPost('/api/oee/downtime/bulk-set-fault', { ids, isFault });
                        for (let i = 0; i < synth.length; i++) {
                            const d = synth[i];
                            this.bulkProgress = `확정 중 ${ids.length + i + 1}/${rows.length}`;
                            await this.apiPost('/api/oee/downtime/' + d.id + '/set-fault', {
                                isFault, flow: d.flowName || null, startAt: d.startAt || null, endAt: d.endAt || null,
                            });
                        }
                        this.clearSel();
                        await this.loadOee();
                        this.flashDtMsg(`${rows.length}건 → ${isFault ? '고장' : '유지보수'} 일괄 적용`);
                    } catch (e) {
                        this.oeeError = '일괄 변경 실패: ' + e.message;
                        await this.loadOee();   // 부분 적용분이 화면에 반영되도록 재조회
                    } finally { this.bulkBusy = false; this.bulkProgress = ''; }
                },
                // 비생산↔비가동 일괄 이동 — 단건 reclassify 를 순차 호출(합성 행 materialize·감지로그 청소·
                // 이전 분류 복원 semantics 를 서버 단건 경로와 100% 동일하게 유지하려고 벌크 엔드포인트를 두지 않음).
                async bulkReclassify(toNonProd) {
                    const rows = this.selectedVisibleRows;
                    if (!rows.length || this.bulkBusy) return;
                    this.bulkBusy = true; this.bulkProgress = '';
                    let done = 0;
                    try {
                        for (const d of rows) {
                            this.bulkProgress = `이동 중 ${done + 1}/${rows.length}`;
                            await this.apiPost('/api/oee/downtime/reclassify', {
                                id: d.id > 0 ? d.id : null,
                                flow: d.flowName || null,
                                startAt: d.startAt || null,
                                endAt: d.endAt || null,
                                toNonProd,
                            });
                            done++;
                        }
                        this.clearSel();
                        await this.loadOee();
                        this.dtTab = toNonProd ? 'nonprod' : 'down';
                        this.flashDtMsg(`${done}건 → ${toNonProd ? '비생산 탭으로 이동 (A 분모 밖)' : '비가동 탭으로 이동 (이전 분류 복원)'}`);
                    } catch (e) {
                        this.oeeError = `일괄 이동 실패(${done}/${rows.length}건 처리됨): ` + e.message;
                        await this.loadOee();
                    } finally { this.bulkBusy = false; this.bulkProgress = ''; }
                },
                async bulkClose() {
                    const ids = this.filteredDowntime.filter(d => this.selectedIds[d.id] && d.status === 'open').map(d => d.id);
                    if (!ids.length) return;
                    if (!confirm(ids.length + '건의 진행중 정지를 마감 처리할까요?\n마감 후에는 화면에서 되돌릴 수 없습니다.')) return;
                    this.bulkBusy = true;
                    try {
                        await this.apiPost('/api/oee/downtime/bulk-close', { ids });
                        this.clearSel();
                        await this.loadOee();
                        this.flashDtMsg(`${ids.length}건 마감 처리됨`);
                    } catch (e) {
                        this.oeeError = '일괄 마감 실패: ' + e.message;
                    } finally { this.bulkBusy = false; }
                },

                // 정지 구성 도넛 (고장/유지보수/비생산) — 벽시계 단일모델(2026-07-06): 서버가 비가동을 유지보수/감지 정지
                // 이벤트 구간과 겹쳐 귀속한 값 + 비생산 벽시계(nonProdWallMs, A 분모 밖 — 2026-07-08 당일 판정 모델로
                // 정지 구성에 포함). 가용성 정산 분해·시간별 추이 정지부와 동일 소스라 세 뷰가 항상 일치한다.
                // 가동간 공백(감지 정지에 안 덮인 잔여 비가동, 2026-07-14)은 정지가 아니므로 도넛에 넣지 않는다.
                get faultDist() {
                    const o = this.oee || {};
                    const run = Math.max(0, o.runWallMs || 0), avail = Math.max(0, o.availableWallMs || 0);
                    const down = Math.max(0, avail - run);
                    const maintMs = Math.min(down, Math.max(0, o.downMaintWallMs || 0));
                    // 고장 = 감지 정지 이벤트에 실제로 덮인 비가동만(2026-07-14) — 가동간 공백(잔여 슬랙)은 정지 아님.
                    const faultMs = Math.min(Math.max(0, down - maintMs), Math.max(0, o.downFaultWallMs || 0));
                    // 비생산을 [일반 비생산 / 대기(고장 여파, doc/25)] 로 분화 — 대기는 계산상 비생산(분모 밖)이지만
                    // "계획 외 유휴"가 아니라 "라인 고장 때문에 선 시간"이므로 세그먼트를 분리해 사건이 보이게 한다.
                    const waitMs = Math.min(Math.max(0, o.waitWallMs || 0), Math.max(0, o.nonProdWallMs || 0));
                    const nonProdMs = Math.max(0, (o.nonProdWallMs || 0) - waitMs);
                    // 정지건수 = 벽시계 감지 정지 이벤트 수(요약 KPI failureCount = 가용성 정산 바의 'N건'과 동일 소스).
                    //   failureCount 는 이상치초과 사이클 + 무사이클 갭을 센다(this.downtime 로그 테이블엔 무사이클/고장비트만
                    //   적재돼 로그 0 이어도 실제 정지는 있을 수 있으므로 로그 행 수는 쓰지 않는다).
                    const count = Math.max(0, o.failureCount || 0);
                    const totalMs = faultMs + maintMs + nonProdMs + waitMs;
                    // 표시 게이트: 정지 이벤트(failureCount)나 비생산/대기가 하나라도 있으면 표시(고장=귀속값이라 공백은 이미 제외).
                    if (totalMs <= 0 || (count <= 0 && nonProdMs <= 0 && waitMs <= 0)) return { count, has: false, segs: [] };
                    const C = 2 * Math.PI * 38;
                    const segs = [];
                    const defs = [{ def: FAULT_DEF, ms: faultMs }, { def: MAINT_DEF, ms: maintMs }, { def: NONPROD_DEF, ms: nonProdMs }, { def: WAIT_DEF, ms: waitMs }].filter(x => x.ms > 0);
                    let prior = 0;
                    for (const { def, ms } of defs) {
                        const len = ms / totalMs * C, gap = C - len, offset = -(prior / totalMs * C);
                        prior += ms;
                        segs.push({ label: def.label, color: def.color, cls: def.cls, pat: def.pat, ms, share: Math.round(ms * 100 / totalMs),
                                    dash: len.toFixed(2) + ' ' + gap.toFixed(2), offset: offset.toFixed(2) });
                    }
                    return { count, has: true, segs };
                },
                // faultDist → 도넛 내부 SVG 문자열 (x-html)
                get faultDonutSvg() {
                    const d = this.faultDist;
                    // 정지 유형 대각선 빗금 패턴(색=유형, 빗금=정지 신호 — 가동 솔리드와 대비). userSpaceOnUse 로 링 전체에 타일링.
                    const pat = (id, color) => `<pattern id="${id}" patternUnits="userSpaceOnUse" width="7" height="7">`
                        + `<rect width="7" height="7" fill="${color}"></rect>`
                        + `<path d="M0,7 L7,0 M-1.5,1.5 L1.5,-1.5 M5.5,8.5 L8.5,5.5" stroke="rgba(255,255,255,0.55)" stroke-width="1.3"></path></pattern>`;
                    let s = `<defs>${pat('up-pat-fault', 'var(--oee-fault)')}${pat('up-pat-maint', 'var(--oee-maint)')}${pat('up-pat-nonprod', 'var(--nonprod)')}${pat('up-pat-wait', 'var(--oee-slack, #7dd3fc)')}</defs>`;
                    s += '<circle class="up-donut-track" cx="50" cy="50" r="38" fill="none" stroke-width="14"></circle>';
                    // 고장/유지보수 세그는 클릭 드릴다운(날짜별 비가동 패턴) 대상 — data-seg 로 onFaultDonutClick 이 식별.
                    for (const seg of d.segs) {
                        const segKey = seg.pat === 'up-pat-fault' ? 'fault' : (seg.pat === 'up-pat-maint' ? 'maint'
                            : (seg.pat === 'up-pat-wait' ? 'wait' : 'nonprod'));
                        const drill = segKey === 'fault' || segKey === 'maint';   // 대기/비생산은 드릴다운 비대상
                        const click = drill ? ` data-seg="${segKey}" style="cursor:pointer;"` : '';
                        s += `<circle cx="50" cy="50" r="38" fill="none" stroke="url(#${seg.pat})" stroke-width="14" stroke-dasharray="${seg.dash}" stroke-dashoffset="${seg.offset}" transform="rotate(-90 50 50)"${click}><title>${this.esc(seg.label)}${drill ? ' — 클릭 → 날짜별 비가동 패턴' : ''}</title></circle>`;
                    }
                    s += `<text class="up-donut-total" x="50" y="49" text-anchor="middle">${d.count}</text>`;
                    s += '<text class="up-donut-cap" x="50" y="61" text-anchor="middle">정지건수</text>';
                    return s;
                },

                // ── 날짜별 비가동 패턴 (드릴다운) — 가용성 정산 빨간부·정지 도넛/범례(고장·유지보수) 클릭으로 토글 ──
                // '날짜별 비생산 패턴'(uptime-teep)과 같은 up-npd 골격이되 소스가 다르다: 비생산=서버 days 접기,
                // 여기는 이미 로드된 정지 이벤트(this.downtime — 기간·설비 필터로 조회됨)를 클라에서 날짜별로 접는다.
                // 겹침(여러 설비 동시 정지)은 병합하지 않는다 — 일 합계 = Σ이벤트 지속시간(페이지의 '설비 합산' 규약과 정합).
                openDtPattern(filter) {
                    this.dtPat.filter = filter || 'all';
                    this.dtPat.show = true;
                    this.$nextTick(() => {
                        const el = document.getElementById('downtime-pattern-section');
                        if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                    });
                },
                // 도넛 세그먼트 클릭 — faultDonutSvg 가 circle 에 data-seg 를 심어둠. 비생산 세그는 대상 아님
                // (그 패턴은 생산효율 페이지의 '날짜별 비생산 패턴' 담당 — 여기는 A 분모 안의 비가동만).
                onFaultDonutClick(ev) {
                    const seg = ev.target && ev.target.dataset ? ev.target.dataset.seg : null;
                    if (seg === 'fault') this.openDtPattern('fault');
                    else if (seg === 'maint') this.openDtPattern('maintenance');
                },
                onFaultLegendClick(seg) {
                    if (seg.pat === 'up-pat-fault') this.openDtPattern('fault');
                    else if (seg.pat === 'up-pat-maint') this.openDtPattern('maintenance');
                },
                // 패턴 대상 이벤트 — 비가동만(비생산·대기 제외, doc/25) + 하위 필터(고장/유지보수)
                dtPatEvents() {
                    return this.downtime.filter(d => !d.isNonProd && !d.isWait
                        && (this.dtPat.filter === 'all' || (this.dtPat.filter === 'fault' ? !!d.isFailure : !d.isFailure)));
                },
                // 날짜 행 배열(최근이 위) — 각 이벤트 [startAt, endAt|now] 를 기간으로 클립 후 로컬 자정 경계로 접고,
                // 그 날의 비생산∪미계측 창(ps.actualNonProd.days — 가용성 정산이 A 분모에서 빼는 것과 동일 소스)을
                // 차집합으로 뺀다. 안 빼면 주말·무오더 장기 정지가 통째로 빨갛게 나와 정산 바의 비가동(2%대)과 어긋난다.
                // Alpine 렌더마다 재호출되므로 (downtime/actualNonProd identity, 필터, 기간) 메모로 재계산 억제 —
                // 10초 폴링이 downtime 을 새 배열로 교체하면 자동 무효화(진행중 이벤트의 '현재까지' 연장도 그때 반영).
                dtPatDays() {
                    const src = this.downtime, anp = this.ps.actualNonProd, r = this.rangeForPeriod();
                    const memoKey = this.dtPat.filter + '|' + r.from + '|' + r.to.slice(0, 10);
                    if (this._dtPatMemo && this._dtPatMemo.src === src && this._dtPatMemo.anp === anp && this._dtPatMemo.key === memoKey)
                        return this._dtPatMemo.days;
                    const now = Date.now();
                    const fromMs = new Date(r.from).getTime();
                    const toParsed = new Date(r.to).getTime();
                    const toMs = Math.min(isNaN(toParsed) ? now : toParsed, now);
                    const evs = [];
                    for (const d of this.dtPatEvents()) {
                        const s = new Date(d.startAt).getTime();
                        const e = d.endAt ? new Date(d.endAt).getTime() : now;
                        if (isNaN(s) || isNaN(e) || e <= s) continue;
                        const cs = Math.max(s, fromMs), ce = Math.min(e, toMs);
                        if (ce > cs) evs.push({ s: cs, e: ce, isFailure: !!d.isFailure, flow: d.flowName || d.systemName || '', open: d.status === 'open' });
                    }
                    // 날짜 → 그 날 제외(비생산∪미계측) 창 — 서버 FoldToDay 산출이라 이미 서로소·오름차순.
                    const cutsByDate = {};
                    for (const nd of (anp && anp.days) || [])
                        cutsByDate[String(nd.date).slice(0, 10)] = nd.windows || [];
                    // [s,e)분 구간에서 cuts(서로소 오름차순, 분 단위)를 뺀 잔여 구간들
                    const subtract = (s, e, cuts) => {
                        const out = []; let cur = s;
                        for (const c of cuts) {
                            if (c.endMinutes <= cur) continue;
                            if (c.startMinutes >= e) break;
                            if (c.startMinutes > cur) out.push([cur, Math.min(c.startMinutes, e)]);
                            cur = Math.max(cur, c.endMinutes);
                            if (cur >= e) break;
                        }
                        if (cur < e) out.push([cur, e]);
                        return out;
                    };
                    const rows = [];
                    const cur = new Date(fromMs); cur.setHours(0, 0, 0, 0);
                    const last = new Date(toMs); last.setHours(0, 0, 0, 0);
                    const p = (x) => String(x).padStart(2, '0');
                    while (cur.getTime() <= last.getTime()) {
                        const dayStart = cur.getTime();
                        const next = new Date(cur); next.setDate(next.getDate() + 1);
                        const dayEnd = next.getTime();
                        const dateKey = `${cur.getFullYear()}-${p(cur.getMonth() + 1)}-${p(cur.getDate())}`;
                        const cuts = cutsByDate[dateKey] || [];
                        const windows = [];
                        for (const ev of evs) {
                            const ws = Math.max(ev.s, dayStart), we = Math.min(ev.e, dayEnd);
                            if (we <= ws) continue;
                            for (const [ss, ee] of subtract((ws - dayStart) / 60000, (we - dayStart) / 60000, cuts)) {
                                ev.used = true; // 비생산 차감 후에도 남는 이벤트만 건수로 셈(정산 바 'N건'과 결이 같게)
                                windows.push({
                                    startMinutes: ss, endMinutes: ee, ms: (ee - ss) * 60000,
                                    cls: ev.isFailure ? 'hatch-fault' : 'hatch-maint',
                                    kind: (ev.isFailure ? '고장' : '유지보수') + (ev.open ? ' (진행중)' : ''), flow: ev.flow,
                                });
                            }
                        }
                        windows.sort((a, b) => a.startMinutes - b.startMinutes);
                        rows.push({ date: dateKey, windows });
                        cur.setDate(cur.getDate() + 1);
                    }
                    rows.reverse();
                    const daysClipped = rows.length > 92; // 커스텀 초장기 기간 안전판 — 서버 비생산 패턴의 92일 클립과 동일 규약
                    const days = daysClipped ? rows.slice(0, 92) : rows;
                    this._dtPatMemo = { src, anp, key: memoKey, days, daysClipped, evCount: evs.filter(x => x.used).length };
                    return days;
                },
                dtPatDense() { return this.dtPatDays().length > 21; },
                dtPatDayMs(d) { return (d.windows || []).reduce((a, w) => a + w.ms, 0); },
                dtPatWinTitle(d, w) {
                    return this.teepNpDayInfo(d).label + ' ' + this.minToHHMM(w.startMinutes) + '–' + this.minToHHMM(w.endMinutes)
                        + ' ' + w.kind + (w.flow ? ' · ' + w.flow : '') + ' (' + this.durShort(w.ms) + ')';
                },
                dtPatSub() {
                    const days = this.dtPatDays();
                    if (!days.length) return '';
                    const total = days.reduce((a, d) => a + this.dtPatDayMs(d), 0);
                    const f = { all: '비가동', fault: '고장', maintenance: '유지보수' }[this.dtPat.filter] || '비가동';
                    return (days.length === 1 ? '1일 × 24시간' : days.length + '일 × 24시간 · 최근이 위')
                        + ' · ' + f + ' ' + (this._dtPatMemo ? this._dtPatMemo.evCount : 0) + '건 · ' + (total > 0 ? this.durShort(total) : '0')
                        + (this._dtPatMemo && this._dtPatMemo.daysClipped ? ' · 최근 92일만 표시' : '');
                },

                // ── 순위 메달 ──
                medal(i) { return i === 0 ? '🥇' : i === 1 ? '🥈' : i === 2 ? '🥉' : (i + 1); },

                // ── Flow 이름 목록 (입력 datalist 자동완성) ──
                get flowNames() {
                    const s = new Set();
                    for (const d of this.downtime) { if (d.flowName) s.add(d.flowName); }
                    for (const r of this.ranking) { if (r.flowName) s.add(r.flowName); }
                    for (const r of this.ctTable) { if (r.flowName) s.add(r.flowName); }
                    return [...s].sort();
                },

                // ── 규칙기반 AI 인사이트 한 줄 ──
                get insight() {
                    const scored = this.ranking.filter(x => x.oee != null);
                    let worst = null;
                    if (scored.length > 0) worst = scored.reduce((a, b) => b.oee < a.oee ? b : a);
                    else if (this.ranking.length > 0) worst = this.ranking.reduce((a, b) => b.downtimeMs > a.downtimeMs ? b : a);
                    if (!worst) return null;
                    const oeeTxt = worst.oee != null ? ('OEE ' + this.pct(worst.oee)) : ('정지 ' + this.durShort(worst.downtimeMs));
                    let s = `정지 기준 가장 취약한 설비는 <b>${worst.flowName}</b> (${oeeTxt}, 정지 ${worst.downtimeCount}건) 입니다.`;
                    const d = this.faultDist;
                    const faultSeg = d.segs.find(x => x.label === FAULT_DEF.label);
                    const maintSeg = d.segs.find(x => x.label === MAINT_DEF.label);
                    if (faultSeg && faultSeg.share > 0) s += ` 정지의 ${faultSeg.share}%가 고장입니다.`;
                    if (maintSeg && maintSeg.share > 0) s += ` ${maintSeg.share}%는 유지보수입니다.`;
                    if (this.oee && this.oee.qualitySource === 'measured' && this.oee.quality != null && this.oee.quality < 0.98)
                        s += ` 품질 ${this.pct(this.oee.quality)}(불량 입력 반영) 가 OEE 손실에 기여합니다.`;
                    else if (this.oee && this.oee.qualitySource === 'assumed')
                        s += ` 품질은 100% 가정 중 — 불량율/불량수를 입력하면 OEE 가 측정값으로 정밀화됩니다.`;
                    return s;
                },

                // ── 인터랙션: 고장/유지보수 토글 / 비생산↔비가동 보내기 / 수동마감 / 불량 / 표준 가동시간 ──
                async setFault(d, isFault) {
                    try {
                        // 합성 행(id<0, 이상치 초과 사이클)은 flow/start/end 로 서버가 실제 이벤트 행을 만들어 분류(doc/25).
                        await this.apiPost('/api/oee/downtime/' + d.id + '/set-fault', {
                            isFault,
                            flow: d.id > 0 ? null : (d.flowName || null),
                            startAt: d.id > 0 ? null : (d.startAt || null),
                            endAt: d.id > 0 ? null : (d.endAt || null),
                        });
                        await this.loadOee();
                        this.flashDtMsg(`${d.flowName || d.systemName || ''} → ${isFault ? '고장' : '유지보수'}`);
                    } catch (e) {
                        this.oeeError = '변경 실패: ' + e.message;
                    }
                },
                // 비생산↔비가동 보내기 — 당일 자동 판정을 행 단위로 사용자 확정(서버 classifySource='manual' 오버라이드).
                // 합성 행(id<0, 계산 유래)은 flow/start/end 로 서버가 실제 이벤트 행을 만들어 확정한다.
                // 성공 시 대상 탭으로 자동 이동 — 옮겨진 행을 그 자리에서 확인·판단(비가동 복귀는 이전 유지보수/고장 분류 복원).
                async reclassifyDt(d, toNonProd) {
                    this.dtReclassBusy = true;
                    try {
                        await this.apiPost('/api/oee/downtime/reclassify', {
                            id: d.id > 0 ? d.id : null,
                            flow: d.flowName || null,
                            startAt: d.startAt || null,
                            endAt: d.endAt || null,
                            toNonProd,
                        });
                        await this.loadOee();
                        this.dtTab = toNonProd ? 'nonprod' : 'down';
                        this.flashDtMsg(`${d.flowName || d.systemName || ''} → ${toNonProd ? '비생산 탭으로 이동 (A 분모 밖)' : '비가동 탭으로 이동 (이전 분류 복원)'}`);
                    } catch (e) {
                        this.oeeError = '구분 변경 실패: ' + e.message;
                    } finally { this.dtReclassBusy = false; }
                },
                async closeEvent(d) {
                    if (!confirm(`'${d.flowName || d.systemName || '이 이벤트'}' 정지를 마감 처리할까요?\n마감 후에는 화면에서 되돌릴 수 없습니다.`)) return;
                    try {
                        await this.apiPost('/api/oee/downtime/' + d.id + '/close', {});
                        await this.loadOee();
                        this.flashDtMsg('마감 처리됨 — ' + (d.flowName || d.systemName || ''));
                    } catch (e) {
                        this.oeeError = '수동 마감 실패: ' + e.message;
                    }
                },
                // ── 표준 가동시간(idealCT) Flow 테이블 ──
                async loadCtTable() {
                    this.ctLoading = true;
                    try {
                        const rows = await this.apiGet('/api/oee/ideal-cycle/table');
                        // 수동 오버라이드 전용(doc/22): 기본 가동시간 이상치=14일 평균(자동). 수동값(source≠auto/auto-median)만 입력칸에 시드.
                        // 자동기입(p10) 값은 사이클 OEE 가 쓰지 않으므로 '미설정(=14일 평균)'으로 표시하고 입력칸은 비운다.
                        this.ctTable = (Array.isArray(rows) ? rows : []).map(r => {
                            const isManual = r.idealCycleTimeMs != null && r.source !== 'auto' && r.source !== 'auto-median';
                            return { ...r, isManual, draft: isManual ? r.idealCycleTimeMs / 1000 : null, _initMs: isManual ? r.idealCycleTimeMs : null };
                        });
                        this.ctError = null;
                    } catch (e) {
                        this.ctError = '표준 가동시간 테이블을 불러오지 못했습니다: ' + e.message;
                    } finally { this.ctLoading = false; }
                },
                // draft(입력, 초) 정규화: 양수면 ms 정수(×1000), 아니면 null(해제 → 14일 평균)
                ctNorm(v) { return (v != null && v > 0) ? Math.round(v * 1000) : null; },
                // 변경 = 정규화된 입력값이 초기 수동값과 다르면(값 입력/수정 또는 비워서 해제).
                rowChanged(row) { return this.ctNorm(row.draft) !== (row._initMs ?? null); },
                flashCtMsg(msg) {
                    this.ctMsg = msg;
                    clearTimeout(this._ctMsgTimer);
                    this._ctMsgTimer = setTimeout(() => { this.ctMsg = ''; }, 5000);
                },
                async applyAllIdealCt() {
                    this.ctMsg = ''; this.ctError = null;
                    const items = this.ctTable
                        .filter(r => this.rowChanged(r))
                        .map(r => {
                            const ms = this.ctNorm(r.draft);
                            return ms != null
                                ? { flow: r.flowName, idealCycleTimeMs: ms, mode: 'manual' }      // 값 입력 = 수동 고정
                                : { flow: r.flowName, idealCycleTimeMs: null, mode: 'auto' };      // 비움 = 14일 평균(자동)으로 해제
                        });
                    if (items.length === 0) { this.flashCtMsg('변경된 항목이 없습니다.'); return; }
                    this.ctApplying = true;
                    try {
                        const res = await this.apiPost('/api/oee/ideal-cycle/batch', { items });
                        this.flashCtMsg(`${res.count}개 Flow 표준 가동시간 일괄 적용됨`);
                        await this.loadCtTable();
                        await this.loadOee();
                    } catch (e) {
                        this.ctError = '일괄 적용 실패: ' + e.message;
                    } finally { this.ctApplying = false; }
                }
            };
        }

        // x축(category) 눈금 라벨: 첫·마지막은 항상 표시, 나머지는 균등 간격. autoSkip 이 끝 눈금을
        // 보장하지 않아 마지막 날짜/시각 라벨이 잘려 안 보이던 문제 해결.
        function _edgeTickCallback(value, index, ticks) {
            const n = ticks.length;
            if (n <= 1 || index === 0 || index === n - 1) return this.getLabelForValue(value);
            const step = Math.max(1, Math.ceil(n / 12));
            return index % step === 0 ? this.getLabelForValue(value) : '';
        }

        function _dailyChartOptions(granularity, yMax) {
            const cs = getComputedStyle(document.documentElement);
            const cText = cs.getPropertyValue('--color-text-secondary').trim() || '#888';
            const cGrid = cs.getPropertyValue('--color-lines').trim() || '#e5e7eb';
            return {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                plugins: {
                    legend: {
                        position: 'top',
                        align: 'end',
                        labels: { color: cText, boxWidth: 12, boxHeight: 12, font: { size: 11 } },
                        onClick(e, item, legend) {
                            Chart.defaults.plugins.legend.onClick.call(legend.chart, e, item, legend);
                        },
                    },
                    tooltip: {
                        mode: 'index',
                        callbacks: {
                            label(ctx) {
                                const v = ctx.parsed.y;
                                if (v == null || v <= 0) return null;
                                return `${ctx.dataset.label}: ${window.dspFmt.durHours(v)}`;
                            },
                        },
                    },
                },
                scales: {
                    x: {
                        stacked: true,
                        ticks: { color: cText, font: { size: 11 }, maxRotation: granularity === 'hour' ? 45 : 0, autoSkip: false, callback: _edgeTickCallback },
                        grid: { color: cGrid },
                    },
                    y: {
                        stacked: true,
                        min: 0,
                        // 고정 상한: 시간별=1h, 일별=24h (전체=×설비수). 없으면 자동(하위호환).
                        ...(yMax > 0 ? { max: yMax } : {}),
                        ticks: {
                            color: cText,
                            font: { size: 11 },
                            callback: v => v + '시간',
                        },
                        grid: { color: cGrid },
                    },
                },
            };
        }
