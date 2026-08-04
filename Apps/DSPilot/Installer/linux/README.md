# DSPilot — Linux 설치 (systemd)

Windows 의 Inno Setup 설치본을 Linux(systemd) 로 옮긴 패키지입니다. `.NET 설치 불필요`(self-contained).

## 구성
| 파일 | 역할 |
|------|------|
| `build-linux.sh` | 빌드 머신에서 실행 → linux-x64 publish + MediaMTX 다운로드 + tarball 생성 (`Output/DSPilot_linux-x64_<ver>.tar.gz`) |
| `install.sh` | 타깃 Linux 에서 실행 → 서비스 계정·디렉터리·systemd 유닛·방화벽 구성 후 기동 |
| `uninstall.sh` | 제거 (`--purge` 로 데이터/계정까지) |
| `dspilot.service` / `mediamtx.service` | 웹·CCTV systemd 유닛 템플릿 |
| `promaker-agent.service` / `ds2-collector.service` | PLC/OPC UA Agent와 typed 이력 Collector 유닛 템플릿 |

## 1) 빌드 (개발/빌드 머신; Windows·WSL·Linux 무관, dotnet SDK 9 필요)
```bash
cd Apps/DSPilot/Installer/linux
./build-linux.sh            # CCTV 포함
# SKIP_MEDIAMTX=1 ./build-linux.sh   # CCTV 제외
```
산출물: `Apps/DSPilot/Output/DSPilot_linux-x64_<버전>.tar.gz`

## 2) 설치 (타깃 Linux)
```bash
tar -xzf DSPilot_linux-x64_<버전>.tar.gz
cd DSPilot_linux-x64_<버전>
sudo ./install.sh                 # 포트 8080 기본
# sudo ./install.sh --port 80     # 80 사용(자동으로 CAP_NET_BIND_SERVICE 부여)
# sudo ./install.sh --no-cctv     # CCTV 없이
```

## 동작 개요
- 앱: `/opt/dspilot`, 서비스 계정 `dspilot`, 웹 = `http://<host>:<port>`
- 공유 데이터: `/var/lib/dualsoft/Shared` (`project.aasx` / `plc.db` / `oee.db` / `PlcConnection.json` / `agent/active.flag`)
  - 경로 **단일 출처(SSOT)** = `/etc/dualsoft/dualsoft.env` 의 `DUALSOFT_SHARED_DIR`. DSPilot·Promaker.Agent 의 systemd 유닛이 이 파일을 `EnvironmentFile` 로 함께 읽어 **항상 같은 폴더**를 본다 — 경로를 바꾸려면 이 한 줄만 고치고 `install.sh` 재실행(또는 `--shared-dir`).
  - 대문자 `Shared` 고정: Linux 는 경로 대소문자를 구분하므로 코드 기본값(`DSPilot.Infrastructure.SharedPaths` / `Promaker.Shared.SharedPaths`)과 글자까지 일치시켜, env 변수가 없어도 어긋나지 않게 한다.
- CCTV: MediaMTX 가 별도 서비스(`dspilot-mediamtx`), DSPilot 은 제어 API(:9997)로 카메라만 동기화
- 수집 스택: Linux 기본 설치에서 `promaker-agent`와 `ds2-collector`가 함께 기동된다. Collector는 secure OPC UA `opc.tcp://localhost:62541/Ds2/OpcUa/Server`를 구독하고 `/var/lib/dualsoft/collector`에 typed SQLite 이력을 저장한다.
- Data API: `http://127.0.0.1:62542` localhost 전용. 방화벽 포트를 열지 않는다.
- 인증서: 자동 미신뢰 허용은 꺼져 있다. 같은 설치본의 Agent ApplicationUri를 검증하고 공개 인증서만 상호 trusted store에 등록한다.
- 업그레이드: `install.sh` 재실행 — `appsettings.Production.json`(사용자 설정)·`uploads`·`mediamtx.yml`·공유 데이터 보존

## 운영 메모
- **AASX 모델**: Promaker(Windows) 가 `project.aasx` 를 만든다. Linux 박스에는 이 파일을 `/var/lib/dualsoft/Shared/project.aasx` 로 직접 두면 자동 인식된다(파일 워처).
- **libicu**: 한글/문화권 정렬에 필요. 미설치 시 `install.sh` 가 경고 — `apt-get install -y libicu`(Ubuntu).
- 로그: `journalctl -u dspilot -f`, `journalctl -u promaker-agent -f`, `journalctl -u ds2-collector -f`
