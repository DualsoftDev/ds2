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
PROJECT_FILE   := Promaker/Promaker.csproj
PUBLISH_DIR    := $(abspath Promaker/bin/Release/net9.0-windows/win-x64/$(PUBLISH_SUBDIR))
# Promaker.Agent — 헤드리스 모니터링 Windows Service (Setup.iss 가 옵트인으로 등록).
# net9.0 (windows 미부착), SDK Web. 동일 sc/fd 모드 따라감.
AGENT_PROJECT  := Promaker.Agent/Promaker.Agent.csproj
AGENT_PUBLISH  := $(abspath Promaker.Agent/bin/Release/net9.0/win-x64/$(PUBLISH_SUBDIR))
SETUP_ISS      := Installer/Setup.iss
OUTPUT_DIR     := $(abspath Installer/Output)
INSTALLER_EXE  := $(OUTPUT_DIR)/Promaker_Setup_$(PROMAKER_VER)$(OUTPUT_SUFFIX).exe

# ISCC.exe (Inno Setup 6). 환경에 따라 override 가능.
ISCC ?= C:/Program Files (x86)/Inno Setup 6/ISCC.exe

# scp 대상 — dist-common.mk default 와 동일하지만 override 시 명시.
SCP_DEST ?= download@dualsoft.co.kr:/media/download/Dualsoft Software/Setup ProMaker(DS2_ProMaker) - Windows x64

# ── LightHouse Service ─────────────────────────────────────
# install 은 빌드 산출물을 *설치본* 폴더로 복사 후 등록 (설치본 기준 — 2026-05-30). console 구동(light-house)은 dotnet run.
LH_SERVICE_DIR   := ../../Solutions/Tools/Ds2.LightHouseService
LH_SERVICE_PROJ  := $(LH_SERVICE_DIR)/Ds2.LightHouseService.fsproj
LH_SCRIPTS_DIR   := $(LH_SERVICE_DIR)/scripts
LH_PFX_PATH      ?= C:/ProgramData/Dualsoft/LightHouseService/service.pfx

# 인스톨러 번들 + make install 공용 산출물 — Promaker Setup.iss 의 [Components] ai (default checked) 가
# {app}\LightHouseService\ 로 배치. make install 도 *동일* 산출물(LH_INSTALLER_PUBLISH)을 설치본에 복사
# (setup-cert-and-service.ps1) → 설치본과 비트-동일 보장. self-contained/framework-dependent 혼합 방지를 위해
# RID(win-x64) + sc/fd 모드를 Setup.iss 와 정확히 일치시킨다 (Setup.iss default = publish-self-contained).
LH_INSTALLER_PUBLISH := $(abspath $(LH_SERVICE_DIR)/bin/Release/net9.0/win-x64/$(PUBLISH_SUBDIR))

.PHONY: help dist dist-installer publish publish-agent publish-agenttray publish-lighthouse iscc clean install light-house build run

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
	@echo "Targets (LightHouse Service):"
	@echo "  install         - Build + copy service into the INSTALLED Promaker dir, then register."
	@echo "                    dotnet publish -> copy to {InstallLocation}\\LightHouseService ->"
	@echo "                    setup-cert-and-service.ps1 (self-signed PFX + DPAPI PSK/cert pw +"
	@echo "                    sc register, binPath = installed exe). Requires admin + Promaker"
	@echo "                    installed with the 'AI' component."
	@echo "  light-house     - Run LightHouse service in console (dotnet run, Ctrl+C to stop)."
	@echo "                    Config must exist at %PROGRAMDATA%/Dualsoft/LightHouseService."
	@echo "                    Stop the Windows Service first if registered (sc stop Ds2.LightHouseService)"
	@echo "                    to free the listen port."
	@echo ""
	@echo "Targets (build):"
	@echo "  build           - Build Promaker apps (Promaker / Promaker.Agent / Promaker.AgentTray)"
	@echo "                    + delegate to ../../Solutions/Tools/Makefile build target"
	@echo "                    (Ds2.LightHouse.Cli / Ollama / LightHouseService)."
	@echo "                    Override config: make build BUILD_CONFIG=Debug"
	@echo ""
	@echo "Targets (run):"
	@echo "  run             - Build + launch Promaker in background (shell returns immediately)."
	@echo "                    Override config: make run RUN_CONFIG=Release"
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
	@echo "  LH_INSTALLER_PUBLISH  $(LH_INSTALLER_PUBLISH)"

dist-installer: iscc
	@echo "==========================================================="
	@echo "Promaker installer build complete (no scp / no bump)"
	@echo "  -> $(INSTALLER_EXE)"
	@echo "==========================================================="

# installer — framework-dependent 모드로 dist-installer 재귀 호출 (default sc 와 분리).
.PHONY: installer
installer:
	@$(MAKE) dist-installer MODE=fd

publish:
	@echo "=== dotnet restore ==="
	dotnet restore "$(PROJECT_FILE)" --verbosity quiet
	@echo "=== dotnet publish ($(MODE), self-contained=$(SELF_CONTAINED)) ==="
	dotnet publish "$(PROJECT_FILE)" -c Release -r win-x64 \
		--self-contained $(SELF_CONTAINED) \
		-o "$(PUBLISH_DIR)" \
		-p:DebugType=none -p:DebugSymbols=false -m:1

# Promaker.Agent — Setup.iss 가 {app}\Agent\ 로 번들. Setup.iss 의 HasAgent 매크로가
# AgentPublishDir 안에 Promaker.Agent.exe 가 있을 때만 [Tasks]/[Files]/[Run] 활성화 → 산출물
# 누락 시에도 Promaker 본체 인스톨러는 정상 생성된다. publish 와 동일 sc/fd 모드 따라감.
publish-agent:
	@echo "=== dotnet restore (Promaker.Agent) ==="
	dotnet restore "$(AGENT_PROJECT)" --verbosity quiet
	@echo "=== dotnet publish Promaker.Agent ($(MODE), self-contained=$(SELF_CONTAINED)) ==="
	dotnet publish "$(AGENT_PROJECT)" -c Release -r win-x64 \
		--self-contained $(SELF_CONTAINED) \
		-o "$(AGENT_PUBLISH)" \
		-p:DebugType=none -p:DebugSymbols=false -m:1

# Promaker.AgentTray — Agent 상태 노출용 사용자 컨텍스트 트레이 (WPF).
# Setup.iss 의 HasAgentTray 매크로가 동일하게 산출물 존재 시에만 번들/HKCU\Run 등록.
AGENT_TRAY_PROJECT := Promaker.AgentTray/Promaker.AgentTray.csproj
AGENT_TRAY_PUBLISH := $(abspath Promaker.AgentTray/bin/Release/net9.0-windows/win-x64/$(PUBLISH_SUBDIR))

publish-agenttray:
	@echo "=== dotnet restore (Promaker.AgentTray) ==="
	dotnet restore "$(AGENT_TRAY_PROJECT)" --verbosity quiet
	@echo "=== dotnet publish Promaker.AgentTray ($(MODE), self-contained=$(SELF_CONTAINED)) ==="
	dotnet publish "$(AGENT_TRAY_PROJECT)" -c Release -r win-x64 \
		--self-contained $(SELF_CONTAINED) \
		-o "$(AGENT_TRAY_PUBLISH)" \
		-p:DebugType=none -p:DebugSymbols=false -m:1

# Ds2.LightHouseService — Setup.iss [Components] ai 가 {app}\LightHouseService\ 로 번들.
# Promaker UI 의 "로컬 LightHouse 활성화" 가 publish 결과 + scripts 를 호출하여 service 등록/시작.
# 인스톨러는 파일 배치만, service 등록/PSK 생성/cert 발급은 UI 가 수행 (사후 트리거).
publish-lighthouse:
	@echo "=== dotnet restore (Ds2.LightHouseService) ==="
	dotnet restore "$(LH_SERVICE_PROJ)" --verbosity quiet
	@echo "=== dotnet publish Ds2.LightHouseService ($(MODE), self-contained=$(SELF_CONTAINED)) ==="
	dotnet publish "$(LH_SERVICE_PROJ)" -c Release -r win-x64 \
		--self-contained $(SELF_CONTAINED) \
		-o "$(LH_INSTALLER_PUBLISH)" \
		-p:DebugType=none -p:DebugSymbols=false -m:1

iscc: publish publish-agent publish-agenttray publish-lighthouse
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
	          /DAgentPublishDir="$(AGENT_PUBLISH)" \
	          /DAgentTrayPublishDir="$(AGENT_TRAY_PUBLISH)" \
	          /DLightHousePublishDir="$(LH_INSTALLER_PUBLISH)" \
	          /DLightHouseScriptsDir="$(abspath $(LH_SCRIPTS_DIR))" \
	          /DSelfContainedMode="$(SELF_CONTAINED)" \
	          /DOutputSuffix="$(OUTPUT_SUFFIX)" \
	          /DOutputVersion="$(PROMAKER_VER)" \
	          "$(SETUP_ISS)"

clean:
	@echo "=== Cleaning bin/obj/Output ==="
	-rm -rf Installer/Output
	-rm -rf Promaker/bin Promaker/obj
	-rm -rf Promaker.Agent/bin Promaker.Agent/obj
	-rm -rf Promaker.AgentTray/bin Promaker.AgentTray/obj
	-rm -rf Promaker.Shared/bin Promaker.Shared/obj

# ── LightHouse Service ─────────────────────────────────────
# install — 빌드 산출물을 *설치본* 폴더로 복사 + 인증서 + Service 등록 (설치본 기준 — 2026-05-30 사용자 결정).
#   Promaker GUI 'AI 기능 활성화' 경로(LightHouseLocalInstaller.ResolveDeployment)와 동일하게 dev repo 빌드
#   출력을 binPath 로 쓰지 않고, {InstallLocation}\LightHouseService\ 로 복사 후 그 exe 를 등록.
#   → Promaker installer 의 'AI 기능 활성화' 컴포넌트가 먼저 설치돼 있어야 함 (미설치 시 ps1 throw).
#   1) install-ollama.ps1 (embedding backend)
#   2) publish-lighthouse 재사용 — Setup.iss 와 동일 RID(win-x64)+모드(MODE=sc/fd)로 publish.
#      (framework-dependent 산출물을 self-contained 설치본 위에 덮으면 런타임 dll 혼합으로 부팅 실패 → 동일 모드 강제)
#   3) setup-cert-and-service.ps1 — 빌드 산출물 설치본 복사(복사 전 service 자동 stop) + PFX 1회 입력 →
#      generate-dev-cert + install-service (DPAPI 암호화 PSK/cert pw → config.json + sc.exe register, binPath=설치본 exe)
#   4) sc.exe start — 등록(start= auto)만 된 service 를 즉시 기동 + RUNNING 검증 (실패 시 fail-hard).
# 모두 관리자 권한 PowerShell 필요. install 마지막에 service 자동 start. 콘솔 디버그는 'make light-house' (service 정지 후).
# 설치본을 fd 모드로 설치했다면 'make install MODE=fd' 로 모드를 맞출 것.
install:
	@echo "============================================================="
	@echo "LightHouseService install (registers the INSTALLED copy, MODE=$(MODE))"
	@echo "  Requires Administrator PowerShell + Promaker installed (AI component)."
	@echo "  Steps: 1) Ollama + bge-m3  2) publish-lighthouse  3) copy to install dir + setup-cert-and-service.ps1  4) sc start"
	@echo "         (build output is copied into {InstallLocation}\\LightHouseService, then registered & started)"
	@echo "============================================================="
	@echo ""
	@echo "=== Step 1/4: install-ollama.ps1 (embedding backend) ==="
	powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(LH_SCRIPTS_DIR)/install-ollama.ps1"
	@echo ""
	@echo "=== Step 2/4: publish-lighthouse ($(MODE)) — Setup.iss 와 동일 모드로 빌드 ==="
	@$(MAKE) publish-lighthouse
	@echo ""
	@echo "=== Step 3/4: copy to install dir + setup-cert-and-service.ps1 (PFX password single prompt) ==="
	powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(LH_SCRIPTS_DIR)/setup-cert-and-service.ps1" -PfxPath "$(LH_PFX_PATH)" -PublishDir "$(LH_INSTALLER_PUBLISH)"
	@echo ""
	@echo "=== Step 4/4: sc.exe start Ds2.LightHouseService ==="
	@# 등록만 된 service (start= auto) 를 즉시 기동. `-` 로 'already running'(1056) 무시.
	@# start 는 async (START_PENDING) → RUNNING 까지 polling 후 최종 상태 검증 (실패 시 fail-hard).
	-sc.exe start Ds2.LightHouseService
	@i=0; while [ $$i -lt 60 ]; do sc.exe query Ds2.LightHouseService 2>/dev/null | grep -q RUNNING && break; sleep 0.5; i=$$((i+1)); done
	@sc.exe query Ds2.LightHouseService 2>/dev/null | grep -q RUNNING || { echo "[ERROR] Ds2.LightHouseService did not reach RUNNING within 30s -- check EventLog / config (cert/PSK)."; exit 1; }
	@echo ""
	@echo "============================================================="
	@echo "Install complete -- service started (RUNNING)."
	@echo "  sc query Ds2.LightHouseService   # status"
	@echo "  sc stop  Ds2.LightHouseService   # stop"
	@echo "  make light-house                 # console run (dev / debug; stop service first)"
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

# ── build (Promaker apps + Tools gateway) ─────────────────
# Promaker 작업 디렉터리에서 본체/Agent/AgentTray + LightHouse 솔루션을
# 한 줄로 일괄 빌드하기 위함.
#   - Promaker 본체:  Promaker.csproj (WPF, net9.0-windows)
#   - Agent:          Promaker.Agent.csproj (Windows Service, net9.0)
#   - AgentTray:      Promaker.AgentTray.csproj (WPF tray)
#   - (Promaker.Shared 는 본체가 참조 — transitively 빌드)
# Tools/Makefile 의 build 가 Ds2.LightHouse.{Cli,Ollama,LightHouseService} 를 커버.
TOOLS_DIR    := ../../Solutions/Tools
BUILD_CONFIG ?= Release

PROMAKER_BUILD_PROJECTS := \
	Promaker/Promaker.csproj \
	Promaker.Agent/Promaker.Agent.csproj \
	Promaker.AgentTray/Promaker.AgentTray.csproj

build:
	@echo "=== build Promaker apps (CONFIG=$(BUILD_CONFIG)) ==="
	@for proj in $(PROMAKER_BUILD_PROJECTS); do \
		echo "==> build $$proj ($(BUILD_CONFIG))"; \
		dotnet build "$$proj" -c $(BUILD_CONFIG) || exit $$?; \
	done
	@echo "=== build Tools (CONFIG=$(BUILD_CONFIG)) — delegate to $(TOOLS_DIR)/Makefile ==="
	$(MAKE) -C "$(TOOLS_DIR)" build CONFIG=$(BUILD_CONFIG)

# ── run (Promaker 본체 실행) ──────────────────────────────
# 개발/디버그용 빠른 실행. dotnet build 로 산출물 생성 후 exe 를 background 로 분리 실행
# → 셸은 즉시 프롬프트 복귀, Promaker GUI 는 별도 프로세스로 잔존.
# (WPF WinExe 라 부모 셸 종료와 무관하게 살아남음.)
RUN_CONFIG  ?= Debug
PROMAKER_EXE := Promaker/bin/$(RUN_CONFIG)/net9.0-windows/Promaker.exe

run:
	@echo "=== Build Promaker (CONFIG=$(RUN_CONFIG)) ==="
	dotnet build "$(PROJECT_FILE)" -c $(RUN_CONFIG)
	@echo "=== Launching Promaker in background ==="
	@"$(PROMAKER_EXE)" >/dev/null 2>&1 &
	@echo "Promaker started (PID detached). Shell returns immediately."

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
