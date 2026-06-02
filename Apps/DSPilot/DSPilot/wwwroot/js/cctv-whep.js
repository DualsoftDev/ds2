// CCTV WebRTC(WHEP) 플레이어.
// MediaMTX 가 RTSP 를 받아 WebRTC 로 재게시한 스트림을 브라우저에서 시청한다.
// MediaMTX WHEP 엔드포인트: http://<host>:<port>/<cameraName>/whep
//
// host 는 브라우저가 DSPilot 에 접속한 호스트와 동일하게 사용한다(원격 접속/방화벽 대비).
// trickle ICE 대신 ICE gathering 완료를 기다린 뒤 한 번에 offer 를 POST 하는 단순 흐름.
window.cctvWhep = (function () {
    const sessions = {}; // videoId -> { pc, port, name, closed, retryTimer }

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

    async function negotiate(session) {
        const { videoId, port, name } = session;
        const video = document.getElementById(videoId);
        if (!video || session.closed) return;

        const pc = new RTCPeerConnection({
            iceServers: [] // 로컬/온프렘 — STUN 불필요
        });
        session.pc = pc;

        pc.addTransceiver('video', { direction: 'recvonly' });
        pc.addTransceiver('audio', { direction: 'recvonly' });

        pc.ontrack = (e) => {
            if (e.streams && e.streams[0]) {
                video.srcObject = e.streams[0];
                video.play().catch(() => { /* autoplay 정책 — muted 라 보통 통과 */ });
            }
        };

        pc.onconnectionstatechange = () => {
            if (session.closed) return;
            // 연결 상태를 호출자에게 통지(옵션) — 실제 스트림 헬스 표시용(전역 sync 상태와 구분).
            if (session.onState) { try { session.onState(pc.connectionState); } catch (e) {} }
            if (pc.connectionState === 'failed' || pc.connectionState === 'disconnected') {
                scheduleReconnect(session);
            }
        };

        try {
            const offer = await pc.createOffer();
            await pc.setLocalDescription(offer);
            await waitIceGathering(pc);

            const res = await fetch(buildUrl(port, name), {
                method: 'POST',
                headers: { 'Content-Type': 'application/sdp' },
                body: pc.localDescription.sdp,
            });
            if (!res.ok) throw new Error(`WHEP ${res.status}`);

            const answerSdp = await res.text();
            if (session.closed) return;
            await pc.setRemoteDescription({ type: 'answer', sdp: answerSdp });
        } catch (err) {
            console.warn(`[cctv] ${name} 연결 실패:`, err.message);
            scheduleReconnect(session);
        }
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
            const session = { videoId, port, name, onState: onState || null, closed: false, pc: null, retryTimer: null };
            sessions[videoId] = session;
            negotiate(session);
        },

        stop(videoId) {
            const session = sessions[videoId];
            if (!session) return;
            session.closed = true;
            if (session.retryTimer) { clearTimeout(session.retryTimer); session.retryTimer = null; }
            try { session.pc && session.pc.close(); } catch { }
            const video = document.getElementById(videoId);
            if (video) video.srcObject = null;
            delete sessions[videoId];
        },

        stopAll() {
            Object.keys(sessions).forEach((id) => this.stop(id));
        },
    };
})();
