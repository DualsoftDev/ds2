module Ds2.Aasx.Tests.DiagnosticTests

open System
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Core.StandardSubmodels
open Ds2.Aasx
open Ds2.Aasx.Tests.PilotAssetFixtures
open Xunit
open Xunit.Abstractions

type DiagnosticTests(output: ITestOutputHelper) =

    let dumpElem (indent: int) (e: ISubmodelElement) : string =
        let prefix = System.String(' ', indent * 2)
        let idShort = if isNull e.IdShort then "<null>" else e.IdShort
        let kind = e.GetType().Name
        sprintf "%s%s [%s]" prefix idShort kind

    let rec dumpTree (indent: int) (e: ISubmodelElement) : string list =
        [
            yield dumpElem indent e
            match e with
            | :? SubmodelElementCollection as smc when not (isNull smc.Value) ->
                for child in smc.Value do
                    yield! dumpTree (indent + 1) child
            | :? Property as p ->
                yield sprintf "%s  = %s (%A)" (System.String(' ', indent * 2)) p.Value p.ValueType
            | _ -> ()
        ]

    [<Fact>]
    let ``dump CNC01 AID submodel structure`` () =
        let aid = cnc01Aid()
        let sm = AasxExportStandardSubmodels.aidToSubmodel aid "cnc01"
        output.WriteLine(sprintf "SM IdShort: %s" sm.IdShort)
        output.WriteLine(sprintf "SM SubmodelElements count: %d" sm.SubmodelElements.Count)
        for e in sm.SubmodelElements do
            for line in dumpTree 0 e do
                output.WriteLine(line)
        Assert.True(sm.SubmodelElements.Count > 0)
