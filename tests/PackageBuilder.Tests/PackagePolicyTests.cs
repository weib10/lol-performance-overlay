using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public void RepositoryScanExcludesBuildOutputAndInstalledDependenciesOnly()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = ReadPackageConfig(repositoryRoot);
        var scan = document.RootElement.GetProperty("scan");
        var excludedDirectoryNames = ReadStrings(scan, "excludedDirectories");

        // node_modules is not produced by this repository any more, but a stale
        // install left in a working checkout must not be walked or scanned.
        Assert.True(PackageBuilder.IsRepositoryScanPathExcluded(
            "node_modules/some-package/package.json",
            excludedDirectoryNames));
        Assert.True(PackageBuilder.IsRepositoryScanPathExcluded(
            "src/LolPerformanceOverlay/obj/project.assets.json",
            excludedDirectoryNames));
        Assert.True(PackageBuilder.IsRepositoryScanPathExcluded(
            "artifacts/package/publish",
            excludedDirectoryNames));
        Assert.False(PackageBuilder.IsRepositoryScanPathExcluded(
            "eng/PackageBuilder/Program.cs",
            excludedDirectoryNames));
        // "logs" is a path segment, not an excluded directory name.
        Assert.False(PackageBuilder.IsRepositoryScanPathExcluded(
            "docs/logs/retention-policy.md",
            excludedDirectoryNames));
        Assert.False(PackageBuilder.IsRepositoryScanPathExcluded(
            "src/LolPerformanceOverlay.Core/Models.cs",
            excludedDirectoryNames));
    }

    [Fact]
    public void RepositoryFileEnumerationDoesNotDescendIntoExcludedDirectories()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = ReadPackageConfig(repositoryRoot);
        var scan = document.RootElement.GetProperty("scan");
        var excludedDirectoryNames = ReadStrings(scan, "excludedDirectories");
        var syntheticRoot = Path.Combine(Path.GetTempPath(), $"package-scan-{Guid.NewGuid():N}");

        try
        {
            WriteSyntheticFile(syntheticRoot, "node_modules/dependency/package.json");
            WriteSyntheticFile(syntheticRoot, "src/App/obj/project.assets.json");
            WriteSyntheticFile(syntheticRoot, "artifacts/package/publish/App.exe");
            WriteSyntheticFile(syntheticRoot, "outputs/SHA256SUMS.txt");
            WriteSyntheticFile(syntheticRoot, "src/App/Program.cs");
            WriteSyntheticFile(syntheticRoot, "docs/logs/retention-policy.md");

            var relativeFiles = PackageBuilder.EnumerateRepositoryFiles(
                    syntheticRoot,
                    excludedDirectoryNames)
                .Select(path => Path.GetRelativePath(syntheticRoot, path).Replace('\\', '/'))
                .ToArray();

            Assert.Equal(
                [
                    "docs/logs/retention-policy.md",
                    "src/App/Program.cs"
                ],
                relativeFiles);
        }
        finally
        {
            if (Directory.Exists(syntheticRoot))
            {
                Directory.Delete(syntheticRoot, recursive: true);
            }
        }
    }

    // The sandbox home was exempt while the Issue worker lived here. That exemption
    // went with it, so no /home path may reach an assembly, the guide, or the ZIP.
    //
    // The path is assembled with string.Concat so this test file does not itself hold
    // a literal the repository developer-path scan would flag: only the rules file is
    // exempt from that scan, so writing one out breaks the release gate.
    [Theory]
    [InlineData("agent")]
    [InlineData("developer-name")]
    public void EveryPosixHomeDirectoryIsARejectedDeveloperPath(string account)
    {
        var patterns = ReadDeveloperPathPatterns();
        var developerPath = string.Concat("/home/", account, "/project/README.md");

        Assert.Contains(patterns, pattern => pattern.IsMatch(developerPath));
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
    public void BinaryNoiseShapedLikeARiotIdDoesNotFailTheRelease()
    {
        // The exact run a compiled Core assembly produced when unrelated metadata shifted:
        // a quote byte, '{', NUL, '#', '8', 'a', then another quote byte.
        var noise = string.Concat("{", "\0", "#", "8a");
        var noiseViolations = new HashSet<string>(StringComparer.Ordinal);

        PackageBuilder.ValidateSyntheticRiotIds(
            noiseViolations,
            "src/LolPerformanceOverlay.Core/bin/Release/net8.0/LolPerformanceOverlay.Core.dll",
            $"\"{noise}\"",
            ["Synthetic", "Fixture"],
            ["TEST", "SAFE", "SYNTHETIC"]);

        Assert.Empty(noiseViolations);

        // A genuine identity in that same assembly must still fail the gate.
        var realIdentity = string.Concat("Ordinary", "Summoner", "#", "TW2");
        var realViolations = new HashSet<string>(StringComparer.Ordinal);

        PackageBuilder.ValidateSyntheticRiotIds(
            realViolations,
            "src/LolPerformanceOverlay.Core/bin/Release/net8.0/LolPerformanceOverlay.Core.dll",
            $"\"{realIdentity}\"",
            ["Synthetic", "Fixture"],
            ["TEST", "SAFE", "SYNTHETIC"]);

        Assert.Single(realViolations);
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
        var excludedDirectoryNames = ReadStrings(scan, "excludedDirectories");
        var violations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in PackageBuilder.EnumerateRepositoryFiles(
                     repositoryRoot,
                     excludedDirectoryNames)
                 .Where(path => string.Equals(
                     Path.GetExtension(path),
                     ".cs",
                     StringComparison.OrdinalIgnoreCase)))
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
        var processName = string.Concat("League", "Client", "Ux");
        var syntheticLockfile = string.Join(':', processName, "1234", "2999", new string('S', 24), "https");

        Assert.Contains(
            ReadSecretPatterns(),
            pattern => pattern.IsMatch(syntheticLockfile));
    }

    [Fact]
    public void ConfiguredSecretPatternsDetectAgentAndGitHubCredentials()
    {
        var credentialSamples = new[]
        {
            string.Concat("GH", "_TOKEN=\"", new string('G', 24), "\""),
            string.Concat("GITHUB", "_TOKEN=", new string('H', 24)),
            string.Concat("CODEX", "_ACCESS_TOKEN=eyJ", new string('C', 24), ".segment"),
            string.Concat("COPILOT", "_GITHUB_TOKEN=", new string('K', 24)),
            string.Concat("GITHUB", "_APP_PRIVATE_KEY=", new string('Q', 32)),
            string.Concat("OPEN", "AI", "_API", "_KEY=", new string('O', 24)),
            string.Concat("\"GITHUB", "_TOKEN\": \"", new string('J', 24), "\""),
            string.Concat("gh", "p_", new string('P', 36)),
            string.Concat("github", "_pat_", new string('F', 80))
        };
        var patterns = ReadSecretPatterns();

        foreach (var sample in credentialSamples)
        {
            Assert.Contains(patterns, pattern => pattern.IsMatch(sample));
        }
    }

    [Fact]
    public void ConfiguredSecretPatternsAllowCredentialReferencesAndPlaceholders()
    {
        string[] safeReferences =
        [
            "GH_TOKEN=${GH_TOKEN}",
            "GITHUB_TOKEN: \"${GITHUB_TOKEN}\"",
            "\"GH_TOKEN\": \"${GH_TOKEN}\"",
            "CODEX_ACCESS_TOKEN: process.env.CODEX_ACCESS_TOKEN",
            "GH_TOKEN=$GH_TOKEN",
            "GITHUB_TOKEN=<provided-by-host>",
            "CODEX_ACCESS_TOKEN={{CODEX_ACCESS_TOKEN}}",
            "COPILOT_GITHUB_TOKEN=${COPILOT_GITHUB_TOKEN}",
            "GITHUB_APP_PRIVATE_KEY=<provided-by-host>",
            "OPENAI_API_KEY=process.env.OPENAI_API_KEY"
        ];
        var patterns = ReadSecretPatterns();

        foreach (var reference in safeReferences)
        {
            Assert.DoesNotContain(patterns, pattern => pattern.IsMatch(reference));
        }
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

    private static Regex[] ReadDeveloperPathPatterns()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = ReadPackageConfig(repositoryRoot);
        var patterns = ReadStrings(document.RootElement.GetProperty("scan"), "developerPathRegexes");
        return PackageBuilder.CompileRegexes(patterns);
    }

    private static Regex[] ReadSecretPatterns()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = ReadPackageConfig(repositoryRoot);
        var patterns = ReadStrings(document.RootElement.GetProperty("scan"), "secretRegexes");
        return PackageBuilder.CompileRegexes(patterns);
    }

    private static NetworkConfig ReadNetworkConfig(JsonElement network) => new()
    {
        RuntimeHosts = ReadStrings(network, "runtimeHosts"),
        UserInitiatedBrowserHosts = ReadStrings(network, "userInitiatedBrowserHosts"),
        DocumentationHosts = ReadStrings(network, "documentationHosts"),
        NonFetchingMarkupNamespaceHosts = ReadStrings(network, "nonFetchingMarkupNamespaceHosts")
    };

    private static void WriteSyntheticFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic");
    }

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
