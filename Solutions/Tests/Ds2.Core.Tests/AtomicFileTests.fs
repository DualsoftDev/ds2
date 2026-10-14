module Ds2.Core.Tests.AtomicFileTests

open System
open System.Diagnostics
open System.IO
open System.Text
open Xunit
open Ds2.Core

let private withTempDir (body: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"atomic-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore
    try body dir
    finally
        for f in Directory.GetFiles dir do File.SetAttributes(f, FileAttributes.Normal)
        Directory.Delete(dir, true)

/// 쓰는 도중 실패(디스크 가득 참, 네트워크 끊김, 직렬화 예외)해도 원본은 그대로여야 한다. 예전 .sdf/.json 저장은
/// File.Create 로 원본을 먼저 잘라 냈기 때문에, 실패하면 0바이트나 반쯤 쓴 파일이 남았다.
[<Fact>]
let ``a write that fails midway keeps the original file and leaves no temp file behind`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "model.sdf")
        File.WriteAllText(path, "original")

        let ex = Assert.Throws<IOException>(fun () ->
            AtomicFile.write path (fun stream ->
                stream.Write(Encoding.UTF8.GetBytes "half-writt", 0, 10)
                raise (IOException "디스크 공간이 부족합니다.")))

        Assert.Contains("디스크", ex.Message)
        Assert.Equal("original", File.ReadAllText path)
        Assert.Equal<string>([| path |], Directory.GetFiles dir))

/// 읽기 전용 대상은 기다려도 풀리지 않는다 — 바로 실패해야 한다. 예전 AASX 저장은 이 경우 UI 스레드에서 22.5초를
/// 재시도한 뒤에야 실패했다("응답 없음").
[<Fact>]
let ``replacing a read-only target fails immediately and keeps the original`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "locked.aasx")
        File.WriteAllText(path, "original")
        File.SetAttributes(path, FileAttributes.ReadOnly)

        let sw = Stopwatch.StartNew()
        Assert.Throws<UnauthorizedAccessException>(fun () ->
            AtomicFile.write path (fun stream -> stream.WriteByte 1uy)) |> ignore
        sw.Stop()

        Assert.True(sw.ElapsedMilliseconds < 1000L, $"{sw.ElapsedMilliseconds}ms 걸렸다")
        Assert.Equal("original", File.ReadAllText path)
        Assert.Equal<string>([| path |], Directory.GetFiles dir))
