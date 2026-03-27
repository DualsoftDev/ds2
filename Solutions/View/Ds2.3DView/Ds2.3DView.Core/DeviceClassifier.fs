namespace Ds2.ThreeDView

open System

// =============================================================================
// Device Classifier — 장비 이름/타입으로 DeviceType 분류
// =============================================================================

module DeviceClassifier =

    /// 대문자로 변환 후 키워드 매칭
    let classify (deviceName: string) (systemType: string option) : DeviceType =
        let upper =
            (deviceName |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant()

        let sysType =
            systemType
            |> Option.map (fun s -> s.ToUpperInvariant())
            |> Option.defaultValue ""

        // Robot keywords
        if upper.Contains("RB") || upper.Contains("ROBOT") || sysType.Contains("ROBOT") then
            DeviceType.Robot

        // Small device keywords
        elif upper.Contains("CLP")
             || upper.Contains("CT")
             || upper.Contains("SV")
             || upper.Contains("AIR")
             || upper.Contains("SLIDE")
             || upper.Contains("LOCK")
             || upper.Contains("CYLINDER")
             || upper.Contains("SENSOR")
             || sysType.Contains("SENSOR")
             || sysType.Contains("ACTUATOR") then
            DeviceType.Small

        // General (conveyor, controller, etc.)
        else
            DeviceType.General
