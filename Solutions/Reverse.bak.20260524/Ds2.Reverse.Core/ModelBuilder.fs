/// DsStore (Ds2.Core 타입) emit — Promaker 호환 모델 생성.
namespace Ds2.Reverse.Core

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store

module ModelBuilder =

    /// 변수명을 Promaker call 이름 형식 (DevicesAlias.ApiName) 으로 sanitize.
    /// '.' 가 alias 에 있으면 setter 가 잘못 파싱하므로 '_' 로 치환.
    /// 입력에 '.' 가 여러 개 (예: "F1.PRE.START") 인 경우 첫 '.' 는 alias 구분자로 보존,
    /// 그 다음부터는 '_' 로 치환.
    let sanitizeCallName (rawVar: string) : string * string =
        let safe = rawVar.Replace(".", "_")
        let alias, api =
            match safe.IndexOf '_' with
            | -1  -> safe, "VAL"
            | idx -> safe.Substring(0, idx), safe.Substring(idx + 1)
        let api2 = api.Replace(".", "_")
        let alias2 = alias.Replace(".", "_")
        let alias3 = if String.IsNullOrEmpty alias2 then "VAR" else alias2
        let api3 = if String.IsNullOrEmpty api2 then "VAL" else api2
        alias3, api3

    /// raw 이름 ("F1.PRE.START" 또는 "F1_PRE_START") 에서 Promaker call name 생성.
    /// Event name normalize 와 동일 결과 — timesByName 매칭에 필수.
    let normalizeFullName (rawVar: string) : string =
        let alias, api = sanitizeCallName rawVar
        $"{alias}.{api}"

    /// 최소 골격 빈 DsStore — Project + Active System.
    let emptyStore (projectName: string) (activeSysName: string) : DsStore * Guid * Guid =
        let store = DsStore()
        let proj = Project(projectName)
        let sys = DsSystem(activeSysName)
        store.Projects.[proj.Id] <- proj
        store.Systems.[sys.Id] <- sys
        proj.ActiveSystemIds.Add sys.Id
        store, proj.Id, sys.Id

    /// Flow 추가.
    let addFlow (store: DsStore) (parentSysId: Guid) (name: string) : Guid =
        let f = Flow(name, parentSysId)
        store.Flows.[f.Id] <- f
        f.Id

    /// Work 추가.
    let addWork (store: DsStore) (parentFlowId: Guid) (flowPrefix: string) (localName: string) : Guid =
        let w = Work(flowPrefix, localName, parentFlowId)
        store.Works.[w.Id] <- w
        w.Id

    /// Call + ApiDef + ApiCall 1쌍 추가.
    let addCallWithApi (store: DsStore) (parentWorkId: Guid) (parentFlowId: Guid)
                      (rawVar: string) (_address: string) : Guid =
        let alias, api = sanitizeCallName rawVar
        // ApiDef
        let apiDef = ApiDef(api, parentFlowId)
        apiDef.TxGuid <- Some parentWorkId
        apiDef.RxGuid <- Some parentWorkId
        store.ApiDefs.[apiDef.Id] <- apiDef
        // Call
        let call = Call(alias, api, parentWorkId)
        // ApiCall
        let apiCall = ApiCall(call.Name)
        apiCall.ApiDefId <- Some apiDef.Id
        apiCall.OriginFlowId <- Some parentFlowId
        call.ApiCalls.Add apiCall
        store.Calls.[call.Id] <- call
        call.Id

    /// ArrowBetweenCalls 추가.
    let addArrowCall (store: DsStore) (parentWorkId: Guid)
                    (srcCallId: Guid) (tgtCallId: Guid) (arrowType: ArrowType) : Guid =
        let a = ArrowBetweenCalls(parentWorkId, srcCallId, tgtCallId, arrowType)
        store.ArrowCalls.[a.Id] <- a
        a.Id

    /// ArrowBetweenWorks 추가.
    let addArrowWork (store: DsStore) (parentSysId: Guid)
                    (srcWorkId: Guid) (tgtWorkId: Guid) (arrowType: ArrowType) : Guid =
        let a = ArrowBetweenWorks(parentSysId, srcWorkId, tgtWorkId, arrowType)
        store.ArrowWorks.[a.Id] <- a
        a.Id

    /// 통계 추출.
    let summarize (store: DsStore) =
        {| Projects = store.Projects.Count
           Systems = store.Systems.Count
           Flows = store.Flows.Count
           Works = store.Works.Count
           Calls = store.Calls.Count
           ApiDefs = store.ApiDefs.Count
           ArrowCalls = store.ArrowCalls.Count
           ArrowWorks = store.ArrowWorks.Count |}
