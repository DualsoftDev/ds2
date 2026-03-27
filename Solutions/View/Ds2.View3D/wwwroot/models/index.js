/**
 * Ds2.View3D 산업용 3D 모델 라이브러리 — Preset(모델) 파일 레지스트리
 *
 * 역할: Preset 이름 → Lib3D 파일 매핑 (파일 로딩 전담)
 * ※ SystemType → Preset 매핑은 F# ContextBuilder.modelTypeMap (단일 정의 위치)
 *
 * 등록된 Preset: 6개 + Dummy(fallback)
 *   Unit · Lifter · Pusher · Conveyor · Robot_6Axis · Robot_SCARA · Dummy
 */

// index.js 로드 시점에 현재 스크립트의 디렉토리 경로를 캡처
// (document.currentScript은 동기 실행 중에만 유효)
const _Ds2LibBase = (function() {
  const s = document.currentScript;
  if (s && s.src) return s.src.replace(/[^/]*$/, ''); // "https://host/models/"
  return './models/'; // 폴백
})();

const Ds2View3DLibrary = {
  version: '2.0.0',

  /**
   * 등록된 모델 목록 (모델명 → 파일/클래스/높이) - 총 29개
   */
  deviceTypes: {
    // 기존 모델 (9개)
    'Unit':        { file: 'Lib3D/Unit.js',        class: 'Unit',        height: 2.0, description: '전진/후진 유닛 (ADV;RET)',                         dirs: ['ADV','RET'] },
    'Lifter':      { file: 'Lib3D/Lifter.js',      class: 'Lifter',      height: 2.5, description: '승강 리프터 (UP;DOWN)',                             dirs: ['UP','DOWN'] },
    'Pusher':      { file: 'Lib3D/Pusher.js',      class: 'Pusher',      height: 1.8, description: '푸셔 (FWD;BWD)',                                    dirs: ['FWD','BWD'] },
    'Conveyor':    { file: 'Lib3D/Conveyor.js',    class: 'Conveyor',    height: 2.0, description: '컨베이어 (MOVE;STOP)',                              dirs: ['MOVE','STOP'] },
    'Robot_6Axis': { file: 'Lib3D/Robot_6Axis.js', class: 'Robot_6Axis', height: 3.0, description: '6축 로봇 (CMD1;CMD2;HOME)',                         dirs: ['CMD1','CMD2','HOME'] },
    'Robot_SCARA': { file: 'Lib3D/Robot_SCARA.js', class: 'Robot_SCARA', height: 2.5, description: 'SCARA 로봇 (POS1;POS2;HOME)',                       dirs: ['POS1','POS2','HOME'] },
    'AGV':         { file: 'Lib3D/AGV.js',         class: 'AGV',         height: 1.5, description: 'AGV (MOVE;TURN;STOP)',                              dirs: ['MOVE','TURN','STOP'] },
    'Stacker':     { file: 'Lib3D/Stacker.js',     class: 'Stacker',     height: 3.0, description: '스태커 (UP;DOWN;EXTEND)',                           dirs: ['UP','DOWN','EXTEND'] },
    'Dummy':       { file: 'Lib3D/Dummy.js',       class: 'Dummy',       height: 2.0, description: '미등록 장치',                                        dirs: [] },

    // 로봇/매니퓰레이터 (5개)
    'Robot_Delta':         { file: 'Lib3D/Robot_Delta.js',         class: 'Robot_Delta',         height: 3.0, description: '델타 로봇 (PICK;PLACE;HOME)',           dirs: ['PICK','PLACE','HOME'] },
    'Robot_Collaborative': { file: 'Lib3D/Robot_Collaborative.js', class: 'Robot_Collaborative', height: 2.8, description: '협동 로봇 (ASSIST;WORK;HOME)',           dirs: ['ASSIST','WORK','HOME'] },
    'Robot_Gantry':        { file: 'Lib3D/Robot_Gantry.js',        class: 'Robot_Gantry',        height: 3.5, description: '갠트리 로봇 (MOVE_X;MOVE_Y;MOVE_Z)',     dirs: ['MOVE_X','MOVE_Y','MOVE_Z'] },
    'Gripper_Pneumatic':   { file: 'Lib3D/Gripper_Pneumatic.js',   class: 'Gripper_Pneumatic',   height: 1.5, description: '공압 그리퍼 (GRIP;RELEASE;HOLD)',          dirs: ['GRIP','RELEASE','HOLD'] },
    'Gripper_Vacuum':      { file: 'Lib3D/Gripper_Vacuum.js',      class: 'Gripper_Vacuum',      height: 1.2, description: '진공 그리퍼 (SUCTION_ON;SUCTION_OFF;PICKUP)', dirs: ['SUCTION_ON','SUCTION_OFF','PICKUP'] },

    // 이송/컨베이어 (7개)
    'Conveyor_Belt_Animated': { file: 'Lib3D/Conveyor_Belt_Animated.js', class: 'Conveyor_Belt_Animated', height: 1.5, description: '애니메이션 컨베이어 (RUN;STOP;REVERSE)',           dirs: ['RUN','STOP','REVERSE'] },
    'Lift_Table':          { file: 'Lib3D/Lift_Table.js',          class: 'Lift_Table',          height: 2.5, description: '리프트 테이블 (RAISE;LOWER;HOLD)',              dirs: ['RAISE','LOWER','HOLD'] },
    'Rotary_Table':        { file: 'Lib3D/Rotary_Table.js',        class: 'Rotary_Table',        height: 1.5, description: '회전 테이블 (ROTATE_CW;ROTATE_CCW;STOP)',       dirs: ['ROTATE_CW','ROTATE_CCW','STOP'] },
    'Transfer_Shuttle':    { file: 'Lib3D/Transfer_Shuttle.js',    class: 'Transfer_Shuttle',    height: 1.5, description: '셔틀 이송기 (SHUTTLE_LEFT;SHUTTLE_RIGHT;CENTER)', dirs: ['SHUTTLE_LEFT','SHUTTLE_RIGHT','CENTER'] },
    'Turntable':           { file: 'Lib3D/Turntable.js',           class: 'Turntable',           height: 1.2, description: '턴테이블 (TURN_90;TURN_180;RESET)',              dirs: ['TURN_90','TURN_180','RESET'] },
    'Elevator_Vertical':   { file: 'Lib3D/Elevator_Vertical.js',   class: 'Elevator_Vertical',   height: 4.0, description: '수직 엘리베이터 (UP;DOWN;FLOOR_1;FLOOR_2;FLOOR_3)', dirs: ['UP','DOWN','FLOOR_1','FLOOR_2','FLOOR_3'] },
    'Tilter':              { file: 'Lib3D/Tilter.js',              class: 'Tilter',              height: 2.0, description: '틸터 (TILT_FORWARD;TILT_BACK;LEVEL)',            dirs: ['TILT_FORWARD','TILT_BACK','LEVEL'] },

    // 크레인/호이스트 (3개)
    'Crane_Overhead_Animated': { file: 'Lib3D/Crane_Overhead_Animated.js', class: 'Crane_Overhead_Animated', height: 4.0, description: '오버헤드 크레인 (MOVE_X;MOVE_Y;HOIST_UP;HOIST_DOWN)', dirs: ['MOVE_X','MOVE_Y','HOIST_UP','HOIST_DOWN'] },
    'Hoist':               { file: 'Lib3D/Hoist.js',               class: 'Hoist',               height: 3.0, description: '호이스트 (LIFT;LOWER;HOLD)',                    dirs: ['LIFT','LOWER','HOLD'] },
    'Jib_Crane':           { file: 'Lib3D/Jib_Crane.js',           class: 'Jib_Crane',           height: 3.5, description: '지브 크레인 (ROTATE;EXTEND;RETRACT;HOIST)',      dirs: ['ROTATE','EXTEND','RETRACT','HOIST'] },

    // 게이트/도어 (3개)
    'Door_Sliding':        { file: 'Lib3D/Door_Sliding.js',        class: 'Door_Sliding',        height: 2.5, description: '슬라이딩 도어 (OPEN;CLOSE;HOLD)',               dirs: ['OPEN','CLOSE','HOLD'] },
    'Gate_Vertical':       { file: 'Lib3D/Gate_Vertical.js',       class: 'Gate_Vertical',       height: 2.5, description: '수직 게이트 (RAISE;LOWER;STOP)',                 dirs: ['RAISE','LOWER','STOP'] },
    'Barrier_Arm':         { file: 'Lib3D/Barrier_Arm.js',         class: 'Barrier_Arm',         height: 2.0, description: '차단기 암 (RAISE;LOWER;STOP)',                   dirs: ['RAISE','LOWER','STOP'] },

    // 특수 액추에이터 (2개)
    'Pusher_Pneumatic':    { file: 'Lib3D/Pusher_Pneumatic.js',    class: 'Pusher_Pneumatic',    height: 1.5, description: '공압 푸셔 (PUSH;RETRACT;HOLD)',                  dirs: ['PUSH','RETRACT','HOLD'] },
    'Sorter_Diverter':     { file: 'Lib3D/Sorter_Diverter.js',     class: 'Sorter_Diverter',     height: 1.2, description: '다이버터 (DIVERT_LEFT;DIVERT_RIGHT;CENTER)',      dirs: ['DIVERT_LEFT','DIVERT_RIGHT','CENTER'] }
  },

  /**
   * 로드된 클래스 캐시
   */
  loadedClasses: {},

  /**
   * 모델명 유효성 확인 후 반환 (등록되지 않은 경우 'Dummy')
   * ※ SystemType → 모델명 변환은 F# ContextBuilder.modelTypeMap에서 수행됨
   * @param {string} modelType - 모델명 (e.g. 'Conveyor', 'Robot_6Axis')
   * @returns {string} 유효한 모델명
   */
  resolveModelType(modelType) {
    if (!modelType) return 'Dummy';
    return this.deviceTypes[modelType] ? modelType : 'Dummy';
  },

  /**
   * 모든 모델 클래스 사전 로드 (반환값 Promise 캐시됨 - 중복 호출 안전)
   * @returns {Promise<void>}
   */
  preloadAll() {
    if (!this._preloadPromise) {
      this._preloadPromise = (async () => {
        for (const [typeName, info] of Object.entries(this.deviceTypes)) {
          if (!this.loadedClasses[info.class]) {
            try {
              await this.loadModelClass(info.file, info.class);
            } catch (e) {
              console.warn(`⚠️ Failed to preload ${typeName}:`, e);
            }
          }
        }
        console.log('✅ Ds2View3DLibrary: all models preloaded');
      })();
    }
    return this._preloadPromise;
  },

  /**
   * 모델명으로 3D 모델 비동기 생성 (내부 preload 포함)
   * @param {string} modelType - 모델명 (e.g. 'Conveyor')
   * @param {THREE} THREE - Three.js 인스턴스
   * @param {Object} options - 옵션
   * @returns {Promise<THREE.Group>} 생성된 3D 모델
   */
  async create(modelType, THREE, options = {}) {
    const resolved = this.resolveModelType(modelType);
    const info = this.deviceTypes[resolved];

    if (!this.loadedClasses[info.class]) {
      await this.loadModelClass(info.file, info.class);
    }

    const ModelClass = this.loadedClasses[info.class];
    return ModelClass.create(THREE, { targetHeight: info.height, ...options });
  },

  /**
   * 모델 동기 생성 (preloadAll 완료 후 사용 가능)
   * @param {string} modelType - 모델명 (e.g. 'Conveyor')
   * @param {THREE} THREE - Three.js 인스턴스
   * @param {Object} options - 옵션
   * @returns {THREE.Group|null} 생성된 3D 모델 (미로드 시 null)
   */
  createSync(modelType, THREE, options = {}) {
    const resolved = this.resolveModelType(modelType);
    const info = this.deviceTypes[resolved] || this.deviceTypes['Dummy'];
    const ModelClass = this.loadedClasses[info.class] || this.loadedClasses['Dummy'];
    if (!ModelClass) return null;
    return ModelClass.create(THREE, { targetHeight: info.height, ...options });
  },

  /**
   * 모델 클래스 동적 로드 (_Ds2LibBase 기준 경로 사용)
   */
  async loadModelClass(file, className) {
    return new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = _Ds2LibBase + file;
      script.onload = () => {
        if (typeof window[className] !== 'undefined') {
          this.loadedClasses[className] = window[className];
          console.log(`✅ Loaded: ${className}`);
          resolve();
        } else {
          reject(new Error(`Class ${className} not found after loading ${file}`));
        }
      };
      script.onerror = () => reject(new Error(`Failed to load ${file}`));
      document.head.appendChild(script);
    });
  },

  /**
   * 상태 업데이트 (모델의 isStateIndicator 메시 색상 변경)
   */
  updateState(model, state) {
    const modelType = model.userData.deviceType;
    if (!modelType) return;
    const info = this.deviceTypes[modelType];
    if (!info) return;
    const ModelClass = this.loadedClasses[info.class];
    if (ModelClass && ModelClass.updateState) {
      ModelClass.updateState(model, state);
    }
  },

  /**
   * 등록된 모든 모델명 목록 (Dummy 제외)
   */
  getAllDeviceTypes() {
    return Object.keys(this.deviceTypes).filter(type => type !== 'Dummy');
  },

  /**
   * 모션 애니메이션 (apiDef 방향으로 1 프레임 진행)
   * updateState와 동일한 패턴 — loadedClasses 경유
   * @param {THREE.Group} model - Lib3D create()가 반환한 모델
   * @param {string} direction - ApiDef 이름 (예: 'ADV', 'UP', 'MOVE')
   * @param {number} speed - 보간 속도 (기본 0.08)
   * @returns {boolean} true = 목표 도달
   */
  animate(model, direction, speed = 0.08) {
    const modelType = model.userData.deviceType;
    if (!modelType) return false;
    const info = this.deviceTypes[modelType];
    if (!info) return false;
    const ModelClass = this.loadedClasses[info.class];
    if (!ModelClass || typeof ModelClass.animate !== 'function') return false;
    return ModelClass.animate(model, direction, speed);
  },

  /**
   * 모델 존재 여부 확인
   */
  hasDeviceType(deviceType) {
    return Object.prototype.hasOwnProperty.call(this.deviceTypes, deviceType);
  },

  /**
   * 모델 정보 조회 (없으면 Dummy 반환)
   */
  getDeviceInfo(deviceType) {
    return this.deviceTypes[deviceType] || this.deviceTypes['Dummy'];
  }
};

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Ds2View3DLibrary;
}

if (typeof window !== 'undefined') {
  window.Ds2View3DLibrary = Ds2View3DLibrary;
}
