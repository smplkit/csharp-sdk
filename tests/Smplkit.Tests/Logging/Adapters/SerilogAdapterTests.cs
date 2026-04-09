using Serilog.Core;
using Serilog.Events;
using Smplkit.Logging.Adapters;
using Xunit;

namespace Smplkit.Tests.Logging.Adapters;

public class SerilogAdapterTests
{
    [Fact]
    public void Name_ReturnsSerilog()
    {
        var adapter = new SerilogAdapter();
        Assert.Equal("serilog", adapter.Name);
    }

    [Fact]
    public void Discover_ReturnsTrackedNamespaces()
    {
        var adapter = new SerilogAdapter();

        adapter.GetOrCreateSwitch("MyApp.Services");
        adapter.GetOrCreateSwitch("MyApp.Controllers");

        var discovered = adapter.Discover();

        Assert.Equal(2, discovered.Count);
        Assert.Contains(discovered, d => d.Name == "MyApp.Services");
        Assert.Contains(discovered, d => d.Name == "MyApp.Controllers");
    }

    [Fact]
    public void Discover_EmptyBeforeAnySwitchesCreated()
    {
        var adapter = new SerilogAdapter();
        var discovered = adapter.Discover();
        Assert.Empty(discovered);
    }

    [Fact]
    public void ApplyLevel_SetsLevelSwitch()
    {
        var adapter = new SerilogAdapter();

        adapter.GetOrCreateSwitch("MyApp.Service");
        adapter.ApplyLevel("MyApp.Service", LogLevel.Error);

        var discovered = adapter.Discover();
        var entry = discovered.Single(d => d.Name == "MyApp.Service");
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public void ApplyLevel_CreatesNewSwitchIfNotExists()
    {
        var adapter = new SerilogAdapter();

        adapter.ApplyLevel("MyApp.NewService", LogLevel.Warn);

        var discovered = adapter.Discover();
        Assert.Single(discovered);
        Assert.Equal("MyApp.NewService", discovered[0].Name);
        Assert.Equal(LogLevel.Warn, discovered[0].Level);
    }

    [Fact]
    public void ApplyLevel_UpdatesExistingSwitch()
    {
        var adapter = new SerilogAdapter();

        var sw = adapter.GetOrCreateSwitch("MyApp.Service");
        Assert.Equal(LogEventLevel.Verbose, sw.MinimumLevel);

        adapter.ApplyLevel("MyApp.Service", LogLevel.Fatal);

        Assert.Equal(LogEventLevel.Fatal, sw.MinimumLevel);
    }

    [Fact]
    public void GetOrCreateSwitch_ReturnsSameSwitchForSameName()
    {
        var adapter = new SerilogAdapter();

        var sw1 = adapter.GetOrCreateSwitch("MyApp.Service");
        var sw2 = adapter.GetOrCreateSwitch("MyApp.Service");

        Assert.Same(sw1, sw2);
    }

    [Fact]
    public void InstallHook_DetectsForContext()
    {
        var adapter = new SerilogAdapter();
        var detected = new List<(string Name, LogLevel Level)>();

        adapter.InstallHook((name, level) => detected.Add((name, level)));

        adapter.GetOrCreateSwitch("MyApp.NewNamespace");

        Assert.Single(detected);
        Assert.Equal("MyApp.NewNamespace", detected[0].Name);
        Assert.Equal(LogLevel.Trace, detected[0].Level); // Verbose maps to Trace
    }

    [Fact]
    public void InstallHook_DoesNotFireForExistingSwitch()
    {
        var adapter = new SerilogAdapter();

        adapter.GetOrCreateSwitch("MyApp.ExistingNamespace");

        var detected = new List<(string Name, LogLevel Level)>();
        adapter.InstallHook((name, level) => detected.Add((name, level)));

        // Re-requesting same switch should NOT fire hook
        adapter.GetOrCreateSwitch("MyApp.ExistingNamespace");

        Assert.Empty(detected);
    }

    [Fact]
    public void UninstallHook_StopsDetection()
    {
        var adapter = new SerilogAdapter();
        var detected = new List<(string Name, LogLevel Level)>();

        adapter.InstallHook((name, level) => detected.Add((name, level)));
        adapter.UninstallHook();

        adapter.GetOrCreateSwitch("MyApp.AfterUninstall");

        Assert.Empty(detected);
    }

    [Theory]
    [InlineData(LogLevel.Trace, LogEventLevel.Verbose)]
    [InlineData(LogLevel.Debug, LogEventLevel.Debug)]
    [InlineData(LogLevel.Info, LogEventLevel.Information)]
    [InlineData(LogLevel.Warn, LogEventLevel.Warning)]
    [InlineData(LogLevel.Error, LogEventLevel.Error)]
    [InlineData(LogLevel.Fatal, LogEventLevel.Fatal)]
    public void LevelMapping_AllLevelsMapCorrectly(LogLevel smplLevel, LogEventLevel expectedSerilog)
    {
        Assert.Equal(expectedSerilog, SerilogAdapter.ToSerilogLevel(smplLevel));
        Assert.Equal(smplLevel, SerilogAdapter.ToSmplLevel(expectedSerilog));
    }

    [Fact]
    public void LevelMapping_Silent_MapsToAboveFatal()
    {
        var serilogLevel = SerilogAdapter.ToSerilogLevel(LogLevel.Silent);
        Assert.True((int)serilogLevel > (int)LogEventLevel.Fatal);

        // Reverse mapping from above-Fatal back to Silent
        Assert.Equal(LogLevel.Silent, SerilogAdapter.ToSmplLevel(serilogLevel));
    }

    [Fact]
    public void ToSerilogLevel_DefaultCase_ReturnsInformation()
    {
        var result = SerilogAdapter.ToSerilogLevel((LogLevel)999);
        Assert.Equal(LogEventLevel.Information, result);
    }

    [Fact]
    public void ToSmplLevel_DefaultCase_ReturnsInfo()
    {
        // Use a negative value that doesn't match any standard level
        // and is not above Fatal
        var result = SerilogAdapter.ToSmplLevel((LogEventLevel)(-1));
        Assert.Equal(LogLevel.Info, result);
    }
}
