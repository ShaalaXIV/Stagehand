using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Stagehand.Live;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;
using static Stagehand.Live.LiveVfxObject;
using Object = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace Stagehand.Services;

// We can't hold onto arbitrary scene object pointers across multiple frames because we do not know when they will be freed/resused,
// so instead the picker service stores their info in these classes. The OriginalPointer is used to identify this object in the scene.
public record class PickedObjectInfo(IntPtr OriginalPointer, Vector3 Position, Quaternion Rotation, Vector3 Scale, ObjectType ObjectType)
{
    public override string ToString()
    {
        return $"{ObjectType} at <{Position.X}, {Position.Y}, {Position.Z}>";
    }
}

public record class PickedBgObjectInfo(IntPtr OriginalPointer, Vector3 Position, Quaternion Rotation, Vector3 Scale, string ModelGamePath, ILiveModpack? Modpack, float Transparency, ByteColor? DyeColor) : PickedObjectInfo(OriginalPointer, Position, Rotation, Scale, ObjectType.BgObject)
{
    public override string ToString()
    {
        return $"{base.ToString()}\n{ModelGamePath}{(Modpack != null ? $"\nModpack {Modpack.DebugName}" : "")}";
    }
}

public record class PickedVfxObjectInfo(IntPtr OriginalPointer, Vector3 Position, Quaternion Rotation, Vector3 Scale, string VfxGamePath, ILiveModpack? Modpack, float Transparency, Vector4 TintColor) : PickedObjectInfo(OriginalPointer, Position, Rotation, Scale, ObjectType.VfxObject)
{
    public override string ToString()
    {
        return $"{base.ToString()}\n{VfxGamePath}{(Modpack != null ? $"\nModpack {Modpack.DebugName}" : "")}";
    }
}

public delegate void ViewportPickerObjectDelegate(PickedObjectInfo? pickedObject);
public delegate bool ViewportPickerCancelDelegate();

/// <summary>
/// Allows entering a modal object-picking mode where the user can click on an object in the viewport.
/// </summary>
public interface IViewportPickerService
{
    /// <summary>
    /// Whether object-picking mode is currently active.
    /// </summary>
    bool IsPicking { get; }

    /// <summary>
    /// Tries to enter object-picking mode with the given callbacks, if object-picking mode is not already active.
    /// </summary>
    /// <remarks>
    /// Left clicking on an object in the viewport will invoke <paramref name="objectClickDelegate"/>, and right clicking
    /// in the viewport or pressing the <see cref="IStagehandKeybinds.StopPicking"/> keybind will exit object-picking mode.
    /// </remarks>
    /// <param name="objectHoverDelegate">The delegate to invoke each frame that an object is hovered.</param>
    /// <param name="objectClickDelegate">The delegate to invoke when an object is clicked.</param>
    /// <param name="cancelDelegate">The delegate to invoke every frame to poll whether to exit object-picking mode.</param>
    /// <returns>True if object-picking mode was entered, or false if it was already active.</returns>
    bool TryStartPicking(ViewportPickerObjectDelegate? objectHoverDelegate, ViewportPickerObjectDelegate? objectClickDelegate, ViewportPickerCancelDelegate? cancelDelegate);

    /// <summary>
    /// Exits object-picking mode if it is active.
    /// </summary>
    void CancelPicking();
}

internal class ViewportPickerService : IViewportPickerService
{
    private record class PickingOperation(ViewportPickerObjectDelegate? ObjectHoverDelegate, ViewportPickerObjectDelegate? ObjectClickDelegate, ViewportPickerCancelDelegate? CancelDelegate);

    public bool IsPicking => _pickingOperation != null;

    private readonly IOverlayService _overlayService;
    private readonly IModelBvhCacheService _modelBvhCacheService;
    private readonly IResourceRedirectionService _resourceRedirectionService;
    private readonly IStagehandKeybinds _stagehandKeybinds;

    private PickingOperation? _pickingOperation = null;

    public ViewportPickerService(IOverlayService overlayService, IModelBvhCacheService modelBvhCacheService, IResourceRedirectionService resourceRedirectionService, IStagehandKeybinds stagehandKeybinds)
    {
        _overlayService = overlayService;
        _modelBvhCacheService = modelBvhCacheService;
        _resourceRedirectionService = resourceRedirectionService;
        _stagehandKeybinds = stagehandKeybinds;
    }

    public void CancelPicking()
    {
        var operation = Interlocked.Exchange(ref _pickingOperation, null);
        if (operation != null)
        {
            operation.ObjectHoverDelegate?.Invoke(null);
            _overlayService.DrawOverlays -= OnDrawOverlays;
            _overlayService.IsPicking = false;
            _stagehandKeybinds.StopPicking.Pressed -= CancelPicking;
        }
    }

    public bool TryStartPicking(ViewportPickerObjectDelegate? objectHoverDelegate, ViewportPickerObjectDelegate? objectClickDelegate, ViewportPickerCancelDelegate? cancelDelegate)
    {
        if (Interlocked.CompareExchange(ref _pickingOperation, new PickingOperation(objectHoverDelegate, objectClickDelegate, cancelDelegate), null) == null)
        {
            _overlayService.DrawOverlays += OnDrawOverlays;
            _overlayService.IsPicking = true;
            _stagehandKeybinds.StopPicking.Pressed += CancelPicking;
            return true;
        }
        else
        {
            return false;
        }
    }

    private unsafe void OnDrawOverlays(IOverlayDrawContext drawContext)
    {
        var worldObject = World.Instance();
        var cameraManager = CameraManager.Instance();
        var pickingOperation = _pickingOperation;
        
        bool isOverNonOverlayWindow = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
            && !ImGui.IsWindowHovered();
        if (worldObject != null && cameraManager != null && pickingOperation != null
            && (pickingOperation.ObjectClickDelegate != null || pickingOperation.ObjectHoverDelegate != null))
        {
            if (!isOverNonOverlayWindow)
            {
                var activeCamera = cameraManager->CurrentCamera;
                var mouseRay = activeCamera->ScreenPointToRay(ImGui.GetMousePos());
                float nearestDistanceSq = float.MaxValue;
                Object* nearestObject = null;
                DrawObjectOverlays((Object*)worldObject, drawContext, mouseRay, ref nearestDistanceSq, ref nearestObject);

                if (nearestObject != null)
                {
                    FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds orientedBounds = new();
                    ((DrawObject*)nearestObject)->ComputeOrientedBounds(&orientedBounds);

                    drawContext.DrawBox(orientedBounds.Transform, orientedBounds.HalfExtents, 1.0f, Vector4.One);

                    ImGui.GetIO().WantCaptureMouse = true;

                    var objectInfo = MakeObjectInfo(nearestObject);
                    pickingOperation.ObjectHoverDelegate?.Invoke(objectInfo);

                    using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(8.0f)))
                    using (ImRaii.Tooltip())
                    {
                        ImGui.TextUnformatted(objectInfo.ToString());
                    }

                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        pickingOperation.ObjectClickDelegate?.Invoke(objectInfo);
                        CancelPicking();
                    }
                }
                else
                {
                    pickingOperation.ObjectHoverDelegate?.Invoke(null);
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        CancelPicking();
                        return;
                    }
                }
            }
            else
            {
                pickingOperation.ObjectHoverDelegate?.Invoke(null);
            }

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) || ImGui.IsKeyDown(ImGuiKey.Escape) || (pickingOperation.CancelDelegate != null && pickingOperation.CancelDelegate.Invoke()))
            {
                CancelPicking();
            }
        }
    }

    private unsafe void DrawObjectOverlays(Object* obj, IOverlayDrawContext overlay, Ray mouseRay, ref float nearestDistanceSq, ref Object* nearestObject)
    {
        // Draw this object

        var type = obj->GetObjectType();
        var xDir = Vector3.Transform(Vector3.UnitX, obj->Rotation);
        var yDir = Vector3.Transform(Vector3.UnitY, obj->Rotation);
        var zDir = Vector3.Transform(Vector3.UnitZ, obj->Rotation);

        if (type == ObjectType.BgObject || type == ObjectType.Light || type == ObjectType.CharacterBase || type == ObjectType.VfxObject || type == ObjectType.Decal || type == ObjectType.EnvSpace || type == ObjectType.EnvLocation)
        {
            var drawObj = (DrawObject*)obj;

            // Check for mouse hit
            if (type == ObjectType.BgObject)
            {
                // BgObjects are hit-tested based on their mesh using a StaticBvh

                var bgObject = (BgObject*)drawObj;
                // NOTE: Accessing the bounds of a BgObject that has not yet loaded causes an access violation. Seems like strange design but whatever.
                if (bgObject->ModelResourceHandle->LoadState >= 7 && !bgObject->ModelResourceHandle->FileName.ToString().Contains("lightshaft", StringComparison.Ordinal))
                {
                    FFXIVClientStructs.FFXIV.Common.Math.SphereBounds outSphereBounds;
                    bool broadphaseHit = drawObj->ComputeSphereBounds(&outSphereBounds)->IntersectsRay(mouseRay, out var hitPoint);

                    if (broadphaseHit)
                    {
                        Matrix4x4 translationMatrix = Matrix4x4.CreateTranslation(obj->Position);
                        Matrix4x4 rotationMatrix = Matrix4x4.CreateFromQuaternion(obj->Rotation);
                        Matrix4x4 scaleMatrix = Matrix4x4.CreateScale(obj->Scale);

                        Matrix4x4 matrix = scaleMatrix * rotationMatrix * translationMatrix;
                        if (Matrix4x4.Invert(matrix, out var inverseMatrix))
                        {
                            var localSpaceStart = Vector3.Transform(mouseRay.Origin, inverseMatrix);
                            var localSpaceDirection = Vector3.TransformNormal(mouseRay.Direction, inverseMatrix);

                            // Reverse resolve the modpack and original input resource name from the final BgObject resource handle path
                            _resourceRedirectionService.ParseSourceGamePathAndModpack(bgObject->ModelResourceHandle->FileName.ToString(), out var modelPath, out var modpack);
                            if (_modelBvhCacheService.TryIntersectModel(modelPath, modpack, localSpaceStart, localSpaceDirection, out var intersectionPoint, out var intersectionNormal))
                            {
                                var worldSpaceIntersection = Vector3.Transform(intersectionPoint, matrix);
                                var worldSpaceNormal = Vector3.TransformNormal(intersectionNormal, matrix);

                                var distanceSquared = (worldSpaceIntersection - (Vector3)mouseRay.Origin).LengthSquared();
                                if (distanceSquared < nearestDistanceSq)
                                {
                                    nearestDistanceSq = distanceSquared;
                                    nearestObject = obj;
                                }
                            }
                        }
                    }
                }
            }
            // TODO: Fix!
            //else if (type == ObjectType.VfxObject)
            //{
            //    // VfxObjects are hit-tested based on a small sphere at their origin
            //    var clickSphere = new FFXIVClientStructs.FFXIV.Common.Math.SphereBounds() { CenterPoint = obj->Position, Radius = 0.25f };
            //    mouseHit = clickSphere.IntersectsRay(mouseRay, out var worldSpaceIntersection);
            //    preciseHit = mouseHit;

            //    var distanceSquared = ((Vector3)worldSpaceIntersection - (Vector3)mouseRay.Origin).LengthSquared();
            //    if (distanceSquared < nearestDistanceSq)
            //    {
            //        nearestDistanceSq = distanceSquared;
            //        nearestObject = obj;
            //    }
            //}
        }

        // Recurse
        foreach (var child in obj->ChildObjects)
        {
            DrawObjectOverlays(child, overlay, mouseRay, ref nearestDistanceSq, ref nearestObject);
        }
    }

    private unsafe PickedObjectInfo MakeObjectInfo(Object* sceneObject)
    {
        ObjectType objectType = sceneObject->GetObjectType();
        if (objectType == ObjectType.BgObject)
        {
            var bgObject = (BgObject*)sceneObject;
            ByteColor? dyeColor = null;
            if (bgObject->StainBuffer != null)
            {
                dyeColor = bgObject->StainBuffer->SrgbByteColor;
            }
            string modelGamePath = "";
            ILiveModpack? modpack = null;
            if (bgObject->ModelResourceHandle != null)
            {
                _resourceRedirectionService.ParseSourceGamePathAndModpack(bgObject->ModelResourceHandle->FileName.ToString(), out modelGamePath, out modpack);
            }
            return new PickedBgObjectInfo((IntPtr)sceneObject, bgObject->Position, bgObject->Rotation, bgObject->Scale, modelGamePath, modpack, bgObject->GetTransparency(), dyeColor);
        }
        else if (objectType == ObjectType.VfxObject)
        {
            var vfxObject = (VfxObject*)sceneObject;
            string vfxGamePath = "";
            ILiveModpack? modpack = null;
            var vfxResource = (VfxResourceInstance__Internal*)vfxObject->VfxResourceInstance;
            if (vfxResource != null)
            {
                var resourceUnk = vfxResource->VfxResourceUnk;
                if (resourceUnk != null)
                {
                    var vfxResourceHandle = (ResourceHandle*)resourceUnk->ApricotResourceHandle;
                    if (vfxResourceHandle != null)
                    {
                        vfxGamePath = vfxResourceHandle->FileName.ToString();
                        _resourceRedirectionService.ParseSourceGamePathAndModpack(vfxResourceHandle->FileName.ToString(), out vfxGamePath, out modpack);
                    }
                }
            }
            return new PickedVfxObjectInfo((IntPtr)sceneObject, vfxObject->Position, vfxObject->Rotation, vfxObject->Scale, vfxGamePath, modpack, vfxObject->GetTransparency(), vfxObject->Color);
        }
        else
        {
            return new PickedObjectInfo((IntPtr)sceneObject, sceneObject->Position, sceneObject->Rotation, sceneObject->Scale, objectType);
        }
    }
}
