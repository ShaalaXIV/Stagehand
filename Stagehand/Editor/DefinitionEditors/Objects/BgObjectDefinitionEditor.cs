using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Microsoft.Extensions.DependencyInjection;
using Stagehand.AssetLibrary.Assets;
using Stagehand.Definitions.Objects;
using Stagehand.Editor.Services;
using Stagehand.Live;
using Stagehand.Services;
using Stagehand.Utils;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Object = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace Stagehand.Editor.DefinitionEditors.Objects;

internal class BgObjectDefinitionEditor : ObjectDefinitionEditor<BgObjectDefinition>
{
    public static readonly DefinitionTypeInfo StaticTypeInfo = new DefinitionTypeInfo("Background Object", "A static mesh in the scene.", FontAwesomeIcon.Cube);

    public override DefinitionTypeInfo TypeInfo => StaticTypeInfo;

    private readonly IModelBvhCacheService _modelBvhCacheService;
    private readonly IResourceRedirectionService _resourceRedirectionService;
    private readonly IEditorHitTestService _hitTestService;
    private readonly EditorHitTestModel _hitTestModel;

    public string ModelGamePath
    {
        get => Definition.ModelGamePath;
        set => SetPropertyValue(SetModelGamePathInternal, value, Definition.ModelGamePath);
    }

    public float Opacity
    {
        get => Definition.Opacity;
        set => SetPropertyValue(value => Definition.Opacity = value, value, Definition.Opacity);
    }

    public Vector4 DyeColor
    {
        get => Definition.DyeColor;
        set => SetPropertyValue(value => Definition.DyeColor = value, value, Definition.DyeColor);
    }

    public BgObjectDefinitionEditor(IServiceProvider serviceProvider, BgObjectDefinition definition, string key, StageDefinitionEditor stage) : base(serviceProvider, definition, key, stage)
    {
        _modelBvhCacheService = serviceProvider.GetRequiredService<IModelBvhCacheService>();
        _resourceRedirectionService = serviceProvider.GetRequiredService<IResourceRedirectionService>();
        _hitTestService = serviceProvider.GetRequiredService<IEditorHitTestService>();
        _hitTestModel = new EditorHitTestModel(this, ModelGamePath, serviceProvider.GetRequiredService<IModelBvhCacheService>(), serviceProvider.GetRequiredService<IDataManager>())
        {
            Position = Position,
            Rotation = RotationQuaternion,
            Scale = Scale,
            Modpack = GetPreviewModpack(),
        };

        OutlinerNode.ContextMenuItems = GenerateContextMenuItems();
    }

    protected override IEnumerable<OutlinerContextMenuItem> GenerateContextMenuItems()
    {
        yield return new KeybindOutlinerContextMenuItem(StagehandKeybinds.EditorSnapObjectToGround, _ => SnapToGround());
        yield return new KeybindOutlinerContextMenuItem(StagehandKeybinds.EditorSnapRotateObjectToGround, _ => SnapRotateToGround());
        foreach (var baseItem in base.GenerateContextMenuItems())
        {
            yield return baseItem;
        }
    }

    private void SnapToGround()
    {
        using (TransactionManager.BeginTransactionGroup($"Snap {DisplayName} to Ground"))
        {
            SnapToGround(rotateToo: false);
        }
    }

    private void SnapRotateToGround()
    {
        using (TransactionManager.BeginTransactionGroup($"Snap & Rotate {DisplayName} to Ground"))
        {
            SnapToGround(rotateToo: true);
        }
    }

    private unsafe void SnapToGround(bool rotateToo)
    {
        Vector3 direction = -Vector3.UnitY;
        var ray = new Ray(WorldPosition - direction * 0.01f, direction);

        if (HitTestBgObjects(ray, out float distance, out _, out var hitPosition, out var hitNormal))
        {
            WorldPosition += direction * distance;
            if (rotateToo)
            {
                var targetUpVector = Vector3.Normalize(hitNormal);
                var currentUpVector = Vector3.Transform(Vector3.UnitY, WorldRotationQuaternion);
                var dot = Vector3.Dot(currentUpVector, targetUpVector);
                if (dot < 0.99f)
                {
                    var axis = Vector3.Cross(currentUpVector, targetUpVector);
                    var angle = MathF.Acos(dot);
                    WorldRotationQuaternion = Quaternion.Concatenate(WorldRotationQuaternion, Quaternion.Normalize(new Quaternion(axis, 1.0f + dot)));
                }
            }
        }
    }

    private unsafe bool HitTestBgObjects(Ray ray, out float nearestDistance, out Object* nearestObject, out Vector3 hitPosition, out Vector3 hitNormal)
    {
        var worldObject = World.Instance();
        float nearestDistanceSq = float.MaxValue;
        nearestObject = null;
        hitPosition = Vector3.Zero;
        hitNormal = Vector3.Zero;

        if (worldObject != null)
        {
            HitTestObject((Object*)worldObject, ray, ref nearestDistanceSq, ref nearestObject, ref hitPosition, ref hitNormal);
        }

        nearestDistance = MathF.Sqrt(nearestDistanceSq);

        return nearestObject != null;
    }

    private unsafe void HitTestObject(Object* obj, Ray ray, ref float nearestDistanceSq, ref Object* nearestObject, ref Vector3 nearestPosition, ref Vector3 nearestHitNormal)
    {
        var type = obj->GetObjectType();

        // Check for mouse hit
        if (type == ObjectType.BgObject && PreviewLiveObject is LiveBgObject liveBgObject && !liveBgObject.Equals((BgObject*)obj))
        {
            // BgObjects are hit-tested based on their mesh using a StaticBvh

            var bgObject = (BgObject*)obj;
            // NOTE: Accessing the bounds of a BgObject that has not yet loaded causes an access violation. Seems like strange design but whatever.
            if (bgObject->ModelResourceHandle->LoadState >= 7 && !bgObject->ModelResourceHandle->FileName.ToString().Contains("lightshaft", StringComparison.Ordinal))
            {
                FFXIVClientStructs.FFXIV.Common.Math.SphereBounds outSphereBounds;
                bool broadphaseHit = bgObject->ComputeSphereBounds(&outSphereBounds)->IntersectsRay(ray, out var hitPoint);

                if (broadphaseHit)
                {
                    Matrix4x4 translationMatrix = Matrix4x4.CreateTranslation(obj->Position);
                    Matrix4x4 rotationMatrix = Matrix4x4.CreateFromQuaternion(obj->Rotation);
                    Matrix4x4 scaleMatrix = Matrix4x4.CreateScale(obj->Scale);

                    Matrix4x4 matrix = scaleMatrix * rotationMatrix * translationMatrix;
                    if (Matrix4x4.Invert(matrix, out var inverseMatrix))
                    {
                        var localSpaceStart = Vector3.Transform(ray.Origin, inverseMatrix);
                        var localSpaceDirection = Vector3.TransformNormal(ray.Direction, inverseMatrix);

                        // Reverse resolve the modpack and original input resource name from the final BgObject resource handle path
                        _resourceRedirectionService.ParseSourceGamePathAndModpack(bgObject->ModelResourceHandle->FileName.ToString(), out var modelPath, out var modpack);
                        if (_modelBvhCacheService.TryIntersectModel(modelPath, modpack, localSpaceStart, localSpaceDirection, out var intersectionPoint, out var intersectionNormal, forceLoad: true))
                        {
                            var worldSpaceIntersection = Vector3.Transform(intersectionPoint, matrix);
                            var worldSpaceNormal = Vector3.TransformNormal(intersectionNormal, matrix);

                            var distanceSquared = (worldSpaceIntersection - (Vector3)ray.Origin).LengthSquared();
                            if (distanceSquared < nearestDistanceSq)
                            {
                                nearestDistanceSq = distanceSquared;
                                nearestObject = obj;
                                nearestPosition = worldSpaceIntersection;
                                nearestHitNormal = worldSpaceNormal;
                            }
                        }
                    }
                }
            }
        }
        else if (type == ObjectType.Terrain)
        {
            TerrainHitTesting.HitTestTerrain(_modelBvhCacheService, (Terrain*)obj, ray, ref nearestDistanceSq, ref nearestObject, ref nearestPosition, ref nearestHitNormal, forceLoad: true);
        }

        // Recurse
        foreach (var child in obj->ChildObjects)
        {
            HitTestObject(child, ray, ref nearestDistanceSq, ref nearestObject, ref nearestPosition, ref nearestHitNormal);
        }
    }

    public override void AddedToStage()
    {
        base.AddedToStage();

        _hitTestService.AddShape(_hitTestModel);
    }

    protected override void SetPositionInternal(Vector3 position)
    {
        base.SetPositionInternal(position);
        _hitTestModel.Position = WorldPosition;
    }

    protected override void SetRotationPitchYawRollDegreesInternal(Vector3 rotationPYRDegrees)
    {
        base.SetRotationPitchYawRollDegreesInternal(rotationPYRDegrees);
        _hitTestModel.Rotation = WorldRotationQuaternion;
    }

    protected override void SetRotationQuaternionInternal(Quaternion rotationQuaternion)
    {
        base.SetRotationQuaternionInternal(rotationQuaternion);
        _hitTestModel.Rotation = WorldRotationQuaternion;
    }

    protected override void SetScaleInternal(Vector3 scale)
    {
        base.SetScaleInternal(scale);
        _hitTestModel.Scale = WorldScale;
    }

    public override void SetParentTransform(Vector3 parentTranslation, Quaternion parentRotation, float parentUniformScale)
    {
        base.SetParentTransform(parentTranslation, parentRotation, parentUniformScale);
        _hitTestModel.Position = WorldPosition;
        _hitTestModel.Rotation = WorldRotationQuaternion;
        _hitTestModel.Scale = WorldScale;
    }

    protected virtual void SetModelGamePathInternal(string modelGamePath)
    {
        Definition.ModelGamePath = modelGamePath;
        _hitTestModel.ModelResourcePath = ModelGamePath;
    }

    protected override void SetDisplayNameInternal(string displayName)
    {
        base.SetDisplayNameInternal(displayName);
        if (IsSelected)
        {
            AssetLibraryWindow.SetSelectionCallback(DisplayName, "Model Path", AssetType.MdlResource, () => IsInStage && IsSelected, asset => ModelGamePath = asset.GamePath);
        }
    }

    protected override void SetModpackIdInternal(string modpackId)
    {
        base.SetModpackIdInternal(modpackId);
        _hitTestModel.Modpack = GetPreviewModpack();
    }

    public override void RemovedFromStage()
    {
        _hitTestService.RemoveShape(_hitTestModel);

        base.RemovedFromStage();
    }

    public override void Selected()
    {
        base.Selected();

        AssetLibraryWindow.SetSelectionCallback(DisplayName, "Model Path", AssetType.MdlResource, () => IsInStage && IsSelected, asset => ModelGamePath = asset.GamePath);

        StagehandKeybinds.EditorSnapObjectToGround.Pressed += SnapToGround;
        StagehandKeybinds.EditorSnapRotateObjectToGround.Pressed += SnapRotateToGround;
    }

    public override void Deselected()
    {
        base.Deselected();

        StagehandKeybinds.EditorSnapObjectToGround.Pressed -= SnapToGround;
        StagehandKeybinds.EditorSnapRotateObjectToGround.Pressed -= SnapRotateToGround;
    }

    protected override void OnDrawProperties()
    {
        base.OnDrawProperties();

        string modelGamePath = ModelGamePath;
        if (DrawResourceGamePath("Model Path", ref modelGamePath, AssetType.MdlResource, OnObjectPicked))
        {
            ModelGamePath = modelGamePath;
        }

        float opacity = Opacity;
        if (ImGui.SliderFloat("Opacity", ref opacity, vMin: 0.0f, vMax: 1.0f))
        {
            Opacity = opacity;
        }

        Vector4 dyeColor = DyeColor;
        if (ImGui.ColorEdit4("Dye Color", ref dyeColor))
        {
            DyeColor = dyeColor;
        }
    }

    private void OnObjectPicked(PickedObjectInfo? pickedObject)
    {
        if (pickedObject is PickedBgObjectInfo pickedBgObject)
        {
            ModelGamePath = pickedBgObject.ModelGamePath;
            if (!ImGui.IsKeyDown(ImGuiKey.ModShift) && pickedBgObject.DyeColor != null)
            {
                var srgbColor = new Vector4(
                    pickedBgObject.DyeColor.Value.R / 255.0f,
                    pickedBgObject.DyeColor.Value.G / 255.0f,
                    pickedBgObject.DyeColor.Value.B / 255.0f,
                    pickedBgObject.DyeColor.Value.A / 255.0f);
                DyeColor = srgbColor * srgbColor;
            }
        }
    }
}
