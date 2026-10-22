using Launcher.App.Models;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Settings;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.ViewModels.Settings;

public sealed class InfoSettingsUpdateMarkdownTests
{
    [Fact]
    public void MarkdownLinkCommandUsesExternalLinkService()
    {
        var links = new RecordingExternalLinkService(openResult: true);
        var messages = new RecordingFloatingMessageService();
        using var coordinator = CreateCoordinator();
        var viewModel = CreateViewModel(coordinator, links, messages);

        viewModel.OpenUpdateReleaseNotesLinkCommand.Execute("https://example.com/release");

        Assert.Equal("https://example.com/release", links.LastUrl);
        Assert.Empty(messages.Messages);
    }

    [Fact]
    public void MarkdownLinkFailureShowsFriendlyMessage()
    {
        var links = new RecordingExternalLinkService(openResult: false);
        var messages = new RecordingFloatingMessageService();
        using var coordinator = CreateCoordinator();
        var viewModel = CreateViewModel(coordinator, links, messages);

        viewModel.OpenUpdateReleaseNotesLinkCommand.Execute("file:///C:/unsafe.txt");

        Assert.Single(messages.Messages);
    }

#if DEBUG
    [Fact]
    public void DebugPreviewUsesRawMarkdownAndOpensDialog()
    {
        using var coordinator = CreateCoordinator();
        var viewModel = CreateViewModel(
            coordinator,
            new RecordingExternalLinkService(openResult: true),
            new RecordingFloatingMessageService());

        viewModel.PreviewUpdateDialogCommand.Execute(null);

        Assert.True(viewModel.IsUpdateAvailableDialogOpen);
        Assert.True(viewModel.HasUpdateReleaseNotes);
        Assert.StartsWith("# 1.0.0-preview", viewModel.UpdateReleaseNotesMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.Dialog_UpdateAvailableTitle, viewModel.UpdateReleaseNotesMarkdown, StringComparison.Ordinal);
        Assert.Contains($"## {Strings.Dialog_UpdatePreviewNewFeaturesTitle}", viewModel.UpdateReleaseNotesMarkdown, StringComparison.Ordinal);
        Assert.Contains($"## {Strings.Dialog_UpdatePreviewFixesTitle}", viewModel.UpdateReleaseNotesMarkdown, StringComparison.Ordinal);
        Assert.Contains($"- {Strings.Dialog_UpdatePreviewNewFeatureOne}", viewModel.UpdateReleaseNotesMarkdown, StringComparison.Ordinal);
    }
#endif

    private static InfoSettingsViewModel CreateViewModel(
        SettingsPersistenceCoordinator coordinator,
        IExternalLinkService links,
        IFloatingMessageService messages) =>
        new(
            coordinator,
            new RecordingStatusService(),
            messages,
            links,
            new StubLauncherUpdateService(),
            new StubLauncherSelfUpdateService(),
            new StubApplicationExitService(),
            new StubReferenceProjectCatalog(),
            NullLogger<InfoSettingsViewModel>.Instance);

    private static SettingsPersistenceCoordinator CreateCoordinator() =>
        new(
            new StubSettingsService(),
            new RecordingStatusService(),
            NullLogger<SettingsPersistenceCoordinator>.Instance);

    private sealed class StubSettingsService : ISettingsService
    {
        public Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LauncherSettings());

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingStatusService : IStatusService
    {
        public event Action<string>? MessageReported
        {
            add { }
            remove { }
        }

        public void Report(string message)
        {
        }
    }

    private sealed class RecordingFloatingMessageService : IFloatingMessageService
    {
        public List<string> Messages { get; } = [];

        public event Action<FloatingMessageRequest>? MessageRequested
        {
            add { }
            remove { }
        }

        public void Show(string message) => Messages.Add(message);

        public void ShowDragHint(object source, string message)
        {
        }

        public void ClearDragHint(object source)
        {
        }

        public void ClearDragHint()
        {
        }
    }

    private sealed class RecordingExternalLinkService(bool openResult) : IExternalLinkService
    {
        public string? LastUrl { get; private set; }

        public bool TryOpen(string url)
        {
            LastUrl = url;
            return openResult;
        }
    }

    private sealed class StubLauncherUpdateService : ILauncherUpdateService
    {
        public Task<LauncherUpdateCheckResult> CheckForUpdatesAsync(
            string currentVersion,
            LauncherUpdateChannel channel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(LauncherUpdateCheckResult.Latest(currentVersion));
    }

    private sealed class StubLauncherSelfUpdateService : ILauncherSelfUpdateService
    {
        public Task<LauncherSelfUpdateStartResult> StartUpdateAsync(
            LauncherUpdateInfo update,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(LauncherSelfUpdateStartResult.Failed());
    }

    private sealed class StubApplicationExitService : IApplicationExitService
    {
        public void Shutdown()
        {
        }
    }

    private sealed class StubReferenceProjectCatalog : IInfoReferenceProjectCatalog
    {
        public IReadOnlyList<InfoReferenceProjectItem> GetProjects() => [];
    }
}
