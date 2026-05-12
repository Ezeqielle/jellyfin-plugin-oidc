PLUGIN_NAME := Jellyfin.Plugin.OIDC
JELLYFIN_PLUGIN_DIR ?= /var/lib/jellyfin/plugins/OIDC-RBAC
BUILD_DIR := dist

.PHONY: build clean install docker-build docker-install package repo

# Build with local .NET SDK
build:
	dotnet publish $(PLUGIN_NAME)/$(PLUGIN_NAME).csproj -c Release -o $(BUILD_DIR)

clean:
	rm -rf $(BUILD_DIR) repo/
	dotnet clean 2>/dev/null || true

# Build + copy to Jellyfin plugin directory
install: build
	mkdir -p $(JELLYFIN_PLUGIN_DIR)
	cp $(BUILD_DIR)/*.dll $(BUILD_DIR)/meta.json $(JELLYFIN_PLUGIN_DIR)/

# Build via Docker (no .NET SDK required)
docker-build:
	docker build --target artifact --output type=local,dest=$(BUILD_DIR) .

docker-install: docker-build
	mkdir -p $(JELLYFIN_PLUGIN_DIR)
	cp $(BUILD_DIR)/*.dll $(BUILD_DIR)/meta.json $(JELLYFIN_PLUGIN_DIR)/

# Build the installable zip package
package:
	docker build --target package --output type=local,dest=$(BUILD_DIR) .
	@echo "Package ready: $(BUILD_DIR)/oidc-rbac.zip"

# Generate a self-hosted plugin repository
# Usage: make repo [REPO_URL=https://your-server.com/jellyfin-plugins]
repo: package
	./scripts/generate-repo.sh $(REPO_URL)
