# Ds2 3D View — 04. 레이아웃 알고리즘

**파일**: `LayoutAlgorithm.fs` (F# Core), `three-interop.js` (JS 재배치)

---

## 1. 상수

| 상수 | 값 | 설명 |
|------|-----|------|
| `DeviceSpacing` | `12.0` | Device 간격 (Three.js 단위) |
| `FlowGap` | `20.0` | Flow 그룹 간 X 방향 간격 |
| `WorkSpacing` | `10.0` | Work 간격 |
| `FlowLineSpacingZ` | `15.0` | 2-Flow 평행 배치 시 Z 간격 |

---

## 2. Device 자동 배치 (`layoutDevices`)

### 알고리즘: Flow별 그룹 격자

```
devices
  |-- groupBy FlowName
  |-- sortBy FlowName (알파벳)
  |
  +-- Flow A: [D1, D2, D3, D4]
  |     cols = ceil(sqrt(4)) = 2
  |
  |     D1(0,0)  D2(12,0)
  |     D3(0,12) D4(12,12)
  |     offsetX += cols * 12 + 20 = 44
  |
  +-- Flow B: [D5, D6, D7]
        cols = ceil(sqrt(3)) = 2

        D5(44,0)  D6(56,0)
        D7(44,12)
```

**수식**:
```
cols    = ceil(sqrt(n))
col     = i % cols
row     = i / cols
x       = offsetX + col * DeviceSpacing
z       = row * DeviceSpacing
offsetX += cols * DeviceSpacing + FlowGap  (다음 Flow)
```

Y는 항상 0.0 (바닥 기준).

---

## 3. Work 자동 배치 (`layoutWorks`)

Flow 수에 따라 3가지 전략:

### 경우 A: Flow 1개 — 정사각형 격자

```
cols = ceil(sqrt(n))
x = col * WorkSpacing
z = row * WorkSpacing
```

### 경우 B: Flow 2개 — 평행 수평 라인

```
Flow 0: z = 0
Flow 1: z = 15 (FlowLineSpacingZ)
각 Flow 내부: x = wi * WorkSpacing (1차원 배열)
```

### 경우 C: Flow 3개 이상 — Zone 격자 (Z축 누적)

```
Flow 0: z = 0     ~ rows0 * 10
Flow 1: z = rows0 * 10 + 20
Flow 2: z = ...
각 Flow 내부: 정사각형 격자
```

---

## 4. FlowZone 계산 (`computeFlowZones`)

Device 배치 좌표에서 Flow별 바운딩 박스 생성:

```
minX = min(device.X) - DeviceSpacing/2
maxX = max(device.X) + DeviceSpacing/2
minZ = min(device.Z) - DeviceSpacing/2
maxZ = max(device.Z) + DeviceSpacing/2

CenterX = (minX + maxX) / 2
CenterZ = (minZ + maxZ) / 2
SizeX   = maxX - minX
SizeZ   = maxZ - minZ
```

색상 팔레트 (순환):
```
["#4285F4", "#EA4335", "#FBBC05", "#34A853", "#FF6D01", "#46BDC6", "#7B1FA2", "#C2185B"]
```

---

## 5. 바닥 크기 계산 (ThreeDViewState.cs)

모든 Device 위치의 bounding box + 여유:

```csharp
var xs = devicePositions.Select(p => p.X);
var zs = devicePositions.Select(p => p.Z);
var spanX = xs.Max() - xs.Min();
var spanZ = zs.Max() - zs.Min();
floorSize = Math.Max(Math.Max(spanX, spanZ) + 40.0, 100.0);
```

`floorSize`는 `init` 메시지 config에 포함되어 JS로 전달됨.

JS에서:
```javascript
const groundGeo = new THREE.PlaneGeometry(floorSize, floorSize);
const gridDivisions = Math.max(Math.round(floorSize / 2), 50);
const grid = new THREE.GridHelper(floorSize, gridDivisions, ...);
scene.fog = new THREE.Fog(0x0f172a, floorSize, floorSize * 3);
controls.maxDistance = Math.max(300, floorSize * 3);
```

---

## 6. 카메라 맞춤 (`_fitCameraToScene`)

장비 bounding box 기반 카메라 위치 자동 조정:

```javascript
// 모든 장비의 posX, posZ로 min/max 계산
// padding = 10 적용
centerX = (minX + maxX) / 2
centerZ = (minZ + maxZ) / 2
maxSize = max(sizeX, sizeZ)
cameraDistance = maxSize * 1.3

camera.position.set(
    centerX,
    cameraDistance * 0.6,        // 높이
    centerZ + cameraDistance * 0.8  // 뒤쪽
)
controls.target.set(centerX, 0, centerZ)
```

---

## 7. JS 재배치 (`autoLayoutDevices`)

씬 이미 생성된 상태에서 Device 그룹을 재위치 이동 (모델 재생성 없음):

```javascript
autoLayoutDevices(elementId):
  1. sceneData.devices → flowGroups 재그룹
  2. Flow별 격자 좌표 계산 (C# LayoutAlgorithm과 동일 수식)
  3. scene.traverse() → deviceId 일치하는 Group 찾아 position.set(x, 0, z)
  4. device 객체의 posX, posZ 업데이트
  5. localStorage에 새 위치 저장
  6. fitCameraToDevices() 호출
```

---

## 8. 레이아웃 저장소 (localStorage, JS 측)

| 키 | 형식 | 설명 |
|----|------|------|
| `ev2_device_positions_{elementId}` | `{ deviceId: {x, z} }` | Device 배치 위치 |
| `ev2-3d-layout-{elementId}` | `{ workId: {x, y, z} }` | Work 배치 위치 |

`resetDeviceLayout(elementId)`:
1. `localStorage.removeItem(key)` 호출
2. `autoLayoutDevices()` 호출
