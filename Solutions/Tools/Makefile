CONFIG ?= Release

PROJECTS := \
	Ds2.LightHouse.Cli/Ds2.LightHouse.Cli.fsproj \
	Ds2.LightHouse.Ollama/Ds2.LightHouse.Ollama.fsproj \
	Ds2.LightHouseService/Ds2.LightHouseService.fsproj

.PHONY: all build clean $(PROJECTS)

all: build

build:
	@for proj in $(PROJECTS); do \
		echo "==> build $$proj ($(CONFIG))"; \
		dotnet build "$$proj" -c $(CONFIG) || exit $$?; \
	done

clean:
	@for proj in $(PROJECTS); do \
		echo "==> clean $$proj ($(CONFIG))"; \
		dotnet clean "$$proj" -c $(CONFIG) || exit $$?; \
	done
	@for d in Ds2.LightHouse.Cli Ds2.LightHouse.Ollama Ds2.LightHouseService; do \
		rm -rf "$$d/bin" "$$d/obj"; \
	done
