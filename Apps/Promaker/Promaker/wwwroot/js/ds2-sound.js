/**
 * Ds2Sound — Web Audio API synthesizer for Ds2 3D simulation view
 *
 * 설비가 Going 상태가 되면 타입별 고유 음정을 재생합니다.
 * 모든 음정은 C장조 펜타토닉 스케일(C·D·E·G·A)로 배정되어
 * 여러 설비가 동시에 동작해도 자연스러운 화음이 형성됩니다.
 */
const Ds2Sound = (() => {

    // ── AudioContext (lazy init) ──────────────────────────────────────────────
    let _ctx   = null;
    let _bus   = null;   // master gain
    let _rev   = null;   // reverb send gain

    // deviceId → { oscs: OscillatorNode[], env: GainNode, stop() }
    const _active = new Map();

    // ── Musical notes (Hz) — C장조 펜타토닉 3옥타브 ─────────────────────────
    const N = {
        C3:130.81, D3:146.83, E3:164.81, G3:196.00, A3:220.00,
        C4:261.63, D4:293.66, E4:329.63, G4:392.00, A4:440.00,
        C5:523.25, D5:587.33, E5:659.25, G5:783.99, A5:880.00
    };

    // ── Device sound definitions ──────────────────────────────────────────────
    // freqs : 동시에 울릴 주파수 배열 (화음 또는 단음)
    // wave  : 파형 — sine(부드러움) / triangle(따뜻함) / sawtooth(기계음) / square(날카로움)
    // gain  : 개별 볼륨 (0~1)
    // a/d/s/r: Attack · Decay · Sustain비율 · Release (초)
    const SOUNDS = {
        // ── 기본 8종 ────────────────────────────────────────────────────────
        'Unit':                   { freqs:[N.D4],              wave:'sawtooth',  gain:0.18, a:0.08, d:0.12, s:0.70, r:0.35 },
        'Lifter':                 { freqs:[N.G4, N.G4*1.005], wave:'sine',      gain:0.20, a:0.25, d:0.10, s:0.80, r:0.55 },
        'Pusher':                 { freqs:[N.A5],              wave:'square',    gain:0.11, a:0.01, d:0.15, s:0.30, r:0.20 },
        'Conveyor':               { freqs:[N.A3, N.E4],        wave:'sawtooth',  gain:0.15, a:0.20, d:0.10, s:0.75, r:0.40 },
        'Robot_6Axis':            { freqs:[N.C4, N.E4, N.G4], wave:'triangle',  gain:0.14, a:0.22, d:0.20, s:0.60, r:0.65 },
        'Robot_SCARA':            { freqs:[N.D4, N.G4],        wave:'triangle',  gain:0.14, a:0.18, d:0.12, s:0.65, r:0.55 },
        'AGV':                    { freqs:[N.E5],              wave:'sine',      gain:0.17, a:0.10, d:0.10, s:0.80, r:0.35 },
        'Stacker':                { freqs:[N.C4, N.G4],        wave:'sawtooth',  gain:0.16, a:0.25, d:0.15, s:0.70, r:0.55 },

        // ── 로봇/매니퓰레이터 ────────────────────────────────────────────────
        'Robot_Delta':            { freqs:[N.E4, N.A4],        wave:'triangle',  gain:0.14, a:0.15, d:0.15, s:0.62, r:0.50 },
        'Robot_Collaborative':    { freqs:[N.C5, N.E5],        wave:'sine',      gain:0.15, a:0.20, d:0.10, s:0.70, r:0.60 },
        'Robot_Gantry':           { freqs:[N.G3, N.D4, N.G4], wave:'sawtooth',  gain:0.13, a:0.28, d:0.18, s:0.65, r:0.70 },
        'Gripper_Pneumatic':      { freqs:[N.G5, N.D5],        wave:'square',    gain:0.11, a:0.02, d:0.12, s:0.40, r:0.25 },
        'Gripper_Vacuum':         { freqs:[N.E5],              wave:'sine',      gain:0.14, a:0.06, d:0.10, s:0.60, r:0.30 },

        // ── 이송/컨베이어 ────────────────────────────────────────────────────
        'Conveyor_Belt_Animated': { freqs:[N.A3, N.D4],        wave:'sawtooth',  gain:0.14, a:0.20, d:0.10, s:0.75, r:0.40 },
        'Lift_Table':             { freqs:[N.D4, N.A4],        wave:'sine',      gain:0.16, a:0.22, d:0.15, s:0.72, r:0.55 },
        'Rotary_Table':           { freqs:[N.G4, N.D5],        wave:'triangle',  gain:0.14, a:0.15, d:0.10, s:0.75, r:0.40 },
        'Transfer_Shuttle':       { freqs:[N.E4, N.E5],        wave:'sawtooth',  gain:0.14, a:0.12, d:0.10, s:0.72, r:0.38 },
        'Turntable':              { freqs:[N.A4, N.E5],        wave:'triangle',  gain:0.13, a:0.15, d:0.10, s:0.70, r:0.40 },
        'Elevator_Vertical':      { freqs:[N.C4, N.G4, N.C5], wave:'sine',      gain:0.13, a:0.32, d:0.20, s:0.70, r:0.65 },
        'Tilter':                 { freqs:[N.D5, N.A5],        wave:'triangle',  gain:0.12, a:0.15, d:0.12, s:0.65, r:0.42 },

        // ── 크레인/호이스트 ──────────────────────────────────────────────────
        'Crane_Overhead_Animated':{ freqs:[N.G3, N.G4],        wave:'sawtooth',  gain:0.16, a:0.30, d:0.20, s:0.65, r:0.70 },
        'Hoist':                  { freqs:[N.C4, N.G4],        wave:'sawtooth',  gain:0.15, a:0.25, d:0.15, s:0.70, r:0.62 },
        'Jib_Crane':              { freqs:[N.E3, N.G3, N.C4], wave:'sawtooth',  gain:0.14, a:0.30, d:0.20, s:0.62, r:0.70 },

        // ── 게이트/도어 ──────────────────────────────────────────────────────
        'Door_Sliding':           { freqs:[N.A4, N.E5],        wave:'sine',      gain:0.14, a:0.15, d:0.20, s:0.52, r:0.55 },
        'Gate_Vertical':          { freqs:[N.D5, N.G5],        wave:'triangle',  gain:0.12, a:0.15, d:0.15, s:0.55, r:0.42 },
        'Barrier_Arm':            { freqs:[N.E5, N.A5],        wave:'triangle',  gain:0.11, a:0.10, d:0.12, s:0.52, r:0.38 },

        // ── 특수 액추에이터 ──────────────────────────────────────────────────
        'Pusher_Pneumatic':       { freqs:[N.A5],              wave:'square',    gain:0.11, a:0.01, d:0.12, s:0.32, r:0.20 },
        'Sorter_Diverter':        { freqs:[N.C5, N.G5],        wave:'triangle',  gain:0.12, a:0.10, d:0.12, s:0.56, r:0.38 },

        'Dummy': null  // 미등록 — 무음
    };

    // ── AudioContext 초기화 ───────────────────────────────────────────────────
    function _boot() {
        if (_ctx) { if (_ctx.state === 'suspended') _ctx.resume(); return; }

        _ctx  = new (window.AudioContext || window.webkitAudioContext)();
        _bus  = _ctx.createGain();
        _bus.gain.value = Ds2Sound.volume;
        _bus.connect(_ctx.destination);

        // 간단한 피드백 딜레이 리버브 (외부 impulse 불필요)
        _rev = _ctx.createGain();
        _rev.gain.value = 0.28;

        const d1 = _ctx.createDelay(1.0); d1.delayTime.value = 0.17;
        const d2 = _ctx.createDelay(1.0); d2.delayTime.value = 0.25;
        const fb1 = _ctx.createGain(); fb1.gain.value = 0.22;
        const fb2 = _ctx.createGain(); fb2.gain.value = 0.16;
        const hpf = _ctx.createBiquadFilter();
        hpf.type = 'highpass'; hpf.frequency.value = 300;

        _rev.connect(hpf);
        hpf.connect(d1); hpf.connect(d2);
        d1.connect(fb1); d2.connect(fb2);
        fb1.connect(d1); fb2.connect(d2);
        d1.connect(_bus); d2.connect(_bus);
    }

    // ── 내부 API ─────────────────────────────────────────────────────────────
    const pub = {
        volume: 0.38,   // 마스터 볼륨 (0~1)
        muted:  false,

        setVolume(v) {
            this.volume = Math.max(0, Math.min(1, v));
            if (_bus) _bus.gain.setTargetAtTime(this.muted ? 0 : this.volume, _ctx.currentTime, 0.05);
        },

        setMuted(m) {
            this.muted = m;
            if (_bus) _bus.gain.setTargetAtTime(m ? 0 : this.volume, _ctx.currentTime, 0.05);
        },

        /** Going 상태 → 해당 설비 사운드 시작 */
        play(deviceId, modelType) {
            if (this.muted || _active.has(deviceId)) return;
            const def = SOUNDS[modelType];
            if (!def) return;

            _boot();
            const ctx = _ctx, now = ctx.currentTime;

            // ADSR 엔벨로프 게인
            const env = ctx.createGain();
            env.gain.setValueAtTime(0, now);
            env.gain.linearRampToValueAtTime(def.gain, now + def.a);
            env.gain.setTargetAtTime(def.gain * def.s, now + def.a + def.d, def.d * 0.5);

            // 저역 통과 필터 (따뜻한 음색)
            const lpf = ctx.createBiquadFilter();
            lpf.type = 'lowpass';
            lpf.frequency.value = 1800;
            lpf.Q.value = 0.8;

            env.connect(lpf);
            lpf.connect(_bus);
            if (_rev) lpf.connect(_rev);

            // 오실레이터 생성 (화음 각 음정)
            const oscs = def.freqs.map((freq, i) => {
                const osc = ctx.createOscillator();
                const og  = ctx.createGain();
                osc.type = def.wave;
                osc.frequency.value = freq;
                // 음색 풍부화: 약간의 디튠 (±4 cents)
                osc.detune.value = i === 0 ? 0 : (i % 2 === 0 ? 4 : -4);
                og.gain.value = i === 0 ? 1.0 : 0.55 / i;
                osc.connect(og); og.connect(env);
                osc.start(now);
                return osc;
            });

            const stop = () => {
                if (!_ctx) return;
                const t = _ctx.currentTime;
                env.gain.setTargetAtTime(0, t, def.r * 0.35);
                setTimeout(() => {
                    oscs.forEach(o => { try { o.stop(); } catch (_) {} });
                    _active.delete(deviceId);
                }, def.r * 1300);
            };

            _active.set(deviceId, { oscs, env, stop });
        },

        /** Going 해제 → 해당 설비 사운드 정지 */
        stop(deviceId) {
            const s = _active.get(deviceId);
            if (s) s.stop();
        },

        /** 시뮬레이션 정지 → 전체 사운드 즉시 페이드아웃 */
        stopAll() {
            _active.forEach(s => s.stop());
        }
    };

    return pub;
})();

if (typeof window !== 'undefined') window.Ds2Sound = Ds2Sound;
