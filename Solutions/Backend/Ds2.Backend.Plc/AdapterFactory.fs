namespace Ds2.Backend.Plc

/// 접속 설정 → 벤더·매체별 어댑터. 게이트웨이(PlcGateway)는 이 진입점만 쓴다.
[<RequireQualifiedAccess>]
module Adapter =
    let create (cfg: PlcConnectionConfig) : IPlcConnectorAdapter =
        match cfg.Vendor, cfg.Transport with
        | (PlcVendor.LsXgi | PlcVendor.LsXgk | PlcVendor.LsXgb), PlcTransport.Usb -> LsUsbAdapter.create cfg
        | (PlcVendor.LsXgi | PlcVendor.LsXgk | PlcVendor.LsXgb), _ -> LsAdapter.create cfg
        | PlcVendor.Mitsubishi, PlcTransport.Usb ->
            // dsev2 MX USB 는 Linux/libusb 만 제품 지원하고(Windows 는 PlatformNotSupportedException) 제품
            // 소비자 통합도 미완이다. AID InterfaceXGT 는 LS 전용이라 설정 경로로는 이 조합이 만들어지지
            // 않는다 — 조용히 이더넷으로 떨어뜨리지 않고 게이트웨이 생성에서 바로 드러낸다.
            invalidOp $"[{cfg.Name}] Mitsubishi USB 수집은 지원하지 않습니다 — Ethernet(TCP/UDP) 을 사용하세요."
        | PlcVendor.Mitsubishi, _ -> MxAdapter.create cfg
        | PlcVendor.MicrexSx, PlcTransport.Usb ->
            invalidOp $"[{cfg.Name}] MICREX-SX 는 USB 수집을 지원하지 않습니다 — 로더 포트(TCP) 를 사용하세요."
        | PlcVendor.MicrexSx, _ -> SxAdapter.create cfg
