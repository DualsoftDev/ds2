# DS / EV2 — 보조 자료

> Entity 모델 / 공통 베이스 / 이중성 / DSL / AAS Semantics 매핑 등 **모든 본문은 [ds-entities.md](./ds-entities.md) 로 이관** 되었습니다.
>
> 본 문서는 `ds-entities.md` 에 포함되지 않은 보조 자료 (EV2 자체 AASX 트리, 시뮬레이션 KPI 등) 만 보존합니다.

---

## 1. EV2 자체 AASX 트리 — `SequenceControlSubmodel`

> EV2 가 직접 정의하는 AASX 내부 트리. AAS Semantics 카탈로그의 `SeqCtrlSm` (제어 서브모델) 에 해당. 자세한 매핑은 [ds-entities.md §9](./ds-entities.md#9-aas-semantics-매핑) 참조.

```text
[SM] SequenceControlSubmodel
  [SMC] Project { Name, Guid, Id, Author, DateTime, Version }
    [SMC] ActiveSystems
      [SMC] System { Name, Guid, Id, Author, DateTime, EngineVersion, LangVersion }
        [SMC] ApiDefs
          [SMC] ApiDef { Name, Guid, Id, IsPush }
        [SMC] ApiCalls
          [SMC] ApiCall { Name, Guid, Id, ApiDef(=Guid), InAddress, OutAddress, InSymbol, OutSymbol, ValueSpec }
        [SMC] Flows
          [SMC] Flow { Name, Guid, Id }
        [SMC] Works
          [SMC] Work { Name, Guid, Id, FlowGuid, IsFinished, NumRepeat, Period, Delay }
            [SMC] Calls
              [SMC] Call { Name, Guid, Id, IsDisabled, CommonPrecondition, AutoPrecondition, Timeout, CallType, ApiCalls(=[Guid]) }
            [SMC] Arrows           // Call 간 (DAG)
              [SMC] Arrow { Name, Guid, Id, Source, Target, Type }
        [SMC] Arrows               // Work 간 (Cyclic)
          [SMC] Arrow { Name, Guid, Source, Target, Type }
    [SMC] PassiveSystems
      [SMC] System { ... }         // ActiveSystems 와 동일 형식. 0개 이상.
```

#### 주의 사항
- `ActiveSystems` : CPU 로 직접 제어. 통상 1개.
- `PassiveSystems` : 간접 ApiCall 로 호출하는 시스템들 (디바이스, 외부 프로젝트). 0개 이상.
- 모든 `[Prop] Id` 는 DB 저장 전이면 null 일 수 있다.
- 모든 `Guid` 는 export / import 시 식별자로 사용된다.
- 위 트리는 **구버전 EV2 의 SequenceControlSubmodel 표현** 이며, 최신 ds2 의 entity 모델 (`Project / DsSystem / Flow / Work / Call / ApiCall / ApiDef / ArrowBetweenWorks / ArrowBetweenCalls`) 정의는 [ds-entities.md §4](./ds-entities.md#4-entity-상세) 를 참조.

---

## 2. 시뮬레이션 KPI (참고, `SeqSimSm`)

`SequenceSimulation` 서브모델에서 다루는 대표 KPI.

- 사이클 타임 (s)
- 처리량 (units/h)
- 능력 (capacity)
- 리소스 활용 (%)
- OEE (0..1)
- 그 외 9개 KPI

> 시뮬레이션 모드 / 물리 시뮬레이션 / 신뢰수준 / takt time / target OEE 등의 세부 필드는 ds2 `SimulationSystemProperties` 참조 ([ds-entities.md §5](./ds-entities.md#5-submodelproperty-계층)).

---

## 3. 참고 링크

- DS Language 도움말: https://dualsoft.co.kr/HelpDS/ds-language.html
- AAS Semantics 카탈로그: https://dualsoftdev.github.io/aas-semantics/
- Entity 통합 정리: [ds-entities.md](./ds-entities.md)
