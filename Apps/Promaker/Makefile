# ------------------------------------------------------------------
# 규약: echo/printf 출력은 ASCII 전용 — 한글은 주석에만 둘 것.
#       (Windows cp949 콘솔 ↔ Git Bash UTF-8 경계에서 깨짐 회피)
# ------------------------------------------------------------------
# Promaker — single-component installer build Makefile
#
# 흐름:
#   make dist-installer  (default: MODE=sc)
#     1) dotnet restore Promaker/Promaker.csproj
#     2) dotnet publish ... --self-contained <sc:true|fd:false>
#     3) ISCC Installer/Setup.iss (PublishDir / SelfContainedMode / OutputSuffix 전달)
#   make dist            — dist-installer + scp + bump (legacy 직접 호출)
#   /dist skill          — dist-installer + ReleaseNote + commit + zip + scp + tag + bump + push
#
# 산출물:
#   Installer/Output/Promaker_Setup_<VER><_sc|_fd>.exe
#   Installer/Output/Promaker_Setup_<VER><_sc|_fd>.zip (/dist skill 만 zip 까지 생성)

# 버전 — BuildVersion.txt 가 SSOT. csproj 는 Directory.Build.props 가 주입.
VERSION_FILE  := BuildVersion.txt
PROMAKER_VER  := $(shell tr -d '[:space:]' < $(VERSION_FILE) 2>/dev/null)
ifeq ($(strip $(PROMAKER_VER)),)
$(error $(VERSION_FILE) is missing or empty — version cannot be resolved)
endif

# ── 빌드 모드 ────────────────────────────────────────────
# MODE=sc (default) — self-contained, .NET 9 runtime 번들 (대용량 / 오프라인 가능)
# MODE=fd          — framework-dependent, 런타임 별도 (소용량 / 첫 설치 시 자동 DL)
MODE ?= sc
ifeq ($(MODE),sc)
    SELF_CONTAINED := true
    PUBLISH_SUBDIR := publish-self-contained
    OUTPUT_SUFFIX  := _sc
else ifeq ($(MODE),fd)
    SELF_CONTAINED := false
    PUBLISH_SUBDIR := publish-framework-dependent
    OUTPUT_SUFFIX  := _fd
else
$(error MODE must be one of: sc, fd (got '$(MODE)'))
endif

# ── 터미널 출력 위생 (Ev2.Backend 와 동일) ───────────────
# dotnet / MSBuild 의 TTY-aware 출력이 cp437 glyph 로 깨지는 현상 방지.
export TERM                          := dumb
export NO_COLOR                      := 1
export CLICOLOR                      := 0
export MSBUILDTERMINALLOGGER         := off
export MSBUILDDISABLENODEREUSE       := 1
export DOTNET_NOLOGO                 := 1
export DOTNET_CLI_TELEMETRY_OPTOUT   := 1

# ── 경로 ──────────────────────────────────────────────────
PROJECT_FILE  := Promaker/Promaker.csproj
PUBLISH_DIR   := $(abspath Promaker/bin/Release/net9.0-windows/win-x64/$(PUBLISH_SUBDIR))
SETUP_ISS     := Installer/Setup.iss
OUTPUT_DIR    := $(abspath Installer/Output)
INSTALLER_EXE := $(OUTPUT_DIR)/Promaker_Setup_$(PROMAKER_VER)$(OUTPUT_SUFFIX).exe

# ISCC.exe (Inno Setup 6). 환경에 따라 override 가능.
ISCC ?= C:/Program Files (x86)/Inno Setup 6/ISCC.exe

# scp 대상 — dist-common.mk default 와 동일하지만 override 시 명시.
SCP_DEST ?= download@dualsoft.co.kr:/media/download/Dualsoft Software/Setup ProMaker(DS2_ProMaker) - Windows x64

# ── LightHouse Service (dev/PoC) ───────────────────────────
# Phase S5c — 개발 환경 install + console 구동. 운영 배포는 install-service.ps1 직접 호출 (관리자 PowerShell).
LH_SERVICE_DIR   := ../../Solutions/Tools/Ds2.LightHouseService
LH_SERVICE_PROJ  := $(LH_SERVICE_DIR)/Ds2.LightHouseService.fsproj
LH_SCRIPTS_DIR   := $(LH_SERVICE_DIR)/scripts
LH_PFX_PATH      ?= C:/ProgramData/Dualsoft/LightHouseService/service.pfx
# 콘솔 실행 시 service.exe 의 절대 경로 (install-service.ps1 ExePath 와 동일 — Service 운영 모드 정합).
LH_PUBLISH_DIR   := $(abspath $(LH_SERVICE_DIR)/bin/Release/net9.0/publish)
LH_SERVICE_EXE   := $(LH_PUBLISH_DIR)/Ds2.LightHouseService.exe

.PHONY: help dist dist-installer publish iscc clean install light-house

help:
	@echo "Promaker installer Makefile"
	@echo ""
	@echo "Targets (installer):"
	@echo "  dist-installer  - Build installer .exe (no zip / no scp / no bump)"
	@echo "  dist            - NOTICE only. Use Claude Code '/dist' skill for the full workflow"
	@echo "                    (build + ReleaseNote + commit + zip + scp + tag + bump + push)"
	@echo "  dist-force      - Legacy: dist-installer + scp + bump (no commit/tag/push)"
	@echo "  publish         - dotnet publish only"
	@echo "  iscc            - ISCC compile only (assumes publish output exists)"
	@echo "  clean           - Remove bin/obj/Output"
	@echo ""
	@echo "Targets (LightHouse Service, dev/PoC):"
	@echo "  install         - Dev env setup: generate self-signed PFX (managed user input)"
	@echo "                    + dotnet publish service + install-service.ps1 (Windows Service"
	@echo "                    register + DPAPI encrypt PSK & PFX password). Requires admin."
	@echo "  light-house     - Run LightHouse service in console (dotnet run, Ctrl+C to stop)."
	@echo "                    Config must exist at %PROGRAMDATA%/Dualsoft/LightHouseService."
	@echo "                    Stop the Windows Service first if registered (sc stop Ds2.LightHouseService)"
	@echo "                    to free the listen port."
	@echo ""
	@echo "Targets (meta):"
	@echo "  help            - This message"
	@echo ""
	@echo "Variables:"
	@echo "  MODE            current: $(MODE)            (sc | fd)"
	@echo "  PROMAKER_VER    current: $(PROMAKER_VER)    ($(VERSION_FILE))"
	@echo "  INSTALLER_EXE   $(INSTALLER_EXE)"
	@echo "  SCP_DEST        $(SCP_DEST)"
	@echo "  LH_PFX_PATH     $(LH_PFX_PATH)"
	@echo "  LH_SERVICE_EXE  $(LH_SERVICE_EXE)"

dist-installer: iscc
	@echo "==========================================================="
	@echo "Promaker installer build complete (no scp / no bump)"
	@echo "  -> $(INSTALLER_EXE)"
	@echo "==========================================================="

publish:
	@echo "=== dotnet restore ==="
	dotnet restore "$(PROJECT_FILE)" --verbosity quiet
	@echo "=== dotnet publish ($(MODE), self-contained=$(SELF_CONTAINED)) ==="
	dotnet publish "$(PROJECT_FILE)" -c Release -r win-x64 \
		--self-contained $(SELF_CONTAINED) \
		-o "$(PUBLISH_DIR)" \
		-p:DebugType=none -p:DebugSymbols=false -m:1

iscc: publish
	@if [ ! -f "$(ISCC)" ]; then \
		echo "[ERROR] Inno Setup compiler not found: $(ISCC)" >&2; \
		echo "        Install Inno Setup 6 or override ISCC=..."  >&2; \
		exit 1; \
	fi
	@echo "=== ISCC compile ==="
	@# MSYS_NO_PATHCONV / MSYS2_ARG_CONV_EXCL: Git Bash 가 native Windows exe 의
	@# /D... 인자를 Unix path 로 오인해 변환하면 ISCC 가 "more than one script
	@# filename" 으로 거부한다. ISCC 호출 한정으로 path conversion 을 무력화.
	MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' \
	"$(ISCC)" /DPublishDir="$(PUBLISH_DIR)" \
	          /DSelfContainedMode="$(SELF_CONTAINED)" \
	          /DOutputSuffix="$(OUTPUT_SUFFIX)" \
	          "$(SETUP_ISS)"

clean:
	@echo "=== Cleaning bin/obj/Output ==="
	-rm -rf Installer/Output
	-rm -rf Promaker/bin Promaker/obj

# ── LightHouse Service (dev/PoC) ───────────────────────────
# install — 인증서 + Service 등록 (todo-lighthouse-kb-server.md Phase S1 / §3.7).
#   1) generate-dev-cert.ps1 (self-signed PFX, LocalMachine\My + service.pfx export)
#   2) dotnet publish service (Release, framework-dependent)
#   3) install-service.ps1 (PSK 평문 입력 → DPAPI 암호화 → config.json + sc.exe register)
# 모두 관리자 권한 PowerShell 필요. 한 번 install 후 'sc start Ds2.LightHouseService' 또는 'make light-house'.
install:
	@echo "============================================================="
	@echo "LightHouseService dev install"
	@echo "  Requires Administrator PowerShell."
	@echo "  Steps: 1) self-signed PFX  2) dotnet publish  3) install-service.ps1"
	@echo "============================================================="
	@echo ""
	@echo "=== Step 1/3: generate-dev-cert.ps1 ==="
	powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(LH_SCRIPTS_DIR)/generate-dev-cert.ps1" -PfxPath "$(LH_PFX_PATH)"
	@echo ""
	@echo "=== Step 2/3: dotnet publish service ==="
	dotnet publish "$(LH_SERVICE_PROJ)" -c Release --self-contained false -o "$(LH_PUBLISH_DIR)" -p:DebugType=none -p:DebugSymbols=false -m:1
	@echo ""
	@echo "=== Step 3/3: install-service.ps1 ==="
	powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(LH_SCRIPTS_DIR)/install-service.ps1" -ExePath "$(LH_SERVICE_EXE)" -TlsCertPath "$(LH_PFX_PATH)"
	@echo ""
	@echo "============================================================="
	@echo "Install complete. Next:"
	@echo "  sc start Ds2.LightHouseService     # Windows Service start (production)"
	@echo "  make light-house               # console run (dev / debug)"
	@echo "============================================================="

# light-house — 콘솔에서 service 실행 (개발/디버그용, dotnet run).
#   Windows Service 정지 필요 (port 충돌 회피).
#   config.json 은 install rule 이 작성한 것 그대로 사용 (DPAPI LocalMachine scope).
light-house:
	@echo "============================================================="
	@echo "Running LightHouseService in console (Ctrl+C to stop)"
	@echo "  Stop Windows Service first if registered:"
	@echo "    sc stop Ds2.LightHouseService"
	@echo "============================================================="
	dotnet run --project "$(LH_SERVICE_PROJ)" -c Debug

# 공용 dist-force 만 활성화. `make dist` 는 아래에서 notice-only 로 재정의하여
# 우발적 배포(scp/bump) 를 방지하고 `/dist` skill 사용을 유도한다.
DIST_COMMON_SKIP_DIST_RULE := 1
include scripts/dist-common.mk

.PHONY: dist
dist:
	@echo "========================================================="
	@echo "[INFO] 'make dist' is disabled to prevent accidental deploys."
	@echo ""
	@echo "  Recommended - use the Claude Code '/dist' skill for the full"
	@echo "  workflow (pre-check + build + pull + ReleaseNote prepend +"
	@echo "  commit + zip + scp + tag + bump + push)."
	@echo ""
	@echo "  To force legacy behavior (scp + bump only, no commit/tag/push):"
	@echo "    make dist-force"
	@echo "========================================================="
