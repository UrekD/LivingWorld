# The game install (Windows build, 1.7): defaults to the nearest folder at or above this one that holds
# Assembly-CSharp.dll or has a RimWorld/ subfolder that does (tools/rimworld-dir.sh).
ifndef RIMWORLD_DIR
RIMWORLD_DIR := $(shell tools/rimworld-dir.sh)
endif
ifeq ($(strip $(RIMWORLD_DIR)),)
ifneq ($(filter-out clean,$(or $(MAKECMDGOALS),build)),)
$(error No RimWorld install found at or above $(CURDIR) (or in a RimWorld/ subfolder of one); set RIMWORLD_DIR=/path/to/RimWorld)
endif
endif
export RIMWORLD_DIR

MOD_DIR  := Mods/LivingWorld
MOD_PROJ := $(MOD_DIR)/Source/LivingWorld/LivingWorld.csproj
DIST_DIR ?= $(CURDIR)/dist

.PHONY: build lint test link dist clean

build:
	dotnet build $(MOD_PROJ) -c Release

# Offline def check: every XML field and def reference against the game's real types (no game run needed).
lint: build
	dotnet build tools/deflint -c Release -v quiet -nologo
	dotnet .work/deflint/deflint.dll $(RIMWORLD_DIR) $(MOD_DIR)

# In-game autotest under the headless harness (DAYS in-game days, PLAN normal|history|...).
DAYS ?= 10
PLAN ?= normal
test: lint
	tools/autotest.sh $(DAYS) $(PLAN)

# Make the game load this checkout (symlink in $(RIMWORLD_DIR)/Mods; no-op when overlaid on the install).
link:
	tools/link-mod.sh

dist: build
	rm -rf $(DIST_DIR)/LivingWorld && mkdir -p $(DIST_DIR)/LivingWorld
	cp -r $(MOD_DIR)/About $(MOD_DIR)/Assemblies $(MOD_DIR)/Defs $(MOD_DIR)/Patches $(MOD_DIR)/Languages $(MOD_DIR)/Textures $(DIST_DIR)/LivingWorld/
	cd $(DIST_DIR) && rm -f LivingWorld.zip && python3 -m zipfile -c LivingWorld.zip LivingWorld
	@echo "Copy $(DIST_DIR)/LivingWorld/ (or unzip LivingWorld.zip) into RimWorld/Mods/ on the gaming PC"

clean:
	rm -rf $(MOD_DIR)/Assemblies $(MOD_DIR)/Source/*/bin $(MOD_DIR)/Source/*/obj
