namespace Ds2.LightHouse.Ollama

open System
open System.Threading
open System.Threading.Tasks
open Ds2.LightHouse
open OllamaSharp
open OllamaSharp.Models

/// 본 adapter 의 log4net logger. lib 의 internal `Log` 모듈 우회 — 별 project 라 직접 사용 불가.
/// Promaker `log4net.config` 의 `<logger name="Ds2.LightHouse.Ollama">` 또는 상위 `Ds2.LightHouse.*` 패턴이 적용.
module internal OllamaLog =
    let logger = log4net.LogManager.GetLogger("Ds2.LightHouse.Ollama")

/// **Phase 4 (s6-r37) P4-C.1** — OllamaSharp 의 `OllamaApiClient.EmbedAsync` wrapper.
///
/// `IEmbeddingProvider` 구현 SSOT — `Ds2.LightHouse` 의 backend-neutral interface 를 본 별 project 가 박제.
/// caller (cli / Promaker / server) 가 ProjectReference 박제 후 instance 생성 → KB facade 또는 Indexer 주입.
///
/// **lifecycle (P4-C.0 IDisposable contract 정합)**: 생성자가 `OllamaApiClient` (또는 외부 주입 client) 보유.
/// `Dispose` 가 owned client 의 HttpClient 자원 회수. 외부 주입 client (mock / 공유 client) 사용 시 ownership
/// 박제 분리 의무 — 본 type 의 `ownsClient` flag 로 결정.
///
/// **fail policy** (interface SSOT 정합): backend (Ollama daemon down / 모델 미설치 / dimension 불일치) 결함은
/// 본 `GenerateAsync` 의 OllamaSharp 예외를 그대로 propagate — caller (Indexer / Searcher) 가 색인 / 검색 전체
/// abort. per-input fail-safe 없음. 사용자 명시 `--no-embedding` 으로만 skip 가능.
///
/// **dimension 박제**: caller 가 생성자 인자 `dimension` 으로 명시. backend 의 실 dim 과 정합 의무 (mismatch 시
/// `GenerateAsync` 결과 검증에서 lib 측 InvalidOperationException). 통상 bge-m3 = 1024 / nomic-embed-text = 768.
/// constructor 가 backend dim 자동 probe 안 함 — 색인 시점 backend 응답 1회 호출 cost 회피 + caller 측 config
/// SSOT 명시 의도.
[<Sealed>]
type OllamaEmbedder
    (
        client: IOllamaApiClient,
        model: string,
        dimension: int,
        ownsClient: bool
    ) =

    /// 편의 생성자 — base URL + model + dimension 만 받아 내부적으로 `OllamaApiClient` 생성 + own.
    /// caller 가 client lifecycle 별도 관리 안 해도 됨 (`Dispose` 자동 회수).
    new (baseUrl: string, model: string, dimension: int) =
        if String.IsNullOrWhiteSpace baseUrl then
            invalidArg (nameof baseUrl) "baseUrl 빈 값 금지 (예: 'http://localhost:11434')."
        if String.IsNullOrWhiteSpace model then
            invalidArg (nameof model) "model 빈 값 금지 (예: 'bge-m3', 'nomic-embed-text')."
        if dimension <= 0 then
            invalidArg (nameof dimension) (sprintf "dimension 양수 의무 — 받은 값=%d" dimension)
        let uri = Uri(baseUrl)
        let c = new OllamaApiClient(uri, model)
        new OllamaEmbedder(c :> IOllamaApiClient, model, dimension, ownsClient = true)

    interface IEmbeddingProvider with
        member _.Dimension = dimension

        member _.GenerateAsync(inputs: string[], ct: CancellationToken) =
            if isNull inputs then
                raise (ArgumentNullException(nameof inputs))
            // 0-input edge — backend 호출 회피 + 빈 결과 즉시 반환 (Indexer 의 Some+빈 chunks no-op path 정합).
            if inputs.Length = 0 then
                Task.FromResult([||])
            else
                task {
                    let req = EmbedRequest(Model = model, Input = ResizeArray<string>(inputs))
                    let! response = client.EmbedAsync(req, ct)
                    let embeddings = response.Embeddings
                    if isNull embeddings then
                        raise (InvalidOperationException(
                            sprintf "OllamaEmbedder: backend 응답에 Embeddings 없음 — model=%s inputs=%d"
                                model inputs.Length))
                    let count = embeddings.Count
                    if count <> inputs.Length then
                        raise (InvalidOperationException(
                            sprintf "OllamaEmbedder: 반환 vector 수=%d ≠ inputs 수=%d (model=%s)"
                                count inputs.Length model))
                    // OllamaSharp 의 Embeddings 는 IList<float[]>. F# array 로 복사.
                    let vectors = Array.zeroCreate<float32[]> count
                    for i = 0 to count - 1 do
                        let v = embeddings.[i]
                        if isNull v then
                            raise (InvalidOperationException(
                                sprintf "OllamaEmbedder: embedding[%d] null — model=%s" i model))
                        if v.Length <> dimension then
                            raise (InvalidOperationException(
                                sprintf "OllamaEmbedder: vector[%d] dim=%d ≠ 박제 dimension=%d (model=%s — 모델/dim 불일치)"
                                    i v.Length dimension model))
                        vectors.[i] <- v
                    return vectors
                }

    interface IDisposable with
        member _.Dispose() =
            if ownsClient then
                match client with
                | :? IDisposable as d ->
                    try d.Dispose()
                    with ex ->
                        OllamaLog.logger.Warn(
                            sprintf "OllamaEmbedder.Dispose: client dispose 실패 — %s" ex.Message)
                | _ -> ()


/// **Phase 4 (s6-r37) P4-C.1** — OllamaEmbedder 의 default 모델 / dimension 박제 SSOT.
///
/// `bge-m3` = 1024 dim, 다국어 retrieval 우수 (sqlite-vec 의 `vec0(embedding float[1024])` 와 정합).
/// caller (cli / Promaker / server) 가 명시 override 안 하면 본 default 사용.
[<RequireQualifiedAccess>]
module OllamaDefaults =
    [<Literal>]
    let BaseUrl = "http://localhost:11434"

    [<Literal>]
    let Model = "bge-m3"

    [<Literal>]
    let Dimension = 1024
