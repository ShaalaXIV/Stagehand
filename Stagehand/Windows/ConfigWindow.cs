using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Microsoft.Extensions.Hosting;
using Stagehand.Services;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stagehand.Windows;

public interface IConfigWindow : IHostedService
{
    void Show();
}

public class ConfigWindow : Window, IConfigWindow, IDisposable
{
    private readonly ILogger _logger;
    private readonly IDalamudPluginInterface _dalamudPluginInterface;
    private readonly IKeybindService _keybindService;
    private readonly ILocalDefinitionService _localDefinitionService;
    private readonly WindowSystem _windowSystem;
    private readonly StagehandConfiguration _configuration;

    private readonly FileDialogManager _fileDialogManager;
    private IKeybindAction? _recordingKeybindAction = null;

    public ConfigWindow(ILogger<ConfigWindow> logger, IDalamudPluginInterface dalamudPluginInterface, IKeybindService keybindService, ILocalDefinitionService localDefinitionService, WindowSystem windowSystem, StagehandConfiguration configuration) : base("Stagehand Configuration")
    {
        SizeCondition = ImGuiCond.Always;

        _logger = logger;
        _dalamudPluginInterface = dalamudPluginInterface;
        _keybindService = keybindService;
        _localDefinitionService = localDefinitionService;
        _windowSystem = windowSystem;
        _configuration = configuration;

        _fileDialogManager = new FileDialogManager();
    }

    void IConfigWindow.Show()
    {
        IsOpen = true;
        RequestFocus = true;
    }

    public void Dispose() { }

    public override void Draw()
    {
        using (ImRaii.ItemWidth(ImGui.GetContentRegionAvail().X * 2.0f / 3.0f))
        {
            using (var tabStrip = ImRaii.TabBar("###ConfigTabs"u8))
            {
                if (tabStrip.Success)
                {
                    using (var generalTab = ImRaii.TabItem("General"u8))
                    {
                        if (generalTab.Success)
                        {
                            DrawGeneralSection();
                        }
                    }

                    using (var keybindsTab = ImRaii.TabItem("Keybinds"u8))
                    {
                        if (keybindsTab.Success)
                        {
                            DrawKeybindsSection();
                        }
                    }

                    using (var debugTab = ImRaii.TabItem("Debug"u8))
                    {
                        if (debugTab.Success)
                        {
                            DrawDebugSection();
                        }
                    }
                }
            }
        }

        _fileDialogManager.Draw();
    }

    private void DrawGeneralSection()
    {
        var definitionLibraryPath = _configuration.DefinitionLibraryPath;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 2.0f / 3.0f - ImGui.GetStyle().ItemInnerSpacing.X - ImGui.GetFrameHeight());
        if (ImGui.InputText("###LibraryFolder", ref definitionLibraryPath, 512, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
        {
            // TODO: Might want to prompt whether to move the player's local definitions
            // TODO: Migrate auto load conditions
            _localDefinitionService.LocalDefinitionDirectory = definitionLibraryPath;
        }
        ImGui.SameLine(0.0f, ImGui.GetStyle().ItemInnerSpacing.X);
        if (ImGuiComponents.IconButton("###BrowseLibraryFolder", FontAwesomeIcon.Folder, new Vector2(ImGui.GetFrameHeight() / ImGuiHelpers.GlobalScale)))
        {
            _fileDialogManager.OpenFolderDialog("Definition Library Folder", (confirmed, path) =>
            {
                if (confirmed)
                {
                    _localDefinitionService.LocalDefinitionDirectory = path;
                }
            }, definitionLibraryPath);
        }
        ImGui.SameLine(0.0f, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Definition Library Folder");
        ImGui.SameLine(0.0f, 0.0f);
        ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight());
        if (ImGuiComponents.IconButton("###OpenLibraryFolder", FontAwesomeIcon.ExternalLinkAlt, new Vector2(ImGui.GetFrameHeight() / ImGuiHelpers.GlobalScale)))
        {
            Process.Start("explorer", $"/root, {_configuration.DefinitionLibraryPath}");
        }
        if (ImGui.IsItemHovered())
        {
            using (ImRaii.Tooltip())
            {
                ImGui.TextUnformatted(_configuration.DefinitionLibraryPath);
                ImGui.Separator();
                ImGui.TextDisabled("Click to open");
            }
        }

        var autosavePath = _configuration.AutosavePath;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 2.0f / 3.0f - ImGui.GetStyle().ItemInnerSpacing.X - ImGui.GetFrameHeight());
        if (ImGui.InputTextWithHint("###AutosaveFolder", "(leave blank for default)", ref autosavePath, 512, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
        {
            _configuration.AutosavePath = autosavePath;
            _configuration.Save();
        }
        ImGui.SameLine(0.0f, ImGui.GetStyle().ItemInnerSpacing.X);
        if (ImGuiComponents.IconButton("###BrowseAutosaveFolder", FontAwesomeIcon.Folder, new Vector2(ImGui.GetFrameHeight() / ImGuiHelpers.GlobalScale)))
        {
            _fileDialogManager.OpenFolderDialog("Autosave Folder", (confirmed, path) =>
            {
                if (confirmed)
                {
                    _configuration.AutosavePath = path;
                    _configuration.Save();
                }
            }, autosavePath);
        }
        ImGui.SameLine(0.0f, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Autosave Folder");
        ImGui.SameLine(0.0f, 0.0f);
        ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight());
        if (ImGuiComponents.IconButton("###OpenAutosaveFolder", FontAwesomeIcon.ExternalLinkAlt, new Vector2(ImGui.GetFrameHeight() / ImGuiHelpers.GlobalScale)))
        {
            Process.Start("explorer", $"/root, {_configuration.FinalAutosavePath}");
        }
        if (ImGui.IsItemHovered())
        {
            using (ImRaii.Tooltip())
            {
                ImGui.TextUnformatted(_configuration.FinalAutosavePath);
                ImGui.Separator();
                ImGui.TextDisabled("Click to open");
            }
        }
    }

    private void DrawKeybindsSection()
    {
        lock (_keybindService.KeybindGroupLock)
        {
            foreach (var group in _keybindService.KeybindGroups)
            {
                if (group.Actions.Count > 0)
                {
                    using (var header = ImRaii.Header(group.DisplayName, ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        if (header.Success)
                        {
                            foreach (var action in group.Actions)
                            {
                                var startX = ImGui.GetCursorPosX();
                                var columnWidth = ImGui.GetContentRegionAvail().X * 2.0f / 3.0f;
                                ImGui.AlignTextToFramePadding();
                                ImGui.TextUnformatted(action.Info.DisplayName);
                                if (action.Info.Description != "")
                                {
                                    ImGui.SameLine();
                                    ImGuiComponents.HelpMarker(action.Info.Description);
                                }

                                ImGui.SameLine();
                                ImGui.SetCursorPosX(startX + columnWidth);

                                bool isRecordingKeybind = _recordingKeybindAction == action;
                                ImGui.SetNextItemWidth(-1);
                                using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 2.0f, isRecordingKeybind))
                                using (ImRaii.PushColor(ImGuiCol.Border, ImGuiColors.DPSRed, isRecordingKeybind))
                                {
                                    if (ImGui.Button(isRecordingKeybind ? "(Recording)" : action.CurrentKeybind.ToString(), new(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight())))
                                    {
                                        if (isRecordingKeybind)
                                        {
                                            _recordingKeybindAction = null;
                                            _keybindService.KeybindPressed -= OnKeybindPressed;
                                            _keybindService.CancelListeningForKeybind();
                                        }
                                        else
                                        {
                                            if (_recordingKeybindAction == null)
                                            {
                                                _keybindService.StartListeningForKeybind();
                                                _keybindService.KeybindPressed += OnKeybindPressed;
                                            }
                                            _recordingKeybindAction = action;
                                        }
                                    }
                                    else if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                                    {
                                        if (isRecordingKeybind)
                                        {
                                            _recordingKeybindAction = null;
                                            _keybindService.KeybindPressed -= OnKeybindPressed;
                                            _keybindService.CancelListeningForKeybind();
                                        }
                                        _keybindService.TrySetActionKeybind(action, Keybind.Unassigned);
                                    }
                                }

                                if (ImGui.IsItemHovered())
                                {
                                    using (ImRaii.Tooltip())
                                    using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + 300.0f * ImGuiHelpers.GlobalScale))
                                    {
                                        if (isRecordingKeybind)
                                        {
                                            ImGui.TextWrapped("Press a key to set the new keybind, left click to cancel recording, or right click to clear the current binding.");
                                        }
                                        else
                                        {
                                            ImGui.TextWrapped("Left click to record a new keybind, or right click to clear the current binding.");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private void DrawDebugSection()
    {
        var logMemoryResourceUntouched = _configuration.LogMemoryResourceUntouched;
        if (ImGui.Checkbox("Log Memory Resource Untouched", ref logMemoryResourceUntouched))
        {
            _configuration.LogMemoryResourceUntouched = logMemoryResourceUntouched;
            _configuration.Save();
        }
        var logMemoryResourceHandled = _configuration.LogMemoryResourceHandled;
        if (ImGui.Checkbox("Log Memory Resource Handled", ref logMemoryResourceHandled))
        {
            _configuration.LogMemoryResourceHandled = logMemoryResourceHandled;
            _configuration.Save();
        }
        var logModpackResourceUntouched = _configuration.LogModpackResourceUntouched;
        if (ImGui.Checkbox("Log Modpack Resource Untouched", ref logModpackResourceUntouched))
        {
            _configuration.LogModpackResourceUntouched = logModpackResourceUntouched;
            _configuration.Save();
        }
        var logModpackResourceHandled = _configuration.LogModpackResourceHandled;
        if (ImGui.Checkbox("Log Modpack Resource Handled", ref logModpackResourceHandled))
        {
            _configuration.LogModpackResourceHandled = logModpackResourceHandled;
            _configuration.Save();
        }
    }

    public override void OnClose()
    {
        base.OnClose();

        if (_recordingKeybindAction != null)
        {
            _recordingKeybindAction = null;
            _keybindService.KeybindPressed -= OnKeybindPressed;
            _keybindService.CancelListeningForKeybind();
        }
    }

    private void OnKeybindPressed(Keybind pressedKeybind)
    {
        if (_recordingKeybindAction != null)
        {
            _keybindService.TrySetActionKeybind(_recordingKeybindAction, pressedKeybind);
            _keybindService.KeybindPressed -= OnKeybindPressed;
            _recordingKeybindAction = null;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _windowSystem.AddWindow(this);

        _dalamudPluginInterface.UiBuilder.OpenConfigUi += Toggle;

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _dalamudPluginInterface.UiBuilder.OpenConfigUi -= Toggle;

        _windowSystem.RemoveWindow(this);

        return Task.CompletedTask;
    }
}
