module Ds2.Core.Tests.PlcVendorTests

open System
open Ds2.Core.StandardSubmodels
open Xunit

/// AID InterfaceXGT 로 표현 가능한 벤더는 LS 세 종류뿐이다 — Core 의 XgtCpuModel(Xgi|Xgk|Xgb) 이 닫힌 DU 이기
/// 때문이다. 이 판정이 저장 경로(XGT ↔ SX endpoint)와 속성 패널의 화면 진실을 갈라놓는다. 비-LS 를 XGT 쪽으로
/// 잘못 분류하면 저장한 SX 벤더가 옛 LS endpoint 값으로 되돌아간다(실제로 그렇게 됐다).
/// 권위 쪽(F#)이 정말 LS 만 받는지는 Ds2.Aasx.Tests 의 "AID XGT endpoint accepts only LS vendors" 가 지킨다.
[<Fact>]
let ``IsAidXgtVendor is exactly the LS family`` () =
    Assert.True(PlcVendorProfile.IsAidXgtVendor PlcVendorChoice.LsXgi)
    Assert.True(PlcVendorProfile.IsAidXgtVendor PlcVendorChoice.LsXgk)
    Assert.True(PlcVendorProfile.IsAidXgtVendor PlcVendorChoice.LsXgb)
    Assert.False(PlcVendorProfile.IsAidXgtVendor PlcVendorChoice.Mitsubishi)
    Assert.False(PlcVendorProfile.IsAidXgtVendor PlcVendorChoice.MicrexSx)

    // 벤더가 늘면 둘 중 하나로 분류해야 한다 — 이 개수 단정이 그 결정을 강제한다.
    Assert.Equal(5, Enum.GetValues<PlcVendorChoice>().Length)

/// 벤더를 바꿀 때 포트를 새 벤더 기본값으로 옮기는 판정 — 사용자가 손으로 넣은 포트를 덮어쓰면 안 되고,
/// 반대로 남겨 두면 SX 를 골라도 LS 의 2004 로 붙는다. 열거는 enum 파생이라 벤더가 늘어도 낡지 않는다.
[<Fact>]
let ``IsAnyVendorDefaultPort covers every vendor and nothing else`` () =
    for vendor in Enum.GetValues<PlcVendorChoice>() do
        let port = PlcVendorProfile.Defaults(vendor).Port
        Assert.True(PlcVendorProfile.IsAnyVendorDefaultPort port, $"{vendor} 의 기본 포트 {port} 가 기본값으로 인식되지 않는다")

    // 사용자가 직접 넣었을 값 — 덮어쓰면 안 된다.
    Assert.False(PlcVendorProfile.IsAnyVendorDefaultPort 502)
    Assert.False(PlcVendorProfile.IsAnyVendorDefaultPort 2005)
    Assert.False(PlcVendorProfile.IsAnyVendorDefaultPort 0)
    Assert.False(PlcVendorProfile.IsAnyVendorDefaultPort -1)

    // SX 기본 포트가 LS 와 다른 것이 이 판정의 존재 이유다. SX 는 로더 인터페이스 서버 509.
    Assert.Equal(509, PlcVendorProfile.Defaults(PlcVendorChoice.MicrexSx).Port)
    Assert.NotEqual(
        PlcVendorProfile.Defaults(PlcVendorChoice.LsXgi).Port,
        PlcVendorProfile.Defaults(PlcVendorChoice.MicrexSx).Port)

/// 벤더마다 고를 수 있는 매체가 다르다 — USB 는 LS 로더 포트만(dsev2 LsUsbConnector), UDP 는 Mitsubishi MC
/// 프로토콜만. 모르는 라벨(옛 파일의 isUdp 같은 것)은 TCP 로 읽는다.
[<Fact>]
let ``TransportsFor offers usb only for LS and udp only for Mitsubishi`` () =
    let tcp, udp, usb = PlcTransports.Tcp, PlcTransports.Udp, PlcTransports.Usb

    for vendor in [ PlcVendorChoice.LsXgi; PlcVendorChoice.LsXgk; PlcVendorChoice.LsXgb ] do
        Assert.Equal<string>([| tcp; usb |], PlcVendorProfile.TransportsFor vendor |> Seq.toArray)
    Assert.Equal<string>([| tcp; udp |], PlcVendorProfile.TransportsFor PlcVendorChoice.Mitsubishi |> Seq.toArray)
    Assert.Equal<string>([| tcp |], PlcVendorProfile.TransportsFor PlcVendorChoice.MicrexSx |> Seq.toArray)

    Assert.Equal(tcp, PlcTransports.normalize "isUdp")
    Assert.Equal(tcp, PlcTransports.normalize null)
    Assert.Equal(usb, PlcTransports.normalize "USB")

/// USB 프로파일은 host 가 없다 — 표기가 USB(selector) 로 나와야 상태바·로그에 ":0" 이 찍히지 않는다.
[<Fact>]
let ``usb profile renders its endpoint label without host and port`` () =
    let p = PlcVendorProfile.Defaults PlcVendorChoice.LsXgb
    p.Transport <- PlcTransports.Usb
    p.UsbDeviceSelector <- "SN12345"
    Assert.True p.IsUsb
    Assert.Equal("USB(SN12345)", p.EndpointLabel)
    Assert.Equal("192.168.0.10:2004", (PlcVendorProfile.Defaults PlcVendorChoice.LsXgi).EndpointLabel)
