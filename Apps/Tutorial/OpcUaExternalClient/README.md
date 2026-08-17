# 외부 OPC UA 클라이언트 연결·구독

DSPilot에서 발급한 PFX를 사용자 인증서로 사용해 Promaker.Agent의 OPC UA 서버에 접속하고, AID/XGT Variable을 구독하는 절차다.

이 연결은 OPC UA Binary 프로토콜을 사용한다. OPC UA endpoint `62541`에서 Browse, Read, Subscribe할 때 API Key는 사용하지 않는다.

## 실행

저장소 루트에서 독립 Web 튜토리얼을 실행한다.

```powershell
dotnet run --project Apps/Tutorial/OpcUaExternalClient/Ds2.Tutorial.OpcUaExternalClient.Web.csproj
```

브라우저에서 다음 주소를 연다.

```text
http://localhost:63141
```

이 프로젝트는 기존 `Apps/Tutorial/Ds2.Tutorial.csproj`의 단계 실행과 분리되어 있다.

실행 화면에서 다음 작업을 직접 수행할 수 있다.

- DSPilot 발급 PFX를 메모리로 읽어 User Identity Certificate로 사용
- Agent 서버 인증서를 Subject·thumbprint 확인 후 로컬 Trusted Store에 등록
- 이 Web 프로그램의 Application Certificate 생성 및 thumbprint 표시
- Agent 주소공간 Browse와 AID/XGT Variable 검색
- 선택한 Variable을 실제 OPC UA MonitoredItem으로 등록
- `Value`, `StatusCode`, `SourceTimestamp` 실시간 최신값과 notification 이력 표시

## 전체 흐름

```text
DSPilot에서 사용자 PFX 발급
  → 클라이언트에 endpoint·보안·PFX 설정
  → 클라이언트가 Agent 서버 인증서 신뢰
  → Agent가 클라이언트 Application Certificate를 최초 1회 거부
  → DSPilot에서 거부 인증서 승인
  → 재접속
  → AID/XGT Variable Browse·Read·Subscribe
```

## 인증서 두 종류

외부 OPC UA 클라이언트 연결에는 역할이 다른 인증서 두 종류가 사용된다.

| 인증서 | 증명 대상 | Agent 저장소 | 처리 방법 |
|---|---|---|---|
| User Identity Certificate | 접속 사용자 | `trustedUser/certs` | DSPilot이 PFX를 발급하면서 공개 인증서를 자동 등록 |
| Application Certificate | Softing 또는 자체 클라이언트 프로그램 | `trusted/certs` | 최초 접속 후 DSPilot의 승인 대기 목록에서 직접 승인 |

다운로드한 PFX에는 사용자 인증에 필요한 개인키가 들어 있다. DSPilot은 PFX 파일 자체나 개인키를 Agent에 보관하지 않고 공개 인증서만 등록한다.

## 1. DSPilot에서 사용자 PFX 발급

1. DSPilot을 연다.
2. `설정 → 고급 설정 → OPC UA 외부 클라이언트`로 이동한다.
3. `PFX 암호`를 입력한다.
4. `Softing용 PFX 발급·다운로드`를 누른다.
5. 다운로드한 `.pfx` 파일과 암호를 클라이언트가 접근할 수 있는 안전한 위치에 보관한다.

발급이 끝나면 PFX의 공개 인증서는 Agent의 `trustedUser/certs`에 이미 등록되어 있다. 별도로 서버에 복사할 필요가 없다.

## 2. 클라이언트 접속값 설정

| 항목 | 값 |
|---|---|
| Endpoint URL | `opc.tcp://<인스턴스 주소>:62541/Ds2/OpcUa/Server` |
| Security Mode | `SignAndEncrypt` |
| Security Policy | `Basic256Sha256` |
| Message Encoding | `Binary` |
| User Identity | `Certificate` |
| Identity Certificate | DSPilot에서 받은 PFX |
| Password | PFX 발급 시 입력한 암호 |

### Softing OPC UA Client 2

1. `Session Connect` 창을 연다.
2. `Endpoint Information`에 endpoint와 보안 설정을 입력한다.
3. `Authentication Settings`의 `User Identity`를 `Certificate`로 선택한다.
4. `Certificate`에서 DSPilot이 발급한 PFX를 선택한다.
5. `Password`에 PFX 발급 시 입력한 암호를 입력한다.
6. 연결을 시도한다.

## 3. 클라이언트에서 Agent 서버 인증서 신뢰

첫 연결에서는 클라이언트가 Agent 서버 인증서를 아직 신뢰하지 않기 때문에 `Certificate is not trusted` 창이 나타날 수 있다.

1. 인증서 Subject와 현재 접속하려는 Agent가 일치하는지 확인한다.
2. Softing에서 `Add Certificate to Trusted Store`를 선택한다.
3. 다시 연결한다.

이 단계는 클라이언트가 Agent 서버를 신뢰하게 만드는 과정이다.

## 4. DSPilot에서 Application Certificate 승인

Agent가 Softing 또는 자체 프로그램의 Application Certificate를 아직 신뢰하지 않으면 첫 연결을 거절하고 인증서를 `rejected/certs`에 기록한다.

1. DSPilot의 `OPC UA 외부 클라이언트` 화면으로 돌아간다.
2. `접속 승인 대기 인증서`에서 `새로고침`을 누른다.
3. 방금 접속한 프로그램의 Subject와 thumbprint를 확인한다.
4. 해당 인증서를 승인한다.
5. 클라이언트에서 다시 연결한다.

승인된 인증서는 `rejected/certs`에서 `trusted/certs`로 이동한다. 같은 Application Certificate를 계속 사용하면 프로그램을 재시작하거나 구독을 다시 만들어도 다시 승인하지 않는다.

## 5. AID/XGT Variable 구독

1. 클라이언트 세션 상태가 `Active`인지 확인한다.
2. 다음 경로를 Browse한다.

   ```text
   Objects
     → DS
       → Assets
         → <자산>
           → AID
             → XGT
   ```

3. 폴더가 아니라 값을 가진 Variable을 선택한다.
4. Subscription을 생성한다.
5. 선택한 Variable의 `Value` 속성을 MonitoredItem으로 추가한다.
6. PLC 값이 변할 때 다음 항목이 함께 갱신되는지 확인한다.

   - `Value`
   - `StatusCode`
   - `SourceTimestamp`

Softing에서는 `Configuration Browse`에서 XGT Variable을 찾은 뒤 `Data Access` 화면의 subscription에 추가한다.

구독을 추가하거나 삭제하는 작업에는 API Key나 추가 인증서 승인이 필요하지 않다.

## 자체 프로그램에서 연결

OPC UA SDK에 다음 네 가지를 설정한다.

```text
endpoint               = opc.tcp://<host>:62541/Ds2/OpcUa/Server
security               = SignAndEncrypt + Basic256Sha256
applicationCertificate = 자체 프로그램의 OPC UA Application Certificate
userIdentity           = DSPilot에서 받은 PFX + 암호
```

세션 연결 후에는 일반 OPC UA subscription과 MonitoredItem을 사용한다.

```text
session      = connect(endpoint, security, applicationCertificate, userIdentity)
subscription = session.createSubscription(publishingInterval)
subscription.monitor(variableNodeId, Value)
```

SDK가 Application Certificate와 User Identity Certificate를 분리한다면 각각 따로 지정한다. 새 프로그램의 Application Certificate는 최초 연결 후 DSPilot에서 한 번 승인해야 한다.

## 오류별 확인

| 메시지 | 뜻 | 처리 |
|---|---|---|
| `BadNotConnected` | TCP 연결 또는 endpoint 도달 실패 | 주소, 인스턴스 인바운드 TCP 62541, Agent 실행 상태 확인 |
| `BadUserAccessDenied` / identity type | Anonymous 등 지원하지 않는 사용자 방식 선택 | User Identity를 `Certificate`로 바꾸고 DSPilot PFX 지정 |
| `Certificate is not trusted` | 클라이언트가 Agent 서버 인증서를 신뢰하지 않음 | 접속 대상을 확인하고 클라이언트 Trusted Store에 서버 인증서 추가 |
| `BadCertificateUntrusted` | Agent가 클라이언트 Application Certificate를 신뢰하지 않음 | DSPilot 승인 대기 목록에서 해당 인증서 승인 후 재접속 |
| `BadSecurityChecksFailed` | 인증서 신뢰 또는 보안 설정 협상 실패 | DSPilot 승인 상태와 `SignAndEncrypt / Basic256Sha256` 설정 확인 |
| 세션은 Active지만 값이 변하지 않음 | 폴더 선택 또는 Variable 품질·PLC 입력 확인 필요 | XGT Variable의 `Value`, `StatusCode`, `SourceTimestamp` 확인 |

## 인증서를 다시 처리하는 경우

다음 경우에만 인증서 처리가 다시 필요하다.

- DSPilot에서 사용자 PFX를 새로 발급한 경우
- 클라이언트 Application Certificate가 새로 생성되거나 교체된 경우

구독 추가·삭제, 클라이언트 프로그램 재시작, 세션 재접속만으로는 인증서를 다시 처리하지 않는다.
