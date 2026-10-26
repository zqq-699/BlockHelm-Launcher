/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.Account;

/// <summary>
/// 管理当前账户的皮肤库、轮播选择、皮肤模型切换及本地与远端状态同步。
/// </summary>
public sealed partial class AccountSkinLibraryViewModel : ObservableObject
{
    // 账户对象是持久化真相，ObservableCollection 是对话框当前会话的可观察投影。
    private readonly AccountListViewModel accountList;
    private readonly IMicrosoftAccountService microsoftAccountService;
    private readonly IAccountSkinLibraryService skinLibraryService;
    private readonly IAccountDialogService dialogService;
    private readonly IFilePickerService filePickerService;
    private readonly IMinecraftSkinFileValidator skinFileValidator;
    private readonly AccountProfileViewModel profile;
    private readonly MicrosoftAccountOperationRetryHandler microsoftOperationRetryHandler;
    private readonly ILogger logger;
    private LauncherSkinRecord? skinPendingModelChange;

    internal AccountSkinLibraryViewModel(
        AccountListViewModel accountList,
        IMicrosoftAccountService microsoftAccountService,
        IAccountSkinLibraryService skinLibraryService,
        AccountSkinModelDialogViewModel skinModelDialog,
        IAccountDialogService dialogService,
        IFilePickerService filePickerService,
        IMinecraftSkinFileValidator skinFileValidator,
        AccountProfileViewModel profile,
        MicrosoftAccountOperationRetryHandler microsoftOperationRetryHandler,
        ILogger logger)
    {
        this.accountList = accountList;
        this.microsoftAccountService = microsoftAccountService;
        this.skinLibraryService = skinLibraryService;
        SkinModelDialog = skinModelDialog;
        this.dialogService = dialogService;
        this.filePickerService = filePickerService;
        this.skinFileValidator = skinFileValidator;
        this.profile = profile;
        this.microsoftOperationRetryHandler = microsoftOperationRetryHandler;
        this.logger = logger;
        profile.PropertyChanged += (_, _) => NotifyCommandState();
    }

    public AccountSkinModelDialogViewModel SkinModelDialog { get; }

    [ObservableProperty]
    private LauncherSkinRecord? selectedSkin;

    [ObservableProperty]
    private bool isManagerDialogOpen;

    public ObservableCollection<LauncherSkinRecord> Skins { get; } = [];

    public LauncherSkinRecord? PreviousSkin => GetAdjacent(-1);

    public LauncherSkinRecord? NextSkin => GetAdjacent(1);

    public bool HasSkins => Skins.Count > 0;

    public bool IsOffline => accountList.SelectedAccount?.IsOffline == true;

    public bool IsThirdParty => accountList.SelectedAccount?.IsThirdParty == true;

    public bool CanShowStandardActions => !IsThirdParty;

    public bool CanShowThirdPartyRefresh => IsThirdParty;

    public bool HasPreview => SelectedSkin is not null;

    public bool CanShowPreviewEmptyState => accountList.SelectedAccount is not null && !HasPreview;

    public bool CanChangeSkin => accountList.SelectedAccount is { IsThirdParty: false };

    public bool CanManageSkins => accountList.SelectedAccount is { IsThirdParty: false };

    public bool CanApplySkin => accountList.SelectedAccount is { IsThirdParty: false } account
        && !profile.IsBusy
        && SelectedSkin is { } skin
        && (!IsAlreadyApplied(account, skin) || NeedsOfflineAvatarRefresh(account));

    public bool CanEditSelectedSkin => accountList.SelectedAccount is { IsThirdParty: false }
        && !profile.IsBusy
        && SelectedSkin is { } skin
        && !IsActiveForSharedAccount(skin);

    public bool CanDeleteSelectedSkin => CanEditSelectedSkin
        && accountList.SelectedAccount is { } account
        && SelectedSkin is { } skin
        && !string.Equals(account.ActiveSkinId, skin.Id, StringComparison.Ordinal);

    public bool CanShowManagerEmptyState => !HasSkins;

    public string? ActiveSkinId => accountList.SelectedAccount?.ActiveSkinId;

    public void SetAccount(LauncherAccount? account)
    {
        // 切换账户时按稳定皮肤 Id 重建集合，不能保留上一账户的 SelectedSkin 对象引用。
        skinPendingModelChange = null;
        if (account is null)
        {
            Skins.Clear();
            SelectedSkin = null;
            IsManagerDialogOpen = false;
        }
        else
        {
            Populate(account, account.ActiveSkinId);
        }
        NotifyState();
    }

    public async Task ConfirmSkinModelDialogAsync()
    {
        if (SkinModelDialog.IsSkinFormatError)
        {
            SkinModelDialog.Cancel();
            return;
        }
        if (!SkinModelDialog.TryConsumeSelection(out var path, out var model))
            return;
        if (skinPendingModelChange is { } pending)
        {
            skinPendingModelChange = null;
            await ChangeSkinModelAsync(pending, model);
            return;
        }
        await AddSkinAsync(path, model);
    }

    [RelayCommand]
    public async Task PickAndChangeSkinAsync()
    {
        // 文件选择与上传分开：用户取消不改变状态，选中后才进入统一新增流程。
        if (!CanChangeSkin)
            return;
        var path = filePickerService.PickMinecraftSkin();
        if (string.IsNullOrWhiteSpace(path))
            return;
        var validation = await skinFileValidator.ValidateAsync(path);
        if (validation.IsValid)
            dialogService.ShowSkinModelDialog(path);
        else
            dialogService.ShowSkinFormatErrorDialog();
    }

    [RelayCommand(CanExecute = nameof(CanManageSkins))]
    public void RequestOpenManagerDialog() => dialogService.ShowSkinManagerDialog();

    [RelayCommand]
    public void RequestCancelManagerDialog() => dialogService.CancelSkinManagerDialog();

    public void OpenManagerDialog()
    {
        if (CanManageSkins)
            IsManagerDialogOpen = true;
    }

    public void CloseManagerDialog() => IsManagerDialogOpen = false;

    [RelayCommand]
    public void SelectSkin(LauncherSkinRecord? skin)
    {
        if (skin is not null && Skins.Any(candidate => string.Equals(candidate.Id, skin.Id, StringComparison.Ordinal)))
            SelectedSkin = skin;
    }

    [RelayCommand(CanExecute = nameof(CanApplySkin))]
    public async Task ApplySkinAsync()
    {
        // 先完成服务端或本地应用，再整体替换账户记录；失败时 UI 仍指向原始可用账户。
        var account = accountList.SelectedAccount;
        var skin = SelectedSkin;
        if (account is null || skin is null || !CanApplySkin)
            return;
        var operation = profile.BeginOperation(
            account,
            account.IsMicrosoft ? Strings.Status_UploadingSkin : string.Empty);
        try
        {
            if (account.IsOffline)
            {
                var avatarSource = await skinLibraryService.CreateAvatarSourceAsync(
                    account,
                    skin,
                    operation.Token);
                if (!profile.IsCurrent(account, operation))
                    return;
                var updatedOfflineAccount = AccountMapper.WithAvatar(
                    AccountMapper.WithSkinLibrary(
                        account,
                        [skin],
                        skin.Id,
                        skin.Source,
                        skin.SkinModel),
                    avatarSource);
                accountList.ReplaceSelectedAccount(account, updatedOfflineAccount);
                Populate(updatedOfflineAccount, skin.Id);
                await accountList.PersistAccountOrderAsync();
                profile.SetMessage(Strings.Status_SkinUpdated, showFloating: true);
                return;
            }

            var result = await microsoftOperationRetryHandler.ExecuteAsync(
                account,
                current => microsoftAccountService.UploadSkinAsync(
                    current,
                    ResolveLocalPath(skin.Source),
                    skin.SkinModel,
                    operation.Token));
            if (!profile.IsCurrent(result.Account, operation))
                return;
            var updated = AccountMapper.WithCapeCache(
                AccountMapper.WithSkinLibrary(
                    AccountMapper.WithAppearanceFallback(result.Value, result.Account),
                    [skin],
                    skin.Id,
                    skin.Source,
                    skin.SkinModel),
                result.Account.CachedCapeOptions);
            accountList.ReplaceSelectedAccount(result.Account, updated);
            Populate(updated, skin.Id);
            await accountList.PersistAccountOrderAsync();
            profile.SetMessage(Strings.Status_SkinUpdated, showFloating: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Account skin apply failed. AccountId={AccountId} AccountKind={AccountKind} SkinId={SkinId} ErrorType={ErrorType}",
                account.Id,
                account.Kind,
                skin.Id,
                exception.GetType().FullName);
            profile.SetError(exception, Strings.Status_SkinUpdateFailed, showFloating: true);
        }
        finally
        {
            profile.Complete(account, operation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedSkin))]
    public void ChangeSelectedSkinModel()
    {
        if (SelectedSkin is not { } skin || !CanEditSelectedSkin)
            return;
        skinPendingModelChange = skin;
        dialogService.ShowSkinModelDialog(skin.SkinModel);
    }

    [RelayCommand(CanExecute = nameof(CanChangeSkinModel))]
    public void ChangeSkinModel(LauncherSkinRecord? skin)
    {
        if (!CanChangeSkinModel(skin))
            return;
        SelectedSkin = skin;
        ChangeSelectedSkinModel();
    }

    public bool CanChangeSkinModel(LauncherSkinRecord? skin) =>
        accountList.SelectedAccount is { IsThirdParty: false }
        && !profile.IsBusy
        && skin is not null
        && !IsActiveForSharedAccount(skin);

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedSkin))]
    public async Task DeleteSelectedSkinAsync()
    {
        // 删除前先决定相邻选择，因为删除后集合索引和轮播邻项都会变化。
        var account = accountList.SelectedAccount;
        var skin = SelectedSkin;
        if (account is null || skin is null || !CanDeleteSelectedSkin)
            return;
        var operation = profile.BeginOperation(account, string.Empty);
        try
        {
            await skinLibraryService.DeleteSkinAsync(account, skin);
            if (!profile.IsCurrent(account, operation))
                return;
            var preferredId = GetPreferredSkinIdAfterDelete(skin);
            Populate(account, preferredId);
            profile.SetMessage(Strings.Status_SkinDeleted);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Account skin delete failed. AccountId={AccountId} AccountKind={AccountKind} SkinId={SkinId} ErrorType={ErrorType}",
                account.Id,
                account.Kind,
                skin.Id,
                exception.GetType().FullName);
            profile.SetError(exception, Strings.Status_SkinDeleteFailed);
        }
        finally
        {
            profile.Complete(account, operation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSkin))]
    public async Task DeleteSkinAsync(LauncherSkinRecord? skin)
    {
        if (!CanDeleteSkin(skin))
            return;
        SelectedSkin = skin;
        await DeleteSelectedSkinAsync();
    }

    public bool CanDeleteSkin(LauncherSkinRecord? skin) =>
        accountList.SelectedAccount is { IsThirdParty: false }
        && !profile.IsBusy && skin is not null
        && !IsActiveForSharedAccount(skin);

    [RelayCommand]
    public void RequestCancelModelDialog()
    {
        skinPendingModelChange = null;
        dialogService.CancelSkinModelDialog();
    }

    [RelayCommand]
    public Task RequestConfirmModelDialogAsync() => dialogService.ConfirmSkinModelDialogAsync();

    [RelayCommand(CanExecute = nameof(CanSelectPrevious))]
    public void SelectPrevious()
    {
        if (PreviousSkin is { } skin)
            SelectedSkin = skin;
    }

    public bool CanSelectPrevious() => PreviousSkin is not null;

    [RelayCommand(CanExecute = nameof(CanSelectNext))]
    public void SelectNext()
    {
        if (NextSkin is { } skin)
            SelectedSkin = skin;
    }

    public bool CanSelectNext() => NextSkin is not null;

    private async Task AddSkinAsync(string path, MinecraftSkinModel model)
    {
        // 服务返回规范化后的皮肤记录，本地路径、哈希或远端标识都以返回值为准。
        var account = accountList.SelectedAccount;
        if (account is not { IsThirdParty: false })
            return;
        var operation = profile.BeginOperation(account, Strings.Status_AddingSkin);
        try
        {
            var imported = await skinLibraryService.ImportSkinAsync(account, path, model);
            if (!profile.IsCurrent(account, operation))
                return;
            Populate(account, imported.Id);
            profile.SetMessage(Strings.Status_SkinAdded);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Account skin import failed. AccountId={AccountId} AccountKind={AccountKind} ErrorType={ErrorType}",
                account.Id,
                account.Kind,
                exception.GetType().FullName);
            profile.SetError(exception, Strings.Status_SkinUpdateFailed);
        }
        finally
        {
            profile.Complete(account, operation);
        }
    }

    private async Task ChangeSkinModelAsync(LauncherSkinRecord skin, MinecraftSkinModel model)
    {
        var account = accountList.SelectedAccount;
        if (account is null || skin.SkinModel == model || IsActiveForSharedAccount(skin))
            return;
        var updatedSkin = await skinLibraryService.ImportSkinAsync(
            account,
            ResolveLocalPath(skin.Source),
            model);
        await skinLibraryService.DeleteSkinAsync(account, skin);
        Populate(account, updatedSkin.Id);
        profile.SetMessage(Strings.Status_SkinModelChanged);
    }

    private void Populate(LauncherAccount account, string? preferredId)
    {
        // 去重后一次性重建，确保同一内容不会因本地缓存和账户资料两个来源显示两次。
        if (account.IsThirdParty)
        {
            Skins.Clear();
            var activeSkin = account.SkinLibrary.FirstOrDefault(skin =>
                    string.Equals(skin.Id, account.ActiveSkinId, StringComparison.Ordinal))
                ?? account.SkinLibrary.FirstOrDefault(skin =>
                    string.Equals(skin.Source, account.SkinSource, StringComparison.Ordinal));
            if (activeSkin is not null)
                Skins.Add(activeSkin);
            SelectedSkin = activeSkin;
            NotifyState();
            return;
        }

        var sharedSkins = DistinctSkins(
                skinLibraryService.GetSharedSkins())
            .ToList();
        foreach (var activeAccountSkin in accountList.Accounts
                     .Select(item => item.Account)
                     .Where(candidate => !candidate.IsThirdParty)
                     .Select(FindActiveSkinReference)
                     .Where(skin => skin is not null)
                     .Select(skin => skin!))
        {
            if (sharedSkins.All(skin => !SameContent(skin, activeAccountSkin)))
                sharedSkins.Add(activeAccountSkin);
        }

        if (!SkinSequencesEqual(Skins, sharedSkins))
        {
            Skins.Clear();
            foreach (var skin in sharedSkins)
                Skins.Add(skin);
        }
        SelectedSkin = Skins.FirstOrDefault(skin => string.Equals(skin.Id, preferredId, StringComparison.Ordinal))
            ?? Skins.FirstOrDefault(skin => string.Equals(skin.Id, account.ActiveSkinId, StringComparison.Ordinal))
            ?? Skins.FirstOrDefault();
        NotifyState();
    }

    partial void OnSelectedSkinChanged(LauncherSkinRecord? value) => NotifyState();

    private LauncherSkinRecord? GetAdjacent(int offset)
    {
        // 轮播在边界不循环；返回 null 会同时隐藏对应槽位并禁用命令。
        if (IsThirdParty || SelectedSkin is null || Skins.Count < 2)
            return null;
        var index = Skins.IndexOf(SelectedSkin) + offset;
        return index >= 0 && index < Skins.Count ? Skins[index] : null;
    }

    private void NotifyState()
    {
        // Previous/Next 是计算属性，选择或集合变化后必须成组通知 3D 控件更新三个槽位。
        OnPropertyChanged(nameof(PreviousSkin));
        OnPropertyChanged(nameof(NextSkin));
        OnPropertyChanged(nameof(HasSkins));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(IsThirdParty));
        OnPropertyChanged(nameof(CanShowStandardActions));
        OnPropertyChanged(nameof(CanShowThirdPartyRefresh));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(CanShowPreviewEmptyState));
        OnPropertyChanged(nameof(CanChangeSkin));
        OnPropertyChanged(nameof(CanManageSkins));
        OnPropertyChanged(nameof(CanApplySkin));
        OnPropertyChanged(nameof(CanEditSelectedSkin));
        OnPropertyChanged(nameof(CanDeleteSelectedSkin));
        OnPropertyChanged(nameof(CanShowManagerEmptyState));
        OnPropertyChanged(nameof(ActiveSkinId));
        NotifyCommandState();
    }

    private void NotifyCommandState()
    {
        RequestOpenManagerDialogCommand.NotifyCanExecuteChanged();
        ApplySkinCommand.NotifyCanExecuteChanged();
        ChangeSelectedSkinModelCommand.NotifyCanExecuteChanged();
        DeleteSelectedSkinCommand.NotifyCanExecuteChanged();
        ChangeSkinModelCommand.NotifyCanExecuteChanged();
        DeleteSkinCommand.NotifyCanExecuteChanged();
        SelectPreviousCommand.NotifyCanExecuteChanged();
        SelectNextCommand.NotifyCanExecuteChanged();
    }

    private string? GetPreferredSkinIdAfterDelete(LauncherSkinRecord deleted)
    {
        var index = Skins.ToList().FindIndex(skin => string.Equals(skin.Id, deleted.Id, StringComparison.Ordinal));
        if (index + 1 < Skins.Count)
            return Skins[index + 1].Id;
        return index > 0 ? Skins[index - 1].Id : null;
    }

    private bool IsActiveForSharedAccount(LauncherSkinRecord skin) =>
        accountList.Accounts.Any(item =>
            !item.Account.IsThirdParty
            && IsAlreadyApplied(item.Account, skin));

    private static bool NeedsOfflineAvatarRefresh(LauncherAccount account) =>
        account.IsOffline
        && (string.IsNullOrWhiteSpace(account.AvatarSource)
            || string.Equals(
                account.AvatarSource,
                LauncherAccount.DefaultSteveAvatarUrl,
                StringComparison.OrdinalIgnoreCase));

    private static string ResolveLocalPath(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : source;

    private static bool IsAlreadyApplied(LauncherAccount account, LauncherSkinRecord skin)
    {
        if (string.Equals(account.ActiveSkinId, skin.Id, StringComparison.Ordinal))
            return account.SkinModel == skin.SkinModel;
        var active = account.SkinLibrary.FirstOrDefault(value => string.Equals(value.Id, account.ActiveSkinId, StringComparison.Ordinal));
        if (active is not null && SameContent(active, skin))
            return true;
        return account.SkinModel == skin.SkinModel && string.Equals(account.SkinSource, skin.Source, StringComparison.Ordinal);
    }

    private static LauncherSkinRecord? FindActiveSkinReference(LauncherAccount account) =>
        account.SkinLibrary.FirstOrDefault(skin =>
                string.Equals(skin.Id, account.ActiveSkinId, StringComparison.Ordinal))
            ?? account.SkinLibrary.FirstOrDefault(skin =>
                account.SkinModel == skin.SkinModel
                && string.Equals(skin.Source, account.SkinSource, StringComparison.Ordinal));

    private static IEnumerable<LauncherSkinRecord> DistinctSkins(IEnumerable<LauncherSkinRecord> skins)
    {
        // 优先使用服务分配的 Id；历史记录缺少 Id 时回退到来源和模型构成的内容身份。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skin in skins)
        {
            var key = string.IsNullOrWhiteSpace(skin.ContentHash)
                ? $"{skin.Source}|{skin.SkinModel}"
                : $"{skin.ContentHash}|{skin.SkinModel}";
            if (seen.Add(key))
                yield return skin;
        }
    }

    private static bool SameContent(LauncherSkinRecord left, LauncherSkinRecord right) =>
        left.SkinModel == right.SkinModel
        && (!string.IsNullOrWhiteSpace(left.ContentHash) && !string.IsNullOrWhiteSpace(right.ContentHash)
            ? string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase)
            : string.Equals(left.Source, right.Source, StringComparison.Ordinal));

    private static bool SkinSequencesEqual(
        IReadOnlyList<LauncherSkinRecord> left,
        IReadOnlyList<LauncherSkinRecord> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Id, right[index].Id, StringComparison.Ordinal)
                || !string.Equals(left[index].Source, right[index].Source, StringComparison.Ordinal)
                || left[index].SkinModel != right[index].SkinModel
                || !string.Equals(
                    left[index].ContentHash,
                    right[index].ContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
