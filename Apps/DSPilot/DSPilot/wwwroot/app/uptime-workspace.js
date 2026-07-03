        // 정지 2-상태: 고장(isFault=true) / 유지보수(isFault=false). isFailure 필드에 대응.
        const FAULT_DEF = { label: '고장', color: 'var(--oee-fault)', cls: 'hatch-fault', pat: 'up-pat-fault' };
        const MAINT_DEF = { label: '유지보수', color: 'var(--oee-maint)', cls: 'hatch-maint', pat: 'up-pat-maint' };

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

        function uptimeApp() {
            return {
                // 기존 UserTag 상태 (+ 구 이상발생 관리 흡수: 필터/페이지/정의/차트)
                ut: null, loading: true, error: null, dark: false,
                tab: 'oee', // 'oee' | 'anomaly' — URL ?tab= 와 동기화
                // OEE 페이지 내부 탭: 'equip'(설비효율=OEE, 기존 UI) | 'teep'(생산효율=TEEP). URL ?section= 와 동기화.
                oeeTab: 'equip',
                // 물리 페이지 뷰: 'oee'(=/uptime-oee) | 'alarm'(=/uptime-alarm) | 'both'(구 통합, 폐기).
                // window.DSP_UPTIME_VIEW 로 각 HTML 이 주입. view 가 곧 표시할 도메인(탭바 없음).
                view: (window.DSP_UPTIME_VIEW || 'both'),
                period: 'today',
                curFlow: '', // '' = 라인 전체, 그 외 = 특정 Flow (OEE/정지/도넛/계획시간을 그 설비로 필터)
                rt: { connected: false },
                _conn: null, _dt: null, _pollTimer: null,
                // stale 응답 가드 — 폴링/기간변경/페이지이동 응답이 뒤늦게 도착해 최신 상태를 덮어쓰는 경합 방지
                _utSeq: 0, _oeeSeq: 0,
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
                oeeExporting: false,   // OEE Excel 내보내기 진행 중
                planTime: null, // /api/oee/plan-time — 계획시간 폴백 체인 + 14일 히스토그램
                downtime: [], ranking: [],
                dtFilterStatus: 'all', dtFilterFault: 'all', // 'all'|'fault'|'maintenance'
                dtMsg: '', _dtMsgTimer: null, _prodMsgTimer: null, _ctMsgTimer: null,
                // 일괄 선택 상태
                selectedIds: {}, bulkBusy: false,
                // 일자 기본값은 로컬 날짜 — toISOString() 은 UTC 라 KST 오전 9시 전엔 어제로 채워짐
                // 품질(양품률) 직접 입력 다이얼로그 — 전반 품질% 를 직접 설정(POST /api/oee/quality, 전역). 품질 Q 카드 클릭으로 염.
                qDialog: { show: false, qualityPct: 100, busy: false, msg: '', err: '' },
                // 오버레이 닫힘 가드 — mousedown 이 오버레이(백드롭)에서 시작했을 때만 닫는다(모달 안에서 시작→백드롭 release 드래그로 오닫힘 방지).
                _qDown: false,
                // 비생산 시간대 (doc/22 §3.3) — auto: 자동 계산(10×가동시간 장시간정지) on/off. source: auto/manual/none. ctMultiplier: 자동판정 배수(10).
                // windows=수동 편집용 사본. selected=선택된 윈도 index(-1=미선택). addMode=드래그 추가 무장.
                // actualNonProd=이번 기간 실제 제외된 비생산(추이 남색과 동일 소스, 자동 모드 타임라인의 유일한 소스이자 수동 전환 시 시드).
                ps: { source: 'none', auto: true, ctMultiplier: 10, windows: [], selected: -1, addMode: false, msg: '', err: '', busy: false, actualNonProd: null, seededFromAuto: false },
                _psDrag: null, // 진행 중 드래그 상태 { mode:'create'|'resize-l'|'resize-r', index, anchor } (비반응형)
                // 정지 이벤트 로그 토글 — 기본 숨김, 정지 원인 구성(도넛)의 [로그 보기 및 설정] 버튼으로 토글
                showDowntimeLog: false,
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
                    this.dark = localStorage.getItem('dspilot-theme') === 'dark';
                    window.addEventListener('storage', (e) => { if (e.key === 'dspilot-theme') { this.dark = e.newValue === 'dark'; this.redrawForTheme(); } });
                    // 사이드바 이상코드 피드에서 진입 시 필터 시드(/uptime?utSystem=&category=&utSearch=).
                    const qp = new URLSearchParams(location.search);
                    // 설비(Flow) 필터는 URL(?flow=)에서만 온다(좌측 메뉴 '가동시간·이상' 트리). 없으면 라인 전체.
                    if (qp.has('flow')) this.curFlow = qp.get('flow') || '';
                    if (qp.has('utSystem')) this.utSystem = qp.get('utSystem') || '';
                    if (qp.has('category')) this.utCategory = qp.get('category') || '';
                    if (qp.has('utSearch')) this.utSearch = qp.get('utSearch') || '';
                    // 피드에서 발생시각(at)을 받으면 그 '날' 하루를 custom 기간으로 맞춰 클릭한 알람이 조회 범위에 들어오게 한다.
                    // (기본 '오늘'이라 과거 알람이면 0건이 되던 문제 해결.) _focusAt 은 로드 후 그 행을 스크롤·하이라이트하는 키.
                    const at = qp.get('at'); // "yyyy-MM-dd HH:mm:ss" (초 단위) — 알림 이력 행의 occurredAtLocal.slice(0,19) 와 동일 형식
                    if (at && at.length >= 19) {
                        const day = at.slice(0, 10); // yyyy-MM-dd
                        this.period = 'custom';
                        this.customFrom = day + 'T00:00';
                        this.customTo = day + 'T23:59';
                        this._focusAt = at.slice(0, 19);
                    }
                    const seeded = qp.has('utSystem') || qp.has('category') || qp.has('utSearch') || qp.has('at');
                    // 활성 탭: 물리 분리 페이지는 view 가 결정(탭바 없음). 구 통합(both)만 URL ?tab=/시드로 분기.
                    this.tab = this.view === 'oee' ? 'oee'
                        : this.view === 'alarm' ? 'anomaly'
                        : ((qp.get('tab') === 'anomaly' || seeded || qp.has('blockMgr')) ? 'anomaly' : 'oee');
                    this.syncTabUrl();
                    // OEE 페이지 내부 탭(설비효율/생산효율) — URL ?section=teep 로 진입 시 시드.
                    this.oeeTab = (qp.get('section') === 'teep') ? 'teep' : 'equip';
                    // 뒤로/앞으로 가기 시 탭 동기화
                    window.addEventListener('popstate', () => { this.applyTabFromUrl(); this.applyOeeTabFromUrl(); });
                    try { this._charts = await import('/js/user-tag-trend-chart.js'); } catch (e) { console.warn('chart module load failed', e); }
                    // 최초 진입 로드도 로딩 인디케이터로 감싼다(기간 변경 setPeriod 와 동일 UX). 폴링(load(true))은 무표시.
                    const initLoad = async () => {
                        await this.load();
                        // OEE 도메인 로드(CT 표·비생산 시간대)는 알람 전용 페이지에서 스킵.
                        if (this.view !== 'alarm') { await this.loadCtTable(); await this.loadPlannedStops(); }
                    };
                    if (window.dspLoading) await window.dspLoading.wrap(initLoad, '불러오는 중…');
                    else await initLoad();
                    this.connectSignalR();
                    this._pollTimer = setInterval(() => { this.load(true); if (this.view !== 'alarm') this.refreshActualNonProd(); }, 10000);
                    // 알람 페이지 진입 시드(필터 스크롤·포커스·차단 모달) — OEE 전용 페이지에서는 무의미하므로 스킵.
                    if (this.view !== 'oee') {
                        // 필터 시드로 진입했으면 UserTag 카드로 스크롤(특정 알람 포커스가 있으면 그 행으로 직접 스크롤하므로 생략).
                        if (seeded && !this._focusAt) this.$nextTick(() => document.getElementById('ut-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' }));
                        if (this._focusAt) this.$nextTick(() => this.focusAlertRow());
                        // 차단 상태는 항상 로드(툴바 버튼의 차단 수 배지) — ?blockMgr=1 진입(설정 페이지 링크)이면 모달 자동 열기.
                        if (qp.has('blockMgr')) this.openBlockMgr();
                        else { this.loadBlockState(); this.loadUserTagBlockState(); }
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

                // ── OEE 페이지 내부 탭 전환 (설비효율 ⇆ 생산효율) + URL ?section= 동기화 ──
                //   설비효율(equip)=기존 OEE UI, 생산효율(teep)=TEEP(Phase 3). x-show(display:none) 로 숨겨졌던
                //   캔버스는 0 크기로 그려지므로 설비효율 복귀 시 차트를 다시 렌더한다(테마 재렌더와 동일 패턴).
                setOeeTab(t) {
                    if (this.oeeTab === t) return;
                    this.oeeTab = t;
                    this.syncOeeTabUrl();
                    if (t === 'equip') this.$nextTick(() => { this.drawCharts(); this.drawDailyChart(); });
                },
                syncOeeTabUrl() {
                    const qp = new URLSearchParams(location.search);
                    if (this.oeeTab === 'equip') qp.delete('section'); else qp.set('section', this.oeeTab);
                    const qs = qp.toString();
                    history.replaceState(null, '', location.pathname + (qs ? '?' + qs : '') + location.hash);
                },
                applyOeeTabFromUrl() {
                    const t = new URLSearchParams(location.search).get('section') === 'teep' ? 'teep' : 'equip';
                    if (t === this.oeeTab) return;
                    this.oeeTab = t;
                    if (t === 'equip') this.$nextTick(() => { this.drawCharts(); this.drawDailyChart(); });
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
                        this.ps.source = r.source || 'none';
                        this.ps.auto = !!r.auto;
                        this.ps.ctMultiplier = r.ctMultiplier || 10;
                        this.ps.windows = (r.windows || []).map(w => ({ startMinutes: w.startMinutes, endMinutes: w.endMinutes, label: w.label || '' }));
                        this.ps.selected = -1; this.ps.addMode = false; this.ps.seededFromAuto = false;
                    } catch (e) { this.ps.err = '비생산 시간대를 불러오지 못했습니다: ' + e.message; }
                    // 자동 모드일 때: 이번 기간 실제 제외 비생산(타임라인의 유일한 소스)만 조회 — 14일 평균 패턴 참고 블록은 폐지.
                    // 비생산 시간대는 시스템(전역) 단위 — flow별 페이지에서도 curFlow 필터 없이 항상 시스템 전체로 표시.
                    if (this.ps.auto) {
                        try {
                            const r = this.rangeForPeriod();
                            const aqs = `from=${encodeURIComponent(r.from)}&to=${encodeURIComponent(r.to)}`;
                            this.ps.actualNonProd = await this.apiGet('/api/oee/planned-stops/actual?' + aqs);
                        } catch (e) { this.ps.actualNonProd = null; }
                    } else {
                        this.ps.actualNonProd = null;
                    }
                },
                // 폴링용 경량 갱신 — 자동 모드에서 '실제 제외 비생산'(+현재 상태 배지)만 다시 읽는다.
                // 수동 편집 상태(ps.windows/selected/addMode)는 건드리지 않아 편집 중 클로버 방지.
                async refreshActualNonProd() {
                    if (!this.ps.auto) return;
                    try {
                        const r = this.rangeForPeriod();
                        this.ps.actualNonProd = await this.apiGet(
                            `/api/oee/planned-stops/actual?from=${encodeURIComponent(r.from)}&to=${encodeURIComponent(r.to)}`);
                    } catch (e) { /* 이전 값 유지 */ }
                },
                // 자동 계산 on/off — on=10×가동시간 장시간정지 자동 비생산, off=수동 시각대만. 수동 적용은 자동을 끈다(서버 SavePlannedStops).
                async psSetAuto(enabled) {
                    this.ps.busy = true; this.ps.msg = ''; this.ps.err = '';
                    // 자동 → 수동 전환 시: loadPlannedStops 호출 전에 '지금 비생산'(actualNonProd) 캡처 (이후 null 로 지워짐)
                    const capturedActualWins = !enabled && this.ps.actualNonProd?.windows?.length
                        ? this.ps.actualNonProd.windows.map(w => ({ startMinutes: w.startMinutes, endMinutes: w.endMinutes, label: w.label || '' }))
                        : null;
                    try {
                        await this.apiPost('/api/oee/planned-stops/auto', { enabled });
                        await this.loadPlannedStops();
                        // 수동 전환 시 지금 비생산 구간을 수동 에디터 초기값(참고용)으로 시드 (사용자가 바로 수정 가능)
                        if (!enabled && capturedActualWins && capturedActualWins.length > 0) {
                            this.ps.windows = capturedActualWins;
                            this.ps.seededFromAuto = true;
                            this.ps.msg = `지금 비생산 ${capturedActualWins.length}개 구간을 불러왔습니다 — 수정 후 [적용]을 누르세요`;
                        } else {
                            this.ps.seededFromAuto = false;
                            this.ps.msg = enabled ? `자동 계산 켜짐 — 평균 가동시간의 ${this.ps.ctMultiplier}배 이상 장시간 정지를 비생산으로 분류` : '자동 계산 꺼짐 — 수동 시간대만 적용';
                        }
                        await this.loadOee();
                    } catch (e) { this.ps.err = '변경 실패: ' + e.message; }
                    finally { this.ps.busy = false; setTimeout(() => { this.ps.msg = ''; }, 8000); }
                },
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
                    if (this.ps.addMode) return;
                    this.ps.selected = i; this.ps.err = '';
                    const w = this.ps.windows[i];
                    this._psDrag = { mode: 'move', index: i, anchor: this._psMinFromEvent(ev), downX: ev.clientX, dur: w.endMinutes - w.startMinutes, origStart: w.startMinutes, moved: false };
                    this._psStartDrag();
                },
                // 막대 양끝 핸들 pointerdown → 리사이즈 드래그 시작
                psResizeStart(ev, i, side) {
                    if (this.ps.addMode) return;
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
                },
                // 선택된 윈도의 시작/끝 시각 인라인 수정(type=time → 분). 라이브 재배치(재정렬은 적용 시).
                psSetTime(which, val) {
                    const m = this.hhmmToMin(val);
                    if (m == null || this.ps.selected < 0) return;
                    const w = this.ps.windows[this.ps.selected];
                    if (!w) return;
                    if (which === 'start') w.startMinutes = m; else w.endMinutes = m;
                    this.ps.err = '';
                },
                psRemove(i) {
                    this.ps.windows = this.ps.windows.filter((_, idx) => idx !== i);
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
                        this.ps.msg = windows.length ? `비생산 시간 ${windows.length}개 적용(수동) — 자동 계산 꺼짐` : '수동 적용(비생산 시간대 없음) — 자동 계산 꺼짐';
                        await this.loadPlannedStops();
                        await this.loadOee();
                    } catch (e) { this.ps.err = '적용 실패: ' + e.message; }
                    finally { this.ps.busy = false; setTimeout(() => { this.ps.msg = ''; }, 5000); }
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
                    if (this.utSystem) p.set('system', this.utSystem);
                    return p.toString();
                },

                async load(silent) {
                    if (!silent) this.loading = true;
                    // OEE 데이터는 스냅샷과 독립적이므로 여기서 즉시(동기) dispatch 해 스냅샷 fetch 뒤로 미루지 않는다.
                    // 뒤로 미루면(특히 스냅샷이 느릴 때) 이미 dispatch 된 옛 기간의 loadOee 가 최신 _oeeSeq 를 차지한 채
                    // 먼저 도착해, 방금 선택한 기간과 화면이 어긋나거나(범위 불일치) 새 기간이 스냅샷 완료 후에야
                    // 뒤늦게 적용되던 경합이 생긴다. 동기 호출하면 rangeForPeriod()·++_oeeSeq 가 방금 바뀐 period 로
                    // 즉시 실행돼 loadOee 의 dispatch 순서가 기간 선택 순서와 정확히 일치한다(최신 선택이 항상 승리).
                    const oeePromise = this.loadOee();
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
                    if (this.view === 'oee') return; // 이상·알람 차트는 OEE 전용 페이지에 캔버스 없음
                    if (!this._charts || !this.ut) return;
                    try {
                        // 설비별 보기는 자동감지만(USERTAG 는 Flow 에 속하지 않음) → 트렌드도 자동감지 단일 시리즈.
                        this._charts.renderTrendChart('ut-trend-chart', this.ut.buckets || [], this.ut.granularity,
                            this.curFlow ? ['ABNORMAL'] : ['ABNORMAL', 'USERTAG']);
                        // 태그별 Top 10 은 경로(FLOW / WORK / CALL)별 집계로 고정.
                        this._charts.renderTopChart('ut-top-chart', (this.ut.topRowsByPath || []).slice(0, 10));
                    } catch (e) { console.warn('chart draw failed', e); }
                },

                // ── 이상발생 필터/페이지/CSV (구 관리페이지) ──
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
                async exportUtCsv() {
                    if (!this.ut) return;
                    if (!this._charts) { this.error = 'CSV 모듈이 로드되지 않았습니다 — 페이지를 새로고침한 뒤 다시 시도하세요.'; return; }
                    let all;
                    try { all = await this.apiGet('/api/user-tags/alerts?' + this.utQs() + '&limit=100000'); }
                    catch (e) { console.error(e); this.error = 'CSV 내보내기 실패: ' + e.message; return; }
                    const esc = (v) => { v = (v ?? '').toString(); return (v.includes(',') || v.includes('"') || v.includes('\n')) ? '"' + v.replace(/"/g, '""') + '"' : v; };
                    const lines = ['Timestamp,LogLevel,System,Name,TagAddress,ValueType,MatchOp,MatchValue,ActualValue'];
                    for (const a of all) lines.push([esc(a.occurredAtLocal), esc(a.logLevel), esc(a.systemName), esc(a.name), esc(a.tagAddress), esc(a.valueType), esc(a.matchOp), esc(a.matchValue || ''), esc(a.actualValue)].join(','));
                    const t = new Date(); const p = (x) => String(x).padStart(2, '0');
                    const fn = `UserTagAlerts_${t.getFullYear()}${p(t.getMonth() + 1)}${p(t.getDate())}_${p(t.getHours())}${p(t.getMinutes())}${p(t.getSeconds())}.csv`;
                    this._charts.downloadCsv(fn, lines.join('\n'));
                },

                async loadOee() {
                    if (this.view === 'alarm') return; // OEE 지표는 알람 전용 페이지에서 미사용
                    const r = this.rangeForPeriod();
                    const qs = `from=${encodeURIComponent(r.from)}&to=${encodeURIComponent(r.to)}`;
                    // fqs = 기간 + 설비 필터. 순위(ranking)는 설비 비교용이라 항상 전체(qs).
                    const fqs = this.curFlow ? qs + `&flow=${encodeURIComponent(this.curFlow)}` : qs;
                    const seq = ++this._oeeSeq;
                    try {
                        const [summary, downtime, ranking, daily, planTime] = await Promise.all([
                            this.apiGet('/api/oee/summary?' + fqs),
                            this.apiGet('/api/oee/downtime?' + fqs),
                            this.apiGet('/api/oee/ranking?' + qs),
                            this.apiGet('/api/oee/daily?' + fqs),
                            this.apiGet('/api/oee/plan-time?' + fqs),
                        ]);
                        if (seq !== this._oeeSeq) return; // stale 응답 폐기
                        this.oee = summary;
                        this.downtime = Array.isArray(downtime) ? downtime : [];
                        this.ranking = Array.isArray(ranking) ? ranking : [];
                        this.dailyData = daily;
                        this.planTime = planTime;
                        this.oeeError = null;
                    } catch (e) {
                        if (seq !== this._oeeSeq) return;
                        this.oeeError = 'OEE 데이터를 불러오지 못했습니다: ' + e.message;
                    }
                    this.$nextTick(() => this.drawDailyChart());
                },

                // ── 내보내기 (종합효율 현황) ─────────────────────────────────────────────
                // CSV = 데이터 전용(요약 KPI + 설비별 순위 + 정지 이벤트, 3 섹션). 서버 미경유 — 클라이언트에서 즉시 빌드.
                // Excel = 화면 상태(요약·순위·정지) + 일자별 추이 차트(캔버스 캡처)를 서버(OeeExcelExporter)가 렌더 → WYSIWYG.
                oeeExportName() { return this.curFlow ? this.curFlow : '라인전체'; },
                _stamp() { const t = new Date(); const p = (x) => String(x).padStart(2, '0'); return `${t.getFullYear()}${p(t.getMonth() + 1)}${p(t.getDate())}_${p(t.getHours())}${p(t.getMinutes())}${p(t.getSeconds())}`; },
                _csvEscape(v) { if (v == null) return ''; const s = String(v); return /[",\n\r]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; },
                _wallOf(iso) { if (!iso) return ''; const d = new Date(iso); if (isNaN(d)) return String(iso); const p = (x) => String(x).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`; },
                _downloadBlob(filename, blob) {
                    const url = URL.createObjectURL(blob);
                    const a = document.createElement('a'); a.href = url; a.download = filename;
                    document.body.appendChild(a); a.click(); document.body.removeChild(a); URL.revokeObjectURL(url);
                },

                exportOeeCsv() {
                    const o = this.oee;
                    if (!o) return;
                    const E = (v) => this._csvEscape(v);
                    const pctv = (v) => (v == null ? '' : (v * 100).toFixed(1));
                    const sec = (ms) => (ms == null ? '' : (ms / 1000).toFixed(1));
                    const r = this.rangeForPeriod();
                    const out = [];
                    out.push('종합효율 현황 데이터');
                    out.push(['설비', E(this.curFlow || '라인 전체')].join(','));
                    out.push(['기간', E(this._wallOf(r.from) + ' ~ ' + this._wallOf(r.to))].join(','));
                    out.push('');
                    out.push('[OEE 종합 지표]');
                    out.push('지표,값(%),비고');
                    out.push(['OEE', pctv(o.oee), ''].join(','));
                    out.push(['가용성 A', pctv(o.availability), E(o.availabilitySource || '')].join(','));
                    out.push(['성능 P', pctv(o.performance), '14일 평균'].join(','));
                    out.push(['품질 Q', pctv(o.quality), E(o.qualitySource || '')].join(','));
                    out.push(['MTBF', '', E(this.durShort(o.mtbf))].join(','));
                    out.push(['MTTR', '', E(this.durShort(o.mttr))].join(','));
                    out.push(['정지 건수', o.downtimeCount != null ? o.downtimeCount : '', ''].join(','));
                    out.push(['정지 시간', '', E(this.durShort(o.downtimeMs))].join(','));
                    out.push('');
                    out.push('[설비별 OEE 순위]');
                    out.push('순위,설비,OEE(%),가용성(%),성능(%),품질(%),정지건수,정지시간(초),생산수');
                    this.ranking.forEach((rk, i) => out.push([
                        i + 1, E(rk.flowName), pctv(rk.oee), pctv(rk.availability), pctv(rk.performance), pctv(rk.quality),
                        rk.downtimeCount != null ? rk.downtimeCount : 0, sec(rk.downtimeMs), rk.totalCount != null ? rk.totalCount : 0,
                    ].join(',')));
                    out.push('');
                    out.push('[정지 이벤트]');
                    out.push('발생,복구,지속(초),설비,장치,구분,감지,상태');
                    this.downtime.forEach(d => out.push([
                        E(this._wallOf(d.startAt)), E(d.endAt ? this._wallOf(d.endAt) : ''), sec(d.durationMs),
                        E(d.flowName || d.systemName || ''), E(d.deviceName || ''),
                        d.isFailure ? '고장' : '유지보수', E(d.detectSource || ''), d.status === 'open' ? '진행중' : '복구',
                    ].join(',')));
                    const text = '﻿' + out.join('\r\n');
                    this._downloadBlob(`OEE_${this.oeeExportName()}_${this._stamp()}.csv`, new Blob([text], { type: 'text/csv;charset=utf-8' }));
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
                            title: this.curFlow || '라인 전체',
                            systemName: null,
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
                            availComp: (ac && ac.hasData) ? { mode: ac.mode, runLabel: ac.runLabel, runMs: ac.runMs, runPct: ac.runPct, stopLabel: ac.stopLabel, stopMs: ac.stopMs, stopPct: ac.stopPct } : null,
                            faultSegs: this.faultDist.segs.map(s => ({ label: s.label, ms: s.ms, share: s.share })),
                            ranking: this.ranking.map(rk => ({ flowName: rk.flowName, oee: rk.oee, availability: rk.availability, performance: rk.performance, quality: rk.quality, downtimeCount: rk.downtimeCount, downtimeMs: rk.downtimeMs, totalCount: rk.totalCount })),
                            downtime: this.downtime.map(d => ({ startAt: d.startAt, endAt: d.endAt, durationMs: d.durationMs, flowName: d.flowName || d.systemName, deviceName: d.deviceName, isFailure: !!d.isFailure, detectSource: d.detectSource, status: d.status })),
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
                        if (d.granularity === 'hour') return s.slot.slice(11, 16); // "HH:00"
                        // "yyyy-MM-dd" → "M/D"
                        const parts = s.slot.split('-');
                        return parts.length === 3 ? (parseInt(parts[1]) + '/' + parseInt(parts[2])) : s.slot;
                    });
                    // 가동·고장·유지보수·비생산 4분해. 서버 슬롯: failureMs=isFailure 1, plannedMs=isFailure 0(유지보수),
                    //   nonProdMs=비생산(A 분모 밖 — 가동에서 카빙), 나머지=가동근사.
                    const failureData = d.slots.map(s => ((s.failureMs || 0) + (s.otherMs || 0) + (s.unclassifiedMs || 0)) / MS); // 고장(isFailure+기타+미분류 전부 포함)
                    const plannedData = d.slots.map(s => (s.plannedMs || 0) / MS); // 유지보수(isFailure=0)
                    const nonProdData = d.slots.map(s => (s.nonProdMs || 0) / MS); // 비생산(제외) — A 분모 밖
                    const runData = d.slots.map(s => Math.max(0, s.slotMs - (s.failureMs || 0) - (s.otherMs || 0) - (s.unclassifiedMs || 0) - (s.plannedMs || 0) - (s.nonProdMs || 0)) / MS);

                    // 평균 가동시간 선 (비생산 카빙 후 실가동 기준)
                    const avgRun = runData.length > 0 ? runData.reduce((a, b) => a + b, 0) / runData.length : 0;

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
                        { label: '가동(근사)', data: runData, backgroundColor: cRun, stack: 's', order: 2 },
                        { label: '고장', data: failureData, backgroundColor: faultHatch, stack: 's', order: 2 },
                        { label: '유지보수', data: plannedData, backgroundColor: maintHatch, stack: 's', order: 2 },
                        // 비생산(제외)은 기본 비활성(범례에서 꺼진 상태) — 필요 시 범례 클릭으로 켬. hidden 은 생성 시 1회만 적용, 이후 사용자 토글 유지.
                        { label: '비생산(제외)', data: nonProdData, backgroundColor: nightHatch, stack: 's', order: 2, hidden: true },
                        {
                            label: `평균 ${avgRun.toFixed(1)}h`,
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
                            options: _dailyChartOptions(d.granularity),
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
                        _dailyChart.options = _dailyChartOptions(d.granularity);
                        _dailyChart.update('none');
                    }
                },

                connectSignalR() {
                    if (!window.signalR) return;
                    const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/monitoring').withAutomaticReconnect([0, 0, 1000, 3000, 5000, 10000]).build();
                    const trigger = () => { clearTimeout(this._dt); this._dt = setTimeout(() => this.load(true), 300); };
                    conn.on('CallStateChangedBatch', trigger);
                    conn.on('CallStateChanged', trigger);
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

                setPeriod(p) { if (this.period === p) return; this.period = p; this.utPage = 0; if (window.dspLoading) window.dspLoading.wrap(() => this.load(), '기간 데이터 불러오는 중…'); else this.load(); },

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
                },
                applyCustomPeriod() {
                    if (!this.customFrom || !this.customTo) return;
                    this.utPage = 0;
                    if (window.dspLoading) window.dspLoading.wrap(() => this.load(), '기간 데이터 불러오는 중…'); else this.load();
                },

                // ── 구분(ABNORMAL/USERTAG) 도넛/배지 ──
                // 구분 판별 SSOT(클라) — abnormal 행은 matchOp='AbnormalDetect'(서버 valueType='Abnormal' 과 대응).
                categoryOf(a) { return (a && a.matchOp === 'AbnormalDetect') ? 'ABNORMAL' : 'USERTAG'; },
                // 표시 라벨: ABNORMAL=자동감지(엔진 자동), USERTAG=사용자지정(사용자 정의 태그).
                categoryLabel(a) { return this.categoryOf(a) === 'ABNORMAL' ? '자동감지' : '사용자지정'; },
                // ds-status 톤: 둘 다 Error 알람이라 bad(빨강)로 통일, 구분은 라벨로만.
                categoryStatus(a) { return 'bad'; },
                cc(cat) { return (this.ut && this.ut.categoryCounts && this.ut.categoryCounts[cat]) || 0; },
                get categoryTotal() { return this.cc('ABNORMAL') + this.cc('USERTAG'); },
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
                durShort(ms) {
                    if (ms == null || ms <= 0) return '—';
                    if (ms < 1000) return Math.round(ms) + 'ms';
                    if (ms < 60000) return (ms / 1000).toFixed(1) + 's';
                    if (ms < 3600000) return Math.floor(ms / 60000) + 'm ' + Math.floor(ms % 60000 / 1000) + 's';
                    if (ms < 86400000) return Math.floor(ms / 3600000) + 'h ' + Math.floor(ms % 3600000 / 60000) + 'm';
                    return Math.floor(ms / 86400000) + 'd ' + Math.floor(ms % 86400000 / 3600000) + 'h';
                },
                dur(ms, d) {
                    // open(진행중)인데 durationMs 없으면 시작→현재 경과 근사 표기
                    if (ms != null && ms > 0) return this.durShort(ms);
                    if (d && d.status === 'open' && d.startAt) {
                        const el = Date.now() - new Date(d.startAt).getTime();
                        return el > 0 ? this.durShort(el) + '~' : '진행중';
                    }
                    return '—';
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

                // ── 정지 필터 ──
                get filteredDowntime() {
                    return this.downtime.filter(d => {
                        if (this.dtFilterStatus !== 'all' && d.status !== this.dtFilterStatus) return false;
                        if (this.dtFilterFault === 'fault' && !d.isFailure) return false;
                        if (this.dtFilterFault === 'maintenance' && d.isFailure) return false;
                        return true;
                    });
                },

                // ── 일괄 선택 computed ──
                // 선택/일괄 작업 범위 = 현재 필터에 보이는 행만. 필터 변경으로 화면에서 사라진 행의
                // 체크 상태는 메모리에 남지만 카운트/일괄 처리 대상에서 제외 — "N건 선택됨"이 항상 화면과 일치.
                get selectedVisibleIds() { return this.filteredDowntime.filter(d => this.selectedIds[d.id]).map(d => d.id); },
                get selectedCount() { return this.selectedVisibleIds.length; },
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
                    const m = { 'nocycle': '무가동', 'fault-bit': '고장비트', 'usertag': '고장비트', 'manual': '수동' };
                    return `<span class="src-chip detect">${m[s] || this.esc(s) || '—'}</span>`;
                },
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
                // 가용성 분해 (활성 모드 통합) — 상단 A KPI와 항상 일치.
                //  cycle : Σ실측CT(정상 사이클) vs Σ비가동CT  (A = 실측/(실측+비가동))
                //  폴백  : 가동시간 vs 정지  (A = 가동 ÷ 계획생산시간, 분모=planTime.plannedMs)
                get availComp() {
                    const o = this.oee || {};
                    const r1 = (x) => Math.round(x * 10) / 10;
                    if (o.availabilitySource === 'cycle') {
                        const n = Math.max(0, o.normalCtMs || 0), i = Math.max(0, o.idleCtMs || 0);
                        const tot = n + i;
                        const runPct = tot > 0 ? r1(n / tot * 100) : 0;
                        return {
                            mode: 'cycle', hasData: tot > 0,
                            runMs: n, stopMs: i, runPct, stopPct: tot > 0 ? r1(100 - runPct) : 0,
                            runLabel: '실측 가동시간 (정상 가동)', stopLabel: '비가동 가동시간',
                            runNote: (o.normalCycleCount || 0) + '회', stopNote: (o.failureCount || 0) + '건',
                            subtitle: 'Σ실측 가동시간 ÷ (Σ실측 + Σ비가동) · 비가동 = 동작시간>이상치 / 미완료 폭주 / 무가동',
                            hint: '한 번의 가동에서 <b>동작시간이 가동시간 이상치를 넘으면</b> 그 가동의 가동시간 전체를 비가동으로 본다(인식지연 + 고장 + 회복 포함). 미완료(가동시간 폭주)·무가동 정지도 비가동에 합산하되 겹친 구간은 1회만 계상한다(이중계상 방지).',
                        };
                    }
                    // 폴백(shift/auto/calendar): 계획시간 분모 기준 — planTime 사용.
                    const pt = this.planTime || {};
                    const planned = Math.max(0, pt.plannedMs || 0);
                    const run = Math.max(0, Math.min(planned, pt.runtimeMs || 0));
                    const stop = Math.max(0, planned - run);
                    const runPct = planned > 0 ? r1(run / planned * 100) : 0;
                    const srcMap = { shift: '사용자 시프트', auto: '14일 자동추정', calendar: '달력근사' };
                    const srcLabel = srcMap[o.availabilitySource] || '달력근사';
                    return {
                        mode: 'fallback', hasData: planned > 0,
                        runMs: run, stopMs: stop, runPct, stopPct: planned > 0 ? r1(100 - runPct) : 0,
                        runLabel: '가동시간', stopLabel: '정지 (비계획)',
                        runNote: null, stopNote: null, sourceLabel: srcLabel,
                        subtitle: '가동시간 ÷ 계획생산시간 · 계획시간 폴백(' + srcLabel + ') — 가동 표본 부족',
                        hint: '클린 가동 표본이 부족해 <b>계획시간 폴백</b>으로 가용성을 산출한다(계획시간 = ' + srcLabel + '). 가동시간에는 유휴가 포함될 수 있어 가동시간 기반보다 느슨한 근사다. 클린 가동이 쌓이면 자동으로 가동시간 기반으로 전환된다.',
                    };
                },
                // 계획시간 폴백 체인 3단계 (활성/건너뜀/대기)
                get planChainSteps() {
                    const pt = this.planTime;
                    const active = pt ? pt.source : 'calendar';
                    const order = ['shift', 'auto', 'calendar'];
                    const ai = order.indexOf(active);
                    const defs = [
                        { key: 'shift', t: '① 사용자 시프트', d: '대시보드 시프트 설정 (UserSet)' },
                        { key: 'auto', t: '② 14일 활동 자동추정', d: '히스토그램 활동창 × 활동일수 (RAM)' },
                        { key: 'calendar', t: '③ 달력근사', d: '조회 기간 전체 (최후 폴백)' },
                    ];
                    return defs.map((s, i) => {
                        let cls = 'up-chain-step', badge;
                        if (s.key === active) { cls += ' active'; badge = '✓ 적용 중'; }
                        else if (i < ai) {
                            cls += ' dim';
                            badge = s.key === 'shift' ? (pt && pt.shiftUserSet ? '계획시간 0 — 건너뜀' : '미설정 — 건너뜀')
                                : s.key === 'auto' ? (pt && pt.autoAvailable ? '계획시간 0 — 건너뜀' : '데이터 부족 — 건너뜀') : '대기';
                        } else { cls += ' dim'; badge = '대기'; }
                        return { key: s.key, t: s.t, d: s.d, cls, badge };
                    });
                },
                // 14일 히스토그램 막대 (활동창 = 피크 10% 이상 inband 강조)
                get histBars() {
                    const h = (this.planTime && this.planTime.histogram) || [];
                    if (!h.length || h.every(v => !v)) return Array.from({ length: 24 }, () => ({ h: 2, cls: 'b' }));
                    const max = Math.max(...h, 0);
                    const thr = max * 0.10;
                    return h.map(v => ({ h: max > 0 ? Math.max(2, Math.round(v / max * 100)) : 2, cls: (max > 0 && v > 0 && v >= thr) ? 'b inband' : 'b' }));
                },
                get planChainFoot() {
                    const pt = this.planTime;
                    if (!pt) return '';
                    const hrs = (ms) => (ms / 3600000).toFixed(1) + 'h';
                    const p2 = (x) => String(x).padStart(2, '0');
                    if (pt.source === 'auto')
                        return `자동추정${pt.autoAvailable ? ` · 활동 ${p2(pt.autoStartHour)}–${p2(pt.autoEndHour)}시` : ''} · 활동일 ${pt.activeDays}일 · 계획시간 ${hrs(pt.plannedMs)} · 가동 ${hrs(pt.runtimeMs)}`;
                    if (pt.source === 'shift')
                        return `사용자 시프트 ${pt.shiftLabel} · 계획시간 ${hrs(pt.plannedMs)} · 가동 ${hrs(pt.runtimeMs)}`;
                    return `달력근사 · 기간 ${hrs(pt.plannedMs)} · 가동 ${hrs(pt.runtimeMs)}`;
                },

                // ── 정지 이벤트 로그 토글 (도넛 [로그 보기 및 설정]) ──
                toggleDowntimeLog() {
                    this.showDowntimeLog = !this.showDowntimeLog;
                    // 로그는 정지 원인 구성 바로 아래에 표시됨 — 화면 밖일 때만 부드럽게 스크롤(인접 시 점프 방지).
                    if (this.showDowntimeLog)
                        this.$nextTick(() => document.getElementById('downtime-log-section')?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }));
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
                async bulkSetFault(isFault) {
                    const ids = this.selectedVisibleIds;
                    if (!ids.length) return;
                    this.bulkBusy = true;
                    try {
                        await this.apiPost('/api/oee/downtime/bulk-set-fault', { ids, isFault });
                        this.clearSel();
                        await this.loadOee();
                        this.flashDtMsg(`${ids.length}건 → ${isFault ? '고장' : '유지보수'} 일괄 적용`);
                    } catch (e) {
                        this.oeeError = '일괄 변경 실패: ' + e.message;
                    } finally { this.bulkBusy = false; }
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

                // 정지 도넛 (고장/유지보수 2-상태, 지속시간 가중 — 합계가 상단 '기간 정지'와 일치)
                get faultDist() {
                    let faultMs = 0, maintMs = 0, count = 0;
                    for (const d of this.downtime) {
                        const ms = (d.durationMs && d.durationMs > 0)
                            ? d.durationMs
                            : (d.status === 'open' && d.startAt ? Math.max(0, Date.now() - new Date(d.startAt).getTime()) : 0);
                        if (ms <= 0) continue;
                        count++;
                        if (d.isFailure) faultMs += ms; else maintMs += ms;
                    }
                    const totalMs = faultMs + maintMs;
                    if (totalMs <= 0 || count === 0) return { count, segs: [] };
                    const C = 2 * Math.PI * 38;
                    const segs = [];
                    const defs = [{ def: FAULT_DEF, ms: faultMs }, { def: MAINT_DEF, ms: maintMs }].filter(x => x.ms > 0);
                    let prior = 0;
                    for (const { def, ms } of defs) {
                        const len = ms / totalMs * C, gap = C - len, offset = -(prior / totalMs * C);
                        prior += ms;
                        segs.push({ label: def.label, color: def.color, cls: def.cls, pat: def.pat, ms, share: Math.round(ms * 100 / totalMs),
                                    dash: len.toFixed(2) + ' ' + gap.toFixed(2), offset: offset.toFixed(2) });
                    }
                    return { count, segs };
                },
                // faultDist → 도넛 내부 SVG 문자열 (x-html)
                get faultDonutSvg() {
                    const d = this.faultDist;
                    // 정지 유형 대각선 빗금 패턴(색=유형, 빗금=정지 신호 — 가동 솔리드와 대비). userSpaceOnUse 로 링 전체에 타일링.
                    const pat = (id, color) => `<pattern id="${id}" patternUnits="userSpaceOnUse" width="7" height="7">`
                        + `<rect width="7" height="7" fill="${color}"></rect>`
                        + `<path d="M0,7 L7,0 M-1.5,1.5 L1.5,-1.5 M5.5,8.5 L8.5,5.5" stroke="rgba(255,255,255,0.55)" stroke-width="1.3"></path></pattern>`;
                    let s = `<defs>${pat('up-pat-fault', 'var(--oee-fault)')}${pat('up-pat-maint', 'var(--oee-maint)')}</defs>`;
                    s += '<circle class="up-donut-track" cx="50" cy="50" r="38" fill="none" stroke-width="14"></circle>';
                    for (const seg of d.segs)
                        s += `<circle cx="50" cy="50" r="38" fill="none" stroke="url(#${seg.pat})" stroke-width="14" stroke-dasharray="${seg.dash}" stroke-dashoffset="${seg.offset}" transform="rotate(-90 50 50)"></circle>`;
                    s += `<text class="up-donut-total" x="50" y="49" text-anchor="middle">${d.count}</text>`;
                    s += '<text class="up-donut-cap" x="50" y="61" text-anchor="middle">정지건수</text>';
                    return s;
                },

                // ── 순위 메달/최하위 ──
                medal(i) { return i === 0 ? '🥇' : i === 1 ? '🥈' : i === 2 ? '🥉' : (i + 1); },
                isWorst(r, i) {
                    // 최하위: OEE 산출 가능 항목 중 최저, 없으면 정지시간 최대(=ranking 마지막)
                    const scored = this.ranking.filter(x => x.oee != null);
                    if (scored.length > 0) {
                        const worst = scored.reduce((a, b) => b.oee < a.oee ? b : a);
                        return r.flowName === worst.flowName;
                    }
                    return i === this.ranking.length - 1 && this.ranking.length > 1;
                },

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

                // ── 인터랙션: 고장/유지보수 토글 / 수동마감 / 불량 / 표준 가동시간 ──
                async setFault(d, isFault) {
                    try {
                        await this.apiPost('/api/oee/downtime/' + d.id + '/set-fault', { isFault });
                        await this.loadOee();
                        this.flashDtMsg(`${d.flowName || d.systemName || ''} → ${isFault ? '고장' : '유지보수'}`);
                    } catch (e) {
                        this.oeeError = '변경 실패: ' + e.message;
                    }
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

        function _dailyChartOptions(granularity) {
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
                                const h = Math.floor(v);
                                const m = Math.round((v - h) * 60);
                                const t = h > 0 ? `${h}h ${m}m` : `${m}m`;
                                return `${ctx.dataset.label}: ${t}`;
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
                        ticks: {
                            color: cText,
                            font: { size: 11 },
                            callback: v => v + 'h',
                        },
                        grid: { color: cGrid },
                    },
                },
            };
        }
