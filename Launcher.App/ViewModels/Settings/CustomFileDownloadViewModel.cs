/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Download;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.Settings;

public sealed partial class CustomFileDownloadViewModel : ObservableObject
{
    private readonly ICustomFileDownloadService downloadService;
    private readonly IFilePickerService filePickerService;
    private readonly IFloatingMessageService floatingMessageService;
    private readonly DownloadTasksPageViewModel downloadTasksPage;
    private readonly ILogger<CustomFileDownloadViewModel> logger;

    public CustomFileDownloadViewModel(
        ICustomFileDownloadService downloadService,
        IFilePickerService filePickerService,
        IFloatingMessageService floatingMessageService,
        DownloadTasksPageViewModel downloadTasksPage,
        ILogger<CustomFileDownloadViewModel>? logger = null)
    {
        this.downloadService = downloadService;
        this.filePickerService = filePickerService;
        this.floatingMessageService = floatingMessageService;
        this.downloadTasksPage = downloadTasksPage;
        this.logger = logger ?? NullLogger<CustomFileDownloadViewModel>.Instance;
    }

    [ObservableProperty]
    private bool isDialogOpen;

    [ObservableProperty]
    private string address = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddressValidationError))]
    private string addressValidationMessage = string.Empty;

    public bool HasAddressValidationError => !string.IsNullOrWhiteSpace(AddressValidationMessage);

    [RelayCommand]
    private void OpenDialog()
    {
        Address = string.Empty;
        AddressValidationMessage = string.Empty;
        IsDialogOpen = true;
    }

    [RelayCommand]
    private void Cancel()
    {
        IsDialogOpen = false;
        Address = string.Empty;
        AddressValidationMessage = string.Empty;
    }

    [RelayCommand]
    private void Download()
    {
        if (!TryNormalizeHttpAddress(Address, out var normalizedAddress, out var sourceUri))
        {
            AddressValidationMessage = Strings.Dialog_CustomFileDownloadAddressValidation;
            return;
        }

        var defaultFileName = ResolveDefaultFileName(sourceUri);
        var destinationPath = filePickerService.PickCustomDownloadDestination(defaultFileName);
        if (string.IsNullOrWhiteSpace(destinationPath))
            return;

        var normalizedDestination = Path.GetFullPath(destinationPath);
        var fileName = Path.GetFileName(normalizedDestination);
        var destinationDirectory = Path.GetDirectoryName(normalizedDestination) ?? string.Empty;

        IsDialogOpen = false;
        Address = string.Empty;
        AddressValidationMessage = string.Empty;

        var taskItem = downloadTasksPage.BeginTask(fileName, destinationDirectory);
        floatingMessageService.Show(string.Format(
            Strings.Status_CustomFileDownloadStartedFormat,
            fileName));
        taskItem.Report(new LauncherProgress(
            "CustomFileDownload",
            Strings.Status_CustomFileDownloadPreparing,
            0));
        var progress = taskItem.CreateProgress(value =>
            taskItem.Report(value with { Message = Strings.Status_CustomFileDownloading }));
        var operation = RunDownloadAsync(
            normalizedAddress,
            normalizedDestination,
            fileName,
            taskItem,
            progress);
        downloadTasksPage.TrackBackgroundTask(operation);
    }

    partial void OnAddressChanged(string value)
    {
        if (HasAddressValidationError)
            AddressValidationMessage = string.Empty;
    }

    private async Task RunDownloadAsync(
        string sourceUrl,
        string destinationPath,
        string fileName,
        DownloadTaskItem taskItem,
        IProgress<LauncherProgress> progress)
    {
        using var diagnosticOperation = DownloadDiagnostics.BeginOperation(logger, "CustomFileDownload", taskItem.Id);
        try
        {
            await downloadService.DownloadAsync(
                sourceUrl,
                destinationPath,
                progress,
                taskItem.CancellationToken);
            taskItem.CancellationToken.ThrowIfCancellationRequested();
            taskItem.Complete(Strings.Status_CustomFileDownloadCompleted);
        }
        catch (OperationCanceledException) when (taskItem.CancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Custom file download canceled. FileName={FileName} DestinationPath={DestinationPath}",
                fileName,
                destinationPath);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Custom file download task failed. FileName={FileName} DestinationPath={DestinationPath}",
                fileName,
                destinationPath);
            taskItem.Fail(Strings.Status_CustomFileDownloadFailed);
        }
    }

    internal static bool TryNormalizeHttpAddress(
        string? value,
        out string normalizedAddress,
        out Uri uri)
    {
        normalizedAddress = string.Empty;
        uri = null!;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed)
            || string.IsNullOrWhiteSpace(parsed.Host)
            || !(parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        uri = parsed;
        normalizedAddress = parsed.AbsoluteUri;
        return true;
    }

    internal static string ResolveDefaultFileName(Uri sourceUri)
    {
        string candidate;
        try
        {
            candidate = Uri.UnescapeDataString(sourceUri.Segments.LastOrDefault()?.TrimEnd('/') ?? string.Empty);
        }
        catch (UriFormatException)
        {
            candidate = string.Empty;
        }

        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(candidate
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(safeName) || safeName is "." or ".."
            ? "download"
            : safeName;
    }
}
