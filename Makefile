.PHONY: default build

default: build

build:
	dotnet build src/

test:
	dotnet run --configuration Release --project src/RSMatrix.Tests/RSMatrix.Tests.csproj --coverage --report-trx

ide:
	code .

clean-release:
	rm -rf src/RSMatrix/bin/Release/

publish: clean-release
	dotnet pack src/RSMatrix --configuration Release
	dotnet nuget push src/RSMatrix/bin/Release/*.nupkg --api-key $(NUGET_KEY) --source https://api.nuget.org/v3/index.json