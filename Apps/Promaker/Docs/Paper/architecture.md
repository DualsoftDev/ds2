# 전체 솔루션 개념도 (GFM · BFM · LH · 설비 모니터링)

### 용어
- GFM: Green field modeling.  아무것도 없는 상태 또는 기존 DS 모델상에서 모델링
- BFM: Brown field modeling.  PLC P/G 으로부터 reverse engineering 을 통한 모델링
- LH : Light house.  문서로부터 색인하여 Knowledge-base 구축

```mermaid
flowchart TB
    %% ===== 입력 소스 =====
    subgraph SRC["입력 소스"]
        direction LR
        PLC["기존 PLC 자산<br/>P/G (래더·SCL·tag)"]
        DOC["공법 문서<br/>(PDF / Word / 도면)"]
    end

    %% ===== LH =====
    subgraph LH["LH"]
        direction TB
        LH_EXT["Extractors / Chunker"]
        LH_EMB(["EmbeddingProvider<br/>🤖 LLM/Embedding"])
        LH_STORE["SqliteStore + ImageStore"]
        LH_SRCH["Searcher / Classifier<br/>/ CaptionGenerator"]
        LH_EXT --> LH_EMB --> LH_STORE --> LH_SRCH
    end
    DOC --> LH_EXT

    %% ===== GFM =====
    subgraph GFM["GFM / Promaker"]
        direction TB
        subgraph PRM["Promaker"]
            direction LR
            PRM_NL["자연어 대화<br/>💬"]
            PRM_GUI["GUI<br/>🖱️"]
        end
        LLA_G(["Ds2.LlmAgent<br/>🤖 LLM"])
        DS_G["DS 모델"]
        PRM_NL <--> LLA_G
        LLA_G --> DS_G
        PRM_GUI --> DS_G
    end
    LH_SRCH -- "RAG 컨텍스트" --> LLA_G
    LLA_G -- "키워드 검색" --> LH_SRCH

    %% ===== BFM =====
    subgraph BFM["BFM"]
        direction TB
        PRE["preprocess<br/>(P/G 파싱 → io.json / pous.json)"]
        S4(["llm_step<br/>🤖 LLM — tag → zone/device/<br/>deviceType entity 추출"])
        S6(["patterns<br/>🤖 LLM — device 동작 패턴 추론"])
        LEX["lexicon/ko_actions<br/>(한↔영 action 정규화, 결정적)"]
        DS_B["DS 모델 산출<br/>(tags.json + patterns.json)"]
        PRE --> S4 --> S6 --> DS_B
        LEX -. seed/규칙 .-> S4
    end
    PLC --> PRE
    LH_SRCH -. "(예정) 공법 지식 주입" .-> S4
    LH_SRCH -. "(예정)" .-> S6

    %% ===== Oracle =====
    subgraph ORA["설비 모니터링"]
        direction TB
        ORA_CHAT["운영자 대화 UI<br/>💬 자연어 질의"]
        ORA_LLM(["모니터링 LLM Agent<br/>🤖 LLM<br/>(DS 모델 + 실시간 상태 → 답변)"])
        ORA_STATE["실시간 설비 상태<br/>(태그 값 / 알람 / 이력)"]
        ORA_CHAT <--> ORA_LLM
        ORA_STATE --> ORA_LLM
    end
    DS_B --> ORA_LLM
    LH_SRCH -. "(예정) 공법 지식 주입" .-> ORA_LLM

    %% ===== 공통 산출 =====
    DS_G --> OUT["DS 모델 (공통 산출물)"]
    DS_B --> OUT

    %% layout hint: BFM 결과물을 받는 ORA 를 BFM 하단에 배치
    BFM ~~~ ORA

    classDef llm fill:#fff3b0,stroke:#c98a00,color:#000;
    classDef nl  fill:#d6eaff,stroke:#2a6fbf,color:#000;
    classDef store fill:#e8e8e8,stroke:#666,color:#000;
    classDef rt fill:#ffe0e0,stroke:#c0392b,color:#000;
    class LH_EMB,LLA_G,S4,S6,ORA_LLM llm;
    class PRM_NL,ORA_CHAT nl;
    class LH_STORE,OUT store;
    class ORA_STATE rt;

    %% 점선 edge 만 dash/gap 길이 확장 (실선과 시각적 구분 강화)
    %% index: 13=seed/규칙, 15/16=LH→BFM(예정), 20=LH→Oracle(예정)
    linkStyle 13,15,16,20 stroke-dasharray: 8 8;
```

## 범례 (Legend)

<table>
  <thead>
    <tr><th>표기</th><th>의미</th></tr>
  </thead>
  <tbody>
    <tr>
      <td>━━━━ &nbsp;실선</td>
      <td>본류 / 구현된 데이터·제어 흐름</td>
    </tr>
    <tr>
      <td>╌ ╌ ╌ &nbsp;점선</td>
      <td>보조 흐름, 또는 <code>(예정)</code> 라벨이 붙은 미구현 연결</td>
    </tr>
    <tr>
      <td><span style="display:inline-block;width:1.1em;height:1.1em;background:#fff3b0;border:1px solid #c98a00;vertical-align:middle"></span> 🤖 노란 노드</td>
      <td>LLM / Embedding 사용 지점</td>
    </tr>
    <tr>
      <td><span style="display:inline-block;width:1.1em;height:1.1em;background:#d6eaff;border:1px solid #2a6fbf;vertical-align:middle"></span> 💬 파란 노드</td>
      <td>자연어 입력 (대화) 진입 지점</td>
    </tr>
    <tr>
      <td><span style="display:inline-block;width:1.1em;height:1.1em;background:#ffe0e0;border:1px solid #c0392b;vertical-align:middle"></span> 빨간 노드</td>
      <td>실시간 운영 데이터</td>
    </tr>
    <tr>
      <td><span style="display:inline-block;width:1.1em;height:1.1em;background:#e8e8e8;border:1px solid #666;vertical-align:middle"></span> 회색 노드</td>
      <td>저장소 / 산출물</td>
    </tr>
  </tbody>
</table>

