/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Modpacks;

internal sealed record ForgeLikeServerInstallerArtifact(
    string Coordinate,
    string ArtifactName,
    string Url,
    string Category);

internal sealed class ServerRuntimeInstaller : IServerRuntimeInstaller
{
    private const string FabricLauncherJar = "fabric-server-launch.jar";
    private const string QuiltLauncherJar = "quilt-server-launch.jar";
    private readonly HttpClient httpClient;
    private readonly ISettingsService settingsService;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly ILoaderInstallerJavaRuntimeResolver? javaRuntimeResolver;
    private readonly IForgeInstallerRunner forgeInstallerRunner;
    private readonly ILogger<ServerRuntimeInstaller> logger;

    public ServerRuntimeInstaller(
        ISettingsService settingsService,
        LoaderInstallerJavaRuntimeResolver javaRuntimeResolver,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<ServerRuntimeInstaller>? logger = null)
        : this(
            MinecraftHttpClientFactory.CreateTransportClient(),
            settingsService,
            javaRuntimeResolver,
            new ForgeInstallerRunner(),
            downloadSpeedLimitState,
            logger)
    {
    }

    internal ServerRuntimeInstaller(
        HttpClient httpClient,
        ISettingsService settingsService,
        ILoaderInstallerJavaRuntimeResolver? javaRuntimeResolver = null,
        IForgeInstallerRunner? forgeInstallerRunner = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<ServerRuntimeInstaller>? logger = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.settingsService = settingsService;
        this.javaRuntimeResolver = javaRuntimeResolver;
        this.forgeInstallerRunner = forgeInstallerRunner ?? new ForgeInstallerRunner();
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerRuntimeInstaller>.Instance;
    }

    public async Task InstallAsync(
        PreparedModpack modpack,
        string targetDirectory,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        if (string.IsNullOrWhiteSpace(modpack.MinecraftVersion))
            throw new ModpackImportException(ModpackImportFailureReason.InvalidManifest, "Minecraft version is missing.");

        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var preference = settings.DownloadSourcePreference;
        var speedLimit = settings.DownloadSpeedLimitMbPerSecond;
        Directory.CreateDirectory(targetDirectory);
        MinecraftPathGuard.EnsureNoReparsePoints(targetDirectory, targetDirectory, "Server runtime directory");

        progress?.Report(new LauncherProgress(ImportProgressStages.InstallingMinecraftBase, string.Empty));
        var versionJson = await VanillaVersionMetadataClient.DownloadVersionJsonAsync(
            httpClient,
            modpack.MinecraftVersion,
            preference,
            speedLimit,
            downloadSpeedLimitState,
            logger,
            cancellationToken).ConfigureAwait(false);
        var serverJarName = $"minecraft_server.{modpack.MinecraftVersion}.jar";
        await DownloadServerJarAsync(
            versionJson,
            targetDirectory,
            serverJarName,
            preference,
            speedLimit,
            progress,
            cancellationToken).ConfigureAwait(false);

        var launchJar = serverJarName;
        switch (modpack.Loader)
        {
            case LoaderKind.Vanilla:
                break;
            case LoaderKind.Fabric:
                launchJar = await InstallProfileLoaderAsync(
                    modpack,
                    targetDirectory,
                    serverJarName,
                    isQuilt: false,
                    preference,
                    speedLimit,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                break;
            case LoaderKind.Quilt:
                launchJar = await InstallProfileLoaderAsync(
                    modpack,
                    targetDirectory,
                    serverJarName,
                    isQuilt: true,
                    preference,
                    speedLimit,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                break;
            case LoaderKind.Forge:
            case LoaderKind.NeoForge:
                await InstallForgeLikeAsync(
                    modpack,
                    targetDirectory,
                    preference,
                    speedLimit,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                launchJar = ResolveForgeLikeLaunchJar(targetDirectory, serverJarName);
                break;
            default:
                throw new ModpackImportException(
                    ModpackImportFailureReason.UnsupportedLoader,
                    $"Unsupported server loader: {modpack.Loader}");
        }

        var log4ShellArguments = ServerLog4ShellMitigation.Apply(versionJson, modpack.Loader, targetDirectory);
        WriteLaunchScriptsIfMissing(targetDirectory, launchJar, log4ShellArguments, modpack);
    }

    private async Task DownloadServerJarAsync(
        JsonObject versionJson,
        string targetDirectory,
        string serverJarName,
        DownloadSourcePreference preference,
        int speedLimit,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var url = VanillaVersionMetadataClient.GetServerJarUrl(versionJson);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ModpackImportException(
                ModpackImportFailureReason.InvalidManifest,
                "The selected Minecraft version has no dedicated server download.");
        }
        await DownloadArtifactAsync(
            url,
            Path.Combine(targetDirectory, serverJarName),
            VanillaVersionMetadataClient.GetServerJarSha1(versionJson),
            VanillaVersionMetadataClient.GetServerJarSize(versionJson),
            targetDirectory,
            "Mojang",
            preference,
            speedLimit,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> InstallProfileLoaderAsync(
        PreparedModpack modpack,
        string targetDirectory,
        string serverJarName,
        bool isQuilt,
        DownloadSourcePreference preference,
        int speedLimit,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modpack.LoaderVersion))
        {
            throw new ModpackImportException(
                ModpackImportFailureReason.InvalidManifest,
                $"{modpack.Loader} loader version is missing.");
        }
        progress?.Report(new LauncherProgress(ImportProgressStages.InstallingLoader, string.Empty));
        var profileUrl = isQuilt
            ? $"https://meta.quiltmc.org/v3/versions/loader/{modpack.MinecraftVersion}/{modpack.LoaderVersion}/server/json"
            : $"https://meta.fabricmc.net/v2/versions/loader/{modpack.MinecraftVersion}/{modpack.LoaderVersion}/server/json";
        var executor = CreateExecutor(speedLimit, DownloadConcurrencyCategory.Metadata);
        var profile = await executor.ExecuteAsync(
            profileUrl,
            preference,
            isQuilt ? "Quilt" : "Fabric",
            async (context, token) =>
            {
                await using var source = await context.Response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var node = await JsonNode.ParseAsync(source, cancellationToken: token).ConfigureAwait(false);
                return node as JsonObject
                    ?? throw new DownloadContentValidationException("Server loader profile is not a JSON object.");
            },
            cancellationToken).ConfigureAwait(false);

        var artifacts = ResolveProfileLibraries(profile).ToArray();
        var librariesRoot = Path.Combine(targetDirectory, "libraries");
        await Parallel.ForEachAsync(
            artifacts,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 8 },
            async (artifact, token) =>
            {
                var destination = MinecraftPathGuard.EnsureWithin(
                    Path.Combine(librariesRoot, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    librariesRoot,
                    "Server loader library");
                await DownloadArtifactAsync(
                    artifact.Url,
                    destination,
                    artifact.Sha1,
                    artifact.Size,
                    librariesRoot,
                    artifact.ResourceCategory,
                    preference,
                    speedLimit,
                    progress,
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        var launchJarName = isQuilt ? QuiltLauncherJar : FabricLauncherJar;
        var profileMainClass = TryGetString(profile, "mainClass");
        if (isQuilt)
        {
            CreateProfileLauncherJar(
                Path.Combine(targetDirectory, launchJarName),
                TryGetString(profile, "launcherMainClass")
                    ?? "org.quiltmc.loader.impl.launch.server.QuiltServerLauncher",
                artifacts.Select(artifact => $"libraries/{artifact.RelativePath.Replace('\\', '/')}").ToArray(),
                launchMainClass: null);
        }
        else
        {
            await FabricServerLauncherJarBuilder.CreateAsync(
                Path.Combine(targetDirectory, launchJarName),
                "net.fabricmc.loader.launch.server.FabricServerLauncher",
                profileMainClass,
                artifacts,
                librariesRoot,
                modpack.LoaderVersion,
                cancellationToken).ConfigureAwait(false);
        }
        var propertiesName = isQuilt
            ? "quilt-server-launcher.properties"
            : "fabric-server-launcher.properties";
        var properties = isQuilt
            ? $"serverJar={serverJarName}{Environment.NewLine}"
            : $"serverJar={serverJarName}{Environment.NewLine}launch.mainClass={profileMainClass}{Environment.NewLine}";
        await File.WriteAllTextAsync(
            Path.Combine(targetDirectory, propertiesName),
            properties,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        return launchJarName;
    }

    private static IEnumerable<ManagedLibraryArtifact> ResolveProfileLibraries(JsonObject profile)
    {
        if (profile["libraries"] is not JsonArray libraries)
            yield break;
        foreach (var libraryNode in libraries)
        {
            if (libraryNode is not JsonObject library || !ManagedLibraryArtifactResolver.IsAllowed(library))
                continue;
            foreach (var artifact in ManagedLibraryArtifactResolver.EnumerateDownloads(library))
                yield return artifact;
        }
    }

    private static string TryGetString(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : string.Empty;

    private async Task InstallForgeLikeAsync(
        PreparedModpack modpack,
        string targetDirectory,
        DownloadSourcePreference preference,
        int speedLimit,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modpack.LoaderVersion))
        {
            throw new ModpackImportException(
                ModpackImportFailureReason.InvalidManifest,
                $"{modpack.Loader} loader version is missing.");
        }
        progress?.Report(new LauncherProgress(ImportProgressStages.InstallingLoader, string.Empty));
        var artifact = ResolveForgeLikeInstallerArtifact(modpack);
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"blockhelm-server-loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var installerPath = Path.Combine(tempDirectory, "installer.jar");
            var expectedSha1 = await LoaderInstallerChecksumResolver.ResolveRequiredSha1Async(
                httpClient,
                downloadSpeedLimitState,
                logger,
                artifact.Url,
                preference,
                artifact.Category,
                speedLimit,
                cancellationToken).ConfigureAwait(false);
            await DownloadArtifactAsync(
                artifact.Url,
                installerPath,
                expectedSha1,
                expectedSize: null,
                tempDirectory,
                artifact.Category,
                preference,
                speedLimit,
                progress,
                cancellationToken).ConfigureAwait(false);

            var artifactService = new LoaderInstallerArtifactService(
                httpClient,
                downloadSpeedLimitState: downloadSpeedLimitState,
                logger: logger);
            var installerPlan = await artifactService.ReadPlanAsync(
                installerPath,
                ModpackInstallEnvironment.Server,
                cancellationToken).ConfigureAwait(false);
            await artifactService.MaterializePrerequisitesAsync(
                installerPath,
                installerPlan,
                targetDirectory,
                preference,
                speedLimit,
                cancellationToken,
                speedMeter: SpeedMeterProgress.TryGet(progress)).ConfigureAwait(false);

            progress?.Report(new LauncherProgress(InstallProgressStages.CheckingJava, string.Empty));
            var resolver = javaRuntimeResolver
                ?? throw new InvalidOperationException("The loader installer Java runtime resolver is not configured.");
            var runtime = await resolver.ResolveAsync(
                new LoaderInstallerJavaRuntimeRequest(
                    modpack.MinecraftVersion,
                    Path.GetFileName(targetDirectory),
                    modpack.Loader,
                    modpack.LoaderVersion,
                    targetDirectory,
                    preference,
                    speedLimit,
                    progress),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new LauncherProgress(InstallProgressStages.RunningLoaderInstaller, string.Empty));
            await forgeInstallerRunner.RunServerInstallerAsync(
                runtime.ExecutablePath,
                installerPath,
                targetDirectory,
                cancellationToken).ConfigureAwait(false);
            await artifactService.MaterializeRuntimeLibrariesAsync(
                installerPath,
                installerPlan,
                targetDirectory,
                preference,
                speedLimit,
                cancellationToken,
                speedMeter: SpeedMeterProgress.TryGet(progress)).ConfigureAwait(false);
            await artifactService.ValidatePublishedArtifactsAsync(
                targetDirectory,
                installerPlan,
                cancellationToken).ConfigureAwait(false);
            ValidateForgeLikeInstall(targetDirectory, modpack, artifact);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task DownloadArtifactAsync(
        string url,
        string destination,
        string? expectedSha1,
        long? expectedSize,
        string managedRoot,
        string category,
        DownloadSourcePreference preference,
        int speedLimit,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
        var executor = CreateExecutor(speedLimit, DownloadConcurrencyCategory.Runtime);
        await executor.DownloadFileAsync(
            url,
            preference,
            category,
            destination,
            expectedSha1,
            expectedSize,
            cancellationToken,
            reportAttemptProgress: (_, transferred, total) => progress?.Report(new LauncherProgress(
                ModProgressStages.DownloadingFile,
                Path.GetFileName(destination),
                total is > 0 ? transferred * 100d / total.Value : null)),
            options: new DownloadFileOptions(ManagedRoot: managedRoot),
            speedMeter: SpeedMeterProgress.TryGet(progress)).ConfigureAwait(false);
    }

    private MinecraftDownloadRequestExecutor CreateExecutor(int speedLimit, DownloadConcurrencyCategory category) =>
        new(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(speedLimit, downloadSpeedLimitState),
            category: category);

    internal static ForgeLikeServerInstallerArtifact ResolveForgeLikeInstallerArtifact(PreparedModpack modpack)
    {
        ArgumentNullException.ThrowIfNull(modpack);
        var minecraftVersion = modpack.MinecraftVersion.Trim();
        var loaderVersion = modpack.LoaderVersion?.Trim()
            ?? throw new InvalidDataException("Loader version is missing.");
        if (modpack.Loader is LoaderKind.Forge)
        {
            var coordinate = AddMinecraftVersionPrefix(minecraftVersion, loaderVersion);
            return new ForgeLikeServerInstallerArtifact(
                coordinate,
                "forge",
                $"https://maven.minecraftforge.net/net/minecraftforge/forge/{coordinate}/forge-{coordinate}-installer.jar",
                "Forge");
        }

        if (modpack.Loader is not LoaderKind.NeoForge)
            throw new InvalidDataException($"Unsupported Forge-like loader: {modpack.Loader}.");

        var neoForgeArtifact = NeoForgeArtifactResolver.ResolveInstaller(minecraftVersion, loaderVersion);
        return new ForgeLikeServerInstallerArtifact(
            neoForgeArtifact.Coordinate,
            neoForgeArtifact.ArtifactName,
            neoForgeArtifact.Url,
            "NeoForge");
    }

    private static string AddMinecraftVersionPrefix(string minecraftVersion, string loaderVersion) =>
        loaderVersion.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
            ? loaderVersion
            : $"{minecraftVersion}-{loaderVersion}";

    private static string RemoveMinecraftVersionPrefix(string minecraftVersion, string loaderVersion) =>
        loaderVersion.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
            ? loaderVersion[(minecraftVersion.Length + 1)..]
            : loaderVersion;

    private static void ValidateForgeLikeInstall(
        string targetDirectory,
        PreparedModpack modpack,
        ForgeLikeServerInstallerArtifact artifact)
    {
        if (LoaderProvidesLaunchScripts(modpack))
        {
            ValidateOfficialLaunchScript(targetDirectory, "run.bat", "win_args.txt");
            ValidateOfficialLaunchScript(targetDirectory, "run.sh", "unix_args.txt");
            return;
        }

        var candidates = new[]
        {
            $"{artifact.ArtifactName}-{artifact.Coordinate}.jar",
            $"{artifact.ArtifactName}-{artifact.Coordinate}-universal.jar"
        };
        var launcherPath = candidates
            .Select(name => Path.Combine(targetDirectory, name))
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidDataException("The server loader installer did not produce the expected launcher JAR.");
        ValidateJar(launcherPath);
    }

    private static void ValidateOfficialLaunchScript(
        string targetDirectory,
        string scriptName,
        string expectedArgumentsFileName)
    {
        var scriptPath = Path.Combine(targetDirectory, scriptName);
        if (!File.Exists(scriptPath) || new FileInfo(scriptPath).Length == 0)
            throw new InvalidDataException($"The server loader installer did not produce {scriptName}.");

        var javaCommand = string.Join(
            Environment.NewLine,
            File.ReadLines(scriptPath).Where(line => line.Contains("java", StringComparison.OrdinalIgnoreCase)));
        var references = Regex.Matches(javaCommand, "@(?:\\\"(?<quoted>[^\\\"]+)\\\"|(?<plain>[^\\s\\\"]+))")
            .Select(match => match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["plain"].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (!references.Any(reference => reference.EndsWith(expectedArgumentsFileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"The server loader script {scriptName} does not reference {expectedArgumentsFileName}.");

        foreach (var reference in references)
        {
            if (Path.IsPathRooted(reference))
                throw new InvalidDataException($"The server loader script {scriptName} contains an unsafe arguments path.");
            var referencedPath = MinecraftPathGuard.EnsureWithin(
                Path.Combine(targetDirectory, reference.Replace('/', Path.DirectorySeparatorChar)),
                targetDirectory,
                "Server loader arguments file");
            MinecraftPathGuard.EnsureNoReparsePoints(targetDirectory, referencedPath, "Server loader arguments file");
            if (!File.Exists(referencedPath))
                throw new InvalidDataException($"The server loader script {scriptName} references a missing arguments file.");
            if (reference.EndsWith(expectedArgumentsFileName, StringComparison.OrdinalIgnoreCase)
                && new FileInfo(referencedPath).Length == 0)
            {
                throw new InvalidDataException($"The server loader arguments file {expectedArgumentsFileName} is empty.");
            }
            if (reference.EndsWith(expectedArgumentsFileName, StringComparison.OrdinalIgnoreCase))
                ValidateArgumentsFileLibraries(targetDirectory, referencedPath, expectedArgumentsFileName);
        }
    }

    private static void ValidateArgumentsFileLibraries(
        string targetDirectory,
        string argumentsPath,
        string argumentsFileName)
    {
        var references = Regex.Matches(
                File.ReadAllText(argumentsPath),
                "(?<path>libraries[\\\\/][^\\s\\\"';:]+?\\.jar)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups["path"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (references.Length == 0)
            throw new InvalidDataException($"The server loader arguments file {argumentsFileName} contains no library JAR references.");

        foreach (var reference in references)
        {
            var libraryPath = MinecraftPathGuard.EnsureWithin(
                Path.Combine(
                    targetDirectory,
                    reference.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)),
                targetDirectory,
                "Server loader library");
            MinecraftPathGuard.EnsureNoReparsePoints(targetDirectory, libraryPath, "Server loader library");
            if (!File.Exists(libraryPath))
                throw new InvalidDataException($"The server loader arguments file {argumentsFileName} references a missing library JAR.");
            ValidateJar(libraryPath, "server loader library JAR");
        }
    }

    private static void ValidateJar(string path, string kind = "server loader launcher JAR")
    {
        if (new FileInfo(path).Length == 0)
            throw new InvalidDataException($"The {kind} is empty.");
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0)
                throw new InvalidDataException($"The {kind} has no entries.");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The {kind} could not be read.", exception);
        }
    }

    private static string ResolveForgeLikeLaunchJar(string targetDirectory, string fallback)
    {
        var serverJar = Path.Combine(targetDirectory, "server.jar");
        if (File.Exists(serverJar))
            return "server.jar";
        return Directory.EnumerateFiles(targetDirectory, "*.jar", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .FirstOrDefault(name => name?.Contains("forge", StringComparison.OrdinalIgnoreCase) == true)
            ?? fallback;
    }

    private static void CreateProfileLauncherJar(
        string path,
        string launcherMainClass,
        IReadOnlyList<string> classPath,
        string? launchMainClass)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifestEntry = archive.CreateEntry("META-INF/MANIFEST.MF", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false), leaveOpen: false))
        {
            writer.NewLine = "\r\n";
            writer.WriteLine("Manifest-Version: 1.0");
            WriteManifestAttribute(writer, "Main-Class", launcherMainClass);
            if (classPath.Count > 0)
                WriteManifestAttribute(writer, "Class-Path", string.Join(' ', classPath));
            writer.WriteLine();
        }
        if (!string.IsNullOrWhiteSpace(launchMainClass))
        {
            var propertiesEntry = archive.CreateEntry("fabric-server-launch.properties", CompressionLevel.NoCompression);
            using var writer = new StreamWriter(propertiesEntry.Open(), new UTF8Encoding(false));
            writer.WriteLine($"launch.mainClass={launchMainClass}");
        }
    }

    private static void WriteManifestAttribute(TextWriter writer, string name, string value)
    {
        var line = $"{name}: {value}";
        const int maxLength = 70;
        writer.WriteLine(line[..Math.Min(maxLength, line.Length)]);
        for (var index = maxLength; index < line.Length; index += maxLength - 1)
            writer.WriteLine(" " + line.Substring(index, Math.Min(maxLength - 1, line.Length - index)));
    }

    private static void WriteLaunchScriptsIfMissing(
        string targetDirectory,
        string launchJar,
        string additionalJvmArguments,
        PreparedModpack modpack)
    {
        var knownScripts = new[] { "run.bat", "run.sh", "LaunchServer.bat", "LaunchServer.sh", "start.bat", "start.sh" };
        var hasKnownScript = knownScripts.Any(name => File.Exists(Path.Combine(targetDirectory, name)));
        var hasLoaderOwnedScript = LoaderProvidesLaunchScripts(modpack)
            && (File.Exists(Path.Combine(targetDirectory, "run.bat"))
                || File.Exists(Path.Combine(targetDirectory, "run.sh")));
        if (hasKnownScript
            && (string.IsNullOrWhiteSpace(additionalJvmArguments) || hasLoaderOwnedScript))
            return;
        var arguments = string.IsNullOrWhiteSpace(additionalJvmArguments)
            ? string.Empty
            : additionalJvmArguments + " ";
        File.WriteAllText(
            Path.Combine(targetDirectory, "LaunchServer.bat"),
            $"@echo off{Environment.NewLine}java -Xmx2G {arguments}-jar \"{launchJar}\" nogui{Environment.NewLine}pause{Environment.NewLine}",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(targetDirectory, "LaunchServer.sh"),
            $"#!/bin/sh\ncd \"$(dirname \"$0\")\"\njava -Xmx2G {arguments}-jar \"{launchJar}\" nogui\n",
            new UTF8Encoding(false));
    }

    private static bool LoaderProvidesLaunchScripts(PreparedModpack modpack)
    {
        if (modpack.Loader == LoaderKind.NeoForge)
            return true;
        if (modpack.Loader != LoaderKind.Forge || string.IsNullOrWhiteSpace(modpack.LoaderVersion))
            return false;

        var normalizedLoaderVersion = RemoveMinecraftVersionPrefix(
            modpack.MinecraftVersion,
            modpack.LoaderVersion.Trim());
        var majorText = normalizedLoaderVersion.Split('.', 2)[0];
        return int.TryParse(majorText, out var major) && major >= 37;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
