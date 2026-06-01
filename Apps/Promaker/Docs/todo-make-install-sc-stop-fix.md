# TODO — `make install` 의 service stop→파일잠금 실패 (queue, 2026-06-01)

## 증상
`cd Apps/Promaker && make install` 의 Step 3/4 (`setup-cert-and-service.ps1`) 에서:
```
기존 service 실행 중 — 파일 복사를 위해 정지: Ds2.LightHouseService
경고: '...' 서비스가 중지될 때까지 기다리는 중...   (14회 반복)
빌드 산출물 복사: ... -> C:\Program Files\Promaker\LightHouseService
Copy-Item : 'C:\Program Files\Promaker\LightHouseService\clrjit.dll' 파일은 다른 프로세스에서 사용 중...
```

## 원인 추정
`setup-cert-and-service.ps1` 의 service stop 로직이 **프로세스 완전 종료를 보장하지 않음**:
- `sc stop` (또는 `Stop-Service`) 가 "중지 대기 중" 을 반복하다 timeout/포기 후 복사로 진행.
- service 프로세스(`Ds2.LightHouseService.exe`)가 아직 살아 있어 self-contained 런타임 DLL(`clrjit.dll` 등)을 잠금 → `Copy-Item` 실패.

## 수정 방향
`setup-cert-and-service.ps1` 의 stop 단계에:
1. `Stop-Service` 후 **프로세스 종료 polling** (예: `Get-Process Ds2.LightHouseService` 가 사라질 때까지 timeout N초 wait).
2. timeout 시 **강제 kill fallback** (`Stop-Process -Force` / `taskkill /F`).
3. 그 후에만 `Copy-Item` 진행.
- 파일: `Solutions/Tools/Ds2.LightHouseService/scripts/setup-cert-and-service.ps1`.
- 참고 commit: `53fc8fbf [light-house] fix(promaker): make install — publish 전 service stop + 끝에 자동 start` (stop/start 도입했으나 종료 wait 누락).

## 임시 우회 (현재)
옛 server 프로세스 강제 종료(`Stop-Process -Name Ds2.LightHouseService -Force`, admin) 후 `make install` 재실행하면 복사 성공.
