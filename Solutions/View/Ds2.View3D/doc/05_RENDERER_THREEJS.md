# Ds2 3D View — 05. Three.js 렌더러 레이어

**파일**: `wwwroot/js/three-interop.js` (~5600줄)
**라이브러리**: Three.js r128, OrbitControls

---

## 1. 전체 구조

`Ev23DViewer` 전역 객체 하나에 모든 씬 로직이 포함:

```javascript
const Ev23DViewer = {
    _scenes: {},         // { elementId → sceneData }
    stateColors: { R, G, F, H },

    // Public API
    init(), updateWorkStates(), updateDeviceStates(),
    fitCameraToDevices(), autoLayoutDevices(), resetDeviceLayout(),
    addDeviceAtPosition(), placeAllDevices(),
    setCallSelectionCallback(),

    // Private
    _createFactoryScene(), _createDeviceStation(), _createRobotModel(),
    _createSmallDeviceModel(), _createGeneralDeviceModel(),
    _createApiDefCube(), _createDeviceLabel(),
    _fitCameraToScene(), _setupInteractionHandlers(),
    _handleClick(), _handleMouseDown(), _handleMouseMove(), _handleMouseUp(),
    _getRaycasterIntersects(), _setupLighting(),
    _loadDevicePositions(), _saveDevicePositions(),
    ...
}
```

---

## 2. 씬 초기화 (`init`)

```javascript
Ev23DViewer.init(elementId, config)
```

**config 필드**:

| 필드 | 타입 | 설명 |
|------|------|------|
| `works` | `Work[]` | Work 기반 씬용 (현재 Promaker에서는 빈 배열) |
| `flowZones` | `FlowZone[]` | 바닥 구역 색상 영역 |
| `floorSize` | `number` | 바닥 평면 크기 (기본 100) |
| `connections` | `Connection[]` | Work 간 연결선 |

**초기화 순서**:
1. `THREE.Scene` 생성, 배경색 `0x0f172a`, fog 적용
2. `PerspectiveCamera(50°, aspect, 0.1, 1000)`
3. `WebGLRenderer(antialias)`, 그림자 활성화
4. `OrbitControls` — LEFT:ROTATE, MIDDLE/RIGHT:PAN
5. `floorSize` 기반 fog/maxDistance 업데이트
6. `_setupLighting(scene)` — 점광원 3개 + AmbientLight
7. `_createFactoryScene(scene, works, flowZones, floorSize)` — 바닥/그리드/구역
8. `_fitCameraToScene()` (works가 있을 경우)
9. Animation loop 시작
10. `_setupInteractionHandlers(elementId)` — 클릭/드래그 이벤트

---

## 3. 바닥/씬 생성 (`_createFactoryScene`)

```javascript
_createFactoryScene(scene, works, flowZones, floorSize)
```

생성 요소:
- **Ground plane**: `PlaneGeometry(floorSize, floorSize)`, 회색 `0x4a5563`
- **GridHelper**: `(floorSize, max(floorSize/2, 50))` — 2단위 셀
- **FlowZone 영역**: 반투명 평면(`opacity:0.15`) + 테두리 선 + 이름 라벨
- **Work 스테이션**: works 배열에서 `_createStation()` 호출 (현재 Promaker에서는 빈 배열)

---

## 4. Device 스테이션 (`_createDeviceStation`)

```javascript
_createDeviceStation(device, index) → { group, indicator }
```

**group** (`THREE.Group`):
- position = `(device.posX, 0, device.posZ)`
- userData.deviceId = device.id

**model** (indicator) — DeviceType에 따라:

| DeviceType | 생성 함수 | 높이 | 특징 |
|-----------|-----------|------|------|
| `robot` | `_createRobotModel()` | 3 | 관절 애니메이션, 용접 파티클 |
| `small` / `cylinder` | `_createSmallDeviceModel()` | 1.5 | 실린더형 |
| `general` | `_createGeneralDeviceModel()` | 2 | 박스형 |

**ApiDef 큐브**: model 위에 1단위 간격으로 배치
- `_createApiDefCube(apiDef, deviceId)` — `BoxGeometry(0.5)`
- callerCount > 1이면 배지 스프라이트 추가

**이름 라벨**: Canvas 2D 텍스처 기반 스프라이트

---

## 5. 로봇 모델 (`_createRobotModel`)

6축 로봇 관절 구조:
```
Group (isRobot=true)
  └── robotJ1     (base, Box)
       └── robotTower   (body, Box)
            └── robotUpperArm (Box)
                 └── robotForearm  (Box)
                      └── robotGripper
                           ├── robotLeftFinger
                           └── robotRightFinger
```

`userData.baseRotationZ`, `userData.basePositionZ` — 원위치 복원용 저장.

---

## 6. 상태 색상

```javascript
stateColors: {
    R: { hex: 0x10b981 },  // Green — Ready
    G: { hex: 0xeab308 },  // Yellow — Going
    F: { hex: 0x3b82f6 },  // Blue — Finish
    H: { hex: 0x6b7280 }   // Gray — Homing
}
```

---

## 7. 상태 업데이트

### updateWorkStates
```javascript
Ev23DViewer.updateWorkStates(elementId, [{id, state}])
```
- `sceneData.stationMeshes[id]` (indicator mesh) traverse
- `emissive` 색 + `emissiveIntensity` 업데이트
- `workData.state` 갱신 → 애니메이션 루프 반영

### updateDeviceStates
```javascript
Ev23DViewer.updateDeviceStates(elementId, [{id, state}])
```
- `sceneData.deviceMeshes[id]` (model) 전체 traverse
- 모든 자식 Mesh의 `material.color`, `material.emissive` 업데이트
- `device.state` + `deviceData.state` 갱신 → 로봇 애니메이션 루프 반영

---

## 8. Animation Loop

매 프레임:
1. `scene.traverse` → `isAASIcon`: 부유 효과
2. `scene.traverse` → `isRobot`:
   - `state === 'G'`: 관절 oscillation + 용접 파티클 생성/업데이트
   - `state !== 'G'`: 파티클 제거, 초기 pose 복원
3. `controls.update()` + `renderer.render()`

---

## 9. Lighting

```javascript
_setupLighting(scene):
  AmbientLight(0x334155, 0.6)            // 기본 환경광
  DirectionalLight(0xffffff, 0.8)         // 태양광 (그림자 캐스터)
  PointLight(0x4a9eff, 1.0, 50)   pos(10,15,10)
  PointLight(0xff6b35, 0.5, 30)   pos(-10,10,-5)
  PointLight(0x22c55e, 0.3, 25)   pos(0,8,15)
```

---

## 10. Public 유틸 함수 (추가됨)

| 함수 | 설명 |
|------|------|
| `fitCameraToDevices(elementId)` | 배치된 모든 Device bounding box 기반 카메라 맞춤 |
| `autoLayoutDevices(elementId)` | Flow 그룹 격자로 재배치 (모델 재생성 없음) |
| `resetDeviceLayout(elementId)` | localStorage 삭제 + autoLayout |
| `screenToWorldPosition(elementId, clientX, clientY)` | 화면 좌표 → 바닥 월드 좌표 |
| `getStoredDevicePositions(elementId)` | localStorage에서 저장된 위치 조회 |
