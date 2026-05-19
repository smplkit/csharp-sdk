using System.Text.Json;
using System.Text.Json.Serialization;

// Generated NSwag clients only emit `[JsonConverter(typeof(JsonStringEnumConverter<T>))]`
// on properties whose type is the enum directly. When an enum is nested inside a
// collection (e.g. `IDictionary<string, List<LogLevel>>` on `Logger.effective_levels`),
// the attribute is omitted and deserialization falls back to numeric parsing — which
// fails with NullReferenceException on the wire's string values.
//
// Each generated client exposes a `static partial void UpdateJsonSerializerSettings`
// hook on its own partial class. Implementing it registers a global converter that
// handles every enum in the type graph regardless of nesting.

namespace Smplkit.Internal.Generated.Logging
{
    public partial class LoggingClient
    {
        static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
        {
            settings.Converters.Add(new JsonStringEnumConverter());
        }
    }
}

namespace Smplkit.Internal.Generated.Audit
{
    public partial class AuditClient
    {
        static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
        {
            settings.Converters.Add(new JsonStringEnumConverter());
        }
    }
}

namespace Smplkit.Internal.Generated.Config
{
    public partial class ConfigClient
    {
        static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
        {
            settings.Converters.Add(new JsonStringEnumConverter());
        }
    }
}

namespace Smplkit.Internal.Generated.Flags
{
    public partial class FlagsClient
    {
        static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
        {
            settings.Converters.Add(new JsonStringEnumConverter());
        }
    }
}

namespace Smplkit.Internal.Generated.App
{
    public partial class AppClient
    {
        static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
        {
            settings.Converters.Add(new JsonStringEnumConverter());
        }
    }
}
