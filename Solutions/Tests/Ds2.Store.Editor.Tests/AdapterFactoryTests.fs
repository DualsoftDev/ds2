module Ds2.Store.Editor.Tests.AdapterFactoryTests

open System
open Ds2.Backend.Plc
open Xunit

/// 벤더·매체 조합 → 어댑터. USB 는 LS 만 지원하고, 다른 벤더의 USB 는 조용히 이더넷으로 떨어지지 않고
/// 게이트웨이 생성 시점에 InvalidOperationException 으로 드러나야 한다(AID 경로로는 만들어지지 않는 조합).
/// LS USB 어댑터는 접속 전에는 dsev2 커넥터를 만들지 않으므로 libusb 없이도 생성된다.
[<Fact>]
let ``adapter factory routes LS USB to the USB adapter and rejects USB for other vendors`` () =
    let lsUsb =
        { PlcConnectionConfig.defaultLs "LS-USB" "" with
            Port = 0
            Transport = PlcTransport.Usb
            UsbDeviceSelector = "SN1" }
    let adapter = Adapter.create lsUsb
    Assert.Equal("LS-USB", adapter.Name)
    Assert.False adapter.IsConnected

    let mxUsb = { PlcConnectionConfig.defaultMx "MX" "" with Transport = PlcTransport.Usb }
    Assert.Throws<InvalidOperationException>(Action(fun () -> Adapter.create mxUsb |> ignore)) |> ignore

    let sxUsb = { PlcConnectionConfig.defaultSx "SX" "" with Transport = PlcTransport.Usb }
    Assert.Throws<InvalidOperationException>(Action(fun () -> Adapter.create sxUsb |> ignore)) |> ignore

/// 접속 표기는 게이트웨이 로그·연결 상태 계약·Promaker·DSPilot 이 공유하는 한 함수에서 나온다 —
/// 이더넷은 host:port, USB 는 USB 또는 USB(selector). 각 화면이 `{ip}:{port}` 를 직접 조립하면 USB 가 `:0` 으로 보인다.
[<Fact>]
let ``endpoint label formats Ethernet as host:port and USB as USB(selector)`` () =
    Assert.Equal("192.168.0.10:2004", PlcConnectionConfig.endpointLabel (PlcConnectionConfig.defaultLs "A" "192.168.0.10"))

    let usb = { PlcConnectionConfig.defaultLs "B" "" with Port = 0; Transport = PlcTransport.Usb }
    Assert.Equal("USB", PlcConnectionConfig.endpointLabel usb)
    Assert.Equal("USB(3:4)", PlcConnectionConfig.endpointLabel { usb with UsbDeviceSelector = "3:4" })
