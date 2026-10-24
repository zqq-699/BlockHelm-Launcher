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

namespace Launcher.App.ViewModels.Resources;

using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

public sealed class ResourcesModVersionListHeaderItem : ObservableObject
{
    private readonly Action<string, bool>? expansionChanged;
    private bool isExpanded;

    public ResourcesModVersionListHeaderItem(
        string title,
        bool isExpanded = true,
        Action<string, bool>? expansionChanged = null)
    {
        Title = title;
        this.isExpanded = isExpanded;
        this.expansionChanged = expansionChanged;
        ToggleExpandedCommand = new RelayCommand(ToggleExpanded, () => expansionChanged is not null);
    }

    public string Title { get; }

    public bool IsExpanded
    {
        get => isExpanded;
        private set => SetProperty(ref isExpanded, value);
    }

    public IRelayCommand ToggleExpandedCommand { get; }

    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        expansionChanged?.Invoke(Title, IsExpanded);
    }
}
