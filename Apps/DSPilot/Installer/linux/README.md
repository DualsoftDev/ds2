# DSPilot — Linux 설치 (systemd)

Windows 의 Inno Setup 설치본을 Linux(systemd) 로 옮긴 패키지입니다. `.NET 설치 불필요`(self-contained).

## 구성
| 파일 | 역할 |
|------|------|
| `build-linux.sh` | 빌드 머신에서 실행 → linux-x64 publish + MediaMTX 다운로드 + tarball 생성 (`Output/DSPilot_linux-x64_<ver>.tar.gz`) |
| `install.sh` | 타깃 Linux 에서 실행 → 서비스 계정·디렉터리·systemd 유닛·방화벽 구성 후 기동 |
| `uninstall.sh` | 제거 (`--purge` 로 데이터/계정까지) |
| `dspilot.service` / `mediamtx.service` | systemd 유닛 템플릿(`install.sh` 가 치환) |

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
- 공유 데이터: `/var/lib/dualsoft/Shared` (`project.aasx` / `plc.db` / `oee.db`) — `DUALSOFT_SHARED_DIR` 환경변수로 단일화
- CCTV: MediaMTX 가 별도 서비스(`dspilot-mediamtx`), DSPilot 은 제어 API(:9997)로 카메라만 동기화
- 업그레이드: `install.sh` 재실행 — `appsettings.Production.json`(사용자 설정)·`uploads`·`mediamtx.yml`·공유 데이터 보존

## 운영 메모
- **AASX 모델**: Promaker(Windows) 가 `project.aasx` 를 만든다. Linux 박스에는 이 파일을 `/var/lib/dualsoft/Shared/project.aasx` 로 직접 두면 자동 인식된다(파일 워처).
- **libicu**: 한글/문화권 정렬에 필요. 미설치 시 `install.sh` 가 경고 — `apt-get install -y libicu`(Ubuntu).
- 로그: `journalctl -u dspilot -f`
