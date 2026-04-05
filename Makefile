.PHONY: install generate \
	config_runtime_showcase config_management_showcase \
	flags_runtime_showcase flags_management_showcase

install:
	dotnet restore
	dotnet tool restore 2>/dev/null || true

generate:
	./scripts/generate.sh

config_runtime_showcase:
	dotnet run --project examples/ConfigShowcase

config_management_showcase:
	dotnet run --project examples/ConfigShowcase -- management

flags_runtime_showcase:
	dotnet run --project examples/FlagsShowcase

flags_management_showcase:
	dotnet run --project examples/FlagsShowcase -- management
