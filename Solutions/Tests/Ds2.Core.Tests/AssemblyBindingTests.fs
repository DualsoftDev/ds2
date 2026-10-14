module Ds2.Core.Tests.AssemblyBindingTests

open System
open System.Reflection
open Xunit
open Ds2.Core.Store

/// AssemblyVersion 은 바인딩 식별자다. 소비 저장소의 미리 빌드된 외부 DLL(Promaker 의 ExternalDlls/AAStoPLC.dll)이
/// "Ds2.Core, Version=1.0.0.0" 을 참조하므로, 로드된 Ds2.Core 의 AssemblyVersion 이 그보다 낮으면 .NET 이
/// FileNotFoundException 을 던져 앱이 시작 즉시 죽는다 — 0.1.32 배포본 사고(BuildVersion.txt 의 0.1.x 를
/// AssemblyVersion 에 주입). 제품 버전은 Version/FileVersion 으로만 흐르고 AssemblyVersion 은 2.0.0.0 에 고정한다.
[<Fact>]
let ``Ds2.Core AssemblyVersion is pinned at 2.0.0.0 and the product version does not leak into it`` () =
    let asm = typeof<DsStore>.Assembly
    Assert.Equal(Version(2, 0, 0, 0), asm.GetName().Version)

    // FileVersion 은 BuildVersion.txt 를 따른다 — 고정값과 같다면 props 의 주입 배선이 뒤바뀐 것이다.
    let fileVersion = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()
    Assert.NotNull fileVersion
    Assert.NotEqual(Version(2, 0, 0, 0), Version.Parse fileVersion.Version)
