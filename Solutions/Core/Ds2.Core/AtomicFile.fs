namespace Ds2.Core

open System
open System.IO
open System.Threading

/// 파일을 원자적으로 교체한다. 같은 폴더의 임시 파일에 끝까지 쓴 뒤 이름 바꾸기(MoveFileEx REPLACE_EXISTING)로
/// 바꿔치기하므로, 쓰는 도중 실패해도(디스크 가득 참, 네트워크 끊김, 직렬화 예외) 원본은 그대로 남는다.
/// 예전 저장은 원본을 먼저 잘라 낸 뒤 쓰거나(.sdf/.json), 완성된 임시 파일을 원본 위에 복사해(.aasx) 실패하면 원본이 깨졌다.
[<RequireQualifiedAccess>]
module AtomicFile =

    let private maxReplaceAttempts = 5

    /// 임시 파일을 대상 자리로 옮긴다. 대상을 다른 프로세스가 잠깐 잡고 있으면(공유 위반 IOException) 짧게 재시도한다.
    /// 읽기 전용·권한 거부(UnauthorizedAccessException)는 기다려도 풀리지 않으므로 바로 실패한다 — 예전 AASX 저장은
    /// 이 경우에도 UI 스레드에서 22.5초를 기다렸다.
    let replace (tempPath: string) (path: string) : unit =
        let rec go attempt =
            try
                File.Move(tempPath, path, true)
            with :? IOException when attempt < maxReplaceAttempts ->
                Thread.Sleep(100 * attempt)
                go (attempt + 1)
        go 1

    /// path 를 writeTo 가 쓴 내용으로 교체한다. 실패하면 임시 파일을 지우고 예외를 그대로 올린다.
    let write (path: string) (writeTo: Stream -> unit) : unit =
        let fullPath = Path.GetFullPath path
        let dir = Path.GetDirectoryName fullPath
        let tempPath = Path.Combine(dir, $".{Path.GetFileName fullPath}.{Guid.NewGuid():N}.tmp")
        try
            do
                use stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                writeTo stream
                stream.Flush(true)
            replace tempPath fullPath
        with _ ->
            try File.Delete tempPath with _ -> ()
            reraise ()
