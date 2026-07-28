namespace Ds2.OpcUa.Server.Server

open System
open System.Collections.Generic
open Opc.Ua
open Opc.Ua.Server
open Ds2.OpcUa.Server.NodeIds

/// StandardServer 상속 · Ds2 전용 nodeset 을 loading.
type DsUaServer(
        allocator: INamespaceAllocator,
        managedNamespaceUris: string array,
        defaultSamplingIntervalMs: int) =
    inherit StandardServer()

    let mutable nodeManager : DsNodeManager option = None

    new(allocator: INamespaceAllocator, managedNamespaceUris: string array) =
        new DsUaServer(allocator, managedNamespaceUris, 1000)

    new(allocator: INamespaceAllocator) =
        new DsUaServer(allocator, [||], 1000)

    /// 외부에서 asset 을 추가할 수 있게 노출.
    member _.NodeManager =
        match nodeManager with
        | Some nm -> nm
        | None -> failwith "DsNodeManager 가 아직 초기화되지 않음 (Start 전)"

    override this.CreateMasterNodeManager(server, configuration) =
        let dsNm =
            new DsNodeManager(
                server,
                configuration,
                allocator,
                managedNamespaceUris,
                max 1 defaultSamplingIntervalMs)
        nodeManager <- Some dsNm
        let managers : INodeManager array = [| dsNm :> INodeManager |]
        new MasterNodeManager(server, configuration, null, managers)

    override _.LoadServerProperties() =
        let props = base.LoadServerProperties()
        props.ManufacturerName <- "DualSoft"
        props.ProductName <- "Ds2.OpcUa.Server"
        props.ProductUri <- "https://dualsoft.com/ds2/opcua"
        props.SoftwareVersion <- "0.1.0"
        props.BuildNumber <- "phase3-wire-up"
        props.BuildDate <- DateTime.UtcNow
        props
