namespace DSPilot.Engine

open System

/// Repository 쿼리 헬퍼 모듈
module QueryHelpers =

    /// UTC DateTime을 SQLite 문자열 형식으로 변환
    let toSqliteUtcString (dt: DateTime) : string =
        let utc =
            if dt.Kind = DateTimeKind.Local then
                dt.ToUniversalTime()
            else
                dt
        utc.ToString("yyyy-MM-dd HH:mm:ss.fffffff") + "Z"

    /// SQLite UTC 문자열을 DateTime (Local)으로 변환
    let fromSqliteUtcString (str: string) : DateTime option =
        try
            if String.IsNullOrEmpty(str) then None
            else
                let trimmed = str.TrimEnd('Z')
                let utc = DateTime.Parse(trimmed, null, System.Globalization.DateTimeStyles.AssumeUniversal)
                Some(utc.ToLocalTime())
        with
        | _ -> None

    /// 날짜 범위 쿼리용 WHERE 절 생성
    let buildDateRangeWhere (columnName: string) (startExclusive: DateTime option) (endInclusive: DateTime option) : string * obj list =
        match startExclusive, endInclusive with
        | None, None ->
            ("", [])
        | Some start, None ->
            let startStr = toSqliteUtcString start
            (sprintf "%s > @Start" columnName, [box startStr])
        | None, Some endTime ->
            let endStr = toSqliteUtcString endTime
            (sprintf "%s <= @End" columnName, [box endStr])
        | Some start, Some endTime ->
            let startStr = toSqliteUtcString start
            let endStr = toSqliteUtcString endTime
            (sprintf "%s > @Start AND %s <= @End" columnName columnName, [box startStr; box endStr])

    /// WHERE 조건 결합
    let combineWhereConditions (conditions: string list) : string =
        let nonEmpty = conditions |> List.filter (String.IsNullOrWhiteSpace >> not)
        if nonEmpty.IsEmpty then
            ""
        else
            "WHERE " + String.concat " AND " nonEmpty

    /// LIKE 패턴 생성
    let toLikePattern (value: string) : string =
        "%" + value + "%"

    /// Optional 문자열 조건 추가
    let addStringCondition (columnName: string) (value: string option) : string option =
        match value with
        | None | Some "" -> None
        | Some v -> Some(sprintf "%s = @%s" columnName columnName)

    /// 리스트를 IN 절로 변환
    let buildInClause (columnName: string) (values: string list) : string option =
        if values.IsEmpty then None
        else
            let placeholders =
                values
                |> List.mapi (fun i _ -> sprintf "@Item%d" i)
                |> String.concat ", "
            Some(sprintf "%s IN (%s)" columnName placeholders)

    /// COUNT 쿼리 생성
    let buildCountQuery (tableName: string) (whereClause: string) : string =
        if String.IsNullOrWhiteSpace(whereClause) then
            sprintf "SELECT COUNT(*) FROM %s" tableName
        else
            sprintf "SELECT COUNT(*) FROM %s %s" tableName whereClause

    /// LIMIT/OFFSET 절 생성
    let buildLimitOffset (limit: int option) (offset: int option) : string =
        match limit, offset with
        | None, None -> ""
        | Some l, None -> sprintf "LIMIT %d" l
        | None, Some o -> sprintf "OFFSET %d" o
        | Some l, Some o -> sprintf "LIMIT %d OFFSET %d" l o
