namespace Smplkit.Tests.Helpers;

/// <summary>
/// Shared test data and JSON response templates.
/// </summary>
internal static class TestData
{
    internal const string ConfigId = "user_service";
    internal const string ConfigName = "User Service";
    internal const string ApiKey = "sk_api_test_key_123";

    internal static string SingleConfigJson(
        string id = ConfigId,
        string name = ConfigName) =>
        $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "config",
                "attributes": {
                    "id": "{{id}}",
                    "name": "{{name}}",
                    "description": "Test config",
                    "parent": null,
                    "items": { "timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"} },
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
                        "id": "{{ConfigId}}",
                        "name": "{{ConfigName}}",
                        "description": "Test config",
                        "parent": null,
                        "items": { "timeout": {"value": 30, "type": "NUMBER"} },
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                },
                {
                    "id": "payment_service",
                    "type": "config",
                    "attributes": {
                        "id": "payment_service",
                        "name": "Payment Service",
                        "description": null,
                        "parent": null,
                        "items": {},
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

    internal static SmplClientOptions DefaultOptions() =>
        new() { ApiKey = ApiKey, Environment = "test", Service = "test-service" };
}
