using Dalamud.Game.ClientState.Keys;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stagehand.Services;

/// <summary>
/// Manages the keybinds Stagehand supports.
/// </summary>
public interface IStagehandKeybinds
{
    // Editor (Clipboard)
    IKeybindAction EditorCutObject { get; }
    IKeybindAction EditorCopyObject { get; }
    IKeybindAction EditorPasteObject { get; }

    // Editor (Objects)
    IKeybindAction EditorDeleteObject { get; }
    IKeybindAction EditorDuplicateObject { get; }
    IKeybindAction EditorHideObject { get; }
    IKeybindAction EditorUnhideObject { get; }
    IKeybindAction EditorSnapObjectToGround { get; }
    IKeybindAction EditorSnapRotateObjectToGround { get; }
    IKeybindAction EditorGroupObjects { get; }
    IKeybindAction EditorUngroupObjects { get; }
    IKeybindAction EditorCenterGroupPivots { get; }

    // Editor
    IKeybindAction EditorUndo { get; }
    IKeybindAction EditorRedo { get; }
    IKeybindAction EditorSave { get; }

    // Picking
    IKeybindAction StopPicking { get; }

    // Quick Picker Window
    IKeybindAction ToggleQuickPickerWindow { get; }
    IKeybindAction StartQuickPicking { get; }
}

internal class StagehandKeybinds : IStagehandKeybinds, IHostedService, IDisposable
{
    private const string EditorClipboardGroupName = "Stage Editor (Clipboard)";
    private const string EditorObjectsGroupName = "Stage Editor (Objects)";
    private const string EditorGroupName = "Stage Editor";
    private const string PickingGroupName = "Picking";
    private const string QuickPickerGroupName = "Quick Picker";

    private readonly IKeybindService _keybindService;

    private bool _isDisposed = false;

    // Note that these variable names drive the keybind IDs, and so changing them will reset that action's keybinds for users.

    public IKeybindAction EditorCutObject { get; }
    public IKeybindAction EditorCopyObject { get; }
    public IKeybindAction EditorPasteObject { get; }

    public IKeybindAction EditorDeleteObject { get; }
    public IKeybindAction EditorDuplicateObject { get; }
    public IKeybindAction EditorHideObject { get; }
    public IKeybindAction EditorUnhideObject { get; }
    public IKeybindAction EditorSnapObjectToGround { get; }
    public IKeybindAction EditorSnapRotateObjectToGround { get; }
    public IKeybindAction EditorGroupObjects { get; }
    public IKeybindAction EditorUngroupObjects { get; }
    public IKeybindAction EditorCenterGroupPivots { get; }

    public IKeybindAction EditorUndo { get; }
    public IKeybindAction EditorRedo { get; }
    public IKeybindAction EditorSave { get; }

    public IKeybindAction StopPicking { get; }

    public IKeybindAction ToggleQuickPickerWindow { get; }
    public IKeybindAction StartQuickPicking { get; }

    public StagehandKeybinds(IKeybindService keybindService)
    {
        _keybindService = keybindService;

        //
        // Editor (Clipboard)
        //
        EditorCutObject = _keybindService.RegisterAction(new(nameof(EditorCutObject),
            "Cut Object",
            EditorClipboardGroupName,
            "Removes the selected object from the stage and places it on the system clipboard.",
            new Keybind(VirtualKey.X, KeybindModifierKeys.Control)));

        EditorCopyObject = _keybindService.RegisterAction(new(nameof(EditorCopyObject),
            "Copy Object",
            EditorClipboardGroupName,
            "Copies the selected object onto the system clipboard.",
            new Keybind(VirtualKey.C, KeybindModifierKeys.Control)));

        EditorPasteObject = _keybindService.RegisterAction(new(nameof(EditorPasteObject),
            "Paste Object",
            EditorClipboardGroupName,
            "Adds an object to the stage from the system clipboard.",
            new Keybind(VirtualKey.V, KeybindModifierKeys.Control)));

        //
        // Editor (Objects)
        //
        EditorDeleteObject = _keybindService.RegisterAction(new(nameof(EditorDeleteObject),
            "Delete Object",
            EditorObjectsGroupName,
            "Deletes the selected object in the stage being edited.",
            new Keybind(VirtualKey.DELETE, KeybindModifierKeys.None)));

        EditorDuplicateObject = _keybindService.RegisterAction(new(nameof(EditorDuplicateObject),
            "Duplicate Object",
            EditorObjectsGroupName,
            "Duplicates the selected object in the stage being edited.",
            new Keybind(VirtualKey.D, KeybindModifierKeys.Control)));

        EditorHideObject = _keybindService.RegisterAction(new(nameof(EditorHideObject),
            "Hide Object",
            EditorObjectsGroupName,
            "Hides the selected object.",
            new Keybind(VirtualKey.H, KeybindModifierKeys.Control)));

        EditorUnhideObject = _keybindService.RegisterAction(new(nameof(EditorUnhideObject),
            "Unhide Object",
            EditorObjectsGroupName,
            "Unhides the selected object.",
            new Keybind(VirtualKey.H, KeybindModifierKeys.Control | KeybindModifierKeys.Shift)));

        EditorSnapObjectToGround = _keybindService.RegisterAction(new(nameof(EditorSnapObjectToGround),
            "Snap Object to Ground",
            EditorObjectsGroupName,
            "Moves the object downwards so that its origin point lies on whatever is directly below it.",
            new Keybind(VirtualKey.DOWN, KeybindModifierKeys.Control)));

        EditorSnapRotateObjectToGround = _keybindService.RegisterAction(new(nameof(EditorSnapRotateObjectToGround),
            "Snap & Rotate Object to Ground",
            EditorObjectsGroupName,
            "Moves the object downwards and rotates it so that its origin point lies on whatever is directly below it.",
            new Keybind(VirtualKey.DOWN, KeybindModifierKeys.Control | KeybindModifierKeys.Shift)));

        EditorGroupObjects = _keybindService.RegisterAction(new(nameof(EditorGroupObjects),
            "Group Objects",
            EditorObjectsGroupName,
            "Creates a new group with any selected objects.",
            new Keybind(VirtualKey.G, KeybindModifierKeys.Control)));

        EditorUngroupObjects = _keybindService.RegisterAction(new(nameof(EditorUngroupObjects),
            "Ungroup Objects",
            EditorObjectsGroupName,
            "Dissolves any selected groups, preserving their contents.",
            new Keybind(VirtualKey.G, KeybindModifierKeys.Control | KeybindModifierKeys.Shift)));

        EditorCenterGroupPivots = _keybindService.RegisterAction(new(nameof(EditorCenterGroupPivots),
            "Center Group Pivots",
            EditorObjectsGroupName,
            "Moves the origin of any selected groups to the center of their child objects.",
            Keybind.Unassigned));

        //
        // Editor
        //
        EditorUndo = _keybindService.RegisterAction(new(nameof(EditorUndo),
            "Undo",
            EditorGroupName,
            "Undoes the most recently performed or undone action.", 
           new Keybind(VirtualKey.Z, KeybindModifierKeys.Control)));

        EditorRedo = _keybindService.RegisterAction(new(nameof(EditorRedo),
            "Redo",
            EditorGroupName,
            "Redoes the most recently undone action.",
            new Keybind(VirtualKey.Z, KeybindModifierKeys.Control | KeybindModifierKeys.Shift)));

        EditorSave = _keybindService.RegisterAction(new(nameof(EditorSave),
            "Save",
            EditorGroupName,
            "Saves the stage being edited.",
            new Keybind(VirtualKey.S, KeybindModifierKeys.Control)));

        //
        // Picking
        //
        StopPicking = _keybindService.RegisterAction(new(nameof(StopPicking),
            "Stop Picking",
            PickingGroupName,
            "Cancels the current pick-from-world action.",
            new Keybind(VirtualKey.ESCAPE, KeybindModifierKeys.None)));

        //
        // Quick Picker
        //
        ToggleQuickPickerWindow = _keybindService.RegisterAction(new(nameof(ToggleQuickPickerWindow),
            "Toggle Quick Picker Window",
            QuickPickerGroupName,
            "Shows or hides the Quick Picker window.",
            Keybind.Unassigned));

        StartQuickPicking = _keybindService.RegisterAction(new(nameof(StartQuickPicking),
            "Start Quick Picking",
            QuickPickerGroupName,
            "Shows the Quick Picker window and begins picking.",
            new Keybind(VirtualKey.P, KeybindModifierKeys.Alt)));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;

            _keybindService.UnregisterAction(StopPicking);

            _keybindService.UnregisterAction(EditorSave);
            _keybindService.UnregisterAction(EditorRedo);
            _keybindService.UnregisterAction(EditorUndo);

            _keybindService.UnregisterAction(EditorCenterGroupPivots);
            _keybindService.UnregisterAction(EditorUngroupObjects);
            _keybindService.UnregisterAction(EditorGroupObjects);
            _keybindService.UnregisterAction(EditorSnapRotateObjectToGround);
            _keybindService.UnregisterAction(EditorSnapObjectToGround);
            _keybindService.UnregisterAction(EditorUnhideObject);
            _keybindService.UnregisterAction(EditorHideObject);
            _keybindService.UnregisterAction(EditorDuplicateObject);
            _keybindService.UnregisterAction(EditorDeleteObject);

            _keybindService.UnregisterAction(EditorPasteObject);
            _keybindService.UnregisterAction(EditorCopyObject);
            _keybindService.UnregisterAction(EditorCutObject);
        }
    }
}
