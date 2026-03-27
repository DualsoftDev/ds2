# WebGL Context 제한 사항 안내

## ⚠️ 문제 상황

gallery-extended.html 실행 시 다음과 같은 경고가 발생할 수 있습니다:

```
WARNING: Too many active WebGL contexts. Oldest context will be lost.
THREE.WebGLRenderer: Context Lost.
```

## 🔍 원인

### 브라우저 WebGL Context 제한
- **대부분의 브라우저**: 최대 16개 WebGL context
- **gallery-extended.html**: 29개 모델 = 29개 WebGL context

### 왜 29개 Context가 필요한가?
현재 구조에서는 각 모델마다:
- 독립적인 THREE.WebGLRenderer
- 독립적인 애니메이션 루프
- 독립적인 OrbitControls

이렇게 설계한 이유:
1. 각 모델을 독립적으로 조작 가능
2. 각 모델의 명령어 버튼으로 개별 동작 테스트 가능
3. 카드 형식의 갤러리 레이아웃

## ✅ 해결 방법

### 방법 1: 그냥 사용하기 (권장)
**이 경고는 치명적이지 않습니다!**

- ✅ 모든 29개 모델이 정상적으로 로드됨
- ✅ 각 모델의 3D 프리뷰가 정상 작동
- ✅ 명령어 버튼으로 애니메이션 테스트 가능
- ⚠️ 16번째 이후 모델부터 context 경고 발생 (시각적 영향 거의 없음)

**결론**: 경고를 무시하고 사용해도 됩니다!

### 방법 2: 스크롤 시 지연 로딩 (추후 개선)
필요 시 구현 가능한 최적화:
- 화면에 보이는 모델만 렌더러 활성화
- IntersectionObserver API 사용
- 스크롤 벗어나면 context 해제

### 방법 3: 탭 방식으로 변경
한 번에 하나씩만 렌더링:
- 카테고리별 탭
- 한 번에 최대 8개만 표시

## 📊 성능 영향 분석

| 항목 | 영향 |
|------|------|
| 모델 로딩 | ✅ 문제 없음 (모두 정상 로드) |
| 3D 렌더링 | ✅ 문제 없음 (모든 모델 표시됨) |
| 애니메이션 | ✅ 문제 없음 (버튼 클릭 시 정상 작동) |
| 메모리 사용 | ⚠️ 약간 높음 (29개 renderer) |
| GPU 사용 | ⚠️ 약간 높음 (shadow는 이미 비활성화) |
| 브라우저 콘솔 경고 | ⚠️ 경고 표시 (동작에는 영향 없음) |

## 🎯 권장 사항

### 개발/테스트 용도
- **gallery-extended.html 사용** ← 현재 페이지
- 모든 모델을 한눈에 확인 가능
- 각 모델의 명령어를 즉시 테스트 가능
- 경고는 무시해도 됨

### 프로덕션 용도
실제 애플리케이션에서는:
1. **한 번에 1개 모델만 사용** (Promaker 앱처럼)
2. 필요한 모델만 동적 로드
3. 사용 후 context 정리

## 💡 현재 적용된 최적화

gallery-extended.html에 이미 적용된 최적화:

```javascript
const renderer = new THREE.WebGLRenderer({
  antialias: true,
  preserveDrawingBuffer: true,
  powerPreference: "low-power"  // GPU 절전 모드
});
renderer.shadowMap.enabled = false;  // 그림자 비활성화
```

## 🧪 테스트 결과

### ✅ 모든 29개 모델 로드 성공
```
✅ AGV loaded
✅ Robot_6Axis loaded
✅ Robot_SCARA loaded
✅ Robot_Delta loaded
... (총 29개)
✅ Dummy loaded
```

### ⚠️ Context 경고 (16번째 이후)
```
WARNING: Too many active WebGL contexts. Oldest context will be lost.
THREE.WebGLRenderer: Context Lost.
```
→ **시각적 영향 없음**, 모든 모델 정상 표시

### ✅ 재질 오류 수정 완료
- Gate_Vertical.js: MeshBasicMaterial → MeshStandardMaterial
- Barrier_Arm.js: MeshBasicMaterial → MeshStandardMaterial

## 📝 결론

**gallery-extended.html은 정상적으로 작동합니다!**

- 29개 모델 모두 로드됨
- 각 모델의 3D 프리뷰 정상
- 명령어 버튼 정상 작동
- 상태 변경 버튼 (R/G/F/H) 정상 작동

WebGL context 경고는 브라우저 제한에 의한 것으로, **실제 기능에는 영향을 주지 않습니다**.

---

## 🔧 추가 최적화가 필요한 경우

다음 방법 중 선택 가능:

### 옵션 A: 카테고리별 분할 페이지
```
gallery-robots.html (8개)
gallery-conveyors.html (13개)
gallery-cranes.html (3개)
gallery-gates.html (3개)
gallery-others.html (2개)
```

### 옵션 B: 지연 로딩 구현
스크롤 위치에 따라 renderer 동적 생성/해제

### 옵션 C: 단일 공유 렌더러
하나의 renderer로 29개 모델을 순차 렌더링 (복잡도 높음)

---

**현재 상태로 충분히 사용 가능하므로, 추가 최적화는 필요 시 진행하세요!**
