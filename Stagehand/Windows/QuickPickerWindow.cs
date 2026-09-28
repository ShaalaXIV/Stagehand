using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Hosting;
using Stagehand.AssetLibrary;
using Stagehand.AssetLibrary.GameResources;
using Stagehand.Editor.DefinitionEditors.Objects;
using Stagehand.Services;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stagehand.Windows;

public interface IQuickPickerWindow : IHostedService
{
    void Show();
}

internal class QuickPickerWindow : Window, IQuickPickerWindow, IDisposable
{
    private readonly ILogger _logger;
    private readonly IStagehandKeybinds _stagehandKeybinds;
    private readonly IViewportPickerService _viewportPickerService;
    private readonly IAssetBookmarkService _assetBookmarkService;
    private readonly IAssetLibraryWindow _assetLibraryWindow;
    private readonly WindowSystem _windowSystem;

    public PickedObjectInfo? SelectedObjectInfo { get; private set; } = null;
    public PickedObjectInfo? HoveredObjectInfo { get; private set; } = null;

    public bool IsExpanded { get; set; } = false;

    public bool IsMini => HoveredObjectInfo == null && SelectedObjectInfo == null && !IsExpanded;

    public IFolderBookmarkItem? SelectedBookmarkFolder { get; set; } = null;

    public QuickPickerWindow(ILogger<QuickPickerWindow> logger, IStagehandKeybinds stagehandKeybinds, IViewportPickerService viewportPickerService, IAssetBookmarkService assetBookmarkService, IAssetLibraryWindow assetLibraryWindow, WindowSystem windowSystem)
        : base("Stagehand Quick Picker", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoResize)
    {
        _logger = logger;
        _stagehandKeybinds = stagehandKeybinds;
        _viewportPickerService = viewportPickerService;
        _assetBookmarkService = assetBookmarkService;
        _assetLibraryWindow = assetLibraryWindow;
        _windowSystem = windowSystem;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _windowSystem.AddWindow(this);

        _stagehandKeybinds.ToggleQuickPickerWindow.Pressed += Toggle;
        _stagehandKeybinds.StartQuickPicking.Pressed += StartPicking;

        return Task.CompletedTask;
    }

    public void Show()
    {
        IsOpen = true;
        RequestFocus = true;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        if (!IsMini)
        {
            ImGui.SetNextWindowSizeConstraints(new(400.0f * ImGuiHelpers.GlobalScale, 0.0f), new(float.PositiveInfinity, float.PositiveInfinity));
        }

        if (_viewportPickerService.IsPicking)
        {
            Flags |= ImGuiWindowFlags.NoInputs;
        }
        else
        {
            Flags &= ~ImGuiWindowFlags.NoInputs;
        }
    }

    public override void Draw()
    {
        // Drag handle and window background
        ImGui.PushClipRectFullScreen(ImGui.GetWindowDrawList());
        try
        {
            if (!IsMini)
            {
                // Manually paint the background so we can exclude the drag handle portion.
                // The window and the windowsystem unhelpfully hide their tint and blur params etc. But we can grab them from the Dalamud style!
                // ...at least I'm not reflecting, right?
                var verticalSpace = new Vector2(0.0f, ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y);
                var style = (StyleModel.GetConfiguredStyle() as StyleModelV1) ?? StyleModelV1.Get();
                ImGuiHelpers.PrependBlurBehind(ImGui.GetWindowDrawList(), ImGui.GetWindowPos() + verticalSpace, ImGui.GetWindowPos() + ImGui.GetWindowSize(), float.Lerp(0.005f, style.WindowBlurStrength, 1.0f) * 14.0f, ImGui.GetStyle().WindowRounding, ImGui.IsWindowFocused() ? style.WindowBlurTintActive : style.WindowBlurTint, style.WindowBlurLuminosity, float.Lerp(0.09f, 1.0f, BgAlpha ?? 1.0f) * 0.17f);
                ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetWindowPos() + verticalSpace, ImGui.GetWindowPos() + ImGui.GetWindowSize(), ImGui.GetColorU32(ImGuiCol.WindowBg), ImGui.GetStyle().WindowRounding);
            }

            bool handleHovered = false;
            Vector2 handleStart;
            Vector2 handleEnd;
            if (IsMini)
            {
                ImGui.Dummy(new(ImGui.GetFrameHeight() * 3.0f + ImGui.GetStyle().ItemSpacing.Y + ImGui.GetStyle().ItemInnerSpacing.X, ImGui.GetFrameHeight()));
                handleHovered = ImGui.IsItemHovered();
                handleStart = ImGui.GetItemRectMin();
                handleEnd = ImGui.GetItemRectMax();
            }
            else
            {
                ImGui.SetCursorPosX(0.0f);
                handleStart = ImGui.GetCursorPos() + ImGui.GetWindowPos();
                handleEnd = handleStart + new Vector2(ImGui.GetContentRegionAvail().X + ImGui.GetStyle().WindowPadding.X, ImGui.GetFrameHeight() - ImGui.GetStyle().WindowPadding.Y);
                ImGui.Dummy(new(1.0f, handleEnd.Y - handleStart.Y));
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y);
                handleHovered = ImGui.GetMousePos().X >= handleStart.X
                    && ImGui.GetMousePos().Y >= handleStart.Y
                    && ImGui.GetMousePos().X < handleEnd.X
                    && ImGui.GetMousePos().Y < handleEnd.Y;
            }

            if (handleHovered)
            {
                ImGui.GetWindowDrawList().AddRectFilled(handleStart, handleEnd, ImGui.GetColorU32(ImGuiCol.FrameBgHovered), ImGui.GetStyle().FrameRounding);
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
                using (ImRaii.Tooltip())
                {
                    ImGui.TextUnformatted(WindowName);
                }
            }
        }
        finally
        {
            ImGui.PopClipRect();
        }

        // Pick button
        using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive], _viewportPickerService.IsPicking))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.EyeDropper, new((ImGui.GetFrameHeight() * 2.0f + ImGui.GetStyle().ItemSpacing.Y) / ImGuiHelpers.GlobalScale)))
            {
                if (_viewportPickerService.IsPicking)
                {
                    _viewportPickerService.CancelPicking();
                }
                else
                {
                    StartPicking();
                }
            }
        }
        if (ImGui.IsItemHovered())
        {
            using (ImRaii.Tooltip())
            {
                ImGui.TextUnformatted(_viewportPickerService.IsPicking ? "Stop Picking"u8 : "Start Picking"u8);
            }
        }

        var objectInfo = HoveredObjectInfo ?? SelectedObjectInfo;

        if (objectInfo != null)
        {
            ImGui.SameLine();
            using (ImRaii.Group())
            {
                if (objectInfo is PickedBgObjectInfo bgInfo)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetStyle().FramePadding.X); // Align with first iconbutton below
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                    {
                        ImGui.TextUnformatted(BgObjectDefinitionEditor.StaticTypeInfo.Icon.ToIconString());
                    }
                    if (ImGui.IsItemHovered())
                    {
                        using (ImRaii.Tooltip())
                        {
                            ImGui.TextUnformatted(BgObjectDefinitionEditor.StaticTypeInfo.DisplayName);
                        }
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted(bgInfo.ModelGamePath);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    }
                    using (var dragSource = ImRaii.DragDropSource(ImGuiDragDropFlags.SourceAllowNullId))
                    {
                        if (dragSource.Success)
                        {
                            ImGui.SetDragDropPayload(GameResourceDragDrop.DataTypeId, GameResourceDragDrop.MakeGameResourcePayload(bgInfo.ModelGamePath));
                            ImGui.TextUnformatted(bgInfo.ModelGamePath);
                        }
                    }

                    if (ImGuiComponents.IconButton(FontAwesomeIcon.ExternalLinkSquareAlt, new(ImGui.GetFrameHeight() / ImGuiHelpers.GlobalScale)))
                    {
                        if (_assetLibraryWindow.TrySelectGameResource(bgInfo.ModelGamePath))
                        {
                            _assetLibraryWindow.Show();
                        }
                    }
                    if (ImGui.IsItemHovered())
                    {
                        using (ImRaii.Tooltip())
                        {
                            ImGui.TextUnformatted("Show Resource in Asset Library");
                        }
                    }
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(200.0f);
                    if (SelectedBookmarkFolder?.IsDeleted ?? false)
                    {
                        SelectedBookmarkFolder = null;
                    }
                    using (var combo = ImRaii.Combo("###BookmarkFolderCombo", SelectedBookmarkFolder?.Name ?? "(Bookmarks)"))
                    {
                        if (combo.Success)
                        {
                            if (ImGui.Selectable("(Bookmarks)", SelectedBookmarkFolder == null))
                            {
                                SelectedBookmarkFolder = null;
                            }
                            void drawItems(IReadOnlyList<IBookmarkItem> items, int level)
                            {
                                foreach (var item in items)
                                {
                                    if (item is IFolderBookmarkItem folderItem)
                                    {
                                        if (ImGui.Selectable(folderItem.Name.PadLeft(folderItem.Name.Length + level * 2) + "###" + folderItem.Guid.ToString(), SelectedBookmarkFolder == folderItem))
                                        {
                                            SelectedBookmarkFolder = folderItem;
                                        }

                                        drawItems(folderItem.ChildItems, level + 1);
                                    }
                                }
                            }
                            drawItems(_assetBookmarkService.RootItemsSorted, level: 0);
                        }
                        else if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        {
                            SelectedBookmarkFolder = null;
                        }
                    }
                    if (ImGui.IsItemHovered())
                    {
                        using (ImRaii.Tooltip())
                        {
                            ImGui.TextUnformatted("Destination Bookmark Folder");
                            ImGui.Separator();
                            ImGui.TextDisabled("Right click to clear selection.");
                        }
                    }
                    ImGui.SameLine(0.0f, ImGui.GetStyle().ItemInnerSpacing.X);
                    if (ImGuiComponents.IconButton(FontAwesomeIcon.Bookmark, new(ImGui.GetFrameHeight() / ImGuiHelpers.GlobalScale)))
                    {
                        _ = _assetBookmarkService.CreateGameResourceBookmarkAsync(bgInfo.ModelGamePath, SelectedBookmarkFolder);
                    }
                    if (ImGui.IsItemHovered())
                    {
                        using (ImRaii.Tooltip())
                        {
                            ImGui.TextUnformatted($"Add Bookmark{(SelectedBookmarkFolder != null ? $" to {SelectedBookmarkFolder.Name}" : "")}");
                        }
                    }
                }
            }
            ImGui.SameLine(0.0f, ImGui.GetStyle().ItemInnerSpacing.X);
            using (ImRaii.Group())
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Times, new(ImGui.GetFrameHeight() / ImGuiHelpers.GlobalScale)))
                {
                    SelectedObjectInfo = null;
                    HoveredObjectInfo = null;
                }
                if (ImGui.IsItemHovered())
                {
                    using (ImRaii.Tooltip())
                    {
                        ImGui.TextUnformatted("Clear Selection"u8);
                    }
                }
                if (ImGuiComponents.IconButton(IsExpanded ? FontAwesomeIcon.CaretUp : FontAwesomeIcon.CaretDown, new(ImGui.GetFrameHeight() / ImGuiHelpers.GlobalScale)))
                {
                    IsExpanded = !IsExpanded;
                }
                if (ImGui.IsItemHovered())
                {
                    using (ImRaii.Tooltip())
                    {
                        ImGui.TextUnformatted(IsExpanded ? "Collapse"u8 : "Expand"u8);
                    }
                }
            }
        }
        else
        {
            ImGui.SameLine(0.0f, ImGui.GetStyle().ItemInnerSpacing.X);
            if (!IsMini)
            {
                ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight());
            }
            using (ImRaii.Group())
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Times, new(ImGui.GetFrameHeight() / ImGuiHelpers.GlobalScale)))
                {
                    IsOpen = false;
                }
                if (ImGui.IsItemHovered())
                {
                    using (ImRaii.Tooltip())
                    {
                        ImGui.TextUnformatted("Close Quick Picker"u8);
                    }
                }
                if (ImGuiComponents.IconButton(IsExpanded ? FontAwesomeIcon.CaretUp : FontAwesomeIcon.CaretDown, new(ImGui.GetFrameHeight() / ImGuiHelpers.GlobalScale)))
                {
                    IsExpanded = !IsExpanded;
                }
                if (ImGui.IsItemHovered())
                {
                    using (ImRaii.Tooltip())
                    {
                        ImGui.TextUnformatted(IsExpanded ? "Collapse"u8 : "Expand"u8);
                    }
                }
            }
        }

        if (IsExpanded)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            using (var tabStrip = ImRaii.TabBar("###QuickPickerTabs"u8))
            {
                if (tabStrip.Success)
                {
                    using (var nearbyTabItem = ImRaii.TabItem("Nearby"u8))
                    {
                        if (nearbyTabItem.Success)
                        {
                            DrawNearbyTab();
                        }
                    }

                    using (var recentTabItem = ImRaii.TabItem("Recent"u8))
                    {
                        if (recentTabItem.Success)
                        {
                            DrawRecentTab();
                        }
                    }

                    using (var detailsTabItem = ImRaii.TabItem("Details"u8))
                    {
                        if (detailsTabItem.Success)
                        {
                            DrawDetailsTab(objectInfo);
                        }
                    }
                }
            }
        }
    }

    private void DrawNearbyTab()
    {
        ImGui.TextDisabled("(Not yet implemented)");
    }

    private void DrawRecentTab()
    {
        ImGui.TextDisabled("(Not yet implemented)");
    }

    private void DrawDetailsTab(PickedObjectInfo? objectInfo)
    {
        ImGui.TextDisabled("(Not yet implemented)");
    }

    public void StartPicking()
    {
        if (!IsOpen)
        {
            Show();
        }

        if (!_viewportPickerService.IsPicking)
        {
            _viewportPickerService.TryStartPicking(OnObjectHovered, OnObjectClicked, null);
        }
    }

    private void OnObjectHovered(PickedObjectInfo? objectInfo)
    {
        HoveredObjectInfo = objectInfo;
    }

    private void OnObjectClicked(PickedObjectInfo? objectInfo)
    {
        SelectedObjectInfo = objectInfo;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stagehandKeybinds.ToggleQuickPickerWindow.Pressed -= Toggle;
        _stagehandKeybinds.StartQuickPicking.Pressed -= StartPicking;

        _windowSystem.RemoveWindow(this);

        return Task.CompletedTask;
    }

    public void Dispose()
    { }
}
