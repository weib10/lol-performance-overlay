using System.Text;
using System.Text.Json;
using Xunit;

namespace PackageBuilderPolicy.Tests;

public sealed class PackagePolicyTests
{
    [Fact]
    public void OfflineGuideGetsRestrictiveCspAndNormalizedLineEndings()
    {
        var guide = PackageBuilder.NormalizeLf("<html>\r\n<head></head>\r<body></body>\r</html>");
        guide = PackageBuilder.AddOfflineContentSecurityPolicy(guide);

        PackageBuilder.ValidateOfflineHtml(guide);
        Assert.DoesNotContain('\r', guide);
        Assert.Contains("default-src 'none'", guide, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none'", guide, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<base href=\"https://github.com/example/\">")]
    [InlineData("<script src=\"relative.js\"></script>")]
    [InlineData("<img src=\"relative.png\">")]
    [InlineData("<img src=relative.png>")]
    [InlineData("<img srcset=\"data:image/png;base64,AAAA 1x, relative.png 2x\">")]
    [InlineData("<a href=\"https://op.gg/\" ping=\"data:text/plain,probe https://op.gg/log\">open</a>")]
    [InlineData("<style>body{background:url(relative.png)}</style>")]
    public void FetchCapableHtmlIsRejected(string injected)
    {
        var guide = PackageBuilder.AddOfflineContentSecurityPolicy(
            $"<html><head></head><body>{injected}</body></html>");

        Assert.Throws<InvalidDataException>(() => PackageBuilder.ValidateOfflineHtml(guide));
    }

    [Fact]
    public void BinaryViewsExposeUtf16ContentToSecretScanner()
    {
        var syntheticSensitiveValue = "RG" + "API-" + new string('S', 24);
        var bytes = Encoding.Unicode.GetBytes(syntheticSensitiveValue);

        Assert.Contains(
            PackageBuilder.DecodeScanViews(bytes),
            view => view.Contains(syntheticSensitiveValue, StringComparison.Ordinal));
    }

    [Fact]
    public void GameIntegrityGateRejectsMemoryInjectionAndAutomatedInputCapabilities()
    {
        var source = string.Join(' ',
            "ReadProcessMemory(handle);",
            "CreateRemoteThread(handle);",
            "SendInput(events);");

        var violations = PackageBuilder.FindForbiddenGameCapabilities(source);

        Assert.Contains("ReadProcessMemory", violations);
        Assert.Contains("CreateRemoteThread", violations);
        Assert.Contains("SendInput(", violations);
    }

    [Fact]
    public void GameIntegrityGateAllowsOrdinaryProcessDiscoveryAndWpfInputEvents()
    {
        const string source = "Process.GetProcessesByName(\"LeagueClientUx\"); MouseMove += OnMouseMove;";

        Assert.Empty(PackageBuilder.FindForbiddenGameCapabilities(source));
    }

    [Fact]
    public void PortableExecutableVersionEvidenceCanContainHostAndProductResources()
    {
        var evidence = string.Join('\0',
            "FileVersion", "8.0.0.0", "FileVersion", "1.1.0.0",
            "ProductVersion", "8.0.0", "ProductVersion", "1.1.0", string.Empty);
        var bytes = Encoding.Unicode.GetBytes(evidence);

        Assert.Contains("1.1.0.0", PackageBuilder.ReadPortableExecutableVersionValues(bytes, "FileVersion"));
        Assert.Contains("1.1.0", PackageBuilder.ReadPortableExecutableVersionValues(bytes, "ProductVersion"));
    }

    [Fact]
    public void TestDiscoveryUsesProjectMetadataInsteadOfFilename()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lol-overlay-package-{Guid.NewGuid():N}");
        var project = Path.Combine(directory, "UnexpectedName.csproj");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(project, "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");

            Assert.True(PackageBuilder.IsTestProject(project));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ProductVersionValidationDoesNotTreatLoopbackIpv4AsAReleaseVersion()
    {
        PackageBuilder.ValidateDocumentVersions(
            "synthetic-guide.html",
            "版本 1.1.0；本機 127.0.0.1",
            "1.1.0",
            requireAtLeastOne: true);
    }

    [Fact]
    public void ProductVersionValidationRejectsArbitraryFourPartIpv4LookingValue()
    {
        Assert.Throws<InvalidDataException>(() => PackageBuilder.ValidateDocumentVersions(
            "synthetic-guide.html",
            "版本 1.1.0；過時欄位 1.0.0.0",
            "1.1.0",
            requireAtLeastOne: true));
    }

    [Fact]
    public void SplitRiotIdentityFixtureWithoutSyntheticMarkerIsRejected()
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);
        var fixture = JsonIdentity("OrdinarySummoner", "TW2");

        PackageBuilder.ValidateSyntheticRiotIds(
            violations,
            "synthetic-fixture.json",
            fixture,
            ["Synthetic", "Fixture"],
            ["TEST", "SAFE", "SYNTHETIC"]);

        Assert.Contains(violations, violation =>
            violation.Contains("split Riot identity fixture", StringComparison.Ordinal));
    }

    [Fact]
    public void SplitRiotIdentityFixtureAllowsMarkerButNotWholeFileExemption()
    {
        var markedViolations = new HashSet<string>(StringComparer.Ordinal);
        PackageBuilder.ValidateSyntheticRiotIds(
            markedViolations,
            "synthetic-fixture.json",
            JsonIdentity("SyntheticSummoner", "TW2", tagFirst: true),
            ["Synthetic", "Fixture"],
            ["TEST", "SAFE", "SYNTHETIC"]);
        Assert.Empty(markedViolations);

        var auditedViolations = new HashSet<string>(StringComparer.Ordinal);
        PackageBuilder.ValidateSyntheticRiotIds(
            auditedViolations,
            "tests/LolPerformanceOverlay.Tests/HistoricalTestData.cs",
            JsonIdentity("OrdinarySummoner", "TW2"),
            ["Synthetic", "Fixture"],
            ["TEST", "SAFE", "SYNTHETIC"]);
        Assert.Contains(auditedViolations, violation =>
            violation.Contains("split Riot identity fixture", StringComparison.Ordinal));
    }

    [Fact]
    public void SyntheticMarkerMustBeANamePrefixOrExplicitTag()
    {
        var falsePositiveViolations = new HashSet<string>(StringComparer.Ordinal);
        var contestant = string.Concat("Con", "testant", "#", "TW2");
        PackageBuilder.ValidateSyntheticRiotIds(
            falsePositiveViolations,
            "synthetic-fixture.cs",
            $"\"{contestant}\"",
            ["Synthetic", "Fixture"],
            ["TEST", "SAFE", "SYNTHETIC"]);
        Assert.Single(falsePositiveViolations);

        var prefixFalsePositiveViolations = new HashSet<string>(StringComparer.Ordinal);
        var testament = string.Concat("Test", "ament", "#", "TW2");
        PackageBuilder.ValidateSyntheticRiotIds(
            prefixFalsePositiveViolations,
            "synthetic-fixture.cs",
            $"\"{testament}\"",
            ["Synthetic", "Fixture"],
            ["TEST", "SAFE", "SYNTHETIC"]);
        Assert.Single(prefixFalsePositiveViolations);

        foreach (var syntheticRiotId in new[]
                 {
                     "\"Synthetic Player#TW2\"",
                     "\"Fixture Player#TW2\"",
                     "\"OrdinarySummoner#TEST\"",
                     "\"OrdinarySummoner#SAFE\""
                 })
        {
            var violations = new HashSet<string>(StringComparer.Ordinal);
            PackageBuilder.ValidateSyntheticRiotIds(
                violations,
                "synthetic-fixture.cs",
                syntheticRiotId,
                ["Synthetic", "Fixture"],
                ["TEST", "SAFE", "SYNTHETIC"]);
            Assert.Empty(violations);
        }
    }

    [Fact]
    public void CSharpEscapedSplitRiotIdentityIsRejected()
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);
        var escapedJson = JsonIdentity("OrdinarySummoner", "TW2")
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var rawCSharpSource = $"var json = \"{escapedJson}\";";

        PackageBuilder.ValidateSyntheticRiotIds(
            violations,
            "synthetic-fixture.cs",
            rawCSharpSource,
            ["Synthetic", "Fixture"],
            ["TEST", "SAFE", "SYNTHETIC"]);

        Assert.Contains(violations, violation =>
            violation.Contains("split Riot identity fixture", StringComparison.Ordinal));
    }

    [Fact]
    public void SplitRiotIdentityPropertyNamesAreCaseInsensitiveLikeTheParser()
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);
        var fixture = JsonIdentity("OrdinarySummoner", "TW2", uppercaseProperties: true);

        PackageBuilder.ValidateSyntheticRiotIds(
            violations,
            "synthetic-fixture.json",
            fixture,
            ["Synthetic", "Fixture"],
            ["TEST", "SAFE", "SYNTHETIC"]);

        Assert.Contains(violations, violation =>
            violation.Contains("split Riot identity fixture", StringComparison.Ordinal));
    }

    [Fact]
    public void RepositoryCSharpSourcesPassSyntheticIdentityPolicy()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = ReadPackageConfig(repositoryRoot);
        var scan = document.RootElement.GetProperty("scan");
        var prefixes = ReadStrings(scan, "syntheticGameNamePrefixes");
        var tagLines = ReadStrings(scan, "syntheticTagLines");
        var violations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(repositoryRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Split(Path.DirectorySeparatorChar)
                         .Any(segment => segment is "bin" or "obj" or "artifacts" or "outputs")))
        {
            string source;
            try
            {
                source = File.ReadAllText(path);
            }
            catch (FileNotFoundException)
            {
                // Concurrent development may atomically replace a source file while this
                // integration test enumerates it. A clean package run has no such writer.
                continue;
            }

            PackageBuilder.ValidateSyntheticRiotIds(
                violations,
                Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
                source,
                prefixes,
                tagLines);
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ConfiguredSecretPatternsDetectSyntheticLcuLockfile()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = ReadPackageConfig(repositoryRoot);
        var patterns = document.RootElement
            .GetProperty("scan")
            .GetProperty("secretRegexes")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
        var processName = string.Concat("League", "Client", "Ux");
        var syntheticLockfile = string.Join(':', processName, "1234", "2999", new string('S', 24), "https");

        Assert.Contains(
            PackageBuilder.CompileRegexes(patterns),
            pattern => pattern.IsMatch(syntheticLockfile));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "eng", "package-config.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find package-config.json from the test output directory.");
    }

    private static JsonDocument ReadPackageConfig(string repositoryRoot) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "eng", "package-config.json")));

    private static string[] ReadStrings(JsonElement parent, string propertyName) =>
        parent.GetProperty(propertyName)
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();

    private static string JsonIdentity(
        string gameName,
        string tagLine,
        bool tagFirst = false,
        bool uppercaseProperties = false)
    {
        var gameNameProperty = uppercaseProperties ? "RIOTIDGAMENAME" : "riotIdGameName";
        var tagLineProperty = uppercaseProperties ? "RIOTIDTAGLINE" : "riotIdTagLine";
        var values = new Dictionary<string, string>();
        if (tagFirst)
        {
            values[tagLineProperty] = tagLine;
            values[gameNameProperty] = gameName;
        }
        else
        {
            values[gameNameProperty] = gameName;
            values[tagLineProperty] = tagLine;
        }

        return JsonSerializer.Serialize(values);
    }
}
