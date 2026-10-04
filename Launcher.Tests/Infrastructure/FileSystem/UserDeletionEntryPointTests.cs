/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Services;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Launcher.Infrastructure;
using Launcher.Infrastructure.Accounts;
using Launcher.Infrastructure.FileSystem;

namespace Launcher.Tests.Infrastructure.FileSystem;

public sealed class UserDeletionEntryPointTests : TestTempDirectory
{
    [Fact]
    public async Task InstanceContentServicesUseUserFileDeletionService()
    {
        Directory.CreateDirectory(TempRoot);
        var deletion = new RecordingUserFileDeletionService();
        var modPath = CreateFile("mod.jar");
        var savePath = Path.Combine(TempRoot, "world");
        Directory.CreateDirectory(savePath);
        var resourcePackPath = CreateFile("resources.zip");
        var shaderPackPath = CreateFile("shaders.zip");

        await new ModService(
                new LauncherPathProvider(TempRoot),
                userFileDeletionService: deletion)
            .DeleteAsync(new LocalMod { FileName = "mod.jar", FullPath = modPath });
        await new LocalSaveService(
                new LauncherPathProvider(TempRoot),
                userFileDeletionService: deletion)
            .DeleteAsync(new LocalSave { Name = "world", DirectoryName = "world", FullPath = savePath });
        await new LocalResourcePackService(
                new LauncherPathProvider(TempRoot),
                userFileDeletionService: deletion)
            .DeleteAsync(new LocalResourcePack { Name = "resources", FullPath = resourcePackPath });
        await new LocalShaderPackService(userFileDeletionService: deletion)
            .DeleteAsync(new LocalShaderPack { Name = "shaders", FullPath = shaderPackPath });

        Assert.Equal(
            [modPath, resourcePackPath, shaderPackPath],
            deletion.Files);
        Assert.Equal([savePath], deletion.Directories);
    }

    [Fact]
    public void ClearLauncherBackgroundImagesUsesUserFileDeletionService()
    {
        Directory.CreateDirectory(TempRoot);
        var deletion = new RecordingUserFileDeletionService();
        var catalog = new LauncherBackgroundImageCatalog(
            new LauncherPathProvider(TempRoot, TempRoot),
            deletion);
        Directory.CreateDirectory(catalog.DirectoryPath);
        var first = Path.Combine(catalog.DirectoryPath, "first.png");
        var second = Path.Combine(catalog.DirectoryPath, "second.jpg");
        var ignored = Path.Combine(catalog.DirectoryPath, "ignored.txt");
        File.WriteAllText(first, string.Empty);
        File.WriteAllText(second, string.Empty);
        File.WriteAllText(ignored, string.Empty);

        catalog.ClearImages();

        Assert.Equal([first, second], deletion.Files);
        Assert.True(File.Exists(ignored));
    }

    [Fact]
    public async Task SkinLibraryDeletionUsesUserFileDeletionService()
    {
        Directory.CreateDirectory(TempRoot);
        var deletion = new RecordingUserFileDeletionService();
        var cacheRoot = Path.Combine(TempRoot, "skins");
        var source = CreateFile("source.png", [1, 2, 3, 4]);
        var cache = new AccountSkinCacheService(
            new HttpClient(new RejectNetworkHandler()),
            cacheRoot,
            deletion);
        var account = new LauncherAccount
        {
            Id = "offline",
            DisplayName = "Offline",
            Uuid = "offline-uuid",
            Kind = LauncherAccountKind.Offline
        };
        var skin = await cache.ImportSkinAsync(
            account,
            source,
            MinecraftSkinModel.Classic,
            CancellationToken.None);

        await cache.DeleteSkinAsync(account, skin, CancellationToken.None);

        var deletedPath = Assert.Single(deletion.Files);
        Assert.Contains("_shared-library", deletedPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
    }

    private string CreateFile(string name, byte[]? contents = null)
    {
        var path = Path.Combine(TempRoot, name);
        File.WriteAllBytes(path, contents ?? [1]);
        return path;
    }

    private sealed class RecordingUserFileDeletionService : IUserFileDeletionService
    {
        public List<string> Files { get; } = [];
        public List<string> Directories { get; } = [];

        public void DeleteFile(string path)
        {
            Files.Add(Path.GetFullPath(path));
            File.Delete(path);
        }

        public void DeleteDirectory(string path)
        {
            Directories.Add(Path.GetFullPath(path));
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test must not access the network.");
    }
}
