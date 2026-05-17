# Ds2.LightHouse — KB 서버 (central Windows Service) 도입

세션 이어받기용 TODO. `todo-lighthouse-kb-index.md` (이하 *parent*) 의 r4 위에 얹는 incremental design.
실제 진입은 parent 의 Phase 1 (in-process MVP) 완료 후. 본 문서는 그 후속 phase 의 설계 박제.

| rev | 일자 | 주요 변경 |
|---|---|---|
| s0 | 2026-05-17 | 초안 — plan 모드 논의 결과 박제. 코드 변경 0. parent r4 의 결정 일부 회귀 (사본 정책 / MCP 호스트 위치 / search 경로 등). |
| s0-r | 2026-05-17 | --inspect-diff 5 reviewer 결과 반영 (16건): (1) D-id / 결정 enum 정의표 §0 신설, (2) §3.1 sub-section 분리 (책임/lib 양분/MCP host 2개), (3) §3.2 통신 흐름 다이어그램 보강, (4) §3.7 mTLS 단원 참조 정정 (S4→S7), (5) §3.8 `unindexableIds` 처리 명시, (6) §3.13 ↔ Phase S5 중복 분리 (사유 vs 체크리스트), (7) §3.14 가 parent ↔ service 회귀 SSOT 임을 명시, (8) parent 패턴 정렬을 위해 단원 번호 환원 (이전 §5/§6/§7/§8 → §5/§6/§7), (9) Phase S1~S7 별 DoD 1줄 추가, (10) `LlmConfig.KbCollections` schema migration 정책을 §4.3 미확정에 추가. |
| s0-r2 | 2026-05-17 | parent r5 의 **대안 B 채택** 반영: (1) §4.1 Phase S0 에 "parent §4.5 / §4.1 첫 task / §4.6 / §4.8 일부 정상 skip 확인" 추가, (2) §4.3 schema migration default 를 "(c) parent §4.5 skip 이라 migration 불필요" 로 갱신, (3) §0 의 선행 의존 항목에 parent r5 결정 12 박제. parent Phase 1 산출물의 60%+ throwaway 문제 해소. |

---

## 0. 현재 상태 / 본 문서 위치

- **모드**: `--plan` 만 수행. 코드 변경 0.
- **선행 의존**: parent 의 Phase 1 **lib 본체만** 완료 (대안 B / parent r5 결정 12). parent §4.2 (부분 통합) + §4.3 (Extractor / Chunker) + §4.4 (SqliteStore / Searcher / Indexer / KnowledgeBase facade) + §4.8 의 lib unit test 만. parent §4.5 (Promaker 통합) / §4.1 첫 task (`.gitignore`) / §4.6 (5.knowledge-base.md) / §4.8 의 Promaker 의존 테스트는 **본 phase (Phase S5) 가 흡수**.
- parent Phase 1 의 schema (§3.12) / RefLocator EBNF (§3.13) / PRAGMA (§3.17) / facade 결정 (§3.18.1) 등은 본 문서에서도 그대로 SSOT.
- **본 문서가 parent r5 로 통합될지, 별도 todo 로 유지될지**: service 도입 phase 진입 시점에 재결정. 통합 시 parent 의 §3.9 / §3.10 / §3.18 / §4.1 / §4.5 / §6 다수 단원이 본 문서 결정으로 큰 폭 재작성됨.

### 본 세션 결정 enum (D-id 정의표)

본 문서 본문에서 참조하는 결정 id 의 의미. 외부 회의록 의존 회피 (stale 차단).

| id | 결정 | 위치 |
|---|---|---|
| R1 | service = storage + share + MCP search host **ONLY** (색인 자체는 client) | §1, §3.1.1 |
| Q1 | collection registry sync = Promaker **시작 시점 1회만** (turn 마다 X) | §3.8 |
| Q2 | ATTACH limit (10) 가드 위치 = server `POST /sessions` hard fail (UI 도 sanity) | §3.9, §4.2 Phase S3 |
| Q3 | session 간 connection 격리 — 각자 별 `SqliteConnection` (pool X) | §3.8 |
| Q4 | active 셋 sync = lazy reject driven (Promaker 가 그대로 보내고 server reject 시 sync) | §3.8 |
| D1 | 사본 정책 = **1회성 import** (등록 시점 snapshot, 자동 drift 감지 X) | §3.5 |
| D2 | server 디렉토리 = `Collections\<guid>-<sanitized-title>\` (guid 식별 + title hint) | §3.4, §3.10 |
| D3 | collection 식별 SSOT = **guid v4** | §3.4 |
| D4 | client → server upload = **multipart zip** (사용자 폴더 통째 포함) | §3.3, §3.9 |
| D5 | drift 갱신 = 사용자 명시 "새 버전 업로드" trigger 만 (FileSystemWatcher X) | §3.5 |
| D6 | citation 클릭 시 원문 보기 = **server 가 stream 응답** (LAN 가정) | §3.9, §4.2 Phase S4 |
| D7 | `DELETE /collections/{id}` = `Collections\<id>\` 전체 purge | §3.9 |
| α | multi-tenant 정책 = **flat** (누구나 모든 collection 보기, β/γ 미채택) | §3.6 |
| N5 | server 가 색인 안 함 → 색인 진행률 polling API 불필요 | §3.1.1, §4.2 Phase S7 |
| N6 | `maxUploadBytes` = **10 GB** (외부 config 노출 상수) | §3.11 |
| L1 | session 생성 trigger = Promaker LLM chat panel **open 시 1회** (chat lifetime) | §3.8 |
| L2 | session 해제 = **3중 cleanup** (panel close / process exit / server idle TTL) | §3.8 |

---

## 1. 작업 목표

LightHouse 의 색인 결과물을 central Windows Service 에 보관 / 다중 client 간 공유 / MCP search 호스트를 service 측에서 운영. Promaker 측은 client 색인 (write-path) + zip upload + HTTP client + chat UI 만 책임.

핵심 한 줄: **색인은 client, 보관·공유·검색은 server** (R1).

---

## 2. 배경 / motivation (parent r4 모델에서 발견한 부담)

| 항목 | parent r4 (in-process) | service 도입 이득 |
|---|---|---|
| Promaker 종료 후 codex / 외부 LLM client 가 KB 사용 | 불가 | **가능** — service 가 항상 켜져있음 |
| 다중 Promaker instance 가 같은 KB 사용 | 각자 in-process Kestrel + MCP host + `.mcp-config` 중복 발행 | **단일 service endpoint 공유** |
| 회사 NAS 의 공용 사양서 검색 | r4 §3.9 의 read-only NAS ATTACH (SMB + SQLite WAL fragility) | **LAN service** 가 자연 흡수 |
| Phase 4 embedding 모델 warm cache | Promaker 시작마다 load (수백MB~GB) | (client 측 색인 시점에 발생 — service 무관) |
| KB 가 사내 공용 자원화 | 가능하나 file-share 의존 | **multi-tenant α flat** 자연 |

note: 이전 세션 중간 논의에서 "service 가 색인까지 수행" 안도 있었으나 **사용자 결정 R1 으로 service = storage + search 호스트만**. 색인 (Extract/Chunk/FTS5/index.db 생성) 은 client 측 `Ds2.LightHouse` lib 의 write-path 가 책임.

---

## 3. 결정된 설계

### 3.1 책임 분리 (SSOT)

#### 3.1.1 client vs service 책임 분리표 (R1)

| 책임 | client (Promaker / `lighthouse-cli`) | service |
|---|---|---|
| Extract (PdfPig / OpenXml) | ✓ | ✗ |
| Chunk | ✓ | ✗ |
| FTS5 `index.db` 생성 | ✓ | ✗ |
| IndexerVersion bump 자동 재색인 | ✓ (재색인 후 새 zip upload) | ✗ |
| Phase 3 OCR | ✓ | ✗ |
| Phase 4 embedding | ✓ | ✗ |
| Phase 2/5 caption | ✓ | ✗ |
| zip 패키징 | ✓ | ✗ |
| **upload 수신 + storage** | ✗ | ✓ |
| **multi-tenant share (α flat)** | ✗ | ✓ |
| **MCP search host** (`attachment_*` 4종) | ✗ | ✓ |
| **session routing** (active 셋) | ✗ | ✓ |
| **file serving** (citation 원문 stream) | ✗ | ✓ |
| **인증 (TLS + PSK) + zip sanitize** | ✗ | ✓ |

derivative: service 가 색인 안 하므로 **색인 진행률 polling API 불필요 (N5)**. server-side 처리 시간 = sanitize + atomic move + IndexerVersion 검증 (수 초 이내). 진행률 표현은 client-side upload byte 만 의미.

#### 3.1.2 LightHouse lib 양분 (write-path / read-path)

- **write-path** (`Indexer` / `Extractor` / `Chunker` / `SqliteStore` 의 write API) → client 측만 사용
- **read-path** (`Searcher` / `KnowledgeBase` facade 의 read API) → service 측만 사용
- 동일 lib, 양쪽 모두 ProjectReference (client = `Promaker.csproj` / `Ds2.LightHouse.Cli.fsproj`, service = `Ds2.LightHouseService.fsproj`)
- **양분 형태 (facade 가 read-only mode 옵션 받는지) 는 §4.3 미확정** — parent §3.18.1 의 facade 결정 (record-of-functions vs interface) 결과에 의존. S1 진입 시 확정.

#### 3.1.3 MCP host 2개 정책

Mutation MCP (parent 의 `apply_model_doc` 등) 은 Promaker in-process 그대로. LLM client 의 `.mcp-config` 에 server 2개 등록:
```
mcpServers:
  promaker   : Promaker in-process (mutation tool 일체)
  lighthouse : Service (attachment_* 4종 + session)
```
LLM 인지 부담 0 — tool 14종 (10 mutation/read + 4 attachment) 가 한 공간에 자연 공존 (이름 중복 0). system prompt `5.knowledge-base.md` 에 출처 명시 권장.

### 3.2 통신 흐름

```
[등록 단계 — 비빈번]
client (Promaker / lighthouse-cli)
  ├─ folder 색인 (Ds2.LightHouse Indexer in-process)
  ├─ zip 패키징 (source/ + .lighthouse-kb/index.db + meta.json)
  └─ POST /collections (multipart) ──────────→ server
                                                ├─ sanitize + atomic move
                                                ├─ Meta.indexer_version 호환성 검증 (§3.12)
                                                └─ Collections\<guid>-<title>\ 배치

[검색 단계 — chat panel lifetime (L1)]
Promaker (panel open)
  ├─ POST /sessions { collectionIds: [...] } ──→ server: SessionState 생성 + ATTACH 준비
  │                                              ← { token, unknownIds?, unindexableIds? }
  ├─ unknownIds / unindexableIds 처리 (§3.8)
  └─ .mcp-config 갱신 (lighthouse server URL + auth + session header)

LLM (codex / API provider)
  └─ attachment_search(...) over MCP HTTP ─────→ server
                                                ├─ session lookup → active collections
                                                ├─ Searcher.fs → multi-db UNION BM25
                                                └─ result return

(citation 클릭 시 — D6)
Promaker UI
  └─ GET /collections/{id}/files/{fileId} ────→ server: source\ 의 원본 byte stream
                                                ← stream + Content-Type

[해제 단계 — 3중 cleanup (L2)]
panel close   → DELETE /sessions/{token}
process exit  → 살아있는 token 일괄 DELETE
server idle TTL → backstop
```

- service bind = **LAN (HTTPS)**
- 모든 호출 = `Authorization: Bearer <PSK>` (§3.7)
- session 호출 = 추가 `X-LightHouse-Session: <token>` (§3.8)

### 3.3 zip layout SSOT (D4)

client ↔ service 의 정합 SSOT:

```
<zip root>/
  meta.json                  # { indexer_version, schema_version, originalFileCount, sourceTitle, ... }
  source/
    plant-spec-v3.pdf        # 원본 파일 사본 (사용자 폴더 통째)
    io-list-2026.xlsx
  .lighthouse-kb/
    index.db                 # FTS5 완성품 (client 가 색인 끝낸 SQLite)
    blobs/images/<sha>.<ext> # Phase 2 이미지 blob (client 추출분)
```

service 는 zip 받으면:
1. **sanitize** (entry path `..` traversal 가드, 절대경로 거부, 전개 후 storage root 하위인지 verify)
2. **zip bomb 가드** (누적 해제 byte / 압축 byte 비율 = `zipBombRatioLimit` 외부 설정)
3. **`.lighthouse-kb/index.db` 의 `Meta.indexer_version` / `Meta.schema_version` 호환성 검증** (§3.12)
4. **atomic move** → `Collections\<guid>-<title>\`

### 3.4 collection 식별 체계

- **server-side stable id = guid v4 (D3)** — 정렬·식별 SSOT
- 디렉토리 명 = `<guid>-<sanitized-title>` (D2) — 디스크 list 시 사람 가독, 정렬은 guid prefix
- `meta.json` 의 `title` 필드가 표시 SSOT (디렉토리 명은 단순 hint)
- client 측 `LlmConfig.KbCollections` schema (parent r4 의 `{path, active}` 폐기):
  ```json
  {
    "KbCollections": [
      { "CollectionId": "<guid>", "DisplayName": "라인A 사양서 v3", "Active": true }
    ],
    "LightHouseService": {
      "BaseUrl": "https://service.company.local:8443",
      "ApiKey": "<PSK>"
    }
  }
  ```
  (parent r4 의 `LlmConfig.cs` 직렬화 관례 = PascalCase. JSON property name attribute 적용 위치는 §4.2 Phase S5 결정.)

### 3.5 사용자 폴더 ↔ server 사본 정책

- **1회성 import (D1)** — 등록 시점 snapshot. 사용자가 폴더 갱신해도 service 자동 미인지.
- **drift 자동 감지 X (D5)** — KbManagerDialog 의 명시 "새 버전 업로드" trigger 만. FileSystemWatcher 사용 안 함.
- **사용자 폴더 안 흔적 0** — `.lighthouse-kb/` 가 사용자 폴더에 안 생김. parent r4 §4.1 의 "`.gitignore` 에 `.lighthouse-kb/` 추가" task 본 phase 진입 시 무효.

### 3.6 multi-tenant 정책 — α flat

사용자 결정: **flat — 누구나 모든 collection 보기** (사내 공유 KB 모델).

- per-user namespace 격리 (β) 안 함
- collection 별 ACL (γ) 안 함
- 즉 `GET /collections` 응답은 모든 client 동일
- 회사 IT / 사용자가 등록한 collection 을 모든 사용자가 active 토글 가능
- **PII 위험 강화**: 사용자가 무심코 비밀 폴더 등록 시 다른 사용자 노출 → 등록 시 **consent dialog 의무화** (§6 m2). parent r4 §6 m15 의 강화판.

### 3.7 인증 / 보안

- **TLS 필수** (HTTPS bind, self-signed 도 OK, 회사 deployment 면 사내 CA 발급 권장)
- **PSK (Pre-Shared Key)** — service 설치 시 발급, Promaker 설정에 수동 입력
  - 모든 API 호출에 `Authorization: Bearer <PSK>`
  - 회전 정책: service config 갱신 + 모든 client 재입력 (운영 부담 — **Phase S7 에서 mTLS 검토**)
- **session token** 은 별도 routing key (§3.8). PSK 와 역할 분리:
  - PSK = "이 호출자가 신뢰된 client" (LAN 인증)
  - session token = "어느 active 셋 routing" (chat lifetime)
- **zip sanitize** — `..` traversal, 절대경로 entry 거부. `Collections\<id>\` 하위로만 전개 강제.
- **zip bomb 가드** — 누적 압축 해제 byte 한도 (외부 설정).

### 3.8 session model (L1/L2/Q1/Q3/Q4 정합)

| 시점 | 처리 |
|---|---|
| Promaker startup | (Q1) `GET /collections` 1회 호출로 registry sync — KbManagerDialog 가 LlmConfig.KbCollections 와 server registry 의 stale entry 비교 + 정리 |
| chat panel open (`LlmChatViewModel.InitializeAsync`) | (L1) `LlmConfig.KbCollections.Active` → `POST /sessions { collectionIds }` → token 박제. **chat lifetime 동안 재사용** (turn 단위 재발급 X — parent r4 §3.18.2 의 turn-scoped 컨텍스트 모델은 본 design 에서 chat-scoped session 으로 대체) |
| chat 진행 (multi-turn) | 같은 token 재사용. MCP HTTP 헤더 `X-LightHouse-Session` 동봉 |
| chat 진행 중 사용자가 KbManagerDialog 에서 active 토글 | **현 session 영향 0**. 다음 chat 부터 반영. UI chip 안내 "변경은 다음 chat 부터" |
| chat panel close | (L2-1) `DELETE /sessions/{token}` (1차 cleanup) |
| Promaker process exit | (L2-2) 살아있는 token 들 일괄 DELETE (panel close 못한 경우 대비) |
| server-side idle TTL (예: 1h) | (L2-3) backstop — process kill / network drop 대비 |

**Active 셋 sync (lazy reject driven, Q4)**:
```
POST /sessions { collectionIds: [LlmConfig active set] }
  service:
    각 id → registry lookup
    각 id 의 status 확인 (idle / indexing / error)
    response: { token, unknownIds?, unindexableIds? }

Promaker:
  if unknownIds 존재:
     → GET /collections 로 sync
     → LlmConfig.KbCollections 에서 해당 entry 제거 (server 가 영구 폐기)
     → atomic Save
  if unindexableIds 존재 (status=error):
     → LlmConfig 에서 제거하지 않음 (재시도 가능 — server 측 복구 후 다시 active)
     → chip 안내 "색인 실패 collection N개 제외"
  unknown + unindexable 모두 제외 후 재요청 POST /sessions
  최종 응답 chip 통합 안내
```

**Session 간 connection 격리 (Q3)**:
- session 당 `SqliteConnection` 1개 + ATTACH 별칭 `kb0..kbN-1`
- 같은 collection 이 여러 session 에 등장해도 각자 별 connection (SQLite WAL multi-reader 라 file lock 부담 없음)
- pool 도입은 Phase S7

### 3.9 API surface

| API | 용도 | 호출 시점 |
|---|---|---|
| `POST /collections` (multipart: zip + title) | 신규 collection 등록 (client 가 색인한 폴더 zip, D4) | KbManagerDialog 의 "추가" / lighthouse-cli upload |
| `GET /collections` | registry list (α flat 이라 전체 응답) | Promaker startup (Q1) / KbManagerDialog open / session reject 시 sync |
| `GET /collections/{id}/status` | 단일 collection 상태 (idle / error / not-found) | UI polling (필요 시) |
| `POST /collections/{id}/payload` | 재업로드 — 같은 id 에 새 zip swap | KbManagerDialog 의 "새 버전 업로드" (D5) |
| `DELETE /collections/{id}` | 제거 (`Collections\<id>\` 전체 purge, D7) | KbManagerDialog 의 "제거" |
| `GET /collections/{id}/files/{fileId}` | 원문 byte stream (PDF/DOCX/..., D6) | citation 클릭 시 |
| `GET /collections/{id}/files/{fileId}/thumbnail` | 미리보기 (Phase S7 옵션) | UI 보조 |
| `POST /sessions` `{ collectionIds }` | active 셋 routing token 발급 (Q2 ATTACH limit hard fail 포함) | chat panel open |
| `DELETE /sessions/{token}` | session 해제 | chat panel close / process exit |
| (MCP) `attachment_list/_outline/_search/_read` | 검색·읽기 (session 헤더로 active 셋 routing) | LLM 호출 |

모든 호출 공통 헤더:
- `Authorization: Bearer <PSK>`
- `X-LightHouse-Session: <token>` (session API 와 MCP 만)

### 3.10 server-side storage layout

```
%PROGRAMDATA%\Dualsoft\LightHouseService\
  config.json                # 외부 설정 (§3.11)
  registry.json              # collection 목록 SSOT (atomic save 패턴, parent r4 LlmConfig.Save 와 동형)
  Collections\
    <guid>-<sanitized-title>\
      meta.json              # { id, title, sourcePathHint, importedAt, fileCount, indexerVersion, ... }
      source\
        plant-spec-v3.pdf
        io-list-2026.xlsx
      .lighthouse-kb\
        index.db
        blobs\images\<sha>.<ext>   # Phase 2+
  Logs\
    service-YYYYMMDD.log     # log4net file appender
  Staging\                   # multipart upload 임시 영역, sweep 대상
    <upload-guid>.tmp
```

### 3.11 service config 외부 노출

```json
// %PROGRAMDATA%\Dualsoft\LightHouseService\config.json
{
  "listenUrl": "https://0.0.0.0:8443",
  "tlsCertPath": "C:\\...\\service.pfx",
  "tlsCertPassword": "...",            // 또는 DPAPI 보호
  "preSharedKey": "...",
  "storageRoot": "%PROGRAMDATA%\\Dualsoft\\LightHouseService\\Collections",
  "maxUploadBytes": 10737418240,       // 10 GB (N6)
  "zipBombRatioLimit": 50,             // 압축 해제 시 누적 / 압축 byte 비율 상한
  "sessionIdleTtlMinutes": 60,
  "stagingSweepIntervalMinutes": 10
}
```

Kestrel `MaxRequestBodySize` = `maxUploadBytes`.

### 3.12 IndexerVersion 호환성 — server gate

client (Promaker / CLI) 가 build 한 `index.db` 의 `Meta.indexer_version` 과 service 의 hosting 가능 범위가 일치해야 검색 정확. service 가 upload 시점에 검증:

```
POST /collections
  → service:
    extract zip → probe `.lighthouse-kb/index.db`
    read Meta.indexer_version, Meta.schema_version
    compare with self.HostingRange (min..max)
    → 범위 안: accept (201 Created)
    → 너무 낮음: 415 + "client lib 업그레이드 필요"
    → 너무 높음: 415 + "service 업그레이드 필요"
```

**paired-release 정책**: Promaker 와 service 의 `Ds2.LightHouse` lib version 은 dist 워크플로에서 강제 동일 (manifest hash 비교). drift 회피 (parent r4 §6 m16 의 완화 근거).

### 3.13 client (Promaker) 측 단순화 — *사유 SSOT*

본 단원은 변경 사유 SSOT. **실제 변경 항목 체크리스트는 §4.2 Phase S5 가 SSOT** (중복 회피).

**왜 변경하는가**:
- attachment_* read tool 의 host 가 service 측으로 이동 → Promaker 측 in-process MCP 가 read tool 호스팅 책임 해제 (`AttachmentTools.cs` 미도입)
- read-path 가 service → Promaker 의 `LlmTurnContext` 에 `KnowledgeBase` 주입 불필요 (parent §3.18.2 채택안 (a) 회귀)
- multi-tenant + LAN → `LlmConfig` 에 service endpoint (`BaseUrl` + `ApiKey`) 추가, 기존 `KbCollections` schema 의 path → guid 식별 전환 (§3.4)
- 색인 자체는 여전히 client → `AttachmentIngestService` 유지, 단 색인 끝나면 **zip 패키징 + upload** 단계 추가

**부수 효과**:
- `PromakerToolNames.All` 의 attachment_* 4종은 parent r4 Phase 1 진입 시 추가되었다가, 본 phase 진입 시 다시 제외 (Promaker 측 MCP 가 host 안 함). DriftTests 의 expectedSet 환원.
- parent §3.0 의 두 경로 분리 invariant (chat image drop ≠ KB ingest) 유지 — service 도입이 KB 경로 만 영향, chat 경로 무관.

### 3.14 parent ↔ service 회귀 매트릭스 (전체 SSOT)

본 매트릭스가 parent ↔ service 회귀의 단일 SSOT. parent 진입 박스의 7행은 *진입 hint* (요약), 본 표가 *전체*.

| parent 단원 | 회귀 내용 |
|---|---|
| §3.9 (저장 위치 — path-based 사용자 자유) | **재작성** — collection = server-side guid, 사용자 폴더 안 사본 X |
| §3.10 (MCP tool surface — server 측 active 셋 fix) | tool surface 자체는 그대로. host 위치만 service 로 이동 (본 문서 §3.1.1 / §3.1.3) |
| §3.17 (SQLite 운영 — WAL/동시성/재색인) | client 측 색인 build 의 SSOT 로 유지. service read 측은 read-only ATTACH 만 |
| §3.18 (DI / lifecycle — KnowledgeBase facade) | client 측은 Indexer facade, service 측은 Searcher facade 로 양분 (본 문서 §3.1.2). r4 의 단일 facade 가정 변경 |
| §3.18.2 (LlmTurnContext 에 KnowledgeBase 주입) | **회피** — read-path 가 service 측이라 turn context 의 KB 주입 자체 불필요 |
| §4.1 첫 task (`.gitignore .lighthouse-kb/`) | **삭제** — 사용자 폴더에 안 생김 |
| §4.5 (Promaker 측 통합 — `AttachmentTools.cs` / KbManagerDialog / LlmConfig 등) | **재작성** — 본 문서 §3.13 (사유) + §4.2 Phase S5 (체크리스트) |
| §4.7 (UI dock 패널 폐기 결정) | 유지 (영향 없음) |
| §6 m15 (PII / 보안 — collection 등록 시 consent) | **강화** — multi-tenant α flat 이라 위험 ↑. 등록 시 "이 collection 은 다른 사용자도 검색 가능" 명시 의무화 (본 문서 §6 m2) |
| §6 m16 (ATTACH 된 collection schema 불일치) | **완화** — service 의 IndexerVersion gate (본 문서 §3.12) + paired-release (본 문서 §6 m1) 가 흡수 |

---

## 4. 남은 할 일 (Phase 별)

### 4.1 Phase S0 — 진입 전 확인 (선행 의존)

- [ ] parent r4 의 Phase 1 **lib 본체** 완료 — §4.2 / §4.3 / §4.4 / §4.8 의 lib unit test 까지. 본 phase 진입 직전 status 점검
- [ ] **parent §4.5 / §4.1 첫 task / §4.6 / §4.8 의 Promaker 의존 테스트가 정상적으로 SKIP 되었는지 확인** (대안 B / parent r5 결정 12). prod 에 `LlmConfig.KbCollections : List<{path, active}>` schema 가 깔리지 않았어야 schema migration 부담 0 (§4.3 default (c) 의 전제)
- [ ] parent r4 의 `Ds2.LightHouse` lib 가 read-path / write-path 분리 가능한 facade 형태인지 점검. parent §3.18.1 의 "record-of-functions vs interface" 결정에 따라 양분 비용 달라짐
- [ ] 사용자 머신에 parent Phase 1 의 시험 산출물 (`<폴더>/.lighthouse-kb/`) 잔재가 있는지 확인 — 있으면 cleanup 정책 결정 (대안 B 의도상 잔재 0 이어야)

### 4.2 Phase S1~S7

각 phase 헤더의 **DoD** = 완료 정의 (acceptance criteria).

#### Phase S1 — service 기반 host

**DoD**: TLS bind 성공 + PSK 인증 미들웨어가 빈 `GET /collections` 요청에 200 응답 (registry 비어있음). `Collections\` / `Staging\` / `Logs\` 초기화 완료. log4net file appender 가 첫 로그 라인 기록. EventLog 에 service start 이벤트 등록.

- [ ] 신규 project `Solutions/Tools/Ds2.LightHouseService/Ds2.LightHouseService.fsproj` (또는 C# host)
  - TargetFramework `net9.0` (`-r win-x64 --self-contained` publish 권장)
  - `Microsoft.Extensions.Hosting.WindowsServices` + `UseWindowsService()`
  - `Ds2.LightHouse` ProjectReference (read-path 사용)
- [ ] config 로드 — `%PROGRAMDATA%\Dualsoft\LightHouseService\config.json` (§3.11)
- [ ] TLS 바인드 — Kestrel HTTPS, `tlsCertPath` 로드
- [ ] PSK auth middleware — `Authorization: Bearer` 검증 + fixed-time compare (parent r4 의 `McpHostService` 의 nonce 검증 패턴 재활용)
- [ ] storage layout (`Collections\` / `Staging\` / `Logs\`) 초기화
- [ ] log4net 설정 — `Logs\service-YYYYMMDD.log` + EventLog 병행
- [ ] sln 등록 — `Apps/Promaker/Promaker.sln` + `Solutions/Ds2.sln` (parent r4 §4.1 의 sln 2개 정책 동일)

#### Phase S2 — collection 관리 API

**DoD**: 최소 zip (`source/` + `.lighthouse-kb/index.db` 최소형 + `meta.json`) 으로 `POST /collections` 성공 → `Collections\<guid>-<title>\` 배치 → `GET /collections` 1행 응답. sanitize (`..` traversal) / zip bomb (10x ratio) / IndexerVersion gate (호환 / too-low / too-high 3 케이스) unit test 통과. `POST /collections/{id}/payload` swap rollback 시나리오 통과.

- [ ] `POST /collections` — multipart 수신 + Staging\ 임시 저장
- [ ] zip sanitize — entry path `..` 가드 + 절대경로 거부 + Collections\<id>\ 하위 verify
- [ ] zip bomb 가드 — `zipBombRatioLimit` 기반 누적 byte 한도
- [ ] IndexerVersion 호환성 검증 (§3.12) — 415 응답 시 명확한 안내
- [ ] atomic move (Staging → Collections\<guid>-<title>\)
- [ ] `registry.json` upsert (parent r4 의 `LlmConfig.Save` atomic 패턴 재활용)
- [ ] `GET /collections` — registry 응답 (α flat)
- [ ] `GET /collections/{id}/status` — 단일 collection 상태
- [ ] `POST /collections/{id}/payload` — 재업로드 swap (기존 Collections\<id>\ → `<id>.old\` rename → 신 zip 전개 → 검증 OK 면 `<id>.old\` purge / fail 시 rollback)
- [ ] `DELETE /collections/{id}` — Collections\<id>\ 전체 purge (D7)
- [ ] Staging\ stale sweep (process exit 시 incomplete upload / timeout)

#### Phase S3 — session + MCP search host

**DoD**: `POST /sessions { collectionIds }` → token 발급. unknownIds / unindexableIds 응답 unit test 통과. ATTACH limit 10 hard fail (Q2) 통과. LLM client (codex 가능) 가 MCP `attachment_list` 호출 시 active 셋 union 응답. session 헤더 누락 시 401. idle TTL sweep 후 connection dispose 통과.

- [ ] `POST /sessions` — collectionIds validate (registry 부분집합) + ATTACH limit 10 가드 (Q2) + token 발급
- [ ] `SessionRegistry` (in-memory) — `{token, activePaths, attachedAliases, connection, lastUsedAt}`
- [ ] per-session `SqliteConnection` 격리 (Q3) — open + ATTACH `kb0..kbN-1` lazy on first MCP call
- [ ] idle TTL sweep (`sessionIdleTtlMinutes`) — connection dispose + registry 제거 (L2-3)
- [ ] `DELETE /sessions/{token}` — 명시 해제
- [ ] MCP server host — `ModelContextProtocol.AspNetCore` `WithHttpTransport()` + `WithToolsFromAssembly()` (parent r4 의 `McpHostService` 패턴 동일)
- [ ] `AttachmentTools` (서버측 신설) — 4종 (`attachment_list/_outline/_search/_read`). session 헤더로 `SessionState` lookup → 그 connection 으로 `Ds2.LightHouse.Searcher` 호출
- [ ] fileId 합성 — `<collection-index>:<documents-id>` (parent r4 §3.10 그대로)
- [ ] 응답에 unknownIds / unindexableIds 동봉 (active 셋 sync 용, §3.8)

#### Phase S4 — file serving (citation 원문)

**DoD**: `GET /collections/{id}/files/{fileId}` 가 `Collections\<id>\source\` 의 원본 byte stream 반환 (D6). Content-Type 추정 OK (PDF / DOCX / XLSX / PPTX / TXT / MD 케이스). 존재하지 않는 fileId 는 404. 권한 (PSK) 없으면 401.

- [ ] `GET /collections/{id}/files/{fileId}` — Collections\<id>\source\ 의 원본 stream (Content-Type 추정)
- [ ] (옵션) `GET /collections/{id}/files/{fileId}/thumbnail` — PDF page 0 / Office 파일 첫 슬라이드 등 작은 미리보기
- [ ] (옵션) `GET /collections/{id}/files/{fileId}/page/{n}.png` — Phase 2 PDF page 렌더

#### Phase S5 — Promaker (client) 통합

**DoD**: Promaker 가 service 에 PSK 로 인증 → KbManagerDialog 에서 폴더 추가 → 색인 → upload → chat 시작 시 session 발급 → LLM 이 attachment_search 호출 → citation 포함 응답 생성. parent r4 의 in-process MCP 시나리오와 동등 UX 도달. KbManagerDialog 에서 active 토글 → 다음 chat 부터 반영 (L1) 확인.

- [ ] `LlmConfig.cs` 확장:
  - `KbCollections` schema 변경: `List<KbCollectionEntry>` = `{CollectionId, DisplayName, Active}` (§3.4)
  - `LightHouseService` 신설: `{BaseUrl, ApiKey}` (§3.4)
  - atomic save / corrupt fallback 패턴 유지
  - schema migration 처리 (§4.3 결정 따라)
- [ ] `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` 신설 — HTTP client wrapper
  - `UploadCollectionAsync(title, zipStream)` → guid
  - `ListCollectionsAsync()` → CollectionInfo[] (Promaker startup 호출 — Q1)
  - `DeleteCollectionAsync(id)`
  - `CreateSessionAsync(collectionIds)` → `{token, unknownIds[], unindexableIds[]}` (§3.8)
  - `DeleteSessionAsync(token)`
  - 모든 요청에 `Authorization: Bearer <PSK>` 자동 동봉
- [ ] `Apps/Promaker/Promaker/Knowledge/CollectionPackager.cs` 신설 — folder → zip (`source/` + `.lighthouse-kb/` + `meta.json`)
- [ ] `Apps/Promaker/Promaker/Knowledge/AttachmentIngestService.cs` 갱신 — 색인 완료 후 zip 패키징 + LightHouseClient 로 upload
- [ ] `Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml(.cs)` 갱신 (parent r4 §4.5 의 KbManagerDialog 와 큰 폭 다름):
  - 추가: folder picker → 색인 진행률 (client 측) → upload 진행률 (HTTP) → 완료
  - 제거: 색인 자체 storage 관리 (server 가 흡수)
  - active 토글 → `LlmConfig.KbCollections[i].Active` 변경만 (server 무영향, 다음 chat 부터 반영)
  - chip 안내 "변경은 다음 chat 부터" (§3.8)
- [ ] `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml(.cs)` 갱신 — LLM 탭에 "LightHouse Service" section (BaseUrl / PSK 입력)
- [ ] `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` 갱신:
  - `InitializeAsync` 에서 `LightHouseClient.CreateSessionAsync` 호출 + 응답 처리 (unknownIds/unindexableIds sync — §3.8)
  - `.mcp-config` 작성 시 lighthouse server 항목 추가 (service URL + session header)
  - chat panel close / Dispose 시 `DeleteSessionAsync` (L2-1)
- [ ] `Apps/Promaker/Promaker/App.xaml.cs` process exit hook — 살아있는 token 일괄 DELETE (L2-2)
- [ ] `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` — attachment_* 4종 **제외** (parent r4 Phase 1 추가분을 본 phase 진입 시 다시 제외)
- [ ] `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` — service 도입 시 attachment_* 가 Promaker 측 ModelTools 에 없음을 확인. expectedSet 은 parent r4 의 doc-level 4 + read 2 = **6종** 으로 환원 (현 상태 grep 후 확정)
- [ ] (parent §4.5 의) `Apps/Promaker/Promaker/LlmAgent/Tools/AttachmentTools.cs` — 본 phase 에서 **만들지 않음** (service 측 신설)
- [ ] (parent §4.5 의) `LlmTurnContext.cs` 의 `KnowledgeBase` 필드 — 본 phase 에서 **만들지 않음** (read-path 가 service 라 회피)

#### Phase S6 — CLI 도구 (옵션, 회사 IT 운영용)

**DoD**: `lighthouse-cli index <folder> --upload <url> --psk <key> --title "..."` 무인 동작으로 색인 + upload 완료 + exit code 0. 인증 실패 / IndexerVersion mismatch / zip 크기 초과 케이스 별 비-0 exit code + stderr 안내.

- [ ] `Solutions/Tools/Ds2.LightHouse.Cli/Ds2.LightHouse.Cli.fsproj`
  - `Ds2.LightHouse` write-path 사용
  - `lighthouse-cli index <folder> --upload <service-url> --psk <key> --title "..."` 명령
  - GUI 없이 batch 색인 + upload
- [ ] (옵션) `lighthouse-cli sync` — registry 기준으로 stale collection 정리 등 운영 명령

#### Phase S7 — 후속 (선택)

**DoD**: 항목별 채택 시점에 결정. mTLS 도입 시 PSK 회전 부담 완화 확인. SSE `/events` 도입 시 KbManagerDialog 의 polling 제거. Resumable upload 도입 시 수 GB zip 실패 후 재개 통과.

- [ ] SSE `/events` — 색인 진행률 stream (현재 upload progress 만 의미. server-side 색인 없음 — N5)
- [ ] resume 가능한 chunked upload (tus-protocol 등) — 대용량 (수 GB) zip 실패 시 재전송 부담 완화
- [ ] **mTLS** (PSK 회전 부담 완화 — §3.7 참조)
- [ ] multi-service routing — 사용자가 동시에 회사 service + 개인 PC service 두 군데 등록 가능. `LlmConfig.LightHouseServices : List<...>`
- [ ] β/γ multi-tenant 확장 (PII 격리 요구 발생 시)
- [ ] connection pool (Q3 격리 정책 완화) — 메모리 압박 시점에 검토

### 4.3 미확정 항목 (Phase S 진입 시 결정)

| 항목 | 위치 | 권장 default | 확정 시점 |
|---|---|---|---|
| service host 언어 — F# vs C# | §4.2 Phase S1 | F# (LightHouse 와 동일 stack) | S1 진입 |
| TLS 인증서 발급 운영 — self-signed vs 사내 CA | §3.7 | self-signed (사내 신뢰 root 추가) | S1 진입 |
| PSK 회전 정책 | §3.7 | 수동 (mTLS 는 Phase S7) | S1 진입 |
| ATTACH connection lazy open vs eager | §3.8 | lazy on first MCP call | S3 진입 |
| `Ds2.LightHouse` lib facade 의 양분 형태 (parent §3.18.1 결정 후 facade 가 read-only mode 옵션 받는지) | §3.1.2 | facade 에 `readonly: bool` 파라미터 추가 | S1 진입 |
| Promaker 의 in-process search fallback 제공 여부 (service 미가동 시) | §3.13 | **fallback 안 함** (SSOT 일관성) | S5 진입 |
| `lighthouse-cli` 의 Phase 진입 시점 | §4.2 Phase S6 | **Phase S5 완료 후 별도** (단순화) | S5 진입 |
| **`LlmConfig.KbCollections` schema migration** — parent r4 의 `{path,active}` 데이터 처리 | §3.4 / Phase S5 | **(c) 마이그레이션 불필요** — parent r5 결정 12 (대안 B) 로 parent Phase 1 에서 §4.5 SKIP. `KbCollections` 자체가 prod 에 깔린 적 없음. fallback (사용자가 어떤 경로로든 `{path,active}` 형태 data 보유 시): (a) 자동 폐기 + chip 안내. default = (c) | S5 진입 직전 (Phase S0 의 잔재 점검 항목과 함께 확인) |
| `LightHouseService` config 의 JSON property attribute 적용 (PascalCase 직렬화 vs camelCase) | §3.4 / Phase S5 | parent r4 `LlmConfig.cs` 의 현 직렬화 관례 grep 후 정합 | S5 진입 |

---

## 5. 관련 파일 / 경로

### 신규 (Phase S 진입 시)

- `Solutions/Tools/Ds2.LightHouseService/Ds2.LightHouseService.fsproj` + 본체 (.fs)
- `Solutions/Tools/Ds2.LightHouse.Cli/Ds2.LightHouse.Cli.fsproj` (옵션, Phase S6)
- `Solutions/Tests/Ds2.LightHouseService.Tests/`
- `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` (HTTP wrapper)
- `Apps/Promaker/Promaker/Knowledge/CollectionPackager.cs` (folder → zip)

### 수정

- `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs` — schema 변경 (`KbCollections` `{CollectionId, DisplayName, Active}` + `LightHouseService { BaseUrl, ApiKey }`)
- `Apps/Promaker/Promaker/Knowledge/AttachmentIngestService.cs` — 색인 후 zip + upload 단계 추가
- `Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml(.cs)` — server registry sync, upload UI
- `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml(.cs)` — LightHouse Service section
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` — session 발급/해제, `.mcp-config` lighthouse 항목 작성
- `Apps/Promaker/Promaker/App.xaml.cs` — process exit hook (살아있는 session 일괄 DELETE)
- `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` — attachment_* 제외 환원
- `Apps/Promaker/Promaker/LlmAgent/Prompts/5.knowledge-base.md` — server-side host 명시
- `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` — expectedSet 6종으로 환원
- `Apps/Promaker/Promaker.sln` + `Solutions/Ds2.sln` — LightHouseService + Tests 추가

### 삭제 / 미도입 (parent r4 대비)

- (parent r4 §4.5 의) `Apps/Promaker/Promaker/LlmAgent/Tools/AttachmentTools.cs` — 본 phase 진입 시 **만들지 않음** (service 측 신설)
- (parent r4 §4.5 의) `LlmTurnContext.cs` 의 `KnowledgeBase` 필드 — 본 phase 진입 시 **만들지 않음** (read-path 가 service 라 회피)

### 참조용 (수정 없음)

- `todo-lighthouse-kb-index.md` (parent design) — Phase 1 본체 + §3.12 schema + §3.13 RefLocator + §3.17 PRAGMA 등 SSOT
- `Apps/Promaker/Promaker/LlmAgent/McpHostService.cs` — service 측 MCP host 의 패턴 참조 (loopback nonce → LAN PSK 로 변환)
- `Apps/Promaker/Promaker/LlmAgent/McpConfigWriter.cs` — `.mcp-config` 작성 패턴 (lighthouse 항목 추가 시 참조)
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` — `InitializeAsync` 의 `_mcpHost.StartAsync` 호출 패턴, session 발급 코드의 자연 위치
- `Solutions/Core/Ds2.LlmAgent/doc/todo-llm-chat-attachment.md` — chat 경로 측 (parent §3.0 두 경로 분리 invariant 유지)

---

## 6. 주의 사항

1. **paired-release 강제** — Promaker 와 service 의 `Ds2.LightHouse` lib version 이 다르면 schema 의미 drift. dist 워크플로 (`make dist`) 에서 manifest hash 비교 후 mismatch 시 build fail.
2. **multi-tenant α flat 의 PII 위험** — 사용자가 등록한 collection 은 모든 사용자가 검색 가능. KbManagerDialog 의 "추가" 클릭 시 **consent dialog 의무화** ("이 collection 의 내용은 다른 사용자도 검색 가능합니다. 비밀 문서 포함 폴더는 등록 금지"). parent r4 §6 m15 의 강화판.
3. **zip sanitize 우선** — entry path `..` traversal / 절대경로 / symlink (있을 경우) 모두 거부. `Collections\<id>\` 하위로만 전개. 누적 byte 한도 (`zipBombRatioLimit`) 가드.
4. **chat panel lifetime = session lifetime (L1)** — turn 마다 재발급 X. 사용자가 KbManagerDialog 에서 active 토글해도 현 chat 영향 0. UI chip "변경은 다음 chat 부터" 강제.
5. **session 3중 cleanup (L2)** — panel close (1차) + process exit (2차) + idle TTL (3차). leak 차단.
6. **server reject 시 lazy sync (Q4)** — `POST /sessions` 응답의 `unknownIds[]` 받으면 Promaker 가 `GET /collections` 로 동기화 + LlmConfig 정리. `unindexableIds[]` 는 LlmConfig 보존 + 재시도 가능. Promaker 시작 시 1회 외 추가 polling 없음 (Q1).
7. **LightHouse lib 양분 시 facade 설계 주의** — write-path (Indexer) 와 read-path (Searcher) 가 같은 facade record 안에 섞이면 service publish 시 write-path 코드 dead-link. parent §3.18.1 의 record-of-functions 선택이라면 read 전용 record 분리 검토. §4.3 미확정.
8. **fallback 금지** — service 미설정 / 미가동 시 Promaker 가 in-process search 로 회귀하지 말 것. SSOT 가 둘이 되면 일관성 깨짐. KB 비활성 + 명확한 안내만.
9. **parent r4 의 §3.18.2 채택안 (a) 회귀** — service 도입 시 read-path 가 LlmTurnContext 와 무관 (server 측에서 자체 routing). r5 통합 시 §3.18.2 단원 큰 폭 재작성.
10. **MCP host 2개의 LLM 인지 부담** — `.mcp-config` 에 promaker + lighthouse 두 server 등록 시 tool 14종 (10 mutation/read + 4 attachment) 가 한 공간에 보임. 충돌 없음 (이름 중복 0) 이지만 system prompt `5.knowledge-base.md` 에 출처 명시 권장.
11. **CLAUDE.md 자가 검열 trigger** — Phase S1~S5 각각 다음 trigger 다중 충족 예상: ② 신규 함수/타입 3+ 신설, ③ 단일 파일 100+ line 또는 2+ 파일 동시 변경, ⑤ public API/SSOT 갱신. 각 phase 별 sub-agent 검열 의무.
12. **commit 정책** — multi-step plan 의 "go" 동의를 commit step 까지 묶지 말 것. commit 은 별도 confirm (memory: `feedback_commit_authorization`).
13. **line number 박제 회피** — 본 문서의 parent 참조 (예: `LlmChatViewModel.InitializeAsync` 의 `_mcpHost.StartAsync` 호출 line) 는 가능한 symbol 기반. 진입 시 grep 재확인. parent r4 §6 m13 와 동일 정책.
14. **parent §3.0 두 경로 분리 invariant 유지** — service 도입은 KB ingest 경로만 영향. chat image/text drop 경로 (`AttachmentClassifier`) 와 무관. `todo-llm-chat-attachment.md` 와의 cross-PR 충돌 없음.
15. **MEMORY.md `## Project` 등록** — Phase S1 진입 commit 직후 본 todo 항목을 메모리에 등록 (parent r4 §6 m11 의 본 phase 판).

---

## 7. 다음 세션 첫 행동

1. 본 문서 + parent 정독 — 특히 **§0 의 D-id 정의표** 부터. 본 문서 본문의 `R1`/`Q1`~`Q4`/`D1`~`D7`/`α`/`N5`/`N6`/`L1`/`L2` 모두 §0 표가 SSOT.
2. parent Phase 1 status 점검 — Phase S0 진입 전 in-process MVP 완료 확인.
3. parent §3.18.1 (KnowledgeBase facade 형식) 결정 결과 확인 — read-path / write-path 양분 비용에 영향 (§3.1.2 / §4.3).
4. §4.3 미확정 항목 8건 중 우선 결정 — 특히 **`LlmConfig.KbCollections` schema migration** (기존 사용자 데이터 처리).
5. Phase S0 의 선행 의존 항목 (parent Phase 1 의 LightHouse facade 가 양분 가능한 형태인지) 점검.
6. Phase S1 진입 confirm 받기 — service project 생성 + sln 2개 + config 외부 노출 layout.
7. commit 은 별도 confirm (memory: `feedback_commit_authorization`).
