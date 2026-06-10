// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services;

public class DashboardEditService
{
    public bool IsEditing { get; private set; }
    public event Action? OnChanged;

    public void Toggle()
    {
        IsEditing = !IsEditing;
        OnChanged?.Invoke();
    }
}
