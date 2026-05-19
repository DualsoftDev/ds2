namespace Ds2.LightHouse

open System.Threading
open System.Threading.Tasks

/// **Phase 4 (s6-r34) — embedding backend abstraction SSOT** (todo-lighthouse-kb-index.md §3.7 / §3.15.2).
///
/// lib 은 backend-neutral — `Indexer.ingestFile` 가 본 interface 를 받아 chunk 마다 embedding 생성.
/// 외부 abstraction (Microsoft.Extensions.AI.IEmbeddingGenerator 등) 의존 0 — caller (Promaker / cli) 가
/// OllamaSharp / OpenAI / 자체 ONNX wrapper 를 본 interface 의 adapter 로 wrap 후 주입.
///
/// **dimension 박제 (parent r0 모델 후보)**:
/// - bge-m3 = 1024
/// - multilingual-e5-large-instruct = 1024
/// - jina-v3 = 1024
/// - nomic-embed-text = 768
///
/// caller 가 backend 의 실 dim 을 `Dimension` 으로 박제. Indexer 는 본 값을 신뢰 + `Chunks_Vectors`
/// virtual table 의 `float[N]` schema 와 정합 의무 (lib 의 SchemaVersion bump 시 dim 변경 동반).
///
/// **batch API 의무**: `GenerateAsync` 는 N 개 input 을 한 번에 처리 (Ollama / OpenAI 모두 batch 지원).
/// caller 가 batch 크기 결정 — lib 은 단순히 chunks 배열 통째로 호출 (Indexer 가 batching policy 결정).
type IEmbeddingProvider =
    /// 본 backend 의 vector dimension. Chunks_Vectors schema 와 정합 의무.
    abstract member Dimension: int

    /// N 개 input → N 개 float32[] vector. 결과 length == inputs.Length, 각 vector length == Dimension.
    ///
    /// **fail policy**: 한 input 의 embedding 생성 실패는 본 method 전체 throw (모든 input 재시도) —
    /// caller (Indexer) 가 chunk 단위 fail-safe 안 함. backend (Ollama daemon down 등) 결함은 색인 전체 abort.
    /// 사용자 명시 `--no-embedding` opt-out 으로 skip 가능 (caller 가 본 provider 자체를 None 으로 주입).
    abstract member GenerateAsync: inputs: string[] * ct: CancellationToken -> Task<float32[][]>
