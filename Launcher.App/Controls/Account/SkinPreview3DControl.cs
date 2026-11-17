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

using Launcher.App.Logging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Launcher.Domain.Models;

namespace Launcher.App.Controls.Account;

/// <summary>
/// 根据皮肤来源和手臂模型重建静态 3D 玩家预览。
/// </summary>
public sealed class SkinPreview3DControl : Viewport3D
{
    // 控件只在输入变化时重建模型，不维护动画计时器或可变纹理状态。
    public static readonly DependencyProperty SkinSourceProperty =
        DependencyProperty.Register(
            nameof(SkinSource),
            typeof(string),
            typeof(SkinPreview3DControl),
            new PropertyMetadata(null, OnPreviewPropertyChanged));

    public static readonly DependencyProperty SkinModelProperty =
        DependencyProperty.Register(
            nameof(SkinModel),
            typeof(MinecraftSkinModel?),
            typeof(SkinPreview3DControl),
            new PropertyMetadata(null, OnPreviewPropertyChanged));

    public string? SkinSource
    {
        get => (string?)GetValue(SkinSourceProperty);
        set => SetValue(SkinSourceProperty, value);
    }

    public MinecraftSkinModel? SkinModel
    {
        get => (MinecraftSkinModel?)GetValue(SkinModelProperty);
        set => SetValue(SkinModelProperty, value);
    }

    public SkinPreview3DControl()
    {
        ClipToBounds = true;
        Camera = new PerspectiveCamera(
            new Point3D(0, 4, 52),
            new Vector3D(0, 0, -52),
            new Vector3D(0, 1, 0),
            28);
    }

    private static void OnPreviewPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SkinPreview3DControl)d).RebuildPreview();
    }

    private void RebuildPreview()
    {
        // 每次替换完整场景，保证旧纹理和模型不会继续留在 Viewport3D 中。
        Children.Clear();

        if (string.IsNullOrWhiteSpace(SkinSource))
            return;

        BitmapImage skin;
        try
        {
            // 无效或正在写入的图片只显示空预览，不能让异常冒泡到 WPF 渲染线程。
            skin = MinecraftSkinPreviewModelBuilder.LoadSkinBitmap(SkinSource);
        }
        catch
        {
            return;
        }

        Children.Add(new ModelVisual3D
        {
            Content = new Model3DGroup
            {
                Children =
                {
                    MinecraftSkinPreviewModelBuilder.CreateAmbientLight(),
                    MinecraftSkinPreviewModelBuilder.CreateDirectionalLight(),
                    MinecraftSkinPreviewModelBuilder.BuildPlayerModel(skin, SkinModel)
                }
            }
        });
    }
}

/// <summary>
/// 把 Minecraft 皮肤图集切分为身体各面的像素纹理，并组装为可复用 3D 模型。
/// </summary>
internal static class MinecraftSkinPreviewModelBuilder
{
    // Eight atlas pixels per source texel still exceeds the largest on-screen texel
    // footprint in the account carousel while reducing texture area by 75% from 16x.
    private const int AtlasPixelScale = 8;

    public static AmbientLight CreateAmbientLight()
    {
        return new AmbientLight(Color.FromRgb(160, 160, 160));
    }

    public static DirectionalLight CreateDirectionalLight()
    {
        return new DirectionalLight(Color.FromRgb(130, 130, 130), new Vector3D(-0.2, -0.35, -0.9));
    }

    public static BitmapImage LoadSkinBitmap(string source)
    {
        // OnLoad 立即读完并释放文件句柄，皮肤缓存随后可以被替换或删除。
        var bitmap = new BitmapImage();
        ImageLoadDiagnostics.Observe(bitmap, source);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(source, UriKind.RelativeOrAbsolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static Model3DGroup BuildPlayerModel(
        BitmapSource skin,
        MinecraftSkinModel? skinModel,
        double brightness = 1)
    {
        ArgumentNullException.ThrowIfNull(skin);
        var height = Math.Max(skin.PixelHeight, 32);
        var armWidth = MinecraftSkinPreviewGeometry.GetArmWidth(skinModel);
        var hasHeadOverlay = MinecraftSkinPreviewGeometry.CanUseHeadOverlay(skin.PixelWidth, height);
        var hasSecondLayer = MinecraftSkinPreviewGeometry.CanUseSecondLayer(height);
        var faces = new List<BatchedSkinFace>(72);

        AddBatchedCuboid(faces, new Rect3D(-4, 12, -4, 8, 8, 8), SkinPart.Head);
        AddBatchedCuboid(faces, new Rect3D(-4, 0, -2, 8, 12, 4), SkinPart.Body);
        AddBatchedCuboid(faces, new Rect3D(-4, -12, -2, 4, 12, 4), SkinPart.RightLeg);
        AddBatchedCuboid(faces, new Rect3D(0, -12, -2, 4, 12, 4), hasSecondLayer ? SkinPart.LeftLeg : SkinPart.RightLeg);
        AddBatchedCuboid(faces, new Rect3D(-4 - armWidth, 0, -2, armWidth, 12, 4), SkinPart.RightArm, armWidth);
        AddBatchedCuboid(faces, new Rect3D(4, 0, -2, armWidth, 12, 4), hasSecondLayer ? SkinPart.LeftArm : SkinPart.RightArm, armWidth);

        if (hasHeadOverlay)
            AddBatchedCuboid(faces, new Rect3D(-4.35, 11.65, -4.35, 8.7, 8.7, 8.7), SkinPart.HeadOverlay, isOverlay: true);

        if (hasSecondLayer)
        {
            AddBatchedCuboid(faces, new Rect3D(-4.25, -0.25, -2.25, 8.5, 12.5, 4.5), SkinPart.BodyOverlay, isOverlay: true);
            AddBatchedCuboid(faces, new Rect3D(-4.25, -12.25, -2.25, 4.5, 12.5, 4.5), SkinPart.RightLegOverlay, isOverlay: true);
            AddBatchedCuboid(faces, new Rect3D(-0.25, -12.25, -2.25, 4.5, 12.5, 4.5), SkinPart.LeftLegOverlay, isOverlay: true);
            AddBatchedCuboid(faces, new Rect3D(-4.25 - armWidth, -0.25, -2.25, armWidth + 0.5, 12.5, 4.5), SkinPart.RightArmOverlay, armWidth, true);
            AddBatchedCuboid(faces, new Rect3D(3.75, -0.25, -2.25, armWidth + 0.5, 12.5, 4.5), SkinPart.LeftArmOverlay, armWidth, true);
        }

        var model = new Model3DGroup();
        AddBatchedLayer(model, skin, faces.Where(item => !item.IsOverlay), brightness, opaqueUnusedPixels: true);
        AddBatchedLayer(model, skin, faces.Where(item => item.IsOverlay), brightness, opaqueUnusedPixels: false);
        var rotation = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 2);
        rotation.Freeze();
        var transform = new RotateTransform3D(rotation);
        transform.Freeze();
        model.Transform = transform;
        model.Freeze();
        return model;
    }

    private static void AddBatchedLayer(
        Model3DGroup model,
        BitmapSource skin,
        IEnumerable<BatchedSkinFace> layerFaces,
        double brightness,
        bool opaqueUnusedPixels)
    {
        var faces = layerFaces.ToArray();
        if (faces.Length == 0)
            return;

        // Base and translucent overlay layers must use separate images. If a material's
        // image contains any translucent pixels, WPF treats the whole GeometryModel3D as
        // translucent and stops the opaque base faces from occluding their rear faces.
        var atlas = PreviewTextureAtlasBuilder.Build(
            skin,
            faces.Select(item => item.TextureRect),
            pixelScale: AtlasPixelScale,
            brightness,
            maximumSourceRowWidth: 128,
            opaqueUnusedPixels);
        var brush = new ImageBrush(atlas.Bitmap)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        var mesh = new PreviewMeshBuilder();
        foreach (var face in faces)
            AddBatchedFace(mesh, face.Bounds, face.Face, atlas.TextureCoordinates[face.TextureRect]);
        AddBatchedMeshModel(model, mesh, material);
    }

    private static void AddBatchedCuboid(
        ICollection<BatchedSkinFace> target,
        Rect3D bounds,
        SkinPart part,
        int armWidth = 4,
        bool isOverlay = false)
    {
        var faces = MinecraftSkinPreviewGeometry.GetFaces(part, armWidth);
        target.Add(new BatchedSkinFace(bounds, CubeFace.Front, faces.Front, isOverlay));
        target.Add(new BatchedSkinFace(bounds, CubeFace.Back, faces.Back, isOverlay));
        target.Add(new BatchedSkinFace(bounds, CubeFace.Left, faces.Left, isOverlay));
        target.Add(new BatchedSkinFace(bounds, CubeFace.Right, faces.Right, isOverlay));
        target.Add(new BatchedSkinFace(bounds, CubeFace.Top, faces.Top, isOverlay));
        target.Add(new BatchedSkinFace(bounds, CubeFace.Bottom, faces.Bottom, isOverlay));
    }

    private static void AddBatchedFace(PreviewMeshBuilder mesh, Rect3D bounds, CubeFace face, Rect textureCoordinates)
    {
        var x0 = bounds.X;
        var x1 = bounds.X + bounds.SizeX;
        var y0 = bounds.Y;
        var y1 = bounds.Y + bounds.SizeY;
        var z0 = bounds.Z;
        var z1 = bounds.Z + bounds.SizeZ;

        Point3D p0;
        Point3D p1;
        Point3D p2;
        Point3D p3;
        switch (face)
        {
            case CubeFace.Front:
                p0 = new Point3D(x0, y1, z1);
                p1 = new Point3D(x1, y1, z1);
                p2 = new Point3D(x1, y0, z1);
                p3 = new Point3D(x0, y0, z1);
                break;
            case CubeFace.Back:
                p0 = new Point3D(x1, y1, z0);
                p1 = new Point3D(x0, y1, z0);
                p2 = new Point3D(x0, y0, z0);
                p3 = new Point3D(x1, y0, z0);
                break;
            case CubeFace.Left:
                p0 = new Point3D(x0, y1, z0);
                p1 = new Point3D(x0, y1, z1);
                p2 = new Point3D(x0, y0, z1);
                p3 = new Point3D(x0, y0, z0);
                break;
            case CubeFace.Right:
                p0 = new Point3D(x1, y1, z1);
                p1 = new Point3D(x1, y1, z0);
                p2 = new Point3D(x1, y0, z0);
                p3 = new Point3D(x1, y0, z1);
                break;
            case CubeFace.Top:
                p0 = new Point3D(x0, y1, z0);
                p1 = new Point3D(x1, y1, z0);
                p2 = new Point3D(x1, y1, z1);
                p3 = new Point3D(x0, y1, z1);
                break;
            default:
                p0 = new Point3D(x0, y0, z1);
                p1 = new Point3D(x1, y0, z1);
                p2 = new Point3D(x1, y0, z0);
                p3 = new Point3D(x0, y0, z0);
                break;
        }

        // Keep the legacy inward winding exactly as-is. Both material sides are retained
        // because user-authored skins may contain transparent base pixels and relied on
        // the original rear-face contribution to preserve their visible composition.
        mesh.AddQuad(p0, p1, p2, p3, textureCoordinates);
    }

    private static void AddBatchedMeshModel(Model3DGroup target, PreviewMeshBuilder mesh, Material material)
    {
        if (mesh.QuadCount == 0)
            return;

        var geometryModel = new GeometryModel3D
        {
            Geometry = mesh.Build(anchorUnitTextureCoordinates: true),
            Material = material,
            BackMaterial = material
        };
        geometryModel.Freeze();
        target.Children.Add(geometryModel);
    }

    private sealed record BatchedSkinFace(Rect3D Bounds, CubeFace Face, Int32Rect TextureRect, bool IsOverlay);

#if false
    private static Model3DGroup BuildPlayerModelLegacy(
        BitmapSource skin,
        MinecraftSkinModel? skinModel,
        double brightness = 1)
    {
        // 旧 64x32 皮肤没有身体第二层，按图像尺寸决定是否生成覆盖几何。
        var model = new Model3DGroup();
        var height = Math.Max(skin.PixelHeight, 32);
        var armWidth = MinecraftSkinPreviewGeometry.GetArmWidth(skinModel);
        var hasHeadOverlay = MinecraftSkinPreviewGeometry.CanUseHeadOverlay(skin.PixelWidth, height);
        var hasSecondLayer = MinecraftSkinPreviewGeometry.CanUseSecondLayer(height);
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 2)));
        model.Transform = transform;

        AddCuboid(model, skin, new Rect3D(-4, 12, -4, 8, 8, 8), height, SkinPart.Head, brightness: brightness);
        AddCuboid(model, skin, new Rect3D(-4, 0, -2, 8, 12, 4), height, SkinPart.Body, brightness: brightness);
        AddCuboid(model, skin, new Rect3D(-4, -12, -2, 4, 12, 4), height, SkinPart.RightLeg, brightness: brightness);
        AddCuboid(model, skin, new Rect3D(0, -12, -2, 4, 12, 4), height, hasSecondLayer ? SkinPart.LeftLeg : SkinPart.RightLeg, brightness: brightness);
        AddCuboid(model, skin, new Rect3D(-4 - armWidth, 0, -2, armWidth, 12, 4), height, SkinPart.RightArm, armWidth, brightness);
        AddCuboid(model, skin, new Rect3D(4, 0, -2, armWidth, 12, 4), height, hasSecondLayer ? SkinPart.LeftArm : SkinPart.RightArm, armWidth, brightness);

        if (hasHeadOverlay)
            AddCuboid(model, skin, new Rect3D(-4.35, 11.65, -4.35, 8.7, 8.7, 8.7), height, SkinPart.HeadOverlay, brightness: brightness);

        if (hasSecondLayer)
        {
            AddCuboid(model, skin, new Rect3D(-4.25, -0.25, -2.25, 8.5, 12.5, 4.5), height, SkinPart.BodyOverlay, brightness: brightness);
            AddCuboid(model, skin, new Rect3D(-4.25, -12.25, -2.25, 4.5, 12.5, 4.5), height, SkinPart.RightLegOverlay, brightness: brightness);
            AddCuboid(model, skin, new Rect3D(-0.25, -12.25, -2.25, 4.5, 12.5, 4.5), height, SkinPart.LeftLegOverlay, brightness: brightness);
            AddCuboid(model, skin, new Rect3D(-4.25 - armWidth, -0.25, -2.25, armWidth + 0.5, 12.5, 4.5), height, SkinPart.RightArmOverlay, armWidth, brightness);
            AddCuboid(model, skin, new Rect3D(3.75, -0.25, -2.25, armWidth + 0.5, 12.5, 4.5), height, SkinPart.LeftArmOverlay, armWidth, brightness);
        }

        return model;
    }

    private static void AddCuboid(
        Model3DGroup group,
        ImageSource skin,
        Rect3D bounds,
        int skinHeight,
        SkinPart part,
        int armWidth = 4,
        double brightness = 1)
    {
        // 六个面从图集中独立裁剪，保持像素采样并允许亮度模拟简单光照。
        var faces = MinecraftSkinPreviewGeometry.GetFaces(part, armWidth);
        AddFace(group, skin, skinHeight, bounds, CubeFace.Front, faces.Front, brightness);
        AddFace(group, skin, skinHeight, bounds, CubeFace.Back, faces.Back, brightness);
        AddFace(group, skin, skinHeight, bounds, CubeFace.Left, faces.Left, brightness);
        AddFace(group, skin, skinHeight, bounds, CubeFace.Right, faces.Right, brightness);
        AddFace(group, skin, skinHeight, bounds, CubeFace.Top, faces.Top, brightness);
        AddFace(group, skin, skinHeight, bounds, CubeFace.Bottom, faces.Bottom, brightness);
    }

    private static void AddFace(
        Model3DGroup group,
        ImageSource skin,
        int skinHeight,
        Rect3D bounds,
        CubeFace face,
        Int32Rect textureRect,
        double brightness)
    {
        var mesh = CreateFaceMesh(bounds, face);
        var faceBitmap = CreatePixelSharpFaceBitmap(skin, textureRect, brightness);
        var brush = new ImageBrush(faceBitmap)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();

        group.Children.Add(new GeometryModel3D
        {
            Geometry = mesh,
            Material = material,
            BackMaterial = material
        });
    }

    private static MeshGeometry3D CreateFaceMesh(Rect3D b, CubeFace face)
    {
        var x0 = b.X;
        var x1 = b.X + b.SizeX;
        var y0 = b.Y;
        var y1 = b.Y + b.SizeY;
        var z0 = b.Z;
        var z1 = b.Z + b.SizeZ;

        Point3D p0;
        Point3D p1;
        Point3D p2;
        Point3D p3;
        switch (face)
        {
            case CubeFace.Front:
                p0 = new Point3D(x0, y1, z1);
                p1 = new Point3D(x1, y1, z1);
                p2 = new Point3D(x1, y0, z1);
                p3 = new Point3D(x0, y0, z1);
                break;
            case CubeFace.Back:
                p0 = new Point3D(x1, y1, z0);
                p1 = new Point3D(x0, y1, z0);
                p2 = new Point3D(x0, y0, z0);
                p3 = new Point3D(x1, y0, z0);
                break;
            case CubeFace.Left:
                p0 = new Point3D(x0, y1, z0);
                p1 = new Point3D(x0, y1, z1);
                p2 = new Point3D(x0, y0, z1);
                p3 = new Point3D(x0, y0, z0);
                break;
            case CubeFace.Right:
                p0 = new Point3D(x1, y1, z1);
                p1 = new Point3D(x1, y1, z0);
                p2 = new Point3D(x1, y0, z0);
                p3 = new Point3D(x1, y0, z1);
                break;
            case CubeFace.Top:
                p0 = new Point3D(x0, y1, z0);
                p1 = new Point3D(x1, y1, z0);
                p2 = new Point3D(x1, y1, z1);
                p3 = new Point3D(x0, y1, z1);
                break;
            default:
                p0 = new Point3D(x0, y0, z1);
                p1 = new Point3D(x1, y0, z1);
                p2 = new Point3D(x1, y0, z0);
                p3 = new Point3D(x0, y0, z0);
                break;
        }

        var mesh = new MeshGeometry3D
        {
            Positions = [p0, p1, p2, p3],
            TextureCoordinates =
            [
                new Point(0, 0),
                new Point(1, 0),
                new Point(1, 1),
                new Point(0, 1)
            ],
            TriangleIndices = [0, 1, 2, 0, 2, 3]
        };
        mesh.Freeze();
        return mesh;
    }

    private static BitmapSource CreatePixelSharpFaceBitmap(
        ImageSource skin,
        Int32Rect textureRect,
        double brightness)
    {
        // 先裁剪原始像素再放大，避免 WPF 插值造成边缘模糊或颜色串行。
        const int scale = 16;
        brightness = Math.Clamp(brightness, 0, 1);
        var source = EnsureBgra32((BitmapSource)skin);
        var clampedX = Math.Clamp(textureRect.X, 0, source.PixelWidth - 1);
        var clampedY = Math.Clamp(textureRect.Y, 0, source.PixelHeight - 1);
        var clampedRect = new Int32Rect(
            clampedX,
            clampedY,
            Math.Max(1, Math.Min(textureRect.Width, source.PixelWidth - clampedX)),
            Math.Max(1, Math.Min(textureRect.Height, source.PixelHeight - clampedY)));
        var stride = clampedRect.Width * 4;
        var pixels = new byte[stride * clampedRect.Height];
        source.CopyPixels(clampedRect, pixels, stride, 0);

        var outputWidth = clampedRect.Width * scale;
        var outputHeight = clampedRect.Height * scale;
        var outputStride = outputWidth * 4;
        var output = new byte[outputStride * outputHeight];
        for (var y = 0; y < outputHeight; y++)
        {
            var sourceY = y / scale;
            for (var x = 0; x < outputWidth; x++)
            {
                var sourceX = x / scale;
                var sourceIndex = sourceY * stride + sourceX * 4;
                var outputIndex = y * outputStride + x * 4;
                output[outputIndex] = ApplyBrightness(pixels[sourceIndex], brightness);
                output[outputIndex + 1] = ApplyBrightness(pixels[sourceIndex + 1], brightness);
                output[outputIndex + 2] = ApplyBrightness(pixels[sourceIndex + 2], brightness);
                output[outputIndex + 3] = pixels[sourceIndex + 3];
            }
        }

        var bitmap = BitmapSource.Create(
            outputWidth,
            outputHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            output,
            outputStride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte ApplyBrightness(byte value, double brightness)
    {
        return (byte)Math.Round(value * brightness);
    }

    private static BitmapSource EnsureBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
            return source;

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

#endif
    private enum CubeFace
    {
        Front,
        Back,
        Left,
        Right,
        Top,
        Bottom
    }
}

/// <summary>
/// 提供 Steve/Alex 模型对应的皮肤图集坐标和几何尺寸规则。
/// </summary>
public static class MinecraftSkinPreviewGeometry
{
    public static int GetArmWidth(MinecraftSkinModel? skinModel)
    {
        // Alex 使用 3 像素手臂，未知模型按经典 Steve 的 4 像素兼容处理。
        return skinModel is MinecraftSkinModel.Slim ? 3 : 4;
    }

    public static bool CanUseSecondLayer(int skinPixelHeight)
    {
        // 64x32 旧格式缺少身体第二层，只对现代 64x64 图集读取覆盖区域。
        return skinPixelHeight >= 64;
    }

    public static bool CanUseHeadOverlay(int skinPixelWidth, int skinPixelHeight)
    {
        // 64x32 旧格式已经包含头部帽子层；只有身体、手臂和腿部覆盖层需要 64x64。
        return skinPixelWidth >= 64 && skinPixelHeight >= 32;
    }

    public static SkinPartFaces GetFaces(SkinPart part, int armWidth = 4)
    {
        return part switch
        {
            SkinPart.Head => Cube(8, 0, 8, 8, 8, 8, 8, 8, 16, 8, 0, 8),
            SkinPart.HeadOverlay => Cube(40, 0, 8, 8, 8, 8, 8, 8, 16, 8, 32, 8),
            SkinPart.Body => Cube(20, 16, 8, 4, 8, 12, 4, 12, 4, 12, 16, 20),
            SkinPart.BodyOverlay => Cube(20, 32, 8, 4, 8, 12, 4, 12, 4, 12, 16, 36),
            SkinPart.RightArm => Arm(40, 16, armWidth),
            SkinPart.RightArmOverlay => Arm(40, 32, armWidth),
            SkinPart.LeftArm => Arm(32, 48, armWidth),
            SkinPart.LeftArmOverlay => Arm(48, 48, armWidth),
            SkinPart.RightLeg => Cube(4, 16, 4, 4, 4, 12, 4, 12, 4, 12, 0, 20),
            SkinPart.RightLegOverlay => Cube(4, 32, 4, 4, 4, 12, 4, 12, 4, 12, 0, 36),
            SkinPart.LeftLeg => Cube(20, 48, 4, 4, 4, 12, 4, 12, 4, 12, 16, 52),
            SkinPart.LeftLegOverlay => Cube(4, 48, 4, 4, 4, 12, 4, 12, 4, 12, 0, 52),
            _ => Cube(8, 0, 8, 8, 8, 8, 8, 8, 16, 8, 0, 8)
        };
    }

    private static SkinPartFaces Arm(int x, int y, int armWidth)
    {
        return Cube(
            x + armWidth,
            y,
            armWidth,
            4,
            armWidth,
            12,
            4,
            12,
            armWidth,
            12,
            x,
            y + 4);
    }

    private static SkinPartFaces Cube(
        int topX,
        int topY,
        int width,
        int depth,
        int frontWidth,
        int height,
        int sideWidth,
        int sideHeight,
        int backWidth,
        int backHeight,
        int leftX,
        int sideY)
    {
        return new SkinPartFaces(
            new Int32Rect(leftX + sideWidth, sideY, frontWidth, height),
            new Int32Rect(leftX + sideWidth + frontWidth + sideWidth, sideY, backWidth, backHeight),
            new Int32Rect(leftX, sideY, sideWidth, sideHeight),
            new Int32Rect(leftX + sideWidth + frontWidth, sideY, sideWidth, sideHeight),
            new Int32Rect(topX, topY, width, depth),
            new Int32Rect(topX + width, topY, width, depth));
    }
}

public enum SkinPart
{
    Head,
    HeadOverlay,
    Body,
    BodyOverlay,
    RightArm,
    RightArmOverlay,
    LeftArm,
    LeftArmOverlay,
    RightLeg,
    RightLegOverlay,
    LeftLeg,
    LeftLegOverlay
}

public readonly record struct SkinPartFaces(
    Int32Rect Front,
    Int32Rect Back,
    Int32Rect Left,
    Int32Rect Right,
    Int32Rect Top,
    Int32Rect Bottom);
