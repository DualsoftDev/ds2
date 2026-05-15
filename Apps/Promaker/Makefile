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

.PHONY: help dist dist-installer publish iscc clean

help:
	@echo "Promaker installer Makefile"
	@echo ""
	@echo "Targets:"
	@echo "  dist-installer  - Build installer .exe (no zip / no scp / no bump)"
	@echo "  dist            - NOTICE only. Use Claude Code '/dist' skill for the full workflow"
	@echo "                    (build + ReleaseNote + commit + zip + scp + tag + bump + push)"
	@echo "  dist-force      - Legacy: dist-installer + scp + bump (no commit/tag/push)"
	@echo "  publish         - dotnet publish only"
	@echo "  iscc            - ISCC compile only (assumes publish output exists)"
	@echo "  clean           - Remove bin/obj/Output"
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
