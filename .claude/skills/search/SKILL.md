---
name: search
description: "색인된 폴더 (`<folder>/.lighthouse-kb/index.db`) 를 hybrid 검색 (BM25 + bge-m3 ANN) 하여 사용자 질문에 답변. /indexer 산출물을 LightHouse Service 업로드 없이 로컬에서 직접 query. Trigger: /search"
trigger: /search
---

# /search

`/indexer` 가 박제한 색인 산출물 (`<folder>/.lighthouse-kb/index.db`) 을 **로컬에서 직접 query** 하여 사용자 질문에 답변. LightHouse Service 미가동 환경에서도 동작 (서버 업로드 불필요).

검색 방식: **hybrid retrieval** = BM25 (ChunksFts trigram) + bge-m3 ANN (sqlite-vec `vec0`). 두 결과를 RRF (reciprocal rank fusion) 로 결합 후 top-K chunk 를 Claude 가 받아 답변 합성.

## Usage

```
/search <folder>                          # 후속 turn 에 사용자 질문 받기
/search <folder> <자연어 질문...>         # 한 줄에 질문까지
```

`<folder>` 의미:
- 절대/상대 경로 모두 허용
- `<folder>/.lighthouse-kb/index.db` 가 존재해야 함
- 미존재 시 사용자에게 `/indexer <folder>` 먼저 실행 안내 후 종료

## 사전 조건

### env var (옵션, default 명시 — `Ds2.LightHouse.Cli` SSOT 와 정합)
- `LIGHTHOUSE_OLLAMA_URL` — default `http://localhost:11434`
- `LIGHTHOUSE_OLLAMA_MODEL` — default `bge-m3`
- `LIGHTHOUSE_OLLAMA_DIM` — default `1024`

### 외부 의존
- **Ollama** listen 중 + `bge-m3` 모델 pull 됨.
  - NaN 방지: `OLLAMA_FLASH_ATTENTION=false` 환경에서 `ollama serve` 권장 (bge-m3 known issue).
- **sqlite3.exe (x64 의무)** — `vec0.dll` 이 x64 전용이라 32-bit sqlite3 와 mismatch 시 `no such module: vec0` 실패.
  - PE header 의 Machine field 검증: `0x8664` (x64) 만 허용. `0x014C` (x86) 거부.
  - PATH 의 sqlite3 가 32-bit 이면 임시 다운로드:
    - URL: 최신은 `https://sqlite.org/download.html` 에서 `sqlite-tools-win-x64-*.zip` 행 파싱 (PRODUCT CSV).
    - 다운로드 → `$env:TEMP\sqlite-x64\` 압축 해제 → 그 안의 `sqlite3.exe` 사용.
- **vec0.dll** (sqlite-vec extension) — light-house repo 빌드 산출물 사용:
  - `<repo>/Solutions/Tools/Ds2.LightHouse.Cli/bin/Release/net9.0/runtimes/win-x64/native/vec0.dll` (우선)
  - `<repo>/Solutions/Tools/Ds2.LightHouse.Cli/bin/Debug/net9.0/runtimes/win-x64/native/vec0.dll` (fallback)
  - 미존재 시 빌드 안내 후 종료.

### vec0.dll 위치 도출 절차
1. skill 의 base dir 에서 `git rev-parse --show-toplevel` → repo root.
2. Release 경로 우선, Debug fallback 으로 탐색.
3. 둘 다 미존재 시:
   ```
   vec0.dll 빌드 안 됨.
   다음 명령 실행 후 재시도:
     dotnet build -c Release <repo>/Solutions/Tools/Ds2.LightHouse.Cli/Ds2.LightHouse.Cli.fsproj
   ```

## 실행 절차 (Claude 가 수행)

### 1. DB 존재 검증
- `<folder>/.lighthouse-kb/index.db` 확인.
- 없으면 "/indexer 먼저 실행" 안내 후 종료.

### 2. 질문 확보
- args 의 두 번째 이후 토큰 → 질문.
- 없으면 사용자에게 일반 텍스트로 질문 요청 (AskUserQuestion 도구 사용 금지).

### 3. Ollama embedding 호출
```powershell
$ollamaUrl = if ($env:LIGHTHOUSE_OLLAMA_URL) { $env:LIGHTHOUSE_OLLAMA_URL } else { 'http://localhost:11434' }
$model     = if ($env:LIGHTHOUSE_OLLAMA_MODEL) { $env:LIGHTHOUSE_OLLAMA_MODEL } else { 'bge-m3' }
$body = @{ model=$model; input=$query } | ConvertTo-Json -Compress
$resp = Invoke-RestMethod -Uri "$ollamaUrl/api/embed" -Method Post -Body $body -ContentType 'application/json' -ErrorAction Stop
$emb = $resp.embeddings[0]
$vecJson = '[' + ($emb -join ',') + ']'   # 1024-dim JSON array text
```
- Ollama 연결 실패 시 "ollama serve + OLLAMA_FLASH_ATTENTION=false" 안내 후 종료.

### 4. BM25 query escaping
FTS5 special character (`"`, `(`, `)`, `:`, `*`) 안전 처리:
- 사용자 질문을 whitespace 로 split → 각 token 을 double-quote 로 wrap → space 로 join.
- 예: `FFT convolution 원리` → `"FFT" "convolution" "원리"`.
- 빈 token 제거.

### 5. sqlite-vec hybrid 검색
임시 .sql 파일 생성 후 `sqlite3 <db> ".read '<sql-forward-slash>'"` 실행 (명령행 길이 회피).

**핵심 함정 박제**:
1. **`.read` 인자 경로는 forward slash 만** — sqlite3 의 dot-command parser 가 backslash 를 escape 로 해석 (`\U`, `\A` 가 사라짐).
2. **SQL 파일은 UTF-8 BOM 없이** 작성. PowerShell 의 `Out-File -Encoding utf8` 는 BOM 추가 → sqlite3 가 첫 줄을 syntax error 로 거부. 대신:
   ```powershell
   [System.IO.File]::WriteAllText($sqlFile, $sql, (New-Object System.Text.UTF8Encoding $false))
   ```
3. **`.load` 인자도 forward slash** — `'F:/path/to/vec0'` (확장자 없이도 OK).

```sql
.load 'F:/Git/ds2/light-house/.../vec0'   -- 확장자 없이도 OK, 경로는 forward slash
.headers off
.mode tabs

WITH vec_hits AS (
  SELECT ChunkId, ROW_NUMBER() OVER (ORDER BY distance) AS rk
  FROM Chunks_Vectors
  WHERE embedding MATCH '<vecJson>' AND k = 10
),
bm25_hits AS (
  SELECT rowid AS ChunkId, ROW_NUMBER() OVER (ORDER BY rank) AS rk
  FROM ChunksFts
  WHERE Text MATCH '<bm25Escaped>'
  ORDER BY rank
  LIMIT 10
),
combined AS (
  SELECT ChunkId, 1.0 / (60.0 + rk) AS score FROM vec_hits
  UNION ALL
  SELECT ChunkId, 1.0 / (60.0 + rk) AS score FROM bm25_hits
),
top_chunks AS (
  SELECT ChunkId, SUM(score) AS total_score
  FROM combined
  GROUP BY ChunkId
  ORDER BY total_score DESC
  LIMIT 5
)
SELECT
  printf('%.4f', t.total_score) AS score,
  d.OriginalPath,
  COALESCE(d.Title, '') AS Title,
  c.RefLocator,
  c.TokenCount,
  c.Text
FROM top_chunks t
JOIN Chunks c ON c.Id = t.ChunkId
JOIN Documents d ON d.Id = c.DocumentId
ORDER BY t.total_score DESC;
```

**fallback 처리**:
- BM25 결과 0건: vec0 결과만 사용 (CTE `bm25_hits` empty 자연 처리).
- vec0 결과 0건: BM25 만 — 보통은 발생 안 함.
- 둘 다 0건: "관련 chunks 없음" 보고 후 종료.

### 6. 답변 생성
- 사용자 질문에 대해 **한국어로 직접 답변** (CLAUDE.md 글로벌 규약).
- chunk 본문을 raw dump 하지 말 것. 요약/통합 후 정리.
- 답변에 **출처 표기**: 파일 basename + RefLocator. 예: `[Ch18.pdf:p=2]`.
- 검색 결과가 질문과 무관해 보이면 솔직히 명시 ("색인된 자료에서 직접 답할 만한 chunk 가 부족합니다").

## 사용 예

```
/search F:/tmp/f
→ (질문 받기) "FFT convolution 의 원리?"
→ Claude: "...overlap-add 기법으로 긴 신호를 작은 segment 로 분할 ... [Ch18.pdf:p=1, p=2]"
```

```
/search F:/tmp/f FFT convolution 의 원리와 overlap-add 사용 이유
→ 한 줄에 질문까지 — 동일 결과
```

## Exit / 오류 케이스

| 케이스 | 메시지 |
|---|---|
| `<folder>` 미존재 | "폴더 미존재 — <folder>" |
| `.lighthouse-kb/index.db` 미존재 | "색인 산출물 없음 — `/indexer <folder>` 먼저 실행." |
| sqlite3.exe 미가용 | "sqlite3 미설치. PATH 에 추가 후 재시도." |
| vec0.dll 미존재 | "vec0.dll 빌드 안 됨. `dotnet build -c Release <CliProj>` 후 재시도." |
| Ollama 연결 실패 | "ollama serve 가동 필요. NaN 방지: `OLLAMA_FLASH_ATTENTION=false`." |
| 검색 0건 | "색인된 자료에서 관련 chunk 미발견." |

## 비포함 (follow-up 후보)

- **이미지/캡션 기반 검색** — `ImageCache.CaptionText` 가 현재 NULL. VLM 캡셔닝 단계 박제 후 가능.
- **`OutlineNodes` 기반 navigation** — heading 단위 답변.
- **다중 폴더 동시 검색** — 현재는 단일 folder 만.
- **LightHouse Service 의 `/sessions` search API 호출** — 본 phase 는 로컬 DB 직접 query. 서버 경유 검색은 별도 skill / CLI command 가 박제될 때.
- **임시 .sql 파일 자동 정리** — 명령 종료 시 `Remove-Item`.

## 동작 SSOT 참조

- 색인 산출물 schema: `<folder>/.lighthouse-kb/index.db`
  - `Documents` / `Chunks` / `ChunksFts` (FTS5 trigram) / `Chunks_Vectors` (vec0 float[1024]) / `OutlineNodes` / `ImageCache` / `ImageReferences` / `Meta`.
- chunk RefLocator format: `p=<pageNo>` (PDF) / `s=<sheetName>:r=<row>` (xlsx, 미래 박제).
- embedding 모델/차원 SSOT: `Solutions/Tools/Ds2.LightHouse.Ollama/OllamaEmbedder.fs` § `OllamaDefaults` (bge-m3 / 1024).
