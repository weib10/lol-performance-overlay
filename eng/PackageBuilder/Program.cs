using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

return PackageBuilder.Run(args);

internal static class PackageBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static int Run(string[] args)
    {
        try
        {
            var root = ResolveRepositoryRoot(args);
            var configPath = Path.Combine(root, "eng", "package-config.json");
            PrepareCleanBuildState(root);
            var config = LoadConfig(configPath);
            var version = ReadVersion(root, config.Product.VersionPropertiesPath);
            var sdkVersion = ReadPinnedSdkVersion(root);
            var actualSdkVersion = Capture(root, GetDotnetHost(), ["--version"]).Trim();
            if (!string.Equals(actualSdkVersion, sdkVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Actual .NET SDK '{actualSdkVersion}' does not match global.json pin '{sdkVersion}'.");
            }

            var context = new BuildContext(root, configPath, config, version, sdkVersion);

            WriteHeading($"Packaging {config.Product.DisplayName} {version}");
            Console.WriteLine($"Repository: {root}");
            Console.WriteLine($"Host: {Environment.OSVersion}");
            Console.WriteLine($"Pinned .NET SDK: {sdkVersion}");
            Console.WriteLine("Release status: internal candidate; stable release gates are not implied by this package.");

            ValidateCleanRepository(context);
            ValidateConfiguration(context);
            ScanRepository(context);
            Restore(context);
            var tests = Test(context);
            var publishedExecutable = Publish(context);
            ScanUncompressedProductAssemblies(context);
            var result = AssembleAndScanPackage(context, publishedExecutable, tests);

            WriteHeading("Package complete");
            Console.WriteLine($"ZIP: {result.ArchivePath}");
            Console.WriteLine($"EXE SHA-256: {result.ExecutableSha256}");
            Console.WriteLine($"ZIP SHA-256: {result.ArchiveSha256}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"PACKAGE FAILED: {exception.Message}");
            return 1;
        }
    }

    private static string ResolveRepositoryRoot(string[] args)
    {
        string? requested = Environment.GetEnvironmentVariable("PACKAGE_REPOSITORY_ROOT");
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--root" && index + 1 < args.Length)
            {
                requested = args[++index];
            }
            else
            {
                throw new InvalidOperationException($"Unknown PackageBuilder argument: {args[index]}");
            }
        }

        var current = new DirectoryInfo(Path.GetFullPath(requested ?? Directory.GetCurrentDirectory()));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "eng", "package-config.json")) &&
                File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root containing eng/package-config.json.");
    }

    private static PackageConfig LoadConfig(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Package configuration is missing.", path);
        }

        var config = JsonSerializer.Deserialize<PackageConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Package configuration is empty.");
        if (config.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported package configuration schema {config.SchemaVersion}.");
        }

        return config;
    }

    private static string ReadVersion(string root, string relativePath)
    {
        var path = ResolveInsideRoot(root, relativePath);
        var document = XDocument.Load(path);
        var version = document.Descendants("VersionPrefix").Select(element => element.Value.Trim()).SingleOrDefault();
        if (string.IsNullOrWhiteSpace(version) ||
            !Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("Directory.Build.props must contain one three-part numeric VersionPrefix.");
        }

        ValidateDerivedVersionProperty(document, "Version", "$(VersionPrefix)");
        ValidateDerivedVersionProperty(document, "AssemblyVersion", "$(VersionPrefix).0");
        ValidateDerivedVersionProperty(document, "FileVersion", "$(VersionPrefix).0");
        ValidateDerivedVersionProperty(document, "InformationalVersion", "$(VersionPrefix)");

        return version;
    }

    private static string ReadPinnedSdkVersion(string root)
    {
        var path = ResolveInsideRoot(root, "global.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var sdk = document.RootElement.GetProperty("sdk");
        var version = sdk.GetProperty("version").GetString();
        var rollForward = sdk.GetProperty("rollForward").GetString();
        if (string.IsNullOrWhiteSpace(version) ||
            !Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant) ||
            !string.Equals(rollForward, "disable", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "global.json must pin one three-part SDK version with rollForward set to disable.");
        }

        return version;
    }

    private static void ValidateDerivedVersionProperty(XDocument document, string name, string expected)
    {
        var actual = document.Descendants(name).Select(element => element.Value.Trim()).SingleOrDefault();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Directory.Build.props {name} must derive from the single VersionPrefix as '{expected}'.");
        }
    }

    private static void ValidateConfiguration(BuildContext context)
    {
        var config = context.Config;
        if (!string.Equals(config.Paths.WorkDirectory, "artifacts/package", StringComparison.Ordinal) ||
            !string.Equals(config.Paths.OutputDirectory, "outputs", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Work/output paths must be the repository-owned packaging directories.");
        }

        var expectedNames = new[]
        {
            config.Product.ExecutableFileName,
            config.Product.FriendGuideFileName,
            config.Product.ArchiveFileName
        };

        foreach (var name in expectedNames)
        {
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name))
            {
                throw new InvalidDataException($"Package filename is unsafe: {name}");
            }

            foreach (var forbidden in config.Scan.ForbiddenArtifactNameTokens)
            {
                if (name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Package filename contains forbidden release wording '{forbidden}': {name}");
                }
            }
        }

        RequireFile(context, config.Paths.WindowsProject);
        RequireFile(context, config.Paths.ApplicationManifest);
        RequireFile(context, config.Paths.Readme);
        RequireFile(context, config.Paths.WindowsWorkflow);
        RequireFile(context, config.Paths.FriendGuideTemplate);
        var testRoot = ResolveInsideRoot(context.Root, config.Paths.TestRoot);
        foreach (var factoryFile in config.Scan.SyntheticIdentityFactoryFiles)
        {
            RequireFile(context, factoryFile);
            var resolved = ResolveInsideRoot(context.Root, factoryFile);
            var testRootPrefix = testRoot.EndsWith(Path.DirectorySeparatorChar)
                ? testRoot
                : testRoot + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(
                    testRootPrefix,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Synthetic identity factory file must remain under the test root: {factoryFile}");
            }
        }

        var expectedAssemblyVersion = context.Version + ".0";
        var manifestPath = ResolveInsideRoot(context.Root, config.Paths.ApplicationManifest);
        var manifest = XDocument.Load(manifestPath);
        var manifestVersion = manifest.Descendants()
            .Where(element => element.Name.LocalName == "assemblyIdentity")
            .Select(element => element.Attribute("version")?.Value)
            .SingleOrDefault(value => value is not null);
        if (!string.Equals(manifestVersion, expectedAssemblyVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Windows app.manifest version '{manifestVersion}' does not match '{expectedAssemblyVersion}'.");
        }

        var readmePath = ResolveInsideRoot(context.Root, config.Paths.Readme);
        var readme = File.ReadAllText(readmePath);
        var readmeVersionMatch = Regex.Match(
            readme,
            @"目前候選版本：`(?<version>[0-9]+\.[0-9]+\.[0-9]+)`",
            RegexOptions.CultureInvariant);
        if (!readmeVersionMatch.Success ||
            !string.Equals(readmeVersionMatch.Groups["version"].Value, context.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"README current candidate version must be '{context.Version}'.");
        }

        if (!readme.Contains("Directory.Build.props", StringComparison.Ordinal) ||
            !readme.Contains("PackageBuilder", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "README must explain that Directory.Build.props is the version source and PackageBuilder enforces consistency.");
        }

        var workflow = File.ReadAllText(ResolveInsideRoot(context.Root, config.Paths.WindowsWorkflow));
        var workflowSdk = Regex.Match(
            workflow,
            "dotnet-version:\\s*[\\\"']?(?<version>[0-9]+\\.[0-9]+\\.[0-9]+)",
            RegexOptions.CultureInvariant).Groups["version"].Value;
        if (!string.Equals(workflowSdk, context.SdkVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Windows workflow SDK '{workflowSdk}' does not match global.json '{context.SdkVersion}'.");
        }

        var actionUses = Regex.Matches(
                workflow,
                @"(?m)^\s*uses:\s*(?<action>actions/[A-Za-z0-9_-]+)@(?<revision>[^\s#]+)",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();
        var allUsesCount = Regex.Matches(workflow, @"(?m)^\s*uses:\s*", RegexOptions.CultureInvariant).Count;
        if (actionUses.Length != allUsesCount ||
            actionUses.Any(match => !Regex.IsMatch(
                match.Groups["revision"].Value,
                "^[0-9a-f]{40}$",
                RegexOptions.CultureInvariant)))
        {
            throw new InvalidDataException("Every GitHub Action must be pinned to one exact 40-character commit SHA.");
        }

        var expectedActionRevisions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["actions/checkout"] = "11d5960a326750d5838078e36cf38b85af677262",
            ["actions/setup-dotnet"] = "67a3573c9a986a3f9c594539f4ab511d57bb3ce9",
            ["actions/upload-artifact"] = "ea165f8d65b6e75b540449e92b4886f43607fa02"
        };
        foreach (var expectedAction in expectedActionRevisions)
        {
            var revisions = actionUses
                .Where(match => string.Equals(
                    match.Groups["action"].Value,
                    expectedAction.Key,
                    StringComparison.Ordinal))
                .Select(match => match.Groups["revision"].Value)
                .ToArray();
            if (revisions.Length != 1 || !string.Equals(revisions[0], expectedAction.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Windows workflow must use verified {expectedAction.Key}@{expectedAction.Value} exactly once.");
            }
        }

        var template = File.ReadAllText(ResolveInsideRoot(context.Root, config.Paths.FriendGuideTemplate));
        var hashPlaceholderCount = CountOccurrences(template, config.Product.ExecutableHashPlaceholder);
        if (hashPlaceholderCount != 1)
        {
            throw new InvalidDataException(
                $"Friend guide must contain exactly one executable-hash placeholder; found {hashPlaceholderCount}.");
        }

        if (!template.Contains(config.Product.VersionPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Friend guide does not contain the product-version placeholder.");
        }

        ValidateDocumentVersions(
            config.Paths.FriendGuideTemplate,
            template.Replace(config.Product.VersionPlaceholder, context.Version, StringComparison.Ordinal),
            context.Version,
            requireAtLeastOne: true);
    }

    internal static void ValidateDocumentVersions(
        string relativePath,
        string text,
        string expectedVersion,
        bool requireAtLeastOne)
    {
        var versionRegex = new Regex(
            @"(?<![0-9])(?<version>[0-9]+\.[0-9]+\.[0-9]+)(?:\.[0-9]+)?(?![0-9])",
            RegexOptions.CultureInvariant);
        var matches = versionRegex.Matches(text);
        if (requireAtLeastOne && matches.Count == 0)
        {
            throw new InvalidDataException($"{relativePath} does not state the current product version.");
        }

        foreach (Match match in matches)
        {
            if (string.Equals(match.Value, "127.0.0.1", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(match.Groups["version"].Value, expectedVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{relativePath} contains version '{match.Value}', expected '{expectedVersion}'.");
            }
        }
    }

    private static void PrepareCleanBuildState(string root)
    {
        WriteHeading("Discard stale package outputs");
        var work = ResolveInsideRoot(root, "artifacts/package");
        var outputs = ResolveInsideRoot(root, "outputs");
        RecreateDirectory(work);
        if (Directory.Exists(outputs))
        {
            Directory.Delete(outputs, recursive: true);
        }

        Console.WriteLine("PASS: stale work and output directories were removed before validation.");
    }

    private static void ValidateCleanRepository(BuildContext context)
    {
        WriteHeading("Clean source gate");
        var status = Capture(
            context.Root,
            "git",
            ["status", "--porcelain=v1", "--untracked-files=all"]);
        if (!string.IsNullOrWhiteSpace(status))
        {
            throw new InvalidDataException(
                "Packaging requires a clean committed source tree. Commit or remove the listed changes first:\n" +
                status.TrimEnd());
        }

        _ = Capture(context.Root, "git", ["rev-parse", "--verify", "HEAD"]);
        Console.WriteLine("PASS: package input is one clean committed Git tree.");
    }

    private static void ScanRepository(BuildContext context)
    {
        WriteHeading("Source and policy scans");
        var config = context.Config;
        var scanFiles = EnumerateRepositoryFiles(context).ToArray();
        var secretRegexes = CompileRegexes(config.Scan.SecretRegexes);
        var pathRegexes = CompileRegexes(config.Scan.DeveloperPathRegexes);
        var knownHosts = config.Network.RuntimeHosts
            .Concat(config.Network.UserInitiatedBrowserHosts)
            .Concat(config.Network.DocumentationHosts)
            .Concat(config.Network.NonFetchingMarkupNamespaceHosts)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var violations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in scanFiles)
        {
            var relative = Path.GetRelativePath(context.Root, path).Replace('\\', '/');
            var isRulesFile = string.Equals(path, context.ConfigPath, StringComparison.OrdinalIgnoreCase);
            foreach (var text in DecodeScanViews(File.ReadAllBytes(path)))
            {
                if (!isRulesFile)
                {
                    AddRegexViolations(violations, relative, text, secretRegexes, "secret-like value");
                    AddRegexViolations(violations, relative, text, pathRegexes, "developer machine path");
                    ValidateSyntheticRiotIds(
                        violations,
                        relative,
                        text,
                        config.Scan.SyntheticGameNamePrefixes,
                        config.Scan.SyntheticTagLines);
                }

                ValidateUrlHosts(violations, relative, text, knownHosts);
            }
        }

        ValidateOverlayDataBoundary(context, violations);
        ValidateSyntheticFixtureConstructionPolicy(context, violations);
        ValidateRuntimeNetworkPolicyContract(context, violations);
        if (violations.Count != 0)
        {
            throw new InvalidDataException(
                "Repository scan failed:\n  - " +
                string.Join("\n  - ", violations.OrderBy(value => value, StringComparer.Ordinal)));
        }

        Console.WriteLine(
            $"PASS: all {scanFiles.Length} repository source/resource files were scanned as ASCII, UTF-8, UTF-16 LE, and UTF-16 BE.");
        Console.WriteLine("PASS: no secret values, real-looking fixture IDs, developer paths, raw Overlay fields, or undeclared URL literals were detected.");
        Console.WriteLine("Declared in-process destination policy: " + string.Join(", ", config.Network.RuntimeHosts));
        Console.WriteLine("Declared optional browser-only destinations: " + string.Join(", ", config.Network.UserInitiatedBrowserHosts));
        Console.WriteLine("NOTE: this is static literal evidence; runtime enforcement is an application policy verified separately by core tests.");
    }

    private static IEnumerable<string> EnumerateRepositoryFiles(BuildContext context)
    {
        var config = context.Config;
        var excluded = config.Scan.ExcludedDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateFiles(context.Root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(context.Root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => excluded.Contains(segment)))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    internal static IEnumerable<string> DecodeScanViews(byte[] bytes)
    {
        yield return Encoding.ASCII.GetString(bytes);
        yield return Encoding.Latin1.GetString(bytes);
        yield return Encoding.UTF8.GetString(bytes);
        yield return Encoding.Unicode.GetString(bytes);
        yield return Encoding.BigEndianUnicode.GetString(bytes);
        if (bytes.Length > 1)
        {
            yield return Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1);
            yield return Encoding.BigEndianUnicode.GetString(bytes, 1, bytes.Length - 1);
        }
    }

    internal static Regex[] CompileRegexes(IEnumerable<string> patterns) =>
        patterns.Select(pattern => new Regex(
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2)))
            .ToArray();

    private static void AddRegexViolations(
        ICollection<string> violations,
        string relativePath,
        string text,
        IEnumerable<Regex> patterns,
        string description)
    {
        foreach (var regex in patterns)
        {
            var match = regex.Match(text);
            if (match.Success)
            {
                violations.Add($"{relativePath}: {description} at line {LineNumber(text, match.Index)}");
            }
        }
    }

    internal static void ValidateSyntheticRiotIds(
        ICollection<string> violations,
        string relativePath,
        string text,
        IReadOnlyCollection<string> syntheticGameNamePrefixes,
        IReadOnlyCollection<string> syntheticTagLines)
    {
        text = text
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\'", "'", StringComparison.Ordinal);
        var regex = new Regex(
            "[\\\"']([^\\\"'\\r\\n]{1,80}#[A-Za-z0-9]{2,6})[\\\"']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        foreach (Match match in regex.Matches(text))
        {
            var candidate = match.Groups[1].Value;
            if (!IsVisiblySyntheticRiotId(candidate, syntheticGameNamePrefixes, syntheticTagLines))
            {
                violations.Add(
                    $"{relativePath}: Riot-ID-shaped literal is not visibly synthetic at line {LineNumber(text, match.Index)}");
            }
        }

        var splitIdentity = new Regex(
            "[\\\"']riotIdGameName[\\\"']\\s*:\\s*[\\\"'](?<gameName>[^\\\"'\\r\\n]{1,80})[\\\"']" +
            "(?s:.{0,512}?)[\\\"']riotIdTagLine[\\\"']\\s*:\\s*[\\\"'](?<tagLine>[A-Za-z0-9]{2,6})[\\\"']" +
            "|[\\\"']riotIdTagLine[\\\"']\\s*:\\s*[\\\"'](?<tagLine>[A-Za-z0-9]{2,6})[\\\"']" +
            "(?s:.{0,512}?)[\\\"']riotIdGameName[\\\"']\\s*:\\s*[\\\"'](?<gameName>[^\\\"'\\r\\n]{1,80})[\\\"']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        foreach (Match match in splitIdentity.Matches(text))
        {
            var candidate = $"{match.Groups["gameName"].Value}#{match.Groups["tagLine"].Value}";
            if (!IsVisiblySyntheticRiotId(candidate, syntheticGameNamePrefixes, syntheticTagLines))
            {
                violations.Add(
                    $"{relativePath}: split Riot identity fixture is not visibly synthetic at line {LineNumber(text, match.Index)}");
            }
        }
    }

    private static bool IsVisiblySyntheticRiotId(
        string candidate,
        IReadOnlyCollection<string> syntheticGameNamePrefixes,
        IReadOnlyCollection<string> syntheticTagLines)
    {
        var separator = candidate.LastIndexOf('#');
        if (separator <= 0 || separator == candidate.Length - 1)
        {
            return false;
        }

        var gameName = candidate[..separator].TrimStart();
        var tagLine = candidate[(separator + 1)..];
        var hasSyntheticGameNamePrefix = syntheticGameNamePrefixes.Any(marker =>
            !string.IsNullOrWhiteSpace(marker) &&
            gameName.StartsWith(marker.TrimStart(), StringComparison.OrdinalIgnoreCase));
        var hasExplicitSyntheticTag = syntheticTagLines.Any(marker =>
            !string.IsNullOrWhiteSpace(marker) &&
            tagLine.Equals(marker.Trim(), StringComparison.OrdinalIgnoreCase));
        return hasSyntheticGameNamePrefix || hasExplicitSyntheticTag;
    }

    private static void ValidateUrlHosts(
        ICollection<string> violations,
        string relativePath,
        string text,
        IReadOnlySet<string> knownHosts)
    {
        var regex = new Regex(@"https?://[A-Za-z0-9.-]+(?::[0-9]+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (Match match in regex.Matches(text))
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) || !knownHosts.Contains(uri.Host))
            {
                violations.Add($"{relativePath}: unexpected URL host '{match.Value}' at line {LineNumber(text, match.Index)}");
            }
        }
    }

    private static void ValidateOverlayDataBoundary(BuildContext context, ICollection<string> violations)
    {
        var rawFields = context.Config.Scan.RawOverlayFieldNames
            .Select(Regex.Escape)
            .ToArray();
        var forbidden = new Regex(
            @"\b(?:" + string.Join("|", rawFields) + @")\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var typeStart = new Regex(
            @"\b(?:record|class)\s+(?<name>(?:Overlay\w+|\w*Overlay\w*ViewModel))\b",
            RegexOptions.CultureInvariant);

        foreach (var path in EnumerateRepositoryFiles(context).Where(path => Path.GetExtension(path) == ".cs"))
        {
            var text = File.ReadAllText(path);
            foreach (Match typeMatch in typeStart.Matches(text))
            {
                var end = FindTypeDeclarationEnd(text, typeMatch.Index);
                var declaration = text[typeMatch.Index..end];
                var rawMatch = forbidden.Match(declaration);
                if (rawMatch.Success)
                {
                    var relative = Path.GetRelativePath(context.Root, path).Replace('\\', '/');
                    violations.Add(
                        $"{relative}: {typeMatch.Groups["name"].Value} exposes forbidden raw field '{rawMatch.Value}' at line {LineNumber(text, typeMatch.Index + rawMatch.Index)}");
                }
            }
        }
    }

    private static void ValidateSyntheticFixtureConstructionPolicy(
        BuildContext context,
        ICollection<string> violations)
    {
        var allowed = context.Config.Scan.SyntheticIdentityFactoryFiles
            .Select(path => Path.GetRelativePath(context.Root, ResolveInsideRoot(context.Root, path)).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var testRoot = ResolveInsideRoot(context.Root, context.Config.Paths.TestRoot);
        if (!Directory.Exists(testRoot))
        {
            return;
        }

        var directCreation = new Regex(
            @"\b(?:TryCreateNormallyRevealed|CreateNormallyRevealed)\s*\(",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        foreach (var path in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            if (!directCreation.IsMatch(text))
            {
                continue;
            }

            var relative = Path.GetRelativePath(context.Root, path).Replace('\\', '/');
            if (!allowed.Contains(relative))
            {
                violations.Add(
                    $"{relative}: direct revealed-identity construction is outside an audited synthetic fixture file");
            }
        }
    }

    private static void ValidateRuntimeNetworkPolicyContract(
        BuildContext context,
        ICollection<string> violations)
    {
        RequireSourceContract(
            context,
            violations,
            "src/LolPerformanceOverlay.Core/NetworkDestinationPolicy.cs",
            "public static class NetworkDestinationPolicy",
            "public static Uri RequireAllowed(",
            "destination.IsLoopback",
            "LoopbackIpv4Host",
            "LoopbackDnsHost",
            "NetworkDestinationPurpose.RuntimeData",
            "NetworkDestinationPurpose.UserInitiatedBrowser");
        RequireSourceContract(
            context,
            violations,
            "src/LolPerformanceOverlay/Infrastructure/DataDragonProvider.cs",
            "private static Uri DataDragonUri(",
            "NetworkDestinationPolicy.RequireAllowed(",
            "NetworkDestinationPurpose.RuntimeData");
        RequireSourceContract(
            context,
            violations,
            "src/LolPerformanceOverlay/Infrastructure/LeagueSessionSource.cs",
            "private static async Task<string> GetStringAsync(",
            "NetworkDestinationPolicy.RequireAllowed(destination, NetworkDestinationPurpose.RuntimeData);",
            "client.GetAsync(destination, cancellationToken)");
        RequireSourceContract(
            context,
            violations,
            "src/LolPerformanceOverlay.Core/Historical/OpGgProfileLinkBuilder.cs",
            "NetworkDestinationPolicy.RequireAllowed(",
            "NetworkDestinationPurpose.UserInitiatedBrowser",
            "ReadsDataBack: false");
        RequireSourceContract(
            context,
            violations,
            "src/LolPerformanceOverlay/App.xaml.cs",
            "private static void OpenExternalLink(Uri destination)",
            "NetworkDestinationPolicy.IsAllowed(",
            "NetworkDestinationPurpose.UserInitiatedBrowser",
            "Process.Start(");

        foreach (var path in EnumerateRepositoryFiles(context)
                     .Where(path => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
                     .Where(path => Path.GetRelativePath(context.Root, path)
                         .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .FirstOrDefault()
                         ?.Equals("src", StringComparison.OrdinalIgnoreCase) == true))
        {
            var relative = Path.GetRelativePath(context.Root, path).Replace('\\', '/');
            if (!string.Equals(relative, "src/LolPerformanceOverlay/App.xaml.cs", StringComparison.Ordinal) &&
                File.ReadAllText(path).Contains("Process.Start(", StringComparison.Ordinal))
            {
                violations.Add($"{relative}: Process.Start is outside the audited browser policy seam");
            }
        }

        var runtimeHosts = context.Config.Network.RuntimeHosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var browserHosts = context.Config.Network.UserInitiatedBrowserHosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var requiredHost in new[] { "127.0.0.1", "localhost", "ddragon.leagueoflegends.com" })
        {
            if (!runtimeHosts.Contains(requiredHost))
            {
                violations.Add($"eng/package-config.json: runtime policy declaration is missing {requiredHost}");
            }
        }

        if (!browserHosts.Contains("op.gg"))
        {
            violations.Add("eng/package-config.json: browser policy declaration is missing op.gg");
        }
    }

    private static void RequireSourceContract(
        BuildContext context,
        ICollection<string> violations,
        string relativePath,
        params string[] requiredSnippets)
    {
        var path = ResolveInsideRoot(context.Root, relativePath);
        if (!File.Exists(path))
        {
            violations.Add($"{relativePath}: required runtime network policy seam is missing");
            return;
        }

        var source = File.ReadAllText(path);
        foreach (var snippet in requiredSnippets)
        {
            if (!source.Contains(snippet, StringComparison.Ordinal))
            {
                violations.Add($"{relativePath}: runtime network policy contract is missing '{snippet}'");
            }
        }
    }

    private static int FindTypeDeclarationEnd(string text, int start)
    {
        var parenStart = text.IndexOf('(', start);
        var braceStart = text.IndexOf('{', start);
        var semicolon = text.IndexOf(';', start);
        var candidates = new[] { parenStart, braceStart, semicolon }.Where(index => index >= 0).ToArray();
        if (candidates.Length == 0)
        {
            return Math.Min(text.Length, start + 2_000);
        }

        var first = candidates.Min();
        if (first == parenStart)
        {
            return FindBalancedEnd(text, parenStart, '(', ')');
        }

        if (first == braceStart)
        {
            return FindBalancedEnd(text, braceStart, '{', '}');
        }

        return semicolon + 1;
    }

    private static int FindBalancedEnd(string text, int openingIndex, char opening, char closing)
    {
        var depth = 0;
        for (var index = openingIndex; index < text.Length; index++)
        {
            if (text[index] == opening)
            {
                depth++;
            }
            else if (text[index] == closing && --depth == 0)
            {
                return index + 1;
            }
        }

        return text.Length;
    }

    private static void Restore(BuildContext context)
    {
        WriteHeading("Restore");
        var windowsProject = ResolveInsideRoot(context.Root, context.Config.Paths.WindowsProject);
        RunDotnet(context, [
            "restore",
            windowsProject,
            "-r", "win-x64",
            "-p:EnableWindowsTargeting=true"
        ]);

        foreach (var testProject in FindTestProjects(context))
        {
            RunDotnet(context, [
                "restore",
                testProject,
                "-p:EnableWindowsTargeting=true"
            ]);
        }
    }

    private static TestSummary Test(BuildContext context)
    {
        WriteHeading("Tests");
        var allProjects = FindTestProjects(context).ToArray();
        var selected = OperatingSystem.IsWindows()
            ? allProjects
            : allProjects.Where(IsCrossPlatformNet8TestProject).ToArray();

        if (selected.Length == 0)
        {
            throw new InvalidOperationException(
                OperatingSystem.IsWindows()
                    ? "No test projects were found."
                    : "No Linux-executable net8.0 test project was found; the package cannot skip all cross-platform tests.");
        }

        var testResultsDirectory = Path.Combine(
            ResolveInsideRoot(context.Root, context.Config.Paths.WorkDirectory),
            "test-results");
        RecreateDirectory(testResultsDirectory);
        var executed = new List<TestExecution>();
        for (var index = 0; index < selected.Length; index++)
        {
            var testProject = selected[index];
            var trxFileName = $"{index:D2}-{Path.GetFileNameWithoutExtension(testProject)}.trx";
            RunDotnet(context, [
                "test",
                testProject,
                "-c", "Release",
                "--no-restore",
                "--nologo",
                "-warnaserror",
                "-p:EnableWindowsTargeting=true",
                "--logger", $"trx;LogFileName={trxFileName}",
                "--results-directory", testResultsDirectory
            ]);
            executed.Add(ParseTrx(
                context,
                testProject,
                Path.Combine(testResultsDirectory, trxFileName)));
        }

        var skipped = allProjects.Except(selected, StringComparer.OrdinalIgnoreCase).ToArray();
        var crossBuilt = Array.Empty<string>();
        if (skipped.Length != 0)
        {
            Console.WriteLine("Linux correctly skipped Windows-only test projects:");
            foreach (var path in skipped)
            {
                Console.WriteLine("  " + Path.GetRelativePath(context.Root, path));
            }

            crossBuilt = skipped;
            WriteHeading("Windows-only test cross-build");
            foreach (var testProject in crossBuilt)
            {
                RunDotnet(context, [
                    "build",
                    testProject,
                    "-c", "Release",
                    "--no-restore",
                    "--nologo",
                    "-warnaserror",
                    "-p:EnableWindowsTargeting=true"
                ]);
            }
        }

        return new TestSummary(
            executed.ToArray(),
            skipped.Select(path => Path.GetRelativePath(context.Root, path).Replace('\\', '/')).ToArray(),
            crossBuilt.Select(path => Path.GetRelativePath(context.Root, path).Replace('\\', '/')).ToArray());
    }

    private static TestExecution ParseTrx(BuildContext context, string projectPath, string trxPath)
    {
        if (!File.Exists(trxPath))
        {
            throw new InvalidDataException($"Test runner did not create the required TRX result: {trxPath}");
        }

        var document = XDocument.Load(trxPath);
        var counters = document.Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "Counters")
            ?? throw new InvalidDataException($"TRX result has no Counters element: {trxPath}");
        var total = ReadTrxCounter(counters, "total", trxPath);
        var executed = ReadTrxCounter(counters, "executed", trxPath);
        var passed = ReadTrxCounter(counters, "passed", trxPath);
        var failed = ReadTrxCounter(counters, "failed", trxPath);
        var errors = ReadTrxCounter(counters, "error", trxPath);
        var timeout = ReadTrxCounter(counters, "timeout", trxPath);
        var aborted = ReadTrxCounter(counters, "aborted", trxPath);
        var notExecuted = ReadTrxCounter(counters, "notExecuted", trxPath);
        if (total <= 0 || executed <= 0)
        {
            throw new InvalidDataException($"TRX result proves no tests ran: {trxPath}");
        }

        if (failed != 0 || errors != 0 || timeout != 0 || aborted != 0)
        {
            throw new InvalidDataException(
                $"TRX result is not clean (failed={failed}, error={errors}, timeout={timeout}, aborted={aborted}): {trxPath}");
        }

        var relative = Path.GetRelativePath(context.Root, projectPath).Replace('\\', '/');
        Console.WriteLine(
            $"PASS: parsed TRX for {relative}: total={total}, executed={executed}, passed={passed}, skipped={notExecuted}.");
        return new TestExecution(relative, total, executed, passed, notExecuted);
    }

    private static int ReadTrxCounter(XElement counters, string name, string trxPath)
    {
        var value = counters.Attribute(name)?.Value;
        if (!int.TryParse(value, out var parsed) || parsed < 0)
        {
            throw new InvalidDataException($"TRX counter '{name}' is missing or invalid in {trxPath}.");
        }

        return parsed;
    }

    private static string Publish(BuildContext context)
    {
        WriteHeading("Windows win-x64 publish");
        var work = ResolveInsideRoot(context.Root, context.Config.Paths.WorkDirectory);
        var publishDirectory = Path.Combine(work, "publish");
        RecreateDirectory(publishDirectory);

        RunDotnet(context, [
            "publish",
            ResolveInsideRoot(context.Root, context.Config.Paths.WindowsProject),
            "-c", "Release",
            "-r", "win-x64",
            "--self-contained", "true",
            "--no-restore",
            "--nologo",
            "-warnaserror",
            "-p:PublishSingleFile=true",
            "-p:EnableCompressionInSingleFile=true",
            "-p:EnableWindowsTargeting=true",
            "-p:ContinuousIntegrationBuild=true",
            "-p:Deterministic=true",
            "-p:DebugType=None",
            "-p:DebugSymbols=false",
            "-o", publishDirectory
        ]);

        var pdbs = Directory.EnumerateFiles(publishDirectory, "*.pdb", SearchOption.AllDirectories).ToArray();
        if (pdbs.Length != 0)
        {
            throw new InvalidDataException("Publish output contains PDB files: " + string.Join(", ", pdbs.Select(Path.GetFileName)));
        }

        var publishedFiles = Directory.EnumerateFiles(publishDirectory, "*", SearchOption.AllDirectories).ToArray();
        if (publishedFiles.Length != 1 ||
            !string.Equals(Path.GetExtension(publishedFiles[0]), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Publish must contain recursively exactly one EXE and no sidecars; found {publishedFiles.Length} file(s): " +
                string.Join(", ", publishedFiles.Select(path => Path.GetRelativePath(publishDirectory, path))));
        }

        return publishedFiles[0];
    }

    private static void ScanUncompressedProductAssemblies(BuildContext context)
    {
        WriteHeading("Uncompressed product assembly scans");
        var sourceRoot = ResolveInsideRoot(context.Root, "src");
        var assemblies = Directory.EnumerateFiles(sourceRoot, "LolPerformanceOverlay*.dll", SearchOption.AllDirectories)
            .Where(path => Path.GetRelativePath(sourceRoot, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains("bin", StringComparer.OrdinalIgnoreCase))
            .Where(path => Path.GetRelativePath(sourceRoot, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains("Release", StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (assemblies.Length == 0)
        {
            throw new InvalidDataException("No uncompressed Release product assembly was found for content scanning.");
        }

        var config = context.Config;
        var knownHosts = config.Network.RuntimeHosts
            .Concat(config.Network.UserInitiatedBrowserHosts)
            .Concat(config.Network.DocumentationHosts)
            .Concat(config.Network.NonFetchingMarkupNamespaceHosts)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sensitivePatterns = CompileRegexes(config.Scan.SecretRegexes.Concat(config.Scan.DeveloperPathRegexes));
        var violations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in assemblies)
        {
            var relative = Path.GetRelativePath(context.Root, path).Replace('\\', '/');
            foreach (var text in DecodeScanViews(File.ReadAllBytes(path)))
            {
                AddRegexViolations(violations, relative, text, sensitivePatterns, "secret-like value or developer path");
                ValidateSyntheticRiotIds(
                    violations,
                    relative,
                    text,
                    config.Scan.SyntheticGameNamePrefixes,
                    config.Scan.SyntheticTagLines);
                ValidateUrlHosts(violations, relative, text, knownHosts);
            }
        }

        if (violations.Count != 0)
        {
            throw new InvalidDataException(
                "Uncompressed product assembly scan failed:\n  - " +
                string.Join("\n  - ", violations.OrderBy(value => value, StringComparer.Ordinal)));
        }

        Console.WriteLine(
            $"PASS: {assemblies.Length} uncompressed Release product assemblies/copies were scanned as ASCII, UTF-8, UTF-16 LE, and UTF-16 BE.");
    }

    private static PackageResult AssembleAndScanPackage(
        BuildContext context,
        string publishedExecutable,
        TestSummary tests)
    {
        WriteHeading("Offline guide, scans, hashes, and ZIP");
        var config = context.Config;
        var work = ResolveInsideRoot(context.Root, config.Paths.WorkDirectory);
        var staging = Path.Combine(work, "staging");
        RecreateDirectory(staging);
        var outputs = ResolveInsideRoot(context.Root, config.Paths.OutputDirectory);
        var candidateOutputs = Path.Combine(work, "candidate-outputs");
        RecreateDirectory(candidateOutputs);

        var executablePath = Path.Combine(staging, config.Product.ExecutableFileName);
        File.Copy(publishedExecutable, executablePath, overwrite: true);
        var executableHash = Sha256(executablePath);

        var templatePath = ResolveInsideRoot(context.Root, config.Paths.FriendGuideTemplate);
        var guide = File.ReadAllText(templatePath)
            .Replace(config.Product.ExecutableHashPlaceholder, executableHash, StringComparison.Ordinal)
            .Replace(config.Product.VersionPlaceholder, context.Version, StringComparison.Ordinal);
        guide = AddOfflineContentSecurityPolicy(NormalizeLf(guide));
        var guidePath = Path.Combine(staging, config.Product.FriendGuideFileName);
        File.WriteAllText(guidePath, guide, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        ValidatePublishedMetadata(context, executablePath, guide);

        ScanPackagedFiles(context, executablePath, guidePath);

        var archivePath = Path.Combine(candidateOutputs, config.Product.ArchiveFileName);

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            AddDeterministicZipEntry(archive, executablePath, config.Product.ExecutableFileName);
            AddDeterministicZipEntry(archive, guidePath, config.Product.FriendGuideFileName);
        }

        VerifyArchive(context, archivePath, [
            new ExpectedArchiveFile(config.Product.ExecutableFileName, executablePath),
            new ExpectedArchiveFile(config.Product.FriendGuideFileName, guidePath)
        ]);
        var archiveHash = Sha256(archivePath);
        var guideHash = Sha256(guidePath);
        var commit = Capture(context.Root, "git", ["rev-parse", "HEAD"]).Trim();
        var gitDirty = !string.IsNullOrWhiteSpace(Capture(context.Root, "git", ["status", "--porcelain"]));
        if (gitDirty)
        {
            throw new InvalidDataException("Repository content changed while packaging; refusing to promote candidate outputs.");
        }

        var manifest = new
        {
            schemaVersion = 1,
            product = config.Product.DisplayName,
            version = context.Version,
            sdkVersion = context.SdkVersion,
            releaseStatus = "internal-candidate",
            stableReleaseEligible = false,
            target = "win-x64",
            selfContained = true,
            singleFile = true,
            gitCommit = commit,
            gitDirty,
            generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            package = new
            {
                fileName = config.Product.ArchiveFileName,
                sha256 = archiveHash,
                length = new FileInfo(archivePath).Length
            },
            files = new object[]
            {
                new
                {
                    fileName = config.Product.ExecutableFileName,
                    sha256 = executableHash,
                    length = new FileInfo(executablePath).Length
                },
                new
                {
                    fileName = config.Product.FriendGuideFileName,
                    sha256 = guideHash,
                    length = new FileInfo(guidePath).Length
                }
            },
            tests = new
            {
                executed = tests.Executed,
                skippedAsWindowsOnly = tests.SkippedAsWindowsOnly,
                crossBuiltOnLinux = tests.CrossBuiltOnLinux
            },
            scans = new
            {
                repositorySecretsAndPaths = "passed",
                syntheticFixtureIdentities = "passed",
                overlayRawFieldBoundary = "passed",
                declaredUrlLiteralHosts = "passed",
                runtimeNetworkPolicySourceContract = "passed",
                uncompressedProductAssemblies = "passed",
                executableVersionMetadata = OperatingSystem.IsWindows()
                    ? "passed-windows-version-api"
                    : "passed-managed-metadata-and-pe-version-resource-evidence",
                packageSecretsAndPaths = "passed",
                htmlOfflineFetchSurfaceAndCsp = "passed",
                zipEntryNamesLengthsAndHashes = "passed"
            },
            runtimeNetworkPolicyDeclaration = config.Network.RuntimeHosts,
            runtimeNetworkPolicyEvidence = "Static URL-literal and required-seam contract scans plus separately executed core policy tests; packaging still does not prove every possible runtime traffic path.",
            userInitiatedBrowserHosts = config.Network.UserInitiatedBrowserHosts,
            windowsHardwareValidation = new[]
            {
                "Real WPF pointer and focus behavior not validated by packaging",
                "Windows DPI and multi-monitor behavior not validated by packaging",
                "SmartScreen behavior not validated by packaging",
                "Real League of Legends match lifecycle not validated by packaging"
            }
        };

        var manifestPath = Path.Combine(candidateOutputs, "package-manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var sumsPath = Path.Combine(candidateOutputs, "SHA256SUMS.txt");
        File.WriteAllText(
            sumsPath,
            $"{executableHash}  {config.Product.ExecutableFileName}{Environment.NewLine}" +
            $"{archiveHash}  {config.Product.ArchiveFileName}{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        VerifyCandidateOutputSet(candidateOutputs, config.Product.ArchiveFileName);
        PromoteCandidateOutputs(candidateOutputs, outputs);
        return new PackageResult(
            Path.Combine(outputs, config.Product.ArchiveFileName),
            executableHash,
            archiveHash);
    }

    internal static string NormalizeLf(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    internal static string AddOfflineContentSecurityPolicy(string guide)
    {
        if (Regex.IsMatch(
                guide,
                "<meta\\b[^>]*http-equiv\\s*=\\s*[\\\"']Content-Security-Policy[\\\"']",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return guide;
        }

        const string policy =
            "  <meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; " +
            "img-src data:; style-src 'unsafe-inline'; script-src 'none'; connect-src 'none'; " +
            "font-src 'none'; media-src 'none'; object-src 'none'; frame-src 'none'; worker-src 'none'; " +
            "manifest-src 'none'; base-uri 'none'; form-action 'none'\">";
        var head = Regex.Match(guide, "<head\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!head.Success)
        {
            throw new InvalidDataException("Friend guide has no <head> element for the offline content security policy.");
        }

        return guide.Insert(head.Index + head.Length, "\n" + policy);
    }

    private static void PromoteCandidateOutputs(string candidateOutputs, string outputs)
    {
        if (Directory.Exists(outputs) || File.Exists(outputs))
        {
            throw new IOException("Output path unexpectedly reappeared during packaging; refusing to overwrite it.");
        }

        Directory.Move(candidateOutputs, outputs);
        Console.WriteLine("PASS: fully verified candidate outputs were atomically promoted.");
    }

    private static void VerifyCandidateOutputSet(string candidateOutputs, string archiveFileName)
    {
        var actual = Directory.EnumerateFiles(candidateOutputs, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(candidateOutputs, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var expected = new[] { archiveFileName, "package-manifest.json", "SHA256SUMS.txt" }
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Candidate output directory contains unexpected files: " + string.Join(", ", actual));
        }
    }

    private static void ValidatePublishedMetadata(BuildContext context, string executablePath, string guide)
    {
        var expectedAssemblyVersion = context.Version + ".0";
        if (OperatingSystem.IsWindows())
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            RequireVersions(
                "Published EXE Windows version API",
                versionInfo.FileVersion,
                versionInfo.ProductVersion,
                expectedAssemblyVersion,
                context.Version);
        }
        else
        {
            var managedAssembly = FindManagedReleaseAssembly(context);
            var assemblyVersion = AssemblyName.GetAssemblyName(managedAssembly).Version?.ToString();
            var managedVersionInfo = FileVersionInfo.GetVersionInfo(managedAssembly);
            RequireVersions(
                "Managed Release assembly",
                managedVersionInfo.FileVersion,
                managedVersionInfo.ProductVersion,
                expectedAssemblyVersion,
                context.Version);
            if (!string.Equals(assemblyVersion, expectedAssemblyVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Managed Release AssemblyVersion='{assemblyVersion}', expected '{expectedAssemblyVersion}'.");
            }

            var executableBytes = File.ReadAllBytes(executablePath);
            RequirePortableExecutableVersionEvidence(
                executableBytes,
                "FileVersion",
                expectedAssemblyVersion);
            RequirePortableExecutableVersionEvidence(
                executableBytes,
                "ProductVersion",
                context.Version);
        }

        ValidateDocumentVersions(
            context.Config.Product.FriendGuideFileName,
            guide,
            context.Version,
            requireAtLeastOne: true);
        Console.WriteLine(
            $"PASS: README, friend HTML, app.manifest, EXE ProductVersion/FileVersion, and Directory.Build.props agree on {context.Version}.");
    }

    private static string FindManagedReleaseAssembly(BuildContext context)
    {
        var projectDirectory = Path.GetDirectoryName(
            ResolveInsideRoot(context.Root, context.Config.Paths.WindowsProject))!;
        var candidates = Directory.EnumerateFiles(
                Path.Combine(projectDirectory, "bin"),
                "LolPerformanceOverlay.dll",
                SearchOption.AllDirectories)
            .Where(path => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains("Release", StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        return candidates.FirstOrDefault() ?? throw new InvalidDataException(
            "No managed Release application assembly was available for cross-platform metadata validation.");
    }

    private static void RequireVersions(
        string evidence,
        string? fileVersion,
        string? productVersion,
        string expectedFileVersion,
        string expectedProductVersion)
    {
        if (!string.Equals(fileVersion, expectedFileVersion, StringComparison.Ordinal) ||
            !string.Equals(productVersion, expectedProductVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{evidence} mismatch: FileVersion='{fileVersion}', ProductVersion='{productVersion}', " +
                $"expected '{expectedFileVersion}' and '{expectedProductVersion}'.");
        }
    }

    internal static IReadOnlyList<string> ReadPortableExecutableVersionValues(
        byte[] executableBytes,
        string propertyName)
    {
        ArgumentNullException.ThrowIfNull(executableBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        var unicode = Encoding.Unicode.GetString(executableBytes);
        var regex = new Regex(
            Regex.Escape(propertyName) + "\\0+(?<value>[^\\0\\r\\n]{1,120})\\0",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        return regex.Matches(unicode)
            .Select(match => match.Groups["value"].Value.Trim('\0', ' ', '\t'))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void RequirePortableExecutableVersionEvidence(
        byte[] executableBytes,
        string propertyName,
        string expectedValue)
    {
        var values = ReadPortableExecutableVersionValues(executableBytes, propertyName);
        if (!values.Contains(expectedValue, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Published EXE PE resource has no {propertyName}='{expectedValue}'. Found: " +
                string.Join(", ", values));
        }
    }

    private static void ScanPackagedFiles(BuildContext context, string executablePath, string guidePath)
    {
        var config = context.Config;
        var guide = File.ReadAllText(guidePath);
        if (guide.Contains(config.Product.ExecutableHashPlaceholder, StringComparison.Ordinal) ||
            guide.Contains(config.Product.VersionPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Generated friend guide still contains a packaging placeholder.");
        }

        foreach (var forbidden in config.Scan.ForbiddenArtifactNameTokens)
        {
            if (guide.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Generated friend guide contains forbidden release wording '{forbidden}'.");
            }
        }

        ValidateOfflineHtml(guide);

        var allowedGuideHosts = config.Network.RuntimeHosts
            .Concat(config.Network.UserInitiatedBrowserHosts)
            .Concat(config.Network.DocumentationHosts)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();
        ValidateUrlHosts(violations, config.Product.FriendGuideFileName, guide, allowedGuideHosts);
        AddRegexViolations(violations, config.Product.FriendGuideFileName, guide, CompileRegexes(config.Scan.SecretRegexes), "secret-like value");
        AddRegexViolations(violations, config.Product.FriendGuideFileName, guide, CompileRegexes(config.Scan.DeveloperPathRegexes), "developer machine path");
        if (violations.Count != 0)
        {
            throw new InvalidDataException("Friend guide scan failed:\n  - " + string.Join("\n  - ", violations));
        }

        var bytes = File.ReadAllBytes(executablePath);
        var binaryPatterns = CompileRegexes(config.Scan.SecretRegexes.Concat(config.Scan.DeveloperPathRegexes));
        foreach (var binaryText in DecodeScanViews(bytes))
        {
            foreach (var regex in binaryPatterns)
            {
                if (regex.IsMatch(binaryText))
                {
                    throw new InvalidDataException("Published EXE contains a secret-like value or developer machine path.");
                }
            }

            var projectPdb = new Regex(
                @"(?:LolPerformanceOverlay|PackageBuilder)[^\0\r\n]{0,120}\.pdb",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (projectPdb.IsMatch(binaryText))
            {
                throw new InvalidDataException("Published EXE contains a project PDB filename or path.");
            }
        }

        Console.WriteLine("PASS: offline guide has a restrictive CSP and no fetch-capable HTML/CSS/JavaScript surface.");
        Console.WriteLine("PASS: package contains no detected credential, developer path, or project PDB reference in ASCII/UTF-8/UTF-16 views.");
    }

    internal static void ValidateOfflineHtml(string guide)
    {
        var forbiddenTag = new Regex(
            "<(?:base|script|iframe|object|embed|form|link)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (forbiddenTag.IsMatch(guide))
        {
            throw new InvalidDataException(
                "Friend guide contains <base> or another unnecessary fetch-capable element.");
        }

        var refresh = new Regex(
            "<meta\\b[^>]*http-equiv\\s*=\\s*[\\\"']?refresh\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (refresh.IsMatch(guide))
        {
            throw new InvalidDataException("Friend guide contains a meta refresh.");
        }

        var cssFetch = new Regex(
            "(?:@import\\b|url\\s*\\(\\s*(?![\\\"']?data:))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (cssFetch.IsMatch(guide))
        {
            throw new InvalidDataException("Friend guide CSS can fetch a non-embedded resource.");
        }

        var scriptFetch = new Regex(
            @"\b(?:fetch|XMLHttpRequest|WebSocket|EventSource|sendBeacon|importScripts)\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (scriptFetch.IsMatch(guide))
        {
            throw new InvalidDataException("Friend guide contains a JavaScript network primitive.");
        }

        var resourceAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src", "srcset", "poster", "data", "action", "formaction", "ping", "background"
        };
        var tagRegex = new Regex(
            "<(?<name>[A-Za-z][A-Za-z0-9:-]*)\\b(?<attributes>[^>]*)>",
            RegexOptions.CultureInvariant);
        var attributeRegex = new Regex(
            "(?<name>[A-Za-z_:][A-Za-z0-9_.:-]*)\\s*=\\s*(?:\\\"(?<doubleQuoted>[^\\\"]*)\\\"|'(?<singleQuoted>[^']*)'|(?<unquoted>[^\\s\\\"'=<>`]+))",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        foreach (Match tag in tagRegex.Matches(guide))
        {
            var tagName = tag.Groups["name"].Value;
            foreach (Match attribute in attributeRegex.Matches(tag.Groups["attributes"].Value))
            {
                var attributeName = attribute.Groups["name"].Value;
                var value = AttributeValue(attribute).Trim();
                if (attributeName.Equals("srcset", StringComparison.OrdinalIgnoreCase) ||
                    attributeName.Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Friend guide <{tagName}> uses forbidden multi-value attribute {attributeName}.");
                }

                if (resourceAttributes.Contains(attributeName) &&
                    !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Friend guide <{tagName}> has fetch-capable {attributeName}='{value}'.");
                }

                if (attributeName.EndsWith("href", StringComparison.OrdinalIgnoreCase))
                {
                    var isUserNavigation = string.Equals(tagName, "a", StringComparison.OrdinalIgnoreCase) &&
                        (value.StartsWith('#') ||
                         value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
                    if (!isUserNavigation)
                    {
                        throw new InvalidDataException(
                            $"Friend guide <{tagName}> has non-navigation href='{value}'.");
                    }
                }
            }
        }

        var cspMetas = Regex.Matches(
            guide,
            "<meta\\b(?=[^>]*http-equiv\\s*=\\s*[\\\"']Content-Security-Policy[\\\"'])[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (cspMetas.Count != 1)
        {
            throw new InvalidDataException(
                $"Friend guide must have exactly one Content-Security-Policy meta element; found {cspMetas.Count}.");
        }

        var contentAttribute = attributeRegex.Matches(cspMetas[0].Value)
            .Cast<Match>()
            .SingleOrDefault(match => string.Equals(
                match.Groups["name"].Value,
                "content",
                StringComparison.OrdinalIgnoreCase));
        var content = contentAttribute is null ? null : AttributeValue(contentAttribute);
        var requiredDirectives = new[]
        {
            "default-src 'none'", "script-src 'none'", "connect-src 'none'", "object-src 'none'",
            "frame-src 'none'", "base-uri 'none'", "form-action 'none'"
        };
        var actualDirectives = content?
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            ?? [];
        if (string.IsNullOrWhiteSpace(content) ||
            requiredDirectives.Any(required => !actualDirectives.Contains(required, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Friend guide CSP is missing a required offline restriction.");
        }
    }

    private static string AttributeValue(Match attribute)
    {
        foreach (var groupName in new[] { "doubleQuoted", "singleQuoted", "unquoted" })
        {
            var group = attribute.Groups[groupName];
            if (group.Success)
            {
                return group.Value;
            }
        }

        return "";
    }

    private static void VerifyArchive(
        BuildContext context,
        string archivePath,
        IReadOnlyCollection<ExpectedArchiveFile> expectedFiles)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var names = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var expected = expectedFiles.Select(file => file.EntryName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "ZIP top level must contain exactly the EXE and guide. Actual entries: " + string.Join(", ", names));
        }

        if (archive.Entries.Any(entry => entry.FullName.Contains('/') || entry.FullName.Contains('\\')))
        {
            throw new InvalidDataException("ZIP contains a nested path.");
        }

        foreach (var expectedFile in expectedFiles)
        {
            var entry = archive.GetEntry(expectedFile.EntryName)
                ?? throw new InvalidDataException($"ZIP entry is missing: {expectedFile.EntryName}");
            var sourceLength = new FileInfo(expectedFile.SourcePath).Length;
            if (entry.Length != sourceLength)
            {
                throw new InvalidDataException(
                    $"ZIP entry length mismatch for {entry.FullName}: {entry.Length} != {sourceLength}.");
            }

            using var stream = entry.Open();
            var entryHash = Convert.ToHexString(SHA256.HashData(stream));
            var sourceHash = Sha256(expectedFile.SourcePath);
            if (!string.Equals(entryHash, sourceHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"ZIP entry hash mismatch for {entry.FullName}.");
            }
        }

        Console.WriteLine("PASS: ZIP has exactly two top-level files and every entry length/hash matches staging.");
    }

    private static void AddDeterministicZipEntry(ZipArchive archive, string sourcePath, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var source = File.OpenRead(sourcePath);
        using var destination = entry.Open();
        source.CopyTo(destination);
    }

    private static string[] FindTestProjects(BuildContext context)
    {
        return EnumerateRepositoryFiles(context)
            .Where(path => string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(IsTestProject)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsTestProject(string path)
    {
        var document = XDocument.Load(path);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "IsTestProject")
            .Any(element => string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCrossPlatformNet8TestProject(string path)
    {
        var document = XDocument.Load(path);
        var frameworks = document.Descendants()
            .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .SelectMany(element => element.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        return frameworks.Contains("net8.0", StringComparer.OrdinalIgnoreCase) &&
               !frameworks.Any(framework => framework.Contains("-windows", StringComparison.OrdinalIgnoreCase));
    }

    private static void RunDotnet(BuildContext context, IReadOnlyList<string> arguments) =>
        RunProcess(context.Root, GetDotnetHost(), arguments);

    private static string GetDotnetHost() =>
        Environment.GetEnvironmentVariable("PACKAGE_DOTNET_HOST")
        ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? "dotnet";

    private static void RunProcess(string workingDirectory, string executable, IReadOnlyList<string> arguments)
    {
        Console.WriteLine("> " + Path.GetFileName(executable) + " " + string.Join(" ", arguments.Select(DisplayArgument)));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {executable}.");
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}.");
        }
    }

    private static string Capture(string workingDirectory, string executable, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} failed: {standardError.Trim()}");
        }

        return standardOutput;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void RequireFile(BuildContext context, string relativePath)
    {
        var path = ResolveInsideRoot(context.Root, relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required repository file is missing: {relativePath}", path);
        }
    }

    private static string ResolveInsideRoot(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Package configuration path must be relative: {relativePath}");
        }

        var result = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!result.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidDataException($"Package configuration path escapes the repository: {relativePath}");
        }

        return result;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }

    private static int LineNumber(string text, int index)
    {
        var count = 1;
        var length = Math.Clamp(index, 0, text.Length);
        for (var current = 0; current < length; current++)
        {
            if (text[current] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static string DisplayArgument(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    private static void WriteHeading(string text)
    {
        Console.WriteLine();
        Console.WriteLine($"== {text} ==");
    }

    private sealed record BuildContext(
        string Root,
        string ConfigPath,
        PackageConfig Config,
        string Version,
        string SdkVersion);

    private sealed record TestSummary(
        TestExecution[] Executed,
        string[] SkippedAsWindowsOnly,
        string[] CrossBuiltOnLinux);

    private sealed record TestExecution(
        string Project,
        int Total,
        int Executed,
        int Passed,
        int Skipped);

    private sealed record ExpectedArchiveFile(string EntryName, string SourcePath);

    private sealed record PackageResult(string ArchivePath, string ExecutableSha256, string ArchiveSha256);
}

internal sealed class PackageConfig
{
    public int SchemaVersion { get; init; }
    public ProductConfig Product { get; init; } = new();
    public PathConfig Paths { get; init; } = new();
    public NetworkConfig Network { get; init; } = new();
    public ScanConfig Scan { get; init; } = new();
}

internal sealed class ProductConfig
{
    public string DisplayName { get; init; } = "";
    public string ExecutableFileName { get; init; } = "";
    public string FriendGuideFileName { get; init; } = "";
    public string ArchiveFileName { get; init; } = "";
    public string VersionPropertiesPath { get; init; } = "";
    public string VersionPlaceholder { get; init; } = "";
    public string ExecutableHashPlaceholder { get; init; } = "";
}

internal sealed class PathConfig
{
    public string WindowsProject { get; init; } = "";
    public string ApplicationManifest { get; init; } = "";
    public string TestRoot { get; init; } = "";
    public string Readme { get; init; } = "";
    public string WindowsWorkflow { get; init; } = "";
    public string FriendGuideTemplate { get; init; } = "";
    public string WorkDirectory { get; init; } = "";
    public string OutputDirectory { get; init; } = "";
}

internal sealed class NetworkConfig
{
    public string[] RuntimeHosts { get; init; } = [];
    public string[] UserInitiatedBrowserHosts { get; init; } = [];
    public string[] DocumentationHosts { get; init; } = [];
    public string[] NonFetchingMarkupNamespaceHosts { get; init; } = [];
}

internal sealed class ScanConfig
{
    public string[] ExcludedDirectories { get; init; } = [];
    public string[] SecretRegexes { get; init; } = [];
    public string[] DeveloperPathRegexes { get; init; } = [];
    public string[] RawOverlayFieldNames { get; init; } = [];
    public string[] SyntheticGameNamePrefixes { get; init; } = [];
    public string[] SyntheticTagLines { get; init; } = [];
    public string[] SyntheticIdentityFactoryFiles { get; init; } = [];
    public string[] ForbiddenArtifactNameTokens { get; init; } = [];
}
