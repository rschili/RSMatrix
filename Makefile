.PHONY: default build test ide clean-release publish matrix-up matrix-down matrix-logs matrix-status matrix-seed test-integration test-integration-disposable test-matrix-scripts desktop desktop-sample desktop-snapshots test-desktop

MATRIX_PORT ?= 8008
export MATRIX_PORT

INTEGRATION_PROJECT := src/RSMatrix.IntegrationTests/RSMatrix.IntegrationTests.csproj
HOMESERVER_DIR := src/RSMatrix.IntegrationTests/Homeserver
DESKTOP_PROJECT := src/RSMatrix.Desktop/RSMatrix.Desktop.csproj
DESKTOP_USER ?= alice
DESKTOP_DATA := temp/desktop-data/$(DESKTOP_USER)

default: build

build:
	dotnet build src/

test:
	dotnet run --configuration Release --project src/RSMatrix.Tests/RSMatrix.Tests.csproj --coverage --report-trx

# Building the solution compiles these tests; live execution remains opt-in.
matrix-up:
	bash $(HOMESERVER_DIR)/matrix-test-server.sh up

matrix-down:
	bash $(HOMESERVER_DIR)/matrix-test-server.sh down

matrix-logs:
	bash $(HOMESERVER_DIR)/matrix-test-server.sh logs

matrix-status:
	bash $(HOMESERVER_DIR)/matrix-test-server.sh status

# Rooms and messages for alice, bob and carol on a fresh server.
matrix-seed:
	python3 $(HOMESERVER_DIR)/seed-rooms.py

# Signs in to the local server as DESKTOP_USER (make desktop DESKTOP_USER=bob).
# Local data starts fresh, because a recreated server no longer knows the old device.
desktop:
	rm -rf $(DESKTOP_DATA)
	dotnet run --project $(DESKTOP_PROJECT) -- --data-dir $(DESKTOP_DATA) \
		--homeserver http://127.0.0.1:$(MATRIX_PORT) --user @$(DESKTOP_USER):localhost --password rsmatrix-test-password

desktop-sample:
	dotnet run --project $(DESKTOP_PROJECT) -- --sample

# Renders the app with sample data into PNG files, without a display.
desktop-snapshots:
	dotnet run --project src/RSMatrix.Desktop.Snapshots -- temp/snapshots

test-desktop:
	dotnet run --project src/RSMatrix.Desktop.Tests

test-integration:
	RSMATRIX_INTEGRATION_URL="http://127.0.0.1:$(MATRIX_PORT)" dotnet run --configuration Release --project $(INTEGRATION_PROJECT) -- --report-trx

test-integration-disposable:
	bash $(HOMESERVER_DIR)/matrix-test-server.sh test

test-matrix-scripts:
	bash $(HOMESERVER_DIR)/test-lifecycle.sh

ide:
	code .

clean-release:
	rm -rf src/RSMatrix/bin/Release/

publish: clean-release
	dotnet pack src/RSMatrix --configuration Release
	dotnet nuget push src/RSMatrix/bin/Release/*.nupkg --api-key $(NUGET_KEY) --source https://api.nuget.org/v3/index.json