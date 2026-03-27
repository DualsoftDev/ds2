# Ds2 3D View — 설계 문서 목차

| 번호 | 문서 | 핵심 내용 |
|------|------|----------|
| [01](01_OVERVIEW.md) | 전체 구조 개요 | 3-Layer 아키텍처, 컴포넌트 맵, 데이터 흐름 |
| [02](02_CORE_TYPES.md) | Core 타입 계약 | F# 타입 정의, Status4 매핑, DTO 설계 |
| [03](03_SCENE_ENGINE.md) | SceneEngine & ContextBuilder | Public API, DsStore→DTO 변환, ILayoutStore |
| [04](04_LAYOUT_ALGORITHM.md) | 레이아웃 알고리즘 | Device/Work 자동 배치, FlowZone, 바닥 크기 |
| [05](05_RENDERER_THREEJS.md) | Three.js 렌더러 | 씬 초기화, Device 모델, 애니메이션 |
| [06](06_WPF_ADAPTER.md) | WPF 어댑터 레이어 | ThreeDViewState, View3DWindow, 툴바 버튼 |
| [07](07_COMMUNICATION_PROTOCOL.md) | C#↔JS 통신 프로토콜 | 메시지 타입, WebView2 브릿지, 타이밍 처리 |
| [08](08_SIMULATION_INTEGRATION.md) | 시뮬레이션 이벤트 연동 | 이벤트 흐름, Call→Device 매핑, 상태 반영 |
| [09](09_INTERACTION_UX.md) | 인터랙션 및 UX | 컨텍스트 메뉴, 키보드, 카메라, 드래그 |
| [10](10_ROADMAP.md) | 로드맵 | Phase 계획, 개선 항목, 확장 포인트 |

## 원본 스펙 문서

- [DS2_3D_LAYOUT_PORT_SPEC.md](DS2_3D_LAYOUT_PORT_SPEC.md) — Ev2→Ds2 이식 기준서 (원본)
