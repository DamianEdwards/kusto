namespace Kusto.Cli.Tests;

/// <summary>
/// Guards that the Windows package validation scripts require the MSAL broker runtime
/// (<c>msalruntime.dll</c> for win-x64, <c>msalruntime_arm64.dll</c> for win-arm64) so a
/// WAM-capable archive can never ship without the native broker dependency.
/// </summary>
public sealed class PackagingRequiredFilesTests
{
    private static string ReadScript(string fileName)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "scripts", fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException($"Could not locate scripts/{fileName} above the test output directory.");
    }

    [Theory]
    [InlineData("Publish-NativeAsset.ps1")]
    [InlineData("Test-PackagedArchive.ps1")]
    public void Script_RequiresBrokerRuntimePerArchitecture(string scriptName)
    {
        var content = ReadScript(scriptName);

        Assert.Contains("msalruntime.dll", content, StringComparison.Ordinal);
        Assert.Contains("msalruntime_arm64.dll", content, StringComparison.Ordinal);
        Assert.Contains("arm64", content, StringComparison.Ordinal);
        Assert.Contains("x64", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Publish-NativeAsset.ps1")]
    [InlineData("Test-PackagedArchive.ps1")]
    public void Script_DoesNotRequireBrokerRuntimeForNonWindows(string scriptName)
    {
        var content = ReadScript(scriptName);

        foreach (var line in content.Split('\n'))
        {
            if (!line.Contains("msalruntime", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain(".so", line, StringComparison.Ordinal);
            Assert.DoesNotContain(".dylib", line, StringComparison.Ordinal);
        }
    }
}
