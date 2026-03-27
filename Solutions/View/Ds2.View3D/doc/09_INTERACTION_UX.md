# Ds2 3D View — 09. 인터랙션 및 UX

---

## 1. 마우스 조작

| 동작 | 기능 |
|------|------|
| 좌클릭 + 드래그 | 카메라 회전 (OrbitControls) |
| 우클릭 + 드래그 | 카메라 패닝 (OrbitControls) |
| 중간 버튼 드래그 | 카메라 패닝 |
| 스크롤 휠 | 줌 인/아웃 |
| 좌클릭 (장비) | 장비/Call 선택 (Phase 2) |
| 우클릭 | 컨텍스트 메뉴 열기 |
| Alt + 좌클릭 드래그 | 장비 수동 이동 (Edit Mode) |

카메라 제약:
```javascript
controls.maxPolarAngle = Math.PI / 2;   // 바닥 아래로 못 내려감
controls.minDistance   = 10;
controls.maxDistance   = Math.max(300, floorSize * 3);
controls.enableDamping = true;
controls.dampingFactor = 0.05;
```

---

## 2. 키보드 단축키

| 키 | 기능 |
|----|------|
| `F` | 카메라 맞춤 (`fitCameraToDevices`) |
| `Escape` | 컨텍스트 메뉴 닫기 |

`facility3d.html` 내 `document.addEventListener('keydown', ...)`.

---

## 3. 컨텍스트 메뉴

우클릭 시 HTML 오버레이 메뉴 표시.

```
┌──────────────────────────┐
│ 📷 카메라 맞춤 (F)       │
│ 📐 전체 재배치           │
│ ──────────────────────── │
│ 🗑️ 저장 위치 초기화      │
└──────────────────────────┘
```

### 메뉴 항목 동작

| 항목 | 함수 | 설명 |
|------|------|------|
| 카메라 맞춤 | `fitCameraToDevices(SCENE_ID)` | 모든 Device bounding box 기반 카메라 자동 위치 |
| 전체 재배치 | `autoLayoutDevices(SCENE_ID)` | Flow 그룹 격자로 재배치 + localStorage 저장 |
| 저장 위치 초기화 | `resetDeviceLayout(SCENE_ID)` | localStorage 삭제 + autoLayout |

### 구현 (`facility3d.html`)

```javascript
// 표시
function showCtxMenu(x, y) {
    const menu = document.getElementById('ctx-menu');
    menu.style.display = 'block';
    // 뷰포트 경계 처리
    menu.style.left = (x + 200 > vw ? x - 200 : x) + 'px';
    menu.style.top  = (y + 130 > vh ? y - 130 : y) + 'px';
}

// 숨기기 — click outside 또는 메뉴 항목 선택 후
function hideCtxMenu() {
    document.getElementById('ctx-menu').style.display = 'none';
}
```

메뉴 스타일:
```css
background: #1e293b;
border: 1px solid #334155;
border-radius: 6px;
box-shadow: 0 4px 16px rgba(0,0,0,0.6);
```

---

## 4. `autoLayoutDevices` — 재배치 알고리즘

```javascript
autoLayoutDevices(elementId):
1. sceneData.devices → Flow별 그룹
2. 각 Flow: cols = ceil(sqrt(n)), flowSpacing 계산
3. 각 Device: scene.traverse()로 deviceId 매칭 Group 찾아 position.set(x, 0, z)
4. device.posX / posZ 업데이트
5. localStorage 저장
6. fitCameraToDevices() 호출
```

**특징**: 기존 3D 모델을 재생성하지 않고 이동만 수행 → 성능 효율적.

---

## 5. 장비 수동 드래그 (Edit Mode)

`Alt + 좌클릭` 시 편집 모드:
- OrbitControls 일시 비활성화
- 클릭된 Group (deviceId 또는 workId 보유)을 XZ 평면에서 드래그
- mouse up 시 `_saveDevicePositions()` 자동 저장
- 5px 미만 이동은 drag가 아닌 click으로 처리

---

## 6. 좌클릭 선택 (Phase 2)

현재 클릭 시 `_handleClick()` 호출되어 Debug 로그만 출력.
Phase 2에서 아래 연동 예정:

**Device 클릭**:
1. JS `invokeMethodAsync("OnDeviceSelected", deviceId)` 호출
2. `window.chrome.webview.postMessage(...)` → `View3DWindow.OnWebMessageReceived`
3. `ThreeDViewState.OnSelectionMessage("OnDeviceSelected", [deviceId])`
4. `SceneEngine.Select(DeviceSelected(guid))` → PropertyPanel 연동

**ApiDef 클릭**:
1. JS `invokeMethodAsync("OnApiDefSelected", deviceId, apiDefName)` 호출
2. C# PropertyPanel에 ApiDef 상세 표시

---

## 7. 상태 표시줄

씬 하단 고정 힌트:
```html
<div id="status-bar">
  우클릭: 메뉴 | F: 카메라 맞춤 | Alt+드래그: 장비 이동
</div>
```

스타일:
```css
position: fixed; bottom: 8px; left: 50%;
transform: translateX(-50%);
background: rgba(15,23,42,0.85);
color: #94a3b8; font-size: 11px;
pointer-events: none;
```

---

## 8. overlay (초기 대기 메시지)

```html
<div id="overlay">시뮬레이션을 시작하면 3D 장비 배치도가 표시됩니다.</div>
```

`init` 메시지 수신 시:
```javascript
document.getElementById('overlay').classList.add('hidden');
```

`hidden` 클래스: `display: none`

---

## 9. 창 관리

| 동작 | 결과 |
|------|------|
| [3D] 버튼 클릭 (창 없음) | 새 View3DWindow 생성 + BuildScene |
| [3D] 버튼 클릭 (창 열림) | `_view3DWindow.Activate()` |
| 창 닫기 | `SetWebViewSender(null)` + 이후 클릭 시 새 창 생성 |
| 프로젝트 닫기 / 새 파일 | `ThreeD.Reset()` → 기존 창은 빈 씬 유지 |

---

## 10. 포그 (Fog) 설정

장면 깊이감과 큰 씬에서의 성능 최적화:

```javascript
scene.fog = new THREE.Fog(0x0f172a, floorSize * 1.0, floorSize * 3.0);
```

- `near`: `floorSize` (바닥 크기와 동일 거리에서 시작)
- `far`: `floorSize * 3` (3배 거리에서 완전 소멸)
- 배경색과 동일 `0x0f172a`로 자연스러운 페이드
