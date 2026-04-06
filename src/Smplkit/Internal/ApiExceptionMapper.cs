using Smplkit.Errors;

namespace Smplkit.Internal;

/// <summary>
/// Wraps generated client calls and maps NSwag <c>ApiException</c> and HTTP-layer
/// exceptions to the SDK's typed <see cref="SmplException"/> hierarchy.
/// </summary>
internal static class ApiExceptionMapper
{
    /// <summary>
    /// Executes a generated client call and maps exceptions.
    /// </summary>
    internal static async Task<T> ExecuteAsync<T>(Func<Task<T>> call, string operationHint = "")
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (SmplException)
        {
            throw;
        }
        catch (FormatException ex)
        {
            throw new SmplValidationException($"Invalid identifier format{FormatHint(operationHint)}: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        {
            throw new SmplTimeoutException($"Request timed out{FormatHint(operationHint)}.", ex);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new SmplConnectionException($"Connection failed{FormatHint(operationHint)}: {ex.Message}", ex);
        }
        catch (Exception ex) when (IsNSwagApiException(ex))
        {
            var statusCode = ExtractStatusCode(ex);
            if (statusCode >= 200 && statusCode < 300)
                return default!; // Generated client threw for unexpected-but-successful 2xx.
            throw MapApiException(ex, statusCode, operationHint);
        }
    }

    /// <summary>
    /// Executes a generated client call (void return) and maps exceptions.
    /// </summary>
    internal static async Task ExecuteAsync(Func<Task> call, string operationHint = "")
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (SmplException)
        {
            throw;
        }
        catch (FormatException ex)
        {
            throw new SmplValidationException($"Invalid identifier format{FormatHint(operationHint)}: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        {
            throw new SmplTimeoutException($"Request timed out{FormatHint(operationHint)}.", ex);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new SmplConnectionException($"Connection failed{FormatHint(operationHint)}: {ex.Message}", ex);
        }
        catch (Exception ex) when (IsNSwagApiException(ex))
        {
            var statusCode = ExtractStatusCode(ex);
            if (statusCode >= 200 && statusCode < 300)
                return; // Generated client threw for unexpected-but-successful 2xx.
            throw MapApiException(ex, statusCode, operationHint);
        }
    }

    /// <summary>
    /// Checks whether an exception is an NSwag-generated <c>ApiException</c> by
    /// duck-typing the <c>StatusCode</c> and <c>Response</c> properties. Each
    /// generated namespace has its own <c>ApiException</c> class, so we cannot
    /// catch a single type.
    /// </summary>
    private static bool IsNSwagApiException(Exception ex)
    {
        var type = ex.GetType();
        return type.GetProperty("StatusCode") is not null
            && type.GetProperty("Response") is not null;
    }

    /// <summary>
    /// Extracts the HTTP status code from an NSwag <c>ApiException</c>.
    /// </summary>
    private static int ExtractStatusCode(Exception ex) =>
        (int)(ex.GetType().GetProperty("StatusCode")!.GetValue(ex) ?? 0);

    /// <summary>
    /// Extracts <c>Response</c> from an NSwag <c>ApiException</c> and
    /// delegates to <see cref="ApiErrorParser.CreateException"/>.
    /// </summary>
    private static SmplException MapApiException(Exception ex, int statusCode, string operationHint)
    {
        var response = ex.GetType().GetProperty("Response")!.GetValue(ex) as string ?? string.Empty;
        return ApiErrorParser.CreateException(statusCode, response);
    }

    private static string FormatHint(string hint) =>
        string.IsNullOrEmpty(hint) ? "" : $" for {hint}";
}
