namespace Smplkit.Tests.Helpers;

/// <summary>
/// Shared test data and JSON response templates.
/// </summary>
internal static class TestData
{
    internal const string ConfigId = "550e8400-e29b-41d4-a716-446655440000";
    internal const string ConfigKey = "user_service";
    internal const string ConfigName = "User Service";
    internal const string ApiKey = "sk_api_test_key_123";

    internal static string SingleConfigJson(
        string id = ConfigId,
        string key = ConfigKey,
        string name = ConfigName) =>
        $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "config",
                "attributes": {
                    "key": "{{key}}",
                    "name": "{{name}}",
                    "description": "Test config",
                    "parent": null,
                    "values": { "timeout": 30, "retries": 3 },
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    internal static string ConfigListJson() =>
        $$"""
        {
            "data": [
                {
                    "id": "{{ConfigId}}",
                    "type": "config",
                    "attributes": {
                        "key": "{{ConfigKey}}",
                        "name": "{{ConfigName}}",
                        "description": "Test config",
                        "parent": null,
                        "values": { "timeout": 30 },
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                },
                {
                    "id": "660e8400-e29b-41d4-a716-446655440001",
                    "type": "config",
                    "attributes": {
                        "key": "payment_service",
                        "name": "Payment Service",
                        "description": null,
                        "parent": null,
                        "values": {},
                        "environments": {},
                        "created_at": "2024-01-16T10:30:00Z",
                        "updated_at": "2024-01-16T10:30:00Z"
                    }
                }
            ]
        }
        """;

    internal static string EmptyListJson() =>
        """
        {
            "data": []
        }
        """;

    internal static SmplkitClientOptions DefaultOptions() =>
        new() { ApiKey = ApiKey };
}
