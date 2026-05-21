namespace Ds2.LightHouse.Tests

open System.Text

/// 테스트 프로세스 시작 시 1회 — CP949 (`code page 949`) decode 가능하도록 CodePagesEncodingProvider 등록.
///
/// Promaker 의 `App.OnStartup` 에서 register 하는 것과 동일 효과 (test process 에는 entry 없음).
/// 본 module 의 `do` 블록이 namespace open 시점에 평가되도록 `Compile Include` 순서 가장 앞.
module internal TestInit =
    let private ensureRegistered =
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
        true

    /// 다른 test 모듈이 namespace 진입 시점에 강제 평가하도록 노출.
    let registered = ensureRegistered
