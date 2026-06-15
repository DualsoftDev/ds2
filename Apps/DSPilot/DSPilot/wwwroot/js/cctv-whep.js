// CCTV WebRTC(WHEP) 플레이어.
// MediaMTX 가 RTSP 를 받아 WebRTC 로 재게시한 스트림을 브라우저에서 시청한다.
// MediaMTX WHEP 엔드포인트: http://<host>:<port>/<cameraName>/whep
//
// host 는 브라우저가 DSPilot 에 접속한 호스트와 동일하게 사용한다(원격 접속/방화벽 대비).
// trickle ICE 대신 ICE gathering 완료를 기다린 뒤 한 번에 offer 를 POST 하는 단순 흐름.
//
// 주기 갱신(밀림 방지): 브라우저 jitter buffer 는 패킷 로스/탭 스로틀링 후 지연이 누적된 채
// 회복되지 않을 수 있다. 새 WHEP 세션은 항상 라이브 엣지에서 시작하므로, 3~5분마다
// 더블 버퍼 재협상(새 연결을 미리 맺고 첫 프레임이 흐르면 srcObject 만 교체)으로 리셋한다.
// 체감은 검은 화면 없이 라이브 엣지로 스냅하는 한 프레임 점프뿐이다.
//
// 절전 가드(LTE 데이터 절약): 카메라 원본 RTSP 는 현장 LTE(종량제) 회선을 타므로,
// 아무도 보지 않는 스트림은 여기서 끊는다(MediaMTX sourceOnDemand 가 마지막 시청자
// 이탈 10초 뒤 RTSP 원본 연결도 닫음 → LTE 트래픽 0).
//   · 탭 숨김(다른 탭/최소화) → 전 스트림 보류, 탭 복귀 시 자동 재생 (항상 동작)
//   · 설정 시간(기본 60분) 무입력(마우스/키/휠/터치) → 일시정지, 입력 감지 시 자동 재생
//     — 사용여부/시간은 서버 공유 설정(/api/cctv/config 의 idlePause*)을 configureSaver 로 주입
// 보류는 이 레이어에서 투명하게 처리(세션 명세 보관 후 재협상) — 페이지 앱의 폴링/
// 오버레이 렌더는 그대로 동작한다. PiP 송출 세션은 "탭 밖 시청"이라 setKeepAlive 로 면제.
window.cctvWhep = (function () {
    const sessions = {}; // videoId -> { pc, pendingPc, port, name, closed, keepAlive, retryTimer, refreshTimer }

    const REFRESH_BASE_MS = 3 * 60 * 1000;    // 갱신 주기 하한(3분)
    const REFRESH_JITTER_MS = 2 * 60 * 1000;  // +0~2분 랜덤 — 타일들이 같은 순간에 재협상하지 않게 분산
    const FIRST_MEDIA_TIMEOUT_MS = 4000;      // 새 연결의 첫 미디어 대기 한도 — 초과 시 갱신 포기(기존 연결 유지)

    function buildUrl(port, name) {
        return `${location.protocol}//${location.hostname}:${port}/${encodeURIComponent(name)}/whep`;
    }

    async function waitIceGathering(pc) {
        if (pc.iceGatheringState === 'complete') return;
        await new Promise((resolve) => {
            const check = () => {
                if (pc.iceGatheringState === 'complete') {
                    pc.removeEventListener('icegatheringstatechange', check);
                    resolve();
                }
            };
            pc.addEventListener('icegatheringstatechange', check);
            // 안전망: 일부 환경에서 complete 이벤트가 늦거나 누락 → 2초 후 강제 진행
            setTimeout(resolve, 2000);
        });
    }

    // WHEP 협상 코어: 새 RTCPeerConnection 을 만들어 offer/answer 까지 마치고 반환.
    // 트랙은 onTrack(stream) 으로 전달만 하고 video 에는 붙이지 않는다(초기/갱신 공용).
    // 실패 시 pc 를 닫고 throw, 협상 중 세션이 닫히면 pc 를 닫고 null 반환.
    async function openPeer(session, onTrack) {
        const pc = new RTCPeerConnection({
            iceServers: [] // 로컬/온프렘 — STUN 불필요
        });
        session.pendingPc = pc; // 협상 중 stop() 호출 대비
        pc.addTransceiver('video', { direction: 'recvonly' });
        pc.addTransceiver('audio', { direction: 'recvonly' });
        pc.ontrack = (e) => {
            if (e.streams && e.streams[0]) onTrack(e.streams[0]);
        };
        try {
            const offer = await pc.createOffer();
            await pc.setLocalDescription(offer);
            await waitIceGathering(pc);

            const res = await fetch(buildUrl(session.port, session.name), {
                method: 'POST',
                headers: { 'Content-Type': 'application/sdp' },
                body: pc.localDescription.sdp,
            });
            if (!res.ok) throw new Error(`WHEP ${res.status}`);

            const answerSdp = await res.text();
            if (session.closed) { pc.close(); return null; }
            await pc.setRemoteDescription({ type: 'answer', sdp: answerSdp });
            return pc;
        } catch (err) {
            try { pc.close(); } catch { }
            throw err;
        } finally {
            if (session.pendingPc === pc) session.pendingPc = null;
        }
    }

    // pc 를 세션의 활성 연결로 채택: 상태 통지 + 끊김 시 재연결 와이어링.
    function adoptPeer(session, pc) {
        session.pc = pc;
        pc.onconnectionstatechange = () => {
            if (session.closed || session.pc !== pc) return; // 교체된 구 연결의 잔여 이벤트 무시
            // 연결 상태를 호출자에게 통지(옵션) — 실제 스트림 헬스 표시용(전역 sync 상태와 구분).
            if (session.onState) { try { session.onState(pc.connectionState); } catch (e) { } }
            if (pc.connectionState === 'failed' || pc.connectionState === 'disconnected') {
                scheduleReconnect(session);
            }
        };
        if (session.onState) { try { session.onState(pc.connectionState); } catch (e) { } }
    }

    // 수신 트랙에 첫 미디어가 흐를 때까지 대기. true=수신 시작, false=타임아웃.
    function waitFirstMedia(track, timeoutMs) {
        if (!track.muted) return Promise.resolve(true);
        return new Promise((resolve) => {
            const timer = setTimeout(() => resolve(false), timeoutMs);
            track.addEventListener('unmute', () => { clearTimeout(timer); resolve(true); }, { once: true });
        });
    }

    async function negotiate(session) {
        const video = document.getElementById(session.videoId);
        if (!video || session.closed) return;
        try {
            const pc = await openPeer(session, (stream) => {
                const v = document.getElementById(session.videoId);
                if (!v || session.closed) return;
                v.srcObject = stream;
                v.play().catch(() => { /* autoplay 정책 — muted 라 보통 통과 */ });
            });
            if (!pc) return; // 협상 중 stop() 됨
            adoptPeer(session, pc);
            scheduleRefresh(session);
        } catch (err) {
            console.warn(`[cctv] ${session.name} 연결 실패:`, err.message);
            // 협상 실패(오프라인 카메라=WHEP 404, 네트워크 오류 등)는 connectionstatechange 가 안 떠
            // 페이지가 모른다 → onState('failed') 로 알려 로딩/실패 UI 가 '실패'로 전환되게 한다.
            // (scheduleReconnect 가 3초 뒤 재시도하므로 성공하면 다시 connected 로 복구)
            if (session.onState) { try { session.onState('failed'); } catch (e) { } }
            scheduleReconnect(session);
        }
    }

    // 더블 버퍼 갱신: 기존 연결을 살려둔 채 새 연결을 맺고, 첫 프레임이 흐르면 srcObject 교체.
    // 어떤 실패든 기존 연결을 그대로 유지하고 다음 주기에 재시도한다(조용히).
    async function refresh(session) {
        if (session.closed) return;
        const active = session.pc;
        if (!active || active.connectionState !== 'connected') {
            // 미연결 상태면 갱신할 대상이 없음 — 재연결 경로에 맡기고 다음 주기로
            scheduleRefresh(session);
            return;
        }
        let newPc = null;
        try {
            let stream = null;
            newPc = await openPeer(session, (s) => { stream = s; });
            if (!newPc) return; // 협상 중 stop() 됨
            const track = stream && (stream.getVideoTracks()[0] || stream.getTracks()[0]);
            const flowing = track && await waitFirstMedia(track, FIRST_MEDIA_TIMEOUT_MS);
            const video = document.getElementById(session.videoId);
            // 그 사이 세션이 닫혔거나 재연결 경로가 활성 연결을 바꿨으면 개입하지 않는다.
            if (!flowing || !video || session.closed || session.pc !== active) throw new Error('갱신 중단');

            video.srcObject = stream;
            video.play().catch(() => { });
            adoptPeer(session, newPc);
            try { active.close(); } catch { }
            newPc = null; // 채택 완료 — finally 정리 대상 아님
        } catch (err) {
            // 갱신 실패는 무시 — 기존 연결이 계속 재생 중
        } finally {
            if (newPc) { try { newPc.close(); } catch { } }
            scheduleRefresh(session);
        }
    }

    function scheduleRefresh(session) {
        if (session.closed) return;
        if (session.refreshTimer) clearTimeout(session.refreshTimer);
        session.refreshTimer = setTimeout(() => {
            session.refreshTimer = null;
            refresh(session);
        }, REFRESH_BASE_MS + Math.random() * REFRESH_JITTER_MS);
    }

    function scheduleReconnect(session) {
        if (session.closed || session.retryTimer) return;
        try { session.pc && session.pc.close(); } catch { }
        session.pc = null;
        session.retryTimer = setTimeout(() => {
            session.retryTimer = null;
            if (!session.closed) negotiate(session);
        }, 3000);
    }

    function begin(videoId, desc) {
        const session = {
            videoId, port: desc.port, name: desc.name, onState: desc.onState || null,
            keepAlive: false,
            closed: false, pc: null, pendingPc: null, retryTimer: null, refreshTimer: null,
        };
        sessions[videoId] = session;
        negotiate(session);
    }

    // 세션 teardown — 보류(suspended) 레지스트리는 건드리지 않는다(절전 보류와 완전 정지 공용).
    function closeSession(videoId) {
        const session = sessions[videoId];
        if (!session) return;
        session.closed = true;
        if (session.retryTimer) { clearTimeout(session.retryTimer); session.retryTimer = null; }
        if (session.refreshTimer) { clearTimeout(session.refreshTimer); session.refreshTimer = null; }
        try { session.pc && session.pc.close(); } catch { }
        try { session.pendingPc && session.pendingPc.close(); } catch { }
        const video = document.getElementById(videoId);
        if (video) video.srcObject = null;
        delete sessions[videoId];
    }

    // ── 절전 가드 (탭 숨김 / 장시간 무입력 → 스트림 보류, 자동 재개) ──
    // 무조작 일시정지는 서버 공유 설정(/api/cctv/config 의 idlePause*)을 페이지가
    // configureSaver 로 주입한다 — 주입 전엔 기본값(켜짐·60분). 탭 숨김 정지는 항상 동작.
    const IDLE_CHECK_MS = 30 * 1000;
    let idleEnabled = true;
    let idleLimitMs = 60 * 60 * 1000; // 이 시간 동안 입력 없으면 방치로 간주
    const suspended = {}; // videoId -> { port, name, onState } (보류 중 — 재개 시 재협상)
    let saverReason = null; // null | 'hidden' | 'idle'
    let lastInputAt = Date.now();

    const NOTE_ATTR = 'data-cctv-saver-note';
    const NOTE_TEXT = {
        hidden: '데이터 절약을 위해 일시정지됨 · 탭으로 돌아오면 재생됩니다',
        idle: '장시간 조작이 없어 일시정지됨 · 마우스를 움직이면 재생됩니다',
    };

    // 타일에 일시정지 안내를 띄운다. 페이지마다 마크업이 달라 CSS 파일 대신 인라인 스타일로,
    // video 의 부모(.cctv-tile-stage, position:relative)에 부착. pointer-events 없음 — 입력은
    // 절전 해제 신호이기도 하므로 가로채지 않는다. 오버레이 레이어(상태색)는 계속 그 위에서 동작.
    function showNote(videoId, reason) {
        removeNote(videoId);
        const video = document.getElementById(videoId);
        const stage = video && video.parentElement;
        if (!stage) return;
        const note = document.createElement('div');
        note.setAttribute(NOTE_ATTR, videoId);
        note.style.cssText = 'position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:6px;'
            + 'background:rgba(8,13,20,0.72);color:#e7edf5;font:600 13px Inter,"Noto Sans KR",sans-serif;'
            + 'text-align:center;padding:12px;pointer-events:none;z-index:6;';
        const icon = document.createElement('span');
        icon.className = 'material-icons';
        icon.style.cssText = 'font-size:34px;opacity:0.85;';
        icon.textContent = 'pause_circle';
        const label = document.createElement('span');
        label.textContent = NOTE_TEXT[reason] || NOTE_TEXT.idle;
        note.append(icon, label);
        stage.appendChild(note);
    }
    function removeNote(videoId) {
        document.querySelectorAll('[' + NOTE_ATTR + '="' + videoId + '"]').forEach((el) => el.remove());
    }

    // 페이지 상단 고정 배너 — 타일 안내문과 별개로 "일시정지 중"임을 한눈에 알린다(전체화면/원거리 모니터 대비).
    // transientMs 지정 시 토스트처럼 잠시 떴다 사라진다(재개 알림용).
    const BANNER_ID = 'cctv-saver-banner';
    function showBanner(text, iconName, transientMs) {
        removeBanner();
        const el = document.createElement('div');
        el.id = BANNER_ID;
        el.style.cssText = 'position:fixed;top:68px;left:50%;transform:translateX(-50%);z-index:2147483000;'
            + 'display:flex;align-items:center;gap:8px;padding:10px 18px;border-radius:999px;'
            + 'background:rgba(8,13,20,0.88);color:#e7edf5;font:600 13px Inter,"Noto Sans KR",sans-serif;'
            + 'box-shadow:0 4px 16px rgba(0,0,0,0.35);pointer-events:none;white-space:nowrap;';
        const icon = document.createElement('span');
        icon.className = 'material-icons';
        icon.style.cssText = 'font-size:18px;';
        icon.textContent = iconName;
        const label = document.createElement('span');
        label.textContent = text;
        el.append(icon, label);
        document.body.appendChild(el);
        if (transientMs) setTimeout(() => { if (document.getElementById(BANNER_ID) === el) el.remove(); }, transientMs);
    }
    function removeBanner() {
        const el = document.getElementById(BANNER_ID);
        if (el) el.remove();
    }

    function suspendOne(videoId, reason) {
        const session = sessions[videoId];
        if (!session || session.keepAlive) return;
        const desc = { port: session.port, name: session.name, onState: session.onState };
        closeSession(videoId);
        suspended[videoId] = desc;
        showNote(videoId, reason);
    }

    function suspendAll(reason) {
        saverReason = reason;
        Object.keys(sessions).forEach((id) => suspendOne(id, reason));
        // 무조작 일시정지는 사용자가 화면 앞에 있을 수 있으므로 페이지 배너로도 알린다(탭 숨김은 어차피 안 보임).
        if (reason === 'idle' && Object.keys(suspended).length)
            showBanner('장시간 조작이 없어 영상을 일시정지했습니다 · 마우스를 움직이면 재생됩니다', 'pause_circle');
    }

    function resumeAll() {
        const wasIdle = saverReason === 'idle';
        saverReason = null;
        removeBanner();
        let resumedCount = 0;
        Object.keys(suspended).forEach((id) => {
            const desc = suspended[id];
            delete suspended[id];
            removeNote(id);
            begin(id, desc);
            resumedCount++;
        });
        // 무조작 일시정지에서 깨어난 경우는 입력 즉시 재생돼 배너를 읽을 새가 없다 → 재개 토스트로 사후 고지.
        if (wasIdle && resumedCount) showBanner('영상 재생을 재개했습니다', 'play_circle', 3000);
    }

    function onActivity() {
        lastInputAt = Date.now();
        if (saverReason === 'idle') resumeAll();
    }
    ['pointermove', 'pointerdown', 'keydown', 'wheel', 'touchstart'].forEach((ev) =>
        window.addEventListener(ev, onActivity, { passive: true, capture: true }));

    document.addEventListener('visibilitychange', () => {
        if (document.hidden) suspendAll('hidden');
        else { lastInputAt = Date.now(); resumeAll(); }
    });

    setInterval(() => {
        if (!idleEnabled || saverReason || document.hidden) return;
        if (Date.now() - lastInputAt < idleLimitMs) return;
        if (!Object.keys(sessions).some((id) => !sessions[id].keepAlive)) return;
        suspendAll('idle');
    }, IDLE_CHECK_MS);

    return {
        // onState(optional): RTCPeerConnection.connectionState 가 바뀔 때마다 호출됨(per-stream 헬스).
        start(videoId, port, name, onState) {
            this.stop(videoId); // 중복 방지
            const desc = { port, name, onState: onState || null };
            // 백그라운드 탭에서의 시작 요청(숨긴 탭에서 페이지 초기화 등)은 바로 보류 — 탭 복귀 시 재생.
            if (document.hidden) { suspended[videoId] = desc; showNote(videoId, 'hidden'); return; }
            begin(videoId, desc);
        },

        stop(videoId) {
            if (suspended[videoId]) { delete suspended[videoId]; removeNote(videoId); }
            closeSession(videoId);
        },

        stopAll() {
            Object.keys(sessions).forEach((id) => this.stop(id));
            Object.keys(suspended).forEach((id) => this.stop(id));
        },

        // 무조작 일시정지 설정 주입 — /api/cctv/config 의 idlePauseEnabled/idlePauseMinutes 를
        // 페이지(cctv.html, cctv-wall.js)가 로드 직후 넘긴다. 저장 즉시 반영도 같은 경로.
        // 끄는 순간 이미 무조작 일시정지 중이면 바로 재생을 복구한다.
        configureSaver(opts) {
            if (!opts) return;
            if (typeof opts.idleEnabled === 'boolean') idleEnabled = opts.idleEnabled;
            const min = Number(opts.idleMinutes);
            if (Number.isFinite(min) && min >= 1) idleLimitMs = min * 60 * 1000;
            if (!idleEnabled && saverReason === 'idle') resumeAll();
        },

        // 진단용 — DevTools 콘솔에서 cctvWhep.saverState() 로 절전 가드 현재 상태를 본다.
        // idleLimitMs 가 기대값(분×60000)인지, idleForMs 가 실제로 쌓이고 있는지 확인.
        saverState() {
            return {
                idleEnabled,
                idleLimitMs,
                saverReason,
                idleForMs: Date.now() - lastInputAt,
                activeSessions: Object.keys(sessions),
                suspendedSessions: Object.keys(suspended),
            };
        },

        // 절전 가드 면제 토글 — PiP 처럼 탭이 안 보여도 계속 봐야 하는 세션이 켠다.
        // 면제 해제 시 절전이 이미 발동 중이면 그 세션도 즉시 보류한다.
        setKeepAlive(videoId, on) {
            const session = sessions[videoId];
            if (!session) return;
            session.keepAlive = !!on;
            if (!on && saverReason) suspendOne(videoId, saverReason);
        },
    };
})();
