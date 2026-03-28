.PHONY: install generate

install:
	dotnet restore
	dotnet tool restore 2>/dev/null || true

generate:
	./scripts/generate.sh
