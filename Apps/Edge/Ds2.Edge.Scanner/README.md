# Pi5 Scan PoC

Ev2 DLL이 **ARM64 Linux .NET 9**에서 로드되고 실 PLC를 scan(read)하는지, 그리고 프로세스 메모리(RSS)가 1GB Pi5에 맞는지 **실기 검증**하는 최소 콘솔.

- 설계: [`../pi5-edge-scan-design.md`](../pi5-edge-scan-design.md) (P0)
- `Ds2.Backend.Plc`의 `PlcGateway`를 그대로 재사용 → 이게 로드/통신되면 scan 이관의 물리 관문 통과.
- **정적 분석은 이미 통과**: Ev2 = MSIL(AnyCPU) + .NETCoreApp8/9·netstandard2.0 + win32 P/Invoke 0 + System.Net.Sockets TCP. 이 PoC는 그 **실기 확정**용.

## 무엇을 확인하나
1. **로드** — `new PlcGateway(config)` 가 예외 없이 되면 Ev2 DLL이 이 플랫폼(ARM64 Linux)에서 로드된 것.
2. **통신** — `ConnectAllAsync` + `ScanOnceAsync` 로 실 PLC read 성공 여부.
3. **메모리** — 시작/종료 시 `/proc/self/status` VmRSS 출력. 실제 상주 RSS를 눈으로 확인.

## 빌드 & 배포

### 로컬(개발 PC)에서 Pi5용 바이너리 publish
```bash
# repo 루트에서
dotnet publish samples/pi5-scan-poc/pi5-scan-poc.fsproj \
  -c Release -r linux-arm64 --self-contained true \
  -p:PublishReadyToRun=true
# 산출물: samples/pi5-scan-poc/bin/Release/net9.0/linux-arm64/publish/
```
`publish/` 폴더를 Pi5로 복사(scp).

### Pi5에서 실행 (메모리 레시피 적용)
```bash
cd publish
cp plc-poc.example.json plc-poc.json   # 현장 PLC 정보로 수정
chmod +x pi5-scan-poc

# CloudWorks §즉효① 메모리 레시피 — 1GB Pi5 대비
DOTNET_gcServer=0 \
DOTNET_GCConserveMemory=5 \
DOTNET_GCRetainVM=0 \
DOTNET_GCHeapHardLimit=0x18000000 \   # 384MB 상한 (필요시 0x10000000=256MB)
  ./pi5-scan-poc plc-poc.json

# 실행 중 다른 셸에서 실측:
#   ps -o rss,vsz,cmd -C pi5-scan-poc
#   cat /proc/$(pgrep pi5-scan-poc)/status | grep VmRSS
```

## config (plc-poc.json)
```jsonc
{
  "connections": [
    {
      "name": "XGK-1",
      "vendor": "LsXgk",        // LsXgk | LsXgi | Mitsubishi
      "ip": "192.168.250.101",
      "port": 2004,             // LS XGT 기본 2004, MELSEC 5007
      "localEthernet": true,
      "timeoutMs": 3000,
      "scanMs": 100,
      "tags": [
        { "hub": "P00900", "plc": "P00900", "dtype": "Bool" }
        // dtype: Bool | Int16 | UInt16 | Int32 | UInt32 | Float32 | Float64
      ]
    }
  ]
}
```

## 판정
- **PlcGateway 생성 OK + connected=true + scan 변화 관측** → Ev2 ARM64 구동 확정, 설계 P1~ 진행 가능.
- **로드 예외**(TypeLoad/DllNotFound 등) → Ev2가 ARM64에서 못 도는 것 → Ev2 재빌드 또는 대체 경로 검토.
- **로드 OK인데 connect 실패** → 네트워크/방화벽/PLC 주소 문제(플랫폼 아님).
- **RSS 확인** → 이 PoC(gateway만)의 RSS + edge-web(~20MB) + OS(~200MB)로 1GB 여유 실측. GCHeapHardLimit로 상한 잡힘.

> 이 PoC는 검증 도구라 `.sln`에 넣지 않는다. `dotnet build/publish`로 직접 다룬다.
