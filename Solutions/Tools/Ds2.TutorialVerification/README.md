# DS2 학습 모델 100개와 검증 실행기

100편 글에 대응하는 네이티브 `.ds2.sdf`를 Core·Editor의 공식 API로 만들고 실제 Runtime에서 실행합니다. 새로운 패턴을 100개 정의한 것이 아니라 기존 패턴을 여러 글의 관점에 맞춰 재사용합니다.

- 정상 모델: [verified/models](verified/models)
- 입력과 확인 규칙: [verified/scenarios](verified/scenarios)
- 첫 실행 기록: [verified/evidence](verified/evidence)
- 저장 파일의 동일 재실행 기록: [replayed/evidence](replayed/evidence)
- 일부러 조건 하나를 바꾼 반례: [negative](negative)
- 메모리 기반 감지·모드 비교: [probes](probes)
- [검증 결과](모델_검증결과.md) · [스펙 보완사항](DS2_스펙_보완사항.md)

홈페이지 원고는 `D:\AI\DsSpec\patterns\website`에 있습니다. 중복 설명을 8편으로 통합하고, 원본 100개 중 21개 대표 모델을 실제 SDF 기반의 전용 뷰와 함께 연결했습니다. 이 실행기와 원본 100개 모델·입력·검증 기록은 그대로 유지합니다. 홈페이지의 글 번호 01~08과 SDF의 원래 검증 번호는 서로 다릅니다.

## 실행

.NET 9 SDK와 같은 DS2 소스가 필요합니다. PowerShell에서 이 폴더로 이동한 뒤 실행하세요.

```powershell
./verify.ps1
./verify.ps1 -Ids 22,35,52
./verify.ps1 -Mode Negative
./verify.ps1 -Mode Probes
```

기본 실행은 **저장된 파일을 재읽어** 같은 GUID와 입력으로 실행합니다. `-Mode Generate`는 새 GUID의 모델과 시나리오를 새 출력 폴더에 생성합니다. 생성 회차가 다른 모델과 시나리오를 섞지 마세요. `-Ds2SolutionsRoot`로 DS2 소스 위치를 지정할 수 있습니다. 결과는 실행별 `runs` 폴더에 남고 원본 `verified`는 바꾸지 않습니다.

실행기 종료 코드 0은 Simulation 계약이 맞았다는 의미입니다. 정적 검사 결과는 `result.json`의 `StaticValidationPassed`에서 별도로 확인합니다. Negative는 반례가 모두 실패로 검출되면 종료 코드 0입니다. Probes는 관측 기록을 만드는 진단이며 합격 판정 명령이 아닙니다.

## 검증 범위

100개 Simulation 및 저장 파일 재실행 통과. 정적 검사는 9개 통과, 91개는 논리 Call의 입력 주소 규칙 V2에 미통과합니다. 가짜 입력으로 숨기지 않았습니다. 반례 5개는 정상 모델에서 조건만 한 군데 바꿔 모두 오류를 검출했습니다.

제품 공급·판정·허가 신호는 시험 실행기가 담당합니다. 모델에 따라 조립품 새 번호 발행, 회전 정지 횟수도 외부 책임입니다. 시나리오의 Notes와 SupplyRequirements를 읽어 주세요. 모델 파일만 열면 외부 입력까지 자동 공급되는 것은 아닙니다.

기계 시간은 Call 없는 Work에만 직접 지정했습니다. Work·Call 상태를 강제로 완료시키지 않았고, 실제 PLC나 네트워크를 연결하지 않았습니다. Control 비교도 메모리 입력만 사용하는 200 ms 진단입니다.

## 소스 구성

- `Models.fs`: 공통 편집 API 및 기본 모델.
- `Advanced.fs`: 회전·재작업·합류·설정·가변 횟수·조립 등 결합 예.
- `Verify.fs`: 입력 공급, 유한 시간 실행, 사건 기록, 조건 검증, 저장 파일 재읽기.
- `Probes.fs`: 감지 옵션·운전 모드 차이의 최소 비교.
- `Program.fs`: 모델 생성·재실행·단일 변경 반례, 겹치지 않는 화면 배치.
- `catalog.json`: 100편과 모델의 대응.
- `build_provenance.json`: 사용한 Core·Editor·Runtime 소스의 SHA-256. 기존 본체 소스는 수정하지 않았습니다.

`audit.json`은 네이티브 파일 전체 재읽기, 재실행 기록 일치, 참조, 좌표 겹침, 반례의 변경 한 곳 등 별도 감사를 기록합니다. `verified/evidence`의 초기 Finish 이벤트는 새 실행 횟수로 세지 않으며, 생산량은 정한 Sink와 제품 번호로 집계합니다.
