/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class NeoForgeArtifactResolverTests
{
    [Theory]
    [InlineData("https://maven.neoforged.net/releases/net/neoforged/neoforge/21.11.48/neoforge-21.11.48-installer.jar", "https://bmclapi2.bangbang93.com/neoforge/version/21.11.48/download/installer.jar")]
    [InlineData("https://maven.neoforged.net/releases/net/neoforged/neoforge/26.1.0.0-alpha.12+snapshot-7/neoforge-26.1.0.0-alpha.12+snapshot-7-installer.jar", "https://bmclapi2.bangbang93.com/neoforge/version/26.1.0.0-alpha.12+snapshot-7/download/installer.jar")]
    [InlineData("https://maven.neoforged.net/releases/net/neoforged/neoforge/21.11.48/neoforge-21.11.48-installer.jar.sha1", "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/21.11.48/neoforge-21.11.48-installer.jar.sha1")]
    [InlineData("https://maven.neoforged.net/releases/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar", "https://bmclapi2.bangbang93.com/maven/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar")]
    public void InstallerMirrorUsesDedicatedEndpointOnlyForModernInstaller(string originalUrl, string mirrorUrl)
    {
        var requests = MinecraftDownloadSourceResolver.EnumerateRequests(originalUrl, DownloadSourcePreference.BmclApi, "NeoForge").ToArray();
        Assert.Equal(mirrorUrl, requests[0].ActualUrl);
        Assert.Equal(originalUrl, requests[1].ActualUrl);
    }

    [Theory]
    [InlineData(
        "1.20.4",
        "20.4.237",
        "20.4.237",
        "20.4.237",
        "neoforge",
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/20.4.237/neoforge-20.4.237-installer.jar",
        "neoforge-20.4.237",
        "net.neoforged:neoforge:20.4.237")]
    public void ResolveInstallerNormalizesSupportedCoordinates(
        string minecraftVersion,
        string requestedLoaderVersion,
        string expectedLoaderVersion,
        string expectedCoordinate,
        string expectedArtifactName,
        string expectedUrl,
        string expectedVersionId,
        string expectedRuntimeLibrary)
    {
        var artifact = NeoForgeArtifactResolver.ResolveInstaller(
            minecraftVersion,
            requestedLoaderVersion);

        Assert.Equal(expectedLoaderVersion, artifact.LoaderVersion);
        Assert.Equal(expectedCoordinate, artifact.Coordinate);
        Assert.Equal(expectedArtifactName, artifact.ArtifactName);
        Assert.Equal(expectedUrl, artifact.Url);
        Assert.Equal(expectedVersionId, artifact.ExpectedVersionId);
        Assert.Equal(expectedRuntimeLibrary, artifact.RuntimeLibraryCoordinate);
    }

    [Theory]
    [InlineData("1.20.1", NeoForgeArtifactResolver.Legacy1201MetadataUrl, "47.1.106,47.1.105-beta")]
    [InlineData("1.21.1", NeoForgeArtifactResolver.ModernMetadataUrl, "21.1.2")]
    [InlineData("26.1-snapshot-7", NeoForgeArtifactResolver.ModernMetadataUrl, "26.1.0.0-alpha.12+snapshot-7")]
    public async Task ProviderUsesResolvedCatalogAndReturnsOnlyCompatibleVersions(
        string minecraftVersion,
        string expectedMetadataUrl,
        string expectedVersions)
    {
        var handler = new CatalogHandler();
        var provider = new NeoForgeLoaderProvider(new HttpClient(handler));

        var versions = await provider.GetLoaderVersionsAsync(
            minecraftVersion,
            DownloadSourcePreference.Official);

        Assert.Equal(expectedVersions.Split(','), versions.Select(version => version.Version));
        Assert.Equal([expectedMetadataUrl], handler.RequestUris.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task UnsupportedMinecraftVersionReturnsEmptyWithoutMetadataRequest()
    {
        var handler = new CatalogHandler();
        var provider = new NeoForgeLoaderProvider(new HttpClient(handler));

        var versions = await provider.GetLoaderVersionsAsync(
            "classic",
            DownloadSourcePreference.Official);

        Assert.Empty(versions);
        Assert.Empty(handler.RequestUris);
    }

    [Theory]
    [InlineData(
        "1.20.4",
        "1.20.4-20.4.237",
        "20.4.237",
        "neoforge",
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/20.4.237/neoforge-20.4.237-installer.jar")]
    public void ServerUsesSharedNeoForgeArtifactResolution(
        string minecraftVersion,
        string loaderVersion,
        string expectedCoordinate,
        string expectedArtifactName,
        string expectedUrl)
    {
        var artifact = ServerRuntimeInstaller.ResolveForgeLikeInstallerArtifact(new PreparedModpack
        {
            Environment = ModpackInstallEnvironment.Server,
            MinecraftVersion = minecraftVersion,
            Loader = LoaderKind.NeoForge,
            LoaderVersion = loaderVersion
        });

        Assert.Equal(expectedCoordinate, artifact.Coordinate);
        Assert.Equal(expectedArtifactName, artifact.ArtifactName);
        Assert.Equal(expectedUrl, artifact.Url);
        Assert.Equal("NeoForge", artifact.Category);
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            var content = request.RequestUri!.AbsoluteUri switch
            {
                NeoForgeArtifactResolver.Legacy1201MetadataUrl => """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <metadata>
                      <versioning>
                        <versions>
                          <version>1.20.1-47.1.105-beta</version>
                          <version>1.20.1-47.1.106</version>
                        </versions>
                      </versioning>
                    </metadata>
                    """,
                NeoForgeArtifactResolver.ModernMetadataUrl => """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <metadata>
                      <versioning>
                        <versions>
                          <version>0.25w14craftmine.5-beta</version>
                          <version>21.0.166-beta</version>
                          <version>21.0.167</version>
                          <version>21.1.2</version>
                          <version>26.1.0.0-alpha.12+snapshot-7</version>
                          <version>26.1.0.0-alpha.15+pre-3</version>
                          <version>26.1.0.0+rc-1</version>
                          <version>26.1.0.1-beta</version>
                          <version>26.1.0.20</version>
                          <version>26.1.2.70</version>
                          <version>26.2.0.35</version>
                        </versions>
                      </versioning>
                    </metadata>
                    """,
                _ => throw new InvalidOperationException(
                    $"Unexpected request: {request.RequestUri.AbsoluteUri}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(content)
            });
        }
    }
}
