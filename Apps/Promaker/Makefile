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

.PHONY: help dist dist-installer publish publish-agent publish-agenttray iscc clean build build-release run run-release

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
	@echo "Targets (build):"
	@echo "  build           - Build Promaker apps in Debug (Promaker / Promaker.Agent / Promaker.AgentTray)"
	@echo "                    + delegate to ../../Solutions/Tools/Makefile build target"
	@echo "                    (Ds2.LightHouse.Cli / Ollama / LightHouseService)."
	@echo "  build-release   - Same as 'build' but Release config."
	@echo ""
	@echo "Targets (run):"
	@echo "  run             - Build + launch Promaker (Debug) in background (shell returns immediately)."
	@echo "  run-release     - Same as 'run' but Release config."
	@echo ""
	@echo "Targets (meta):"
	@echo "  help            - This message"
	@echo ""
	@echo "Variables:"
	@echo "  MODE            current: $(MODE)            (sc | fd)"
	@echo "  PROMAKER_VER    current: $(PROMAKER_VER)    ($(VERSION_FILE))"
	@echo "  INSTALLER_EXE   $(INSTALLER_EXE)"
	@echo "  SCP_DEST        $(SCP_DEST)"

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

iscc: publish publish-agent publish-agenttray
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

# ── build (Promaker apps + Tools gateway) ─────────────────
# Promaker 작업 디렉터리에서 본체/Agent/AgentTray + LightHouse 솔루션을
# 한 줄로 일괄 빌드하기 위함.
#   - Promaker 본체:  Promaker.csproj (WPF, net9.0-windows)
#   - Agent:          Promaker.Agent.csproj (Windows Service, net9.0)
#   - AgentTray:      Promaker.AgentTray.csproj (WPF tray)
#   - (Promaker.Shared 는 본체가 참조 — transitively 빌드)
# Tools/Makefile 의 build 가 Ds2.LightHouse.{Cli,Ollama,LightHouseService} 를 커버.
TOOLS_DIR    := ../../Solutions/Tools
# build/run 의 기본 config 는 Debug — Release 산출물은 build-release / run-release 로 분리.
BUILD_CONFIG ?= Debug

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

# build-release — build 를 Release config 로 재귀 호출 (installer -> dist-installer MODE=fd 패턴과 동일).
build-release:
	@$(MAKE) build BUILD_CONFIG=Release

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

# run-release — run 을 Release config 로 재귀 호출.
run-release:
	@$(MAKE) run RUN_CONFIG=Release

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
