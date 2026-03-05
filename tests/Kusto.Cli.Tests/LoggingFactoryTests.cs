using Microsoft.Extensions.Logging;

namespace Kusto.Cli.Tests;

public sealed class LoggingFactoryTests
{
    [Fact]
    public void DefaultLogging_WritesFileOnly()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"kusto-log-{Guid.NewGuid():N}.log");
        try
        {
            using var stderrWriter = new StringWriter();
            using (var loggerFactory = LoggingFactoryBuilder.Create(null, logPath, stderrWriter))
            {
                var logger = loggerFactory.CreateLogger("test");
                logger.LogInformation("file-only");
            }

            var fileContents = File.ReadAllText(logPath);
            Assert.Contains("file-only", fileContents);
            Assert.Equal(string.Empty, stderrWriter.ToString());
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public void ExplicitLogLevel_AlsoWritesToStderr()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"kusto-log-{Guid.NewGuid():N}.log");
        try
        {
            using var stderrWriter = new StringWriter();
            using (var loggerFactory = LoggingFactoryBuilder.Create(LogLevel.Warning, logPath, stderrWriter))
            {
                var logger = loggerFactory.CreateLogger("test");
                logger.LogWarning("stderr-enabled");
            }

            Assert.Contains("stderr-enabled", stderrWriter.ToString());
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }
}
