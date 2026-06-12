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
window.cctvWhep = (function () {
    const sessions = {}; // videoId -> { pc, pendingPc, port, name, closed, retryTimer, refreshTimer }

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

    return {
        // onState(optional): RTCPeerConnection.connectionState 가 바뀔 때마다 호출됨(per-stream 헬스).
        start(videoId, port, name, onState) {
            this.stop(videoId); // 중복 방지
            const session = {
                videoId, port, name, onState: onState || null,
                closed: false, pc: null, pendingPc: null, retryTimer: null, refreshTimer: null,
            };
            sessions[videoId] = session;
            negotiate(session);
        },

        stop(videoId) {
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
        },

        stopAll() {
            Object.keys(sessions).forEach((id) => this.stop(id));
        },
    };
})();
