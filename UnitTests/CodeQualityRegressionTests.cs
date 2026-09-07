using System;
using System.IO;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuickFix;
using QuickFix.Store;
using QuickFix.Transport;

namespace UnitTests;

[TestFixture]
public class CodeQualityRegressionTests
{
    [TestCase(30000, 0, 36000d)]
    [TestCase(1100000000, 1, 2640000000d)]
    [TestCase(5, int.MaxValue, 12884901888d)]
    public void TestRequestWaitsForItsThresholdWithoutIntegerOverflow(int interval, int counter, double threshold)
    {
        var received = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        SessionState.NeedTestRequest(received.AddMilliseconds(threshold - 1), interval, received, counter)
            .Should().BeFalse();
        SessionState.NeedTestRequest(received.AddMilliseconds(threshold), interval, received, counter)
            .Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SettingsLoadFailurePreservesItsCause(bool fileExists)
    {
        var file = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".cfg");
        try
        {
            if (fileExists)
                File.WriteAllText(file, "[SESSION]\nBeginString=FIX.4.2\nSenderCompID=S\nTargetCompID=T\nConnectionType=invalid\n");
            Action load = () => _ = new SessionSettings(file);
            var failure = load.Should().Throw<ConfigError>().Which;
            if (fileExists)
            {
                failure.Message.Should().NotContain("not found");
                failure.InnerException.Should().BeOfType<ConfigError>();
            }
            else
            {
                failure.InnerException.Should().BeOfType<FileNotFoundException>();
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestCase("invalid")]
    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("2147483648")]
    [TestCase("99999999999999999999")]
    public void InvalidReconnectIntervalFailsConfiguration(string value)
    {
        var settings = Settings(value);
        using var initiator = new ConfigurableInitiator(settings);
        Action configure = () => initiator.Configure(settings);
        configure.Should().Throw<ConfigError>().WithMessage("*ReconnectInterval*");
    }

    [TestCase(null, 30)]
    [TestCase("1", 1)]
    [TestCase("2147483647", int.MaxValue)]
    [TestCase("60", 60)]
    public void OptionalReconnectIntervalKeepsItsDefaultOrConfiguredValue(string? value, int expected)
    {
        var settings = Settings(value);
        using var initiator = new ConfigurableInitiator(settings);
        initiator.Configure(settings);
        typeof(SocketInitiator).GetField("_reconnectInterval", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(initiator).Should().Be(expected);
    }

    private static SessionSettings Settings(string? reconnectInterval)
    {
        var settings = new SessionSettings();
        var defaults = new SettingsDictionary();
        if (reconnectInterval is not null)
            defaults.SetString(SessionSettings.RECONNECT_INTERVAL, reconnectInterval);
        settings.Set(defaults);
        var session = new SettingsDictionary();
        session.SetString(SessionSettings.CONNECTION_TYPE, "initiator");
        settings.Set(new SessionID("FIX.4.2", "S", "T"), session);
        return settings;
    }

    private sealed class ConfigurableInitiator(SessionSettings settings)
        : SocketInitiator(new NullApplication(), new MemoryStoreFactory(), settings, NullLoggerFactory.Instance)
    {
        public void Configure(SessionSettings value) => OnConfigure(value);
    }
}
