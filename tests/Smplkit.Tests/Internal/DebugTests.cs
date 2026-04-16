using System;
using System.IO;
using Smplkit.Internal;
using Xunit;

namespace Smplkit.Tests.Internal;

/// <summary>
/// Tests for <see cref="Smplkit.Internal.Debug"/>.
/// </summary>
public class DebugTests
{
    // -----------------------------------------------------------------------
    // ParseDebugEnv — env-string parsing
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("Yes")]
    public void ParseDebugEnv_ReturnsTrue_ForTruthyValues(string value)
    {
        Assert.True(Debug.ParseDebugEnv(value), $"Expected true for: {value}");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("no")]
    [InlineData("NO")]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("on")]
    [InlineData("enable")]
    public void ParseDebugEnv_ReturnsFalse_ForFalsyValues(string value)
    {
        Assert.False(Debug.ParseDebugEnv(value), $"Expected false for: {value}");
    }

    [Fact]
    public void ParseDebugEnv_ReturnsFalse_ForNull()
    {
        Assert.False(Debug.ParseDebugEnv(null));
    }

    [Fact]
    public void ParseDebugEnv_StripsWhitespace()
    {
        Assert.True(Debug.ParseDebugEnv("  1  "));
        Assert.True(Debug.ParseDebugEnv("  true  "));
        Assert.False(Debug.ParseDebugEnv("  false  "));
    }

    // -----------------------------------------------------------------------
    // Enabled property
    // -----------------------------------------------------------------------

    [Fact]
    public void Enabled_IsBoolean()
    {
        // Must not throw — just verify the field is accessible and is a bool
        bool enabled = Debug.Enabled;
        Assert.IsType<bool>(enabled);
    }

    // -----------------------------------------------------------------------
    // Log() — output format and no-op behaviour
    // Uses Debug.Out (package-internal) to avoid Console.Error races across
    // parallel test classes.
    // -----------------------------------------------------------------------

    private static (bool prevEnabled, TextWriter prevOut, StringWriter captured) CaptureDebug(bool enable)
    {
        var captured = new StringWriter();
        var prevEnabled = Debug.Enabled;
        var prevOut = Debug.Out;
        Debug.Enabled = enable;
        Debug.Out = captured;
        return (prevEnabled, prevOut, captured);
    }

    private static void RestoreDebug(bool prevEnabled, TextWriter prevOut)
    {
        Debug.Enabled = prevEnabled;
        Debug.Out = prevOut;
    }

    [Fact]
    public void Log_NoOpWhenDisabled()
    {
        var (prevEnabled, prevOut, captured) = CaptureDebug(false);
        try
        {
            Debug.Log("websocket", "should not appear");
            Assert.Equal(string.Empty, captured.ToString());
        }
        finally { RestoreDebug(prevEnabled, prevOut); }
    }

    [Fact]
    public void Log_WritesToOutWhenEnabled()
    {
        var (prevEnabled, prevOut, captured) = CaptureDebug(true);
        try
        {
            Debug.Log("websocket", "connected");
            Assert.Contains("[smplkit:websocket]", captured.ToString());
        }
        finally { RestoreDebug(prevEnabled, prevOut); }
    }

    [Fact]
    public void Log_WritesExpectedPrefix()
    {
        var (prevEnabled, prevOut, captured) = CaptureDebug(true);
        try
        {
            Debug.Log("lifecycle", "SmplClient created");
            Assert.StartsWith("[smplkit:lifecycle]", captured.ToString());
        }
        finally { RestoreDebug(prevEnabled, prevOut); }
    }

    [Fact]
    public void Log_IncludesMessage()
    {
        var (prevEnabled, prevOut, captured) = CaptureDebug(true);
        try
        {
            Debug.Log("api", "GET /api/v1/loggers");
            Assert.Contains("GET /api/v1/loggers", captured.ToString());
        }
        finally { RestoreDebug(prevEnabled, prevOut); }
    }

    [Fact]
    public void Log_ContainsISO8601Timestamp()
    {
        var (prevEnabled, prevOut, captured) = CaptureDebug(true);
        try
        {
            Debug.Log("resolution", "resolving level");
            var output = captured.ToString();
            Assert.Matches(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", output);
        }
        finally { RestoreDebug(prevEnabled, prevOut); }
    }

    [Fact]
    public void Log_EndsWithNewline()
    {
        var (prevEnabled, prevOut, captured) = CaptureDebug(true);
        try
        {
            Debug.Log("adapter", "applying level DEBUG");
            var output = captured.ToString();
            Assert.True(output.EndsWith("\n") || output.EndsWith(Environment.NewLine),
                $"Expected trailing newline, got: {output}");
        }
        finally { RestoreDebug(prevEnabled, prevOut); }
    }

    [Theory]
    [InlineData("lifecycle")]
    [InlineData("websocket")]
    [InlineData("api")]
    [InlineData("discovery")]
    [InlineData("resolution")]
    [InlineData("adapter")]
    [InlineData("registration")]
    public void Log_AllSubsystemsRenderCorrectly(string subsystem)
    {
        var (prevEnabled, prevOut, captured) = CaptureDebug(true);
        try
        {
            Debug.Log(subsystem, "test");
            Assert.Contains($"[smplkit:{subsystem}]", captured.ToString());
        }
        finally { RestoreDebug(prevEnabled, prevOut); }
    }

    [Fact]
    public void Log_OutputStructure_ThreePartFormat()
    {
        var (prevEnabled, prevOut, captured) = CaptureDebug(true);
        try
        {
            Debug.Log("discovery", "new logger: foo.bar");
            var allOutput = captured.ToString();
            // Concurrent test classes may also write to Debug.Out while it is redirected.
            // Find the specific line produced by this call rather than assuming it is the only line.
            var ourLine = allOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .FirstOrDefault(l => l.Contains("[smplkit:discovery]") && l.Contains("new logger: foo.bar"));
            Assert.NotNull(ourLine);
            var parts = ourLine.Split(' ', 3);
            Assert.Equal("[smplkit:discovery]", parts[0]);
            Assert.Contains("T", parts[1]); // ISO-8601 timestamp
            Assert.EndsWith("new logger: foo.bar", ourLine);
        }
        finally { RestoreDebug(prevEnabled, prevOut); }
    }
}
