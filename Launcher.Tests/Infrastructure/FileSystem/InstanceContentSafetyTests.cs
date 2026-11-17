using System.IO.Compression;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure;
using Launcher.Infrastructure.FileSystem;
using Launcher.Infrastructure.Persistence;
using Launcher.Infrastructure.Modpacks;
using Launcher.Infrastructure.CurseForge;

namespace Launcher.Tests.Infrastructure.FileSystem;

public sealed class InstanceContentSafetyTests : TestTempDirectory
{
    [Fact]
    public async Task CanceledModReplacementPreservesExistingContentAndCleansStaging()
    {
        var directory = Path.Combine(TempRoot, "A");
        var mods = Directory.CreateDirectory(Path.Combine(directory, "mods")).FullName;
        var target = Path.Combine(mods, "mod.jar");
        await File.WriteAllTextAsync(target, "keep");
        var source = Path.Combine(TempRoot, "mod.jar");
        await File.WriteAllTextAsync(source, "replacement");
        var instance = new GameInstance { InstanceDirectory = directory };
        InstanceDirectoryOwnership.Observe(instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateServices().Mods.ImportAsync(instance, source, true, cancellation.Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.EnumerateFiles(mods));
    }

    [Fact]
    public async Task ExportRejectsReplacedInstanceAndPreservesExistingOutput()
    {
        var directory = Directory.CreateDirectory(Path.Combine(TempRoot, "A")).FullName;
        var instance = new GameInstance { InstanceDirectory = directory, MinecraftVersion = "1.20.1" };
        InstanceDirectoryOwnership.Observe(instance);
        var services = CreateServices();
        var exporter = new ModpackExportService(services.Mods, services.Packs, services.Shaders, new MissingKeyResolver());
        var output = Path.Combine(TempRoot, "pack.mrpack");
        var request = new ModpackExportRequest(instance, ModpackExportKind.Modrinth, "Pack", "Author", "1.0", output,
            false, false, false, false, false);
        Assert.True((await exporter.ExportAsync(request)).IsSuccess);
        using (var zip = ZipFile.OpenRead(output)) Assert.NotNull(zip.GetEntry("modrinth.index.json"));
        var original = await File.ReadAllBytesAsync(output);
        Directory.Move(directory, directory + "-moved");
        Directory.CreateDirectory(directory);
        var rejected = await exporter.ExportAsync(request);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(ModpackExportFailureReason.FileSystemError, rejected.FailureReason);
        Assert.Equal(original, await File.ReadAllBytesAsync(output));
        Assert.Empty(Directory.EnumerateFiles(TempRoot, "*.tmp"));
    }

    private sealed class MissingKeyResolver : ICurseForgeApiKeyResolver
    {
        public Task<string?> TryResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    [Theory]
    [InlineData("mods", false)]
    [InlineData("mods", true)]
    [InlineData("saves", false)]
    [InlineData("saves", true)]
    [InlineData("resourcepacks", false)]
    [InlineData("resourcepacks", true)]
    [InlineData("shaderpacks", false)]
    [InlineData("shaderpacks", true)]
    public async Task ImportDoesNotRecreateOrWriteAnObsoleteInstanceDirectory(string kind, bool replace)
    {
        var directory = Path.Combine(TempRoot, "A");
        Directory.CreateDirectory(directory);
        var instance = new GameInstance { InstanceDirectory = directory };
        InstanceDirectoryOwnership.Observe(instance);
        var services = CreateServices();
        var archive = Path.Combine(TempRoot, "source.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("world/level.dat").Open());
            writer.Write("world");
        }
        var jar = Path.Combine(TempRoot, "source.jar");
        File.Copy(archive, jar);
        Directory.Move(directory, directory + "-moved");
        if (replace) Directory.CreateDirectory(directory);
        try
        {
            switch (kind)
            {
                case "mods": await services.Mods.ImportAsync(instance, jar); break;
                case "saves": await services.Saves.ImportFromArchiveAsync(instance, archive); break;
                case "resourcepacks": await services.Packs.ImportAsync(instance, archive); break;
                default: await services.Shaders.ImportAsync(instance, archive); break;
            }
        }
        catch (Exception e) when (e is IOException or GameInstanceMutationConflictException) { }
        Assert.Equal(replace, Directory.Exists(directory));
        Assert.False(Directory.Exists(Path.Combine(directory, kind)));
        Assert.True(Directory.Exists(directory + "-moved"));
    }

    [Theory]
    [InlineData("mods", true)]
    [InlineData("mods", false)]
    [InlineData("saves", true)]
    [InlineData("saves", false)]
    [InlineData("resourcepacks", true)]
    [InlineData("resourcepacks", false)]
    [InlineData("shaderpacks", true)]
    [InlineData("shaderpacks", false)]
    public async Task ReloadAfterDirectoryOrContentReplacementDoesNotAuthorizeOldItems(string kind, bool replaceWholeInstance)
    {
        var directory = Path.Combine(TempRoot, "A");
        var content = Path.Combine(directory, kind);
        Directory.CreateDirectory(content);
        var path = Path.Combine(content, kind == "saves" ? "world" : kind == "mods" ? "item.jar" : "item.zip");
        if (kind == "saves") Directory.CreateDirectory(path);
        else using (ZipFile.Open(path, ZipArchiveMode.Create)) { }
        var creation = File.GetCreationTimeUtc(path);
        var write = File.GetLastWriteTimeUtc(path);
        var instance = new GameInstance { InstanceDirectory = directory };
        InstanceDirectoryOwnership.Observe(instance);
        var services = CreateServices();
        async Task<object> Load(GameInstance target) => kind switch
        {
            "mods" => Assert.Single(await services.Mods.GetModsAsync(target)),
            "saves" => Assert.Single(await services.Saves.GetSavesAsync(target)),
            "resourcepacks" => Assert.Single(await services.Packs.GetResourcePacksAsync(target)),
            _ => Assert.Single(await services.Shaders.GetShaderPacksAsync(target))
        };
        var old = await Load(instance);
        var previousPath = replaceWholeInstance ? Path.Combine(directory + "-moved", kind, Path.GetFileName(path)) : Path.Combine(TempRoot, "previous");
        if (replaceWholeInstance) Directory.Move(directory, directory + "-moved");
        else if (kind == "saves") Directory.Move(path, previousPath);
        else File.Move(path, previousPath);
        Directory.CreateDirectory(content);
        if (kind == "saves") Directory.CreateDirectory(path);
        else File.Copy(previousPath, path);
        if (kind == "saves")
        {
            Directory.SetCreationTimeUtc(path, creation);
            Directory.SetLastWriteTimeUtc(path, write);
        }
        else
        {
            File.SetCreationTimeUtc(path, creation);
            File.SetLastWriteTimeUtc(path, write);
        }
        var replacement = replaceWholeInstance ? new GameInstance { InstanceDirectory = directory } : instance;
        InstanceDirectoryOwnership.Observe(replacement);
        var current = await Load(replacement);
        Assert.NotSame(old, current);
        Task Delete(object item) => item switch
        {
            LocalMod mod => services.Mods.DeleteAsync(mod),
            LocalSave save => services.Saves.DeleteAsync(save),
            LocalResourcePack pack => services.Packs.DeleteAsync(pack),
            LocalShaderPack shader => services.Shaders.DeleteAsync(shader),
            _ => throw new InvalidOperationException()
        };
        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => Delete(old));
        if (old is LocalMod oldMod)
            await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => services.Mods.SetEnabledAsync(oldMod, false));
        Assert.True(File.Exists(path) || Directory.Exists(path));
        await Delete(current);
        Assert.False(File.Exists(path) || Directory.Exists(path));
    }

    private (ModService Mods, LocalSaveService Saves, LocalResourcePackService Packs, LocalShaderPackService Shaders) CreateServices()
    {
        var paths = new LauncherPathProvider(Path.Combine(TempRoot, "data"));
        var deletion = new TestDeletion();
        return (new ModService(paths, userFileDeletionService: deletion), new LocalSaveService(paths, userFileDeletionService: deletion),
            new LocalResourcePackService(paths, userFileDeletionService: deletion), new LocalShaderPackService(userFileDeletionService: deletion));
    }

    private sealed class TestDeletion : IUserFileDeletionService
    {
        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
    }
}
