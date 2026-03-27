# Ds2.View3D DeviceType 라이브러리

**명령어 기반 DeviceType 매핑 시스템**

## 📋 Command → DeviceType 매핑

```
"ADV;RET"         => "Unit"
"UP;DOWN"         => "Lifter"
"FWD;BWD"         => "Pusher"
"MOVE;STOP"       => "Conveyor"
"CMD1;CMD2;HOME"  => "Robot_6Axis"
"POS1;POS2;HOME"  => "Robot_SCARA"
_                 => "Dummy"
```

## 📁 폴더 구조

```
models/
├── index.js                 # DeviceType 라이브러리
├── gallery.html             # 7개 모델 갤러리
├── start-server.bat         # HTTP 서버
├── README.md
│
└── Lib3D/                   # 7개 3D 모델
    ├── Unit.js              # 전진/후진 유닛
    ├── Lifter.js            # 승강 리프터
    ├── Pusher.js            # 푸셔
    ├── Conveyor.js          # 컨베이어
    ├── Robot_6Axis.js       # 6축 로봇
    ├── Robot_SCARA.js       # SCARA 로봇
    └── Dummy.js             # 미등록 장치 fallback
```

## 🚀 사용 방법

### HTTP 서버 실행

```bash
start-server.bat
# 브라우저: http://localhost:8000/gallery.html
```

## 💻 DeviceType API

### 기본 사용

```javascript
// DeviceType으로 모델 생성
const model = await Ds2View3DLibrary.create('Unit', THREE);
scene.add(model);

// 상태 변경
Ds2View3DLibrary.updateState(model, 'R');  // Ready (녹색)
Ds2View3DLibrary.updateState(model, 'G');  // Going (노란색)
Ds2View3DLibrary.updateState(model, 'F');  // Finish (파란색)
Ds2View3DLibrary.updateState(model, 'H');  // Homing (회색)
```

### 미등록 DeviceType 자동 처리

```javascript
// 등록되지 않은 DeviceType은 자동으로 Dummy 표시
const unknownModel = await Ds2View3DLibrary.create('SomeUnknownDevice', THREE);
// ⚠️ Console: "DeviceType 'SomeUnknownDevice' not found. Using Dummy."
// 결과: "?" 표시 + SomeUnknownDevice 라벨
```

## 📦 등록된 DeviceType (6개)

### 1️⃣ Unit (ADV;RET)
- **명령어**: ADV (Advance), RET (Retract)
- **용도**: 전진/후진 동작
- **높이**: 2.0m

### 2️⃣ Lifter (UP;DOWN)
- **명령어**: UP, DOWN
- **용도**: 수직 승강
- **높이**: 2.5m

### 3️⃣ Pusher (FWD;BWD)
- **명령어**: FWD (Forward), BWD (Backward)
- **용도**: 제품 밀어내기/당기기
- **높이**: 1.8m

### 4️⃣ Conveyor (MOVE;STOP)
- **명령어**: MOVE, STOP
- **용도**: 컨베이어 이송
- **높이**: 2.0m

### 5️⃣ Robot_6Axis (CMD1;CMD2;HOME)
- **명령어**: CMD1, CMD2, HOME
- **용도**: 6축 산업용 로봇 작업
- **높이**: 3.0m

### 6️⃣ Robot_SCARA (POS1;POS2;HOME)
- **명령어**: POS1, POS2, HOME
- **용도**: SCARA 로봇 작업
- **높이**: 2.5m

### ❓ Dummy (Fallback)
- **용도**: 미등록 장치 기본 형상
- **높이**: 2.0m

## 🎨 상태 코드

| 코드 | 의미 | 색상 |
|------|------|------|
| R | Ready (준비 완료) | 🟢 녹색 |
| G | Going (작동 중) | 🟡 노란색 |
| F | Finish (완료) | 🔵 파란색 |
| H | Homing (원점 복귀) | ⚫ 회색 |

## ✨ 주요 특징

1. **명령어 기반 매핑**: Command → DeviceType 자동 변환
2. **자동 Fallback**: 미등록 Command는 Dummy 표시
3. **단일 폴더 구조**: Lib3D 하나로 통합 관리
4. **동적 로딩**: 필요한 모델만 비동기 로드

## 🛠️ 기술 스택

- **Three.js r128** - 3D 렌더링
- **OrbitControls** - 카메라 컨트롤
- **PBR Materials** - 물리 기반 렌더링
- **Dynamic Loading** - 비동기 모델 로딩

## 📝 버전

- **v2.0.0** - 명령어 기반 DeviceType 시스템

Ds2.View3D © 2024
