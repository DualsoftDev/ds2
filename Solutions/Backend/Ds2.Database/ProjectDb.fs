namespace Ds2.Database

open System
open System.Data
open Dapper
open Ds2.Core

/// <summary>
/// Dapper용 DTO (Data Transfer Object)
/// CLIMutable 특성을 사용하여 Dapper가 레코드를 역직렬화할 수 있게 함
/// </summary>
[<CLIMutable>]
type ProjectDto = {
    Id: string
    Name: string
    Description: string
    CreatedAt: string
    ModifiedAt: string
}

[<CLIMutable>]
type SystemDto = {
    Id: string
    ProjectId: string
    Name: string
    Description: string
}

/// <summary>
/// Dapper를 사용한 Project 데이터베이스 접근
/// Repository 패턴 없이 직접 SQL 실행
/// </summary>
module ProjectDb =

    /// DTO를 Domain 모델로 변환
    let private toDomain (dto: ProjectDto) : Project =
        let project = Project(dto.Name)
        project.Id <- Guid.Parse(dto.Id)  // DB에서 로드한 ID 복원
        project.Properties.Description <- if isNull dto.Description then None else Some dto.Description
        project

    /// 프로젝트 저장
    let save (conn: IDbConnection) (project: Project) : unit =
        let sql = """
            INSERT INTO Projects (Id, Name, Description, CreatedAt, ModifiedAt)
            VALUES (@Id, @Name, @Description, datetime('now'), datetime('now'))
            ON CONFLICT(Id) DO UPDATE SET
                Name = @Name,
                Description = @Description,
                ModifiedAt = datetime('now')
        """

        let param = {|
            Id = project.Id.ToString()
            Name = project.Name
            Description = project.Properties.Description |> Option.toObj
        |}

        conn.Execute(sql, param) |> ignore

    /// 프로젝트 조회
    let load (conn: IDbConnection) (id: string) : Project option =
        let sql = "SELECT * FROM Projects WHERE Id = @Id"

        let dto = conn.QuerySingleOrDefault<ProjectDto>(sql, {| Id = id |})

        if isNull (box dto) then None
        else Some (toDomain dto)

    /// 모든 프로젝트 조회
    let loadAll (conn: IDbConnection) : Project list =
        let sql = "SELECT * FROM Projects ORDER BY ModifiedAt DESC"

        conn.Query<ProjectDto>(sql)
        |> Seq.map toDomain
        |> Seq.toList

    /// 프로젝트 삭제
    let delete (conn: IDbConnection) (id: string) : unit =
        let sql = "DELETE FROM Projects WHERE Id = @Id"

        conn.Execute(sql, {| Id = id |}) |> ignore

    /// 프로젝트 존재 여부 확인
    let exists (conn: IDbConnection) (id: string) : bool =
        let sql = "SELECT COUNT(*) FROM Projects WHERE Id = @Id"

        let count = conn.ExecuteScalar<int>(sql, {| Id = id |})
        count > 0

/// <summary>
/// DsSystem 데이터베이스 접근
/// </summary>
module SystemDb =

    /// DTO를 Domain 모델로 변환
    let private toDomain (dto: SystemDto) : DsSystem =
        let system = DsSystem(dto.Name)
        system.Id <- Guid.Parse(dto.Id)  // DB에서 로드한 ID 복원
        system

    /// 시스템 저장
    let save (conn: IDbConnection) (projectId: string) (system: DsSystem) : unit =
        let sql = """
            INSERT INTO Systems (Id, ProjectId, Name, Description)
            VALUES (@Id, @ProjectId, @Name, @Description)
            ON CONFLICT(Id) DO UPDATE SET
                Name = @Name,
                Description = @Description
        """

        let param = {|
            Id = system.Id.ToString()
            ProjectId = projectId
            Name = system.Name
            Description = null  // SystemProperties no longer has Description in Draft v0.5
        |}

        conn.Execute(sql, param) |> ignore

    /// 프로젝트의 모든 시스템 조회
    let loadByProjectId (conn: IDbConnection) (projectId: string) : DsSystem list =
        let sql = "SELECT * FROM Systems WHERE ProjectId = @ProjectId"

        conn.Query<SystemDto>(sql, {| ProjectId = projectId |})
        |> Seq.map toDomain
        |> Seq.toList

/// <summary>
/// 데이터베이스 스키마 초기화
/// </summary>
module Schema =

    /// SQLite용 스키마 생성
    let createSchema (conn: IDbConnection) : unit =
        let createProjectsTable = """
            CREATE TABLE IF NOT EXISTS Projects (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Description TEXT,
                CreatedAt TEXT NOT NULL,
                ModifiedAt TEXT NOT NULL
            )
        """

        let createSystemsTable = """
            CREATE TABLE IF NOT EXISTS Systems (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            )
        """

        let createFlowsTable = """
            CREATE TABLE IF NOT EXISTS Flows (
                Id TEXT PRIMARY KEY,
                SystemId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT,
                FOREIGN KEY (SystemId) REFERENCES Systems(Id) ON DELETE CASCADE
            )
        """

        let createWorksTable = """
            CREATE TABLE IF NOT EXISTS Works (
                Id TEXT PRIMARY KEY,
                FlowId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT,
                FOREIGN KEY (FlowId) REFERENCES Flows(Id) ON DELETE CASCADE
            )
        """

        conn.Execute(createProjectsTable) |> ignore
        conn.Execute(createSystemsTable) |> ignore
        conn.Execute(createFlowsTable) |> ignore
        conn.Execute(createWorksTable) |> ignore
