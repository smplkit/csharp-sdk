.PHONY: install generate test \
	config_runtime_showcase config_management_showcase \
	flags_runtime_showcase flags_management_showcase \
	logging_runtime_showcase logging_management_showcase \
	audit_runtime_showcase audit_management_showcase \
	jobs_showcase \
	all_showcases

install:
	dotnet restore
	dotnet tool restore 2>/dev/null || true

generate:
	./scripts/generate.sh

test:
	dotnet test --collect:"XPlat Code Coverage"

config_management_showcase:
	dotnet run --project examples/ConfigManagementShowcase

config_runtime_showcase:
	dotnet run --project examples/ConfigRuntimeShowcase

flags_management_showcase:
	dotnet run --project examples/FlagsManagementShowcase

flags_runtime_showcase:
	dotnet run --project examples/FlagsRuntimeShowcase

logging_management_showcase:
	dotnet run --project examples/LoggingManagementShowcase

logging_runtime_showcase:
	dotnet run --project examples/LoggingRuntimeShowcase

audit_runtime_showcase:
	dotnet run --project examples/AuditRuntimeShowcase

audit_management_showcase:
	dotnet run --project examples/AuditManagementShowcase

jobs_showcase:
	dotnet run --project examples/JobsShowcase

all_showcases: config_management_showcase config_runtime_showcase \
	flags_management_showcase flags_runtime_showcase \
	logging_management_showcase logging_runtime_showcase \
	audit_runtime_showcase audit_management_showcase \
	jobs_showcase
