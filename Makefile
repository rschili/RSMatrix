.PHONY: default build test ide clean-release publish matrix-up matrix-down matrix-logs matrix-status test-integration test-integration-disposable test-matrix-scripts

MATRIX_PORT ?= 8008
export MATRIX_PORT

INTEGRATION_PROJECT := src/RSMatrix.IntegrationTests/RSMatrix.IntegrationTests.csproj
HOMESERVER_DIR := src/RSMatrix.IntegrationTests/Homeserver

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