using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Stagehand.AssetLibrary.Assets;
using Stagehand.AssetLibrary.Bookmarks;
using Stagehand.AssetLibrary.GameResources;
using Stagehand.Definitions.Objects;
using Stagehand.Live;
using Stagehand.Services;
using Stagehand.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stagehand.AssetLibrary;

/// <summary>
/// A window where users can browse the various assets available to them.
/// </summary>
public interface IAssetLibraryWindow : IHostedService
{
    /// <summary>
    /// The icon that represents the asset library window.
    /// </summary>
    public const FontAwesomeIcon Icon = FontAwesomeIcon.Cubes;

    /// <summary>
    /// Whether the Asset Library window is open.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Raised when the user requests to create an object definition from an asset.
    /// </summary>
    /// <remarks>
    /// Whether this event has any handlers determines whether the Create button is visible.
    /// </remarks>
    event Action<ObjectDefinition> CreateObject;

    /// <summary>
    /// Shows the Asset Library window and brings it to the front of the window order.
    /// </summary>
    void Show();

    /// <summary>
    /// Hides the Asset Library window.
    /// </summary>
    void Hide();

    /// <summary>
    /// Selects the game resource with the given path in the game resources tab, if it exists.
    /// </summary>
    /// <param name="resourceGamePath">The game path of the resource to select.</param>
    /// <returns>Whether the resource was found and selected.</returns>
    bool TrySelectGameResource(string resourceGamePath);

    /// <summary>
    /// Selects the game folder with the given path in the game resources tab, if it exists.
    /// </summary>
    /// <param name="folderGamePath">The game path of the folder to select.</param>
    /// <returns>Whether the resource was found and selected.</returns>
    bool TrySelectGameFolder(string folderGamePath);

    /// <summary>
    /// Sets the callback to use for assigning an asset from the Asset Library window.
    /// </summary>
    /// <typeparam name="TAssetInfo">The type of asset info that can be selected.</typeparam>
    /// <param name="objectName">The name of the object whose property will be set.</param>
    /// <param name="propertyName">The name of the property that the assignment will set.</param>
    /// <param name="assetType">The type of asset to select.</param>
    /// <param name="stillValidCallback">A callback to test whether this is still valid.</param>
    /// <param name="selectCallback">The callback to invoke to assign an asset.</param>
    void SetSelectionCallback<TAssetInfo>(string objectName, string propertyName, AssetType<TAssetInfo> assetType, Func<bool> stillValidCallback, Action<TAssetInfo> selectCallback);
}

internal partial class AssetLibraryWindow : Window, IAssetLibraryWindow
{
    private record class AssetLibraryTab(string DisplayName, Action DrawAction);

    private interface ISelectionCallback
    {
        string ObjectName { get; }
        string PropertyName { get; }
        AssetType AssetType { get; }
        bool IsValid { get; }
        bool TrySelect(AssetInfo asset);
    }

    private record class SelectionCallback<TAssetInfo>(string ObjectName, string PropertyName, AssetType<TAssetInfo> AssetType, Func<bool> StillValidCallback, Action<TAssetInfo> SelectedCallback) : ISelectionCallback
    {
        public bool IsValid => StillValidCallback.Invoke();

        public bool TrySelect(AssetInfo asset)
        {
            if (asset is TAssetInfo typedAsset && IsValid)
            {
                SelectedCallback.Invoke(typedAsset);
                return true;
            }
            else
            {
                return false;
            }
        }

        AssetType ISelectionCallback.AssetType => AssetType;
    }

    private readonly ILogger _logger;
    private readonly ILiveObjectService _liveObjectService;
    private readonly IObjectTable _objectTable;
    private readonly ITargetManager _targetManager;
    private readonly IOverlayService _overlayService;
    private readonly IGameResourceAssetService _gameResourceAssetService;
    private readonly IAssetBookmarkService _assetBookmarkService;
    private readonly IDataManager _dataManager;
    private readonly ITextureProvider _textureProvider;
    private readonly StagehandConfiguration _configuration;
    private readonly WindowSystem _windowSystem;

    private readonly AssetLibraryTab[] _dataTabs;
    private AssetLibraryTab? _selectedDataTab = null;
    private readonly AssetLibraryTab[] _userTabs;
    private AssetLibraryTab? _selectedUserTab = null;
    private ISelectionCallback? _activeSelectionCallback;
    private readonly GameResourceTreeViewComponent _gameResourceTreeView;
    private readonly BookmarkTreeViewComponent _bookmarkTreeView;

    private HoverPreviewMode HoverPreviewMode
    {
        get;
        set
        {
            field = value;

            _configuration.AssetLibraryPreviewMode = value;
            _configuration.Save();

            if (value == HoverPreviewMode.None)
            {
                _hoverPreviewObject?.Dispose();
                _hoverPreviewObject = null;
            }
        }
    }
    private AssetInfo? _selectedAssetInfo;

    bool IAssetLibraryWindow.IsOpen => base.IsOpen;

    public event Action<ObjectDefinition>? CreateObject;

    private ILiveObject? _hoverPreviewObject;
    private AssetInfo? HoveredAssetInfo
    {
        get;
        set
        {
            if (field != value)
            {
                _hoverPreviewObject?.Dispose();
                _hoverPreviewObject = null;

                field = value;

                var localPlayer = _objectTable.LocalPlayer;
                if ((HoverPreviewMode == HoverPreviewMode.NearPlayer || HoverPreviewMode == HoverPreviewMode.AtTarget) && value != null && localPlayer != null)
                {
                    var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, localPlayer.Rotation);
                    var position = localPlayer.Position + Vector3.Transform(Vector3.UnitZ, rotation) * 2.0f;
                    if (HoverPreviewMode == HoverPreviewMode.AtTarget && _targetManager.Target != null)
                    {
                        rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _targetManager.Target.Rotation);
                        position = _targetManager.Target.Position;
                    }
                    _hoverPreviewObject = value.CreatePreviewObject(_liveObjectService, position, rotation);
                }
            }
        }
    }

    public AssetLibraryWindow(ILogger<AssetLibraryWindow> logger, ILiveObjectService liveObjectService, IObjectTable objectTable, ITargetManager targetManager, IOverlayService overlayService, IGameResourceAssetService gameResourceAssetService, IAssetBookmarkService assetBookmarkService, IDataManager dataManager, ITextureProvider textureProvider, StagehandConfiguration configuration, WindowSystem windowSystem)
        : base("Stagehand Asset Library")
    {
        _logger = logger;
        _liveObjectService = liveObjectService;
        _objectTable = objectTable;
        _targetManager = targetManager;
        _overlayService = overlayService;
        _gameResourceAssetService = gameResourceAssetService;
        _assetBookmarkService = assetBookmarkService;
        _dataManager = dataManager;
        _textureProvider = textureProvider;
        _configuration = configuration;
        _windowSystem = windowSystem;

        HoverPreviewMode = _configuration.AssetLibraryPreviewMode;

        _dataTabs =
        [
            new("Game Resources", DrawGameResourcesTab),
            new("Housing", DrawHousingTab),
        ]; 

        _userTabs =
        [
            new("Bookmarks", DrawBookmarksTab),
            new("Tags", DrawTagsTab),
        ];

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(950, 750),
        };
        SizeCondition = ImGuiCond.FirstUseEver;

        _gameResourceTreeView = new(_gameResourceAssetService, _assetBookmarkService);

        _bookmarkTreeView = new(_assetBookmarkService, _gameResourceAssetService);
        _bookmarkTreeView.GameFolderDoubleClicked += OnGameFolderBookmarkDoubleClicked;
        _bookmarkTreeView.GameResourceDoubleClicked += OnGameResourceBookmarkDoubleClicked;

        LoadHousingNodes();
    }

    public bool TrySelectGameResource(string resourceGamePath)
    {
        if (_gameResourceAssetService.TryGetResource(resourceGamePath, out var resource))
        {
            _selectedDataTab = _dataTabs[0];
            _gameResourceTreeView.ExpandItem(resource);
            _gameResourceTreeView.SelectedItem = resource;
            return true;
        }
        else
        {
            return false;
        }
    }

    public bool TrySelectGameFolder(string folderGamePath)
    {
        if (_gameResourceAssetService.TryGetFolder(folderGamePath, out var folder))
        {
            _selectedDataTab = _dataTabs[0];
            _gameResourceTreeView.ExpandItem(folder);
            _gameResourceTreeView.SelectedItem = folder;
            return true;
        }
        else
        {
            return false;
        }
    }

    private void OnGameResourceBookmarkDoubleClicked(IGameResourceBookmarkItem obj)
    {
        TrySelectGameResource(obj.ResourceGamePath);
    }

    private void OnGameFolderBookmarkDoubleClicked(IGameFolderBookmarkItem obj)
    {
        TrySelectGameFolder(obj.FolderGamePath);
    }

    public void Show()
    {
        IsOpen = true;
        BringToFront();
    }

    public void Hide()
    {
        IsOpen = false;
    }

    public override void OnClose()
    {
        base.OnClose();
        HoveredAssetInfo = null;
    }

    public override void Draw()
    {
        if (_activeSelectionCallback != null && !_activeSelectionCallback.IsValid)
        {
            _activeSelectionCallback = null;
        }

        var defaultCellPadding = ImGui.GetStyle().CellPadding;
        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(defaultCellPadding.X, 0.0f)))
        using (ImRaii.Table($"###AssetLibraryTable", 3, ImGuiTableFlags.Resizable | ImGuiTableFlags.NoBordersInBodyUntilResize, ImGui.GetContentRegionAvail()))
        {
            ImGui.TableSetupColumn("data", ImGuiTableColumnFlags.WidthFixed, 400.0f);
            ImGui.TableSetupColumn("selected", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn("user", ImGuiTableColumnFlags.WidthFixed, 400.0f);
            ImGui.TableNextColumn();
            using (ImRaii.TabBar("AssetSourceTabs"))
            {
                foreach (var tab in _dataTabs)
                {
                    using (var tabItem = ImRaii.TabItem(tab.DisplayName, tab == _selectedDataTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                    {
                        if (tabItem.Success)
                        {
                            _selectedDataTab = null;

                            tab.DrawAction.Invoke();

                            // Bottom bar is shared
                            var addMenuWidth = ImGui.CalcTextSize("Hover Preview").X + ImGui.GetStyle().FramePadding.X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
                            ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - addMenuWidth);
                            ImGui.SetCursorPosY(ImGui.GetContentRegionMax().Y - ImGui.GetFrameHeight());
                            ImGui.SetNextItemWidth(addMenuWidth);
                            using (var addMenu = Utils.ImGuiExtensions.DropdownButton("###AssetPreviewMenu", "Hover Preview"))
                            {
                                if (ImGui.IsItemHovered())
                                {
                                    using (ImRaii.Tooltip())
                                    {
                                        ImGui.TextUnformatted("Options for previewing the hovered asset");
                                    }
                                }

                                if (addMenu.Success)
                                {
                                    if (ImGui.Selectable("Off", HoverPreviewMode == HoverPreviewMode.None))
                                    {
                                        HoverPreviewMode = HoverPreviewMode.None;
                                    }
                                    if (ImGui.IsItemHovered())
                                    {
                                        using (ImRaii.Tooltip())
                                        {
                                            ImGui.TextUnformatted("Don't preview the hovered asset in the game");
                                        }
                                    }

                                    if (ImGui.Selectable("Near Player", HoverPreviewMode == HoverPreviewMode.NearPlayer))
                                    {
                                        HoverPreviewMode = HoverPreviewMode.NearPlayer;
                                    }
                                    if (ImGui.IsItemHovered())
                                    {
                                        using (ImRaii.Tooltip())
                                        {
                                            ImGui.TextUnformatted("Preview the hovered asset near the player character");
                                        }
                                    }

#if false // TODO: Implement editor object preview
                                    if (ImGui.Selectable("Editor Object", HoverPreviewMode == HoverPreviewMode.EditorObject))
                                    {
                                        HoverPreviewMode = HoverPreviewMode.EditorObject;
                                    }
                                    if (ImGui.IsItemHovered())
                                    {
                                        using (ImRaii.Tooltip())
                                        {
                                            ImGui.TextUnformatted("Preview the hovered asset on the object\nbeing edited, if possible");
                                        }
                                    }
#endif
                                    if (ImGui.Selectable("At Target", HoverPreviewMode == HoverPreviewMode.AtTarget))
                                    {
                                        HoverPreviewMode = HoverPreviewMode.AtTarget;
                                    }
                                    if (ImGui.IsItemHovered())
                                    {
                                        using (ImRaii.Tooltip())
                                        {
                                            ImGui.TextUnformatted("Preview the hovered asset at the location of the targeted object");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            ImGui.TableNextColumn();
            if (_selectedAssetInfo != null)
            {
                var startY = ImGui.GetCursorPosY();
                ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight());
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Times, new Vector2(ImGui.GetFrameHeight() / ImGuiHelpers.GlobalScale)))
                {
                    _gameResourceTreeView.SelectedItem = null;
                    _bookmarkTreeView.SelectedItem = null;
                    _selectedAssetInfo = null;
                    HoveredAssetInfo = null;
                }
                else
                {
                    ImGui.SameLine(0.0f);
                    ImGui.SetCursorPosY(startY);
                    Utils.ImGuiExtensions.PropertiesHeader(_selectedAssetInfo.DisplayName, 0, _selectedAssetInfo.Type.DisplayName, _selectedAssetInfo.Type.Icon, _selectedAssetInfo.Type.DisplayDescription, out bool isNameHovered);

                    if (isNameHovered)
                    {
                        using (ImRaii.Tooltip())
                        {
                            ImGui.TextUnformatted(_selectedAssetInfo.ID);
                            ImGui.Separator();
                            ImGui.TextDisabled("Click to copy");
                        }
                    }

                    _selectedAssetInfo.DrawProperties();

                    var createWidth = 0.0f;
                    if (CreateObject != null)
                    {
                        createWidth = ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Plus, "Create");
                    }

                    ImGui.SetCursorPosY(ImGui.GetContentRegionMax().Y - ImGui.GetFrameHeight());
                    if (_activeSelectionCallback != null)
                    {
                        var assignText = "Assign";
                        var assignWidth = ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.ArrowRight, assignText);
                        ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - assignWidth - (createWidth > 0 ? createWidth + ImGui.GetStyle().ItemInnerSpacing.X : 0.0f));
                        ImGui.SetNextItemWidth(assignWidth);
                        using (ImRaii.Disabled(_activeSelectionCallback.AssetType != _selectedAssetInfo.Type))
                        {
                            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowRight, assignText))
                            {
                                _activeSelectionCallback.TrySelect(_selectedAssetInfo);
                            }
                            if (ImGui.IsItemHovered())
                            {
                                using (ImRaii.Tooltip())
                                {
                                    ImGui.TextUnformatted($"Assign to {_activeSelectionCallback.ObjectName}'s {_activeSelectionCallback.PropertyName}");
                                }
                            }
                        }
                        if (CreateObject != null)
                        {
                            ImGui.SameLine(0.0f, ImGui.GetStyle().ItemInnerSpacing.X);
                        }
                    }
                    else
                    {
                        ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - createWidth);
                    }

                    if (CreateObject != null)
                    {
                        ImGui.SetNextItemWidth(createWidth);
                        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Create"))
                        {
                            var rotation = Quaternion.Identity;
                            var position = Vector3.Zero;
                            if (HoverPreviewMode == HoverPreviewMode.NearPlayer && _objectTable.LocalPlayer != null)
                            {
                                rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _objectTable.LocalPlayer.Rotation);
                                position = _objectTable.LocalPlayer.Position + Vector3.Transform(Vector3.UnitZ, rotation) * 2.0f;
                            }
                            if (HoverPreviewMode == HoverPreviewMode.AtTarget && _targetManager.Target != null)
                            {
                                rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _targetManager.Target.Rotation);
                                position = _targetManager.Target.Position;
                            }

                            var newObjectDefinition = _selectedAssetInfo.CreateObjectDefinition(position, rotation);
                            if (newObjectDefinition != null)
                            {
                                if (_hoverPreviewObject != null)
                                {
                                    _hoverPreviewObject.Dispose();
                                    _hoverPreviewObject = null;
                                }

                                CreateObject.Invoke(newObjectDefinition);
                            }
                        }
                        if (ImGui.IsItemHovered())
                        {
                            using (ImRaii.Tooltip())
                            {
                                ImGui.TextUnformatted("Add this to the Stage being edited");
                            }
                        }
                    }
                }
            }
            else
            {
                const string message = "(No asset selected)";
                var size = ImGui.CalcTextSize(message);
                ImGui.SetCursorPosX(ImGui.GetContentRegionAvail().X / 2.0f - size.X / 2.0f);
                ImGui.TextDisabled(message);
            }

            ImGui.TableNextColumn();

            // Bookmarks & Tags
            using (ImRaii.TabBar("UserTabs"))
            {
                foreach (var tab in _userTabs)
                {
                    using (var tabItem = ImRaii.TabItem(tab.DisplayName, tab == _selectedUserTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                    {
                        if (tabItem.Success)
                        {
                            _selectedUserTab = null;

                            tab.DrawAction.Invoke();
                        }
                    }
                }
            }
        }
    }

    public void SetSelectionCallback<TAssetInfo>(string objectName, string propertyName, AssetType<TAssetInfo> assetType, Func<bool> stillValidCallback, Action<TAssetInfo> selectCallback)
    {
        _activeSelectionCallback = new SelectionCallback<TAssetInfo>(objectName, propertyName, assetType, stillValidCallback, selectCallback);
    }

    private void DrawGameResourcesTab()
    {
        float bottomBarHeight = ImGui.GetTextLineHeight() + ImGui.GetStyle().FramePadding.Y * 2.0f;
        var treeComponentSize = ImGui.GetContentRegionAvail() - new Vector2(0.0f, bottomBarHeight + ImGui.GetStyle().ItemSpacing.Y);
        var priorSelection = _gameResourceTreeView.SelectedItem;
        var priorHover = _gameResourceTreeView.HoveredItem;
        _gameResourceTreeView.Draw(treeComponentSize);

        if (priorSelection != _gameResourceTreeView.SelectedItem)
        {
            if (_gameResourceTreeView.SelectedItem is IGameFilesystemResource selectedGameResource)
            {
                _selectedAssetInfo = selectedGameResource.AssetInfo;
            }
            else
            {
                _selectedAssetInfo = null;
                HoveredAssetInfo = null;
            }
        }

        if (priorHover != _gameResourceTreeView.HoveredItem)
        {
            HoveredAssetInfo = (_gameResourceTreeView.HoveredItem as IGameFilesystemResource)?.AssetInfo ?? _selectedAssetInfo;
        }
    }

    private void DrawBookmarksTab()
    {
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FolderPlus, "New Folder"))
        {
            _ = CreateAndSelectBookmarkFolderAsync(null, "New Folder");
        }

        float bottomBarHeight = ImGui.GetTextLineHeight() + ImGui.GetStyle().FramePadding.Y * 2.0f;
        var treeComponentSize = ImGui.GetContentRegionAvail() - new Vector2(0.0f, bottomBarHeight + ImGui.GetStyle().ItemSpacing.Y);
        var priorSelection = _bookmarkTreeView.SelectedItem;
        var priorHover = _bookmarkTreeView.HoveredItem;
        _bookmarkTreeView.Draw(treeComponentSize);

        if (priorSelection != _bookmarkTreeView.SelectedItem)
        {
            if (_bookmarkTreeView.SelectedItem is IGameResourceBookmarkItem selectedGameResourceBookmark
                && _gameResourceAssetService.TryGetResource(selectedGameResourceBookmark.ResourceGamePath, out var selectedResource))
            {
                _selectedAssetInfo = selectedResource.AssetInfo;
            }
            else
            {
                _selectedAssetInfo = null;
                HoveredAssetInfo = null;
            }
        }

        if (priorHover != _bookmarkTreeView.HoveredItem)
        {
            if (_bookmarkTreeView.HoveredItem is IGameResourceBookmarkItem hoveredGameResourceBookmark
                && _gameResourceAssetService.TryGetResource(hoveredGameResourceBookmark.ResourceGamePath, out var hoveredResource))
            {
                HoveredAssetInfo = hoveredResource.AssetInfo ?? _selectedAssetInfo;
            }
            else
            {
                HoveredAssetInfo = _selectedAssetInfo;
            }
        }
    }

    private async Task CreateAndSelectBookmarkFolderAsync(IFolderBookmarkItem? parentItem, string name)
    {
        var newFolder = await _assetBookmarkService.CreateFolderAsync(name, parentItem).ConfigureAwait(false);
        _bookmarkTreeView.ExpandItem(newFolder);
        _bookmarkTreeView.RenamingItem = newFolder;
        _bookmarkTreeView.SelectedItem = newFolder;
        await _assetBookmarkService.SaveBookmarksAsync().ConfigureAwait(false);
    }

    private void DrawTagsTab()
    {
        ImGui.TextDisabled("(Not yet implemented)");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _windowSystem.AddWindow(this);
        _overlayService.DrawOverlays += this.DrawOverlays;

        return Task.CompletedTask;
    }

    private void DrawOverlays(IOverlayDrawContext context)
    {
        if (_hoverPreviewObject != null && _hoverPreviewObject.TryGetOrientedBounds(out var orientedBounds))
        {
            context.DrawBox(orientedBounds.Transform, orientedBounds.HalfExtents, 1.0f, new Vector4(0.9f, 0.9f, 0.9f, 0.5f));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _overlayService.DrawOverlays -= DrawOverlays;
        _windowSystem.RemoveWindow(this);

        if (_hoverPreviewObject != null)
        {
            _hoverPreviewObject.Dispose();
            _hoverPreviewObject = null;
        }

        return Task.CompletedTask;
    }
}
