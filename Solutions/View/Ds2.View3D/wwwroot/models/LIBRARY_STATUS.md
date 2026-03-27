# Ds2.View3D 3D 모델 라이브러리 현황

## 📊 전체 통계
- **총 모델 수**: 29개
- **라이브러리 버전**: 2.0.0
- **렌더링 스타일**: Cartoon/Toon (Three.js MeshToonMaterial)

## 📁 등록된 모델 목록

### 🤖 로봇 / 매니퓰레이터 (8개)
| 모델명 | 파일명 | 명령어 | 높이 |
|--------|--------|--------|------|
| AGV | AGV.js | MOVE, TURN, STOP | 1.5 |
| Robot_6Axis | Robot_6Axis.js | CMD1, CMD2, HOME | 3.0 |
| Robot_SCARA | Robot_SCARA.js | POS1, POS2, HOME | 2.5 |
| Robot_Delta | Robot_Delta.js | PICK, PLACE, HOME | 3.0 |
| Robot_Collaborative | Robot_Collaborative.js | ASSIST, WORK, HOME | 2.8 |
| Robot_Gantry | Robot_Gantry.js | MOVE_X, MOVE_Y, MOVE_Z | 3.5 |
| Gripper_Pneumatic | Gripper_Pneumatic.js | GRIP, RELEASE, HOLD | 1.5 |
| Gripper_Vacuum | Gripper_Vacuum.js | SUCTION_ON, SUCTION_OFF, PICKUP | 1.2 |

### 📦 이송 / 컨베이어 장치 (13개)
| 모델명 | 파일명 | 명령어 | 높이 |
|--------|--------|--------|------|
| Conveyor | Conveyor.js | MOVE, STOP | 2.0 |
| Conveyor_Belt_Animated | Conveyor_Belt_Animated.js | RUN, STOP, REVERSE | 1.5 |
| Lifter | Lifter.js | UP, DOWN | 2.5 |
| Lift_Table | Lift_Table.js | RAISE, LOWER, HOLD | 2.5 |
| Pusher | Pusher.js | FWD, BWD | 1.8 |
| Pusher_Pneumatic | Pusher_Pneumatic.js | PUSH, RETRACT, HOLD | 1.5 |
| Rotary_Table | Rotary_Table.js | ROTATE_CW, ROTATE_CCW, STOP | 1.5 |
| Transfer_Shuttle | Transfer_Shuttle.js | SHUTTLE_LEFT, SHUTTLE_RIGHT, CENTER | 1.5 |
| Turntable | Turntable.js | TURN_90, TURN_180, RESET | 1.2 |
| Elevator_Vertical | Elevator_Vertical.js | UP, DOWN, FLOOR_1, FLOOR_2, FLOOR_3 | 4.0 |
| Tilter | Tilter.js | TILT_FORWARD, TILT_BACK, LEVEL | 2.0 |
| Stacker | Stacker.js | UP, DOWN, EXTEND | 3.0 |
| Sorter_Diverter | Sorter_Diverter.js | DIVERT_LEFT, DIVERT_RIGHT, CENTER | 1.2 |

### 🏗️ 크레인 / 호이스트 (3개)
| 모델명 | 파일명 | 명령어 | 높이 |
|--------|--------|--------|------|
| Crane_Overhead_Animated | Crane_Overhead_Animated.js | MOVE_X, MOVE_Y, HOIST_UP, HOIST_DOWN | 4.0 |
| Hoist | Hoist.js | LIFT, LOWER, HOLD | 3.0 |
| Jib_Crane | Jib_Crane.js | ROTATE, EXTEND, RETRACT, HOIST | 3.5 |

### 🚪 게이트 / 도어 (3개)
| 모델명 | 파일명 | 명령어 | 높이 |
|--------|--------|--------|------|
| Door_Sliding | Door_Sliding.js | OPEN, CLOSE, HOLD | 2.5 |
| Gate_Vertical | Gate_Vertical.js | RAISE, LOWER, STOP | 2.5 |
| Barrier_Arm | Barrier_Arm.js | RAISE, LOWER, STOP | 2.0 |

### ⚙️ 기타 (2개)
| 모델명 | 파일명 | 명령어 | 높이 |
|--------|--------|--------|------|
| Unit | Unit.js | ADV, RET | 2.0 |
| Dummy | Dummy.js | (없음) | 2.0 |

## 🎨 모델 구조 표준

모든 모델은 다음 구조를 따릅니다:

```javascript
class DeviceName {
  // Toon 렌더링용 그라디언트 맵 생성
  static createToonGradient(THREE) { ... }

  // 외곽선(Outline) 메시 생성
  static createOutline(geometry, color = 0x000000, thickness = 1.10) { ... }

  // 3D 모델 생성
  static create(THREE, options = {}) { ... }

  // 상태 업데이트 (R/G/F/H)
  static updateState(device, state) { ... }

  // 애니메이션 실행
  static animate(device, direction, speed) { ... }
}
```

## 🌐 갤러리 페이지

### gallery-extended.html (전체 라이브러리)
- 모든 29개 모델 표시
- 카테고리별 분류
- 실시간 3D 프리뷰
- 명령어 버튼 제공
- 상태 변경 버튼 (R/G/F/H)

### gallery.html (원본 갤러리)
- 기존 모델만 표시

## 🚀 사용 방법

### 1. 서버 실행
```batch
cd C:\ds\ds2\Solutions\View\Ds2.View3D\wwwroot\models
start-server.bat
```

### 2. 브라우저에서 확인
자동으로 `http://localhost:8000/gallery-extended.html` 이 열립니다.

### 3. 프로그래밍 방식 사용

#### 비동기 로드
```javascript
const model = await Ds2View3DLibrary.create('Robot_Delta', THREE);
scene.add(model);
```

#### 전체 사전 로드
```javascript
await Ds2View3DLibrary.preloadAll();
const model = Ds2View3DLibrary.createSync('AGV', THREE);
```

#### 상태 변경
```javascript
Ds2View3DLibrary.updateState(model, 'G'); // Green state
```

#### 애니메이션 실행
```javascript
const DeviceClass = window.Robot_Delta;
DeviceClass.animate(model, 'PICK', 0.03);
```

## 📂 파일 구조

```
wwwroot/models/
├── index.js                    # 라이브러리 레지스트리 (29개 모델 등록)
├── gallery.html                # 원본 갤러리
├── gallery-extended.html       # 전체 라이브러리 갤러리 (29개)
├── start-server.bat            # 서버 실행 스크립트
├── LIBRARY_STATUS.md           # 이 문서
└── Lib3D/
    ├── AGV.js
    ├── Robot_6Axis.js
    ├── Robot_SCARA.js
    ├── Robot_Delta.js
    ├── Robot_Collaborative.js
    ├── Robot_Gantry.js
    ├── Gripper_Pneumatic.js
    ├── Gripper_Vacuum.js
    ├── Conveyor.js
    ├── Conveyor_Belt_Animated.js
    ├── Lifter.js
    ├── Lift_Table.js
    ├── Pusher.js
    ├── Pusher_Pneumatic.js
    ├── Rotary_Table.js
    ├── Transfer_Shuttle.js
    ├── Turntable.js
    ├── Elevator_Vertical.js
    ├── Tilter.js
    ├── Stacker.js
    ├── Sorter_Diverter.js
    ├── Crane_Overhead_Animated.js
    ├── Hoist.js
    ├── Jib_Crane.js
    ├── Door_Sliding.js
    ├── Gate_Vertical.js
    ├── Barrier_Arm.js
    ├── Unit.js
    └── Dummy.js
```

## ✅ 시스템 상태

- ✅ 29개 모델 파일 모두 생성 완료
- ✅ index.js 레지스트리 업데이트 완료
- ✅ gallery-extended.html 생성 완료
- ✅ start-server.bat 업데이트 완료
- ✅ 모든 모델이 동일한 Cartoon/Toon 스타일 적용
- ✅ 모든 모델이 동작(애니메이션) 포함
- ✅ 카테고리별 분류 완료

## 🎯 주요 특징

1. **통일된 렌더링 스타일**: 모든 모델이 Cartoon/Toon 렌더링 사용
2. **외곽선 효과**: 검은색 외곽선으로 만화 스타일 강조
3. **동적 애니메이션**: 각 모델의 특성에 맞는 동작 구현
4. **상태 시각화**: R/G/F/H 상태를 색상으로 표현
5. **모듈식 구조**: 각 모델이 독립적인 클래스로 구현
6. **동적 로딩**: 필요한 모델만 비동기로 로드 가능

## 📝 참고사항

- Three.js r128 버전 사용
- OrbitControls로 3D 뷰 조작 가능
- Python HTTP 서버 필요 (포트 8000)
- 모든 모델은 targetHeight 파라미터로 크기 조절 가능
- SystemType → Preset 매핑은 F# ContextBuilder.modelTypeMap에서 관리
