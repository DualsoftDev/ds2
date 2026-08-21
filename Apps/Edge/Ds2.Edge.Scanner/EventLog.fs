module Pi5ScanPoc.EventLog

// Pi5 로컬 store-and-forward 버퍼 (SQLite append-only 이벤트 로그).
//   설계: research_result.md §10.2 — Kafka 의 "로그+offset" 개념만 경량 구현.
//   - event_log(seq PK AUTOINCREMENT) : seq = Kafka offset 역할 = append 순서.
//   - OriginTsMs(모노토닉 TickCount64) + wall_clock_ms(UTC epoch ms) 두 컬럼 병기(§10.4).
//       평상시 = OriginTsMs 로 정확 / 재부팅 경계 보정 = wall_clock 델타. **보정 자체는 미확정이라 미구현**(이슈).
//   - meta.last_acked_seq : Agent 가 실제 처리한 마지막 seq (offset). 이하 정리.
//   - retention : 최대 1시간분 + maxRows. 초과분 오래된 것부터 삭제(§10.7.4).
//   - seq 리셋 금지(§10.7.6) : DELETE 만 사용, AUTOINCREMENT/sqlite_sequence 는 그대로 둔다.

open System
open Ds2.Backend.Plc
open Microsoft.Data.Sqlite

[<Literal>]
let private LastAckedKey = "last_acked_seq"

/// SQLite 단일 커넥션을 gate lock 으로 직렬화. scan 루프(append)·재연결(flush)·ack 핸들러가
/// 서로 다른 스레드에서 접근하므로 lock 필수 (SQLite connection 은 동시 사용 불가).
type EventLog(dbPath: string, retentionMs: float, maxRows: int, log: string -> unit) =
    // dbPath 부모 디렉토리 보장 — 없으면 SqliteConnection.Open 이 실패한다(예: /var/lib/edge 미생성).
    // 설치 스크립트(setup-pi.sh)가 만들지만 이중 안전 + 임의 경로 견고성. (있으면 no-op → 회귀 0.)
    let ensureDbDir =
        let dir = System.IO.Path.GetDirectoryName(dbPath)
        if not (String.IsNullOrEmpty dir) && not (System.IO.Directory.Exists dir) then
            try System.IO.Directory.CreateDirectory dir |> ignore with _ -> ()
    let conn = new SqliteConnection($"Data Source={dbPath}")
    let gate = obj ()

    /// 재부팅 보정 앵커 (seq_old, OriginTsMs_old, wall_clock_old) — 이전 세션(재부팅 전) event_log 마지막 행.
    /// 생성 직후(첫 Append 전) 1회 캡처. §10.5.2(b): 재부팅=수집 중단이라 재시작 시 직전 마지막 행이 곧 앵커.
    /// 별도 meta 불필요. 첫 설치/empty 면 None → 보정 없음(§10.8).
    /// seq 를 함께 잡는 이유: 복원은 앵커 seq 보다 큰(=현재 세션) 행에만 적용해야 이전 세션의 더 이른
    /// 미전송 행(raw 정확)을 잘못 보정하지 않는다.
    let mutable rebootAnchor : (int64 * int64 * int64) option = None

    do
        conn.Open()
        use pragma = conn.CreateCommand()
        // WAL: 쓰기/읽기 병행 + 크래시 내구성. NORMAL: fsync 부담 완화(전원차단 시 마지막 몇 tx 손실 감수).
        pragma.CommandText <- "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"
        pragma.ExecuteNonQuery() |> ignore
        use ddl = conn.CreateCommand()
        ddl.CommandText <-
            """
            CREATE TABLE IF NOT EXISTS event_log (
                seq           INTEGER PRIMARY KEY AUTOINCREMENT,
                origin_ts_ms  INTEGER NOT NULL,
                wall_clock_ms INTEGER NOT NULL,
                addr          TEXT    NOT NULL,
                value         TEXT    NOT NULL,
                source        TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """
        ddl.ExecuteNonQuery() |> ignore
        // 앵커 캡처 — 첫 Append 전에 이전 세션 마지막 행을 읽는다(이후엔 현재 세션 행으로 덮이므로 지금).
        use anchorCmd = conn.CreateCommand()
        anchorCmd.CommandText <- "SELECT seq, origin_ts_ms, wall_clock_ms FROM event_log ORDER BY seq DESC LIMIT 1"
        use ar = anchorCmd.ExecuteReader()
        if ar.Read() then rebootAnchor <- Some(ar.GetInt64 0, ar.GetInt64 1, ar.GetInt64 2)
        match rebootAnchor with
        | Some(s, o, w) -> log $"[buffer] 재부팅 앵커 로드 — seq_old={s} OriginTsMs_old={o} wall_old={w} (현재 세션 행이 monotonic 리셋 시 push 값 복원)"
        | None -> log "[buffer] 앵커 없음(첫 설치/empty) — 보정 없이 시작"

    /// 재부팅 보정 (§10.4 공식) — **전송(push)값에만** 적용. 저장값은 raw 유지.
    ///   OriginTsMs_복원 = OriginTsMs_old + (wall_clock_new − wall_clock_old)
    /// 조건: (a) seq > seq_old (현재 세션 행만 — 이전 세션의 이른 미전송 행은 raw 정확이라 제외)
    ///       (b) rawOrigin < OriginTsMs_old (monotonic 이 부팅 시 0 으로 리셋 = 재부팅 감지;
    ///           리셋 안 됐으면(크래시 재시작 등 monotonic 유지) raw 그대로가 정확 → 보정 안 함).
    /// wall 델타 음수(NTP 역행) 방어 = max 0(§10.7.6).
    let restoreOrigin (seq: int64) (rawOrigin: int64) (wall: int64) : int64 =
        match rebootAnchor with
        | Some(oldSeq, oldOrigin, oldWall) when seq > oldSeq && rawOrigin < oldOrigin ->
            oldOrigin + max 0L (wall - oldWall)
        | _ -> rawOrigin

    let readMetaInt64 (key: string) (dflt: int64) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT value FROM meta WHERE key=@k"
        cmd.Parameters.AddWithValue("@k", key) |> ignore
        match cmd.ExecuteScalar() with
        | null -> dflt
        | v -> match Int64.TryParse(string v) with | true, n -> n | _ -> dflt

    let writeMetaInt64 (key: string) (v: int64) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "INSERT INTO meta(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=@v"
        cmd.Parameters.AddWithValue("@k", key) |> ignore
        cmd.Parameters.AddWithValue("@v", string v) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// retention 적용 — (a) acked 미만 삭제 (b) 보관시간 초과 삭제 (c) maxRows 초과분 삭제.
    /// lock 안에서만 호출한다.
    let applyRetentionLocked () =
        let acked = readMetaInt64 LastAckedKey 0L
        // (a) Agent 가 처리 확정(ack)한 seq **미만** 정리 — 마지막 acked 행 1개는 재부팅 앵커용으로 남긴다
        //     (§10.5.2(b) "마지막 행이 앵커"; 별도 meta 없이 이 1행이 OriginTsMs_old/wall_old 를 보존).
        //     acked 행은 다음 ack 때 새 마지막 acked 로 대체되므로 잔류는 항상 1행.
        use c1 = conn.CreateCommand()
        c1.CommandText <- "DELETE FROM event_log WHERE seq < @a"
        c1.Parameters.AddWithValue("@a", acked) |> ignore
        c1.ExecuteNonQuery() |> ignore
        // (b) 보관시간(예: 1시간) 초과 — 미소비여도 유실 감수(§10.7.4).
        let cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - int64 retentionMs
        use c2 = conn.CreateCommand()
        c2.CommandText <- "DELETE FROM event_log WHERE wall_clock_ms < @c"
        c2.Parameters.AddWithValue("@c", cutoff) |> ignore
        let byTime = c2.ExecuteNonQuery()
        // (c) 최대 로그 수 초과 — 오래된(작은 seq) 것부터.
        use cnt = conn.CreateCommand()
        cnt.CommandText <- "SELECT COUNT(*) FROM event_log"
        let total = Convert.ToInt32(cnt.ExecuteScalar())
        let byCount =
            if total > maxRows then
                use c3 = conn.CreateCommand()
                c3.CommandText <-
                    "DELETE FROM event_log WHERE seq IN (SELECT seq FROM event_log ORDER BY seq ASC LIMIT @n)"
                c3.Parameters.AddWithValue("@n", total - maxRows) |> ignore
                c3.ExecuteNonQuery()
            else 0
        if byTime > 0 || byCount > 0 then
            log $"[buffer] retention drop: byTime={byTime} byCount={byCount} (미소비 유실 감수)"

    /// 변화 batch 를 append (한 tx). scan 사이클당 1회 호출. wall_clock 은 append 시각으로 각인.
    member _.Append(changes: PlcTagChange list) =
        if not (List.isEmpty changes) then
            lock gate (fun () ->
                let wallNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                use tx = conn.BeginTransaction()
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <-
                    "INSERT INTO event_log(origin_ts_ms, wall_clock_ms, addr, value, source) VALUES(@o,@w,@a,@v,@s)"
                let pO = cmd.Parameters.Add("@o", SqliteType.Integer)
                let pW = cmd.Parameters.Add("@w", SqliteType.Integer)
                let pA = cmd.Parameters.Add("@a", SqliteType.Text)
                let pV = cmd.Parameters.Add("@v", SqliteType.Text)
                let pS = cmd.Parameters.Add("@s", SqliteType.Text)
                for ch in changes do
                    pO.Value <- ch.OriginTsMs
                    pW.Value <- wallNow
                    pA.Value <- ch.HubAddress
                    pV.Value <- ch.Value
                    pS.Value <- ch.Source
                    cmd.ExecuteNonQuery() |> ignore
                tx.Commit())

    /// offset(seq) 이후 청크를 순서대로 반환(전송용). 재전송도 동일 쿼리(순서 보존).
    /// 반환: (seq, PlcTagChange, wall_clock_ms) — **OriginTsMs 는 재부팅 보정된 전송값**(raw+wall 로 복원),
    /// wall_clock_ms 는 스캔 직후 append 시각(UTC epoch ms) — 수신측(DSPilot)이 plcTagLog.dateTime 을
    /// 도착시각이 아닌 이 값으로 기록해 replay 시 원래 시각 복원(TagWrite.WallClockMs 로 전파).
    /// 저장값(event_log.origin_ts_ms)은 raw 그대로 유지 — 저장=raw / 전송=복원 구분(§10.5.2).
    member _.ReadSince(offset: int64, chunk: int) : (int64 * PlcTagChange * int64) list =
        lock gate (fun () ->
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "SELECT seq, origin_ts_ms, wall_clock_ms, addr, value, source FROM event_log WHERE seq > @o ORDER BY seq ASC LIMIT @n"
            cmd.Parameters.AddWithValue("@o", offset) |> ignore
            cmd.Parameters.AddWithValue("@n", chunk) |> ignore
            use rdr = cmd.ExecuteReader()
            [ while rdr.Read() do
                let seq = rdr.GetInt64 0
                let rawOrigin = rdr.GetInt64 1
                let wall = rdr.GetInt64 2
                let change =
                    { HubAddress = rdr.GetString 3
                      Value = rdr.GetString 4
                      Source = rdr.GetString 5
                      OriginTsMs = restoreOrigin seq rawOrigin wall }  // 전송값 = 복원
                yield (seq, change, wall) ])

    /// Agent 가 처리 확정한 마지막 seq. 이 값보다 큰 것만 재전송.
    member _.LastAckedSeq
        with get () = lock gate (fun () -> readMetaInt64 LastAckedKey 0L)

    /// ack 수신 → offset 전진 + retention. 역행(작은 seq) ack 는 무시.
    member _.SetAckedSeq(seq: int64) =
        lock gate (fun () ->
            let cur = readMetaInt64 LastAckedKey 0L
            if seq > cur then
                writeMetaInt64 LastAckedKey seq
                applyRetentionLocked ())

    /// 현재 로그 최대 seq (없으면 last_acked_seq).
    member _.MaxSeq
        with get () =
            lock gate (fun () ->
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "SELECT COALESCE(MAX(seq), 0) FROM event_log"
                Convert.ToInt64(cmd.ExecuteScalar()))

    /// 시간/개수 기반 retention 만 수행(ack 무관). scan 사이클 주기적으로 호출.
    member _.ApplyRetention() = lock gate (fun () -> applyRetentionLocked ())

    interface IDisposable with
        member _.Dispose() =
            try conn.Dispose() with _ -> ()
