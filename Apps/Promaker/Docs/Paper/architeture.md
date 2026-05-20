  flowchart TB
      %% ===== 입력 소스 =====
      subgraph SRC["입력 소스"]
          direction LR
          DOC["공법 문서<br/>(PDF / Word / 도면)"]
          PLC["기존 PLC 자산<br/>P/G (래더·SCL·tag)"]
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
      subgraph GFM["GFM"]
          direction TB
          PRM["Promaker (WPF UI)<br/>💬 자연어 대화"]
          LLA_G(["Ds2.LlmAgent<br/>🤖 LLM"])
          DS_G["Ds2.Core (DS 모델 / Editor)"]
          PRM <--> LLA_G
          LLA_G --> DS_G
          PRM --> DS_G
      end
      LH_SRCH -. "RAG 컨텍스트" .-> LLA_G

      %% ===== BFM =====
      subgraph BFM["BFM"]
          direction TB
          PRE["preprocess<br/>(P/G 파싱 → io.json / pous.json)"]
          S4(["step4 llm_step<br/>🤖 LLM — tag → zone/device/<br/>deviceType entity 추출"])
          S6(["step6 patterns<br/>🤖 LLM — device 동작 패턴 추론"])
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
          ORA_LLM(["Oracle LLM Agent<br/>🤖 LLM<br/>(DS 모델 + 실시간 상태 → 답변)"])
          ORA_STATE["실시간 설비 상태<br/>(태그 값 / 알람 / 이력)"]
          ORA_CHAT <--> ORA_LLM
          ORA_STATE --> ORA_LLM
      end
      DS_B --> ORA_LLM
      LH_SRCH -. "(예정) 공법 지식 주입" .-> ORA_LLM

      %% ===== 공통 산출 =====
      DS_G --> OUT["DS 모델 (공통 산출물)"]
      DS_B --> OUT

      classDef llm fill:#fff3b0,stroke:#c98a00,color:#000;
      classDef nl  fill:#d6eaff,stroke:#2a6fbf,color:#000;
      classDef store fill:#e8e8e8,stroke:#666,color:#000;
      classDef rt fill:#ffe0e0,stroke:#c0392b,color:#000;
      class LH_EMB,LLA_G,S4,S6,ORA_LLM llm;
      class PRM,ORA_CHAT nl;
      class LH_STORE,OUT store;
      class ORA_STATE rt;

      %% 점선 edge 만 dash/gap 길이를 확장 (실선과 시각적 구분 강화)
      %% index: 7=RAG 컨텍스트, 11=seed/규칙, 13/14=LH→BFM(예정), 18=LH→Oracle(예정)
      linkStyle 7,11,13,14,18 stroke-dasharray: 8 8;
