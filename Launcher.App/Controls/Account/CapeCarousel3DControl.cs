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
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Xml.Linq;
using Launcher.Application.Accounts;
using Serilog;

namespace Launcher.App.Controls.Account;

/// <summary>
/// 将披风选项渲染为可切换的 3D 轮播，并协调异步纹理加载、场景重建与过渡动画。
/// </summary>
public sealed class CapeCarousel3DControl : Grid
{
    private const int AnimationMilliseconds = 600;
    private const int CapeModelCacheCapacity = 8;
    private const double CenterBrightness = 1;
    private const double SideBrightness = 0.48;

    public static readonly DependencyProperty PreviousCapeProperty =
        DependencyProperty.Register(
            nameof(PreviousCape),
            typeof(AccountCapeOption),
            typeof(CapeCarousel3DControl),
            new PropertyMetadata(null, OnCapePropertyChanged));

    public static readonly DependencyProperty SelectedCapeProperty =
        DependencyProperty.Register(
            nameof(SelectedCape),
            typeof(AccountCapeOption),
            typeof(CapeCarousel3DControl),
            new PropertyMetadata(null, OnSelectedCapeChanged));

    public static readonly DependencyProperty NextCapeProperty =
        DependencyProperty.Register(
            nameof(NextCape),
            typeof(AccountCapeOption),
            typeof(CapeCarousel3DControl),
            new PropertyMetadata(null, OnCapePropertyChanged));

    public static readonly DependencyProperty PreviousCommandProperty =
        DependencyProperty.Register(
            nameof(PreviousCommand),
            typeof(ICommand),
            typeof(CapeCarousel3DControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty NextCommandProperty =
        DependencyProperty.Register(
            nameof(NextCommand),
            typeof(ICommand),
            typeof(CapeCarousel3DControl),
            new PropertyMetadata(null));

    // viewport 承载 3D 模型，Hover 层保持为普通 WPF 元素以便复用主题资源。
    private readonly Viewport3D viewport = new();
    private readonly Viewport3DIdleRenderCache viewportRenderCache;
    private readonly Border leftHoverHint;
    private readonly Border rightHoverHint;
    // WPF 命中测试返回具体 Model3D，需要反向映射到左/中/右逻辑槽位。
    private readonly Dictionary<Model3D, CapeCarouselSlot> hitSlots = [];
    private readonly Dictionary<CapeCarouselSlot, SlotVisual> currentSlotVisuals = [];
    // 纹理缓存保存已冻结 BitmapSource；请求集合则防止同一 URL 并发加载多次。
    private readonly Dictionary<string, BitmapSource> capeTextureCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> capeTextureRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> capeTextureVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<CapeModelCacheKey, Model3DGroup> capeModelCache = [];
    private readonly Queue<CapeModelCacheKey> capeModelCacheOrder = [];
    private CancellationTokenSource? sceneBuildCancellation;
    // 重建与动画状态组成一个小型状态机，保证属性变化、纹理完成和动画结束不会互相覆盖。
    private bool rebuildQueued;
    private bool isRebuilding;
    private bool isAnimating;
    private bool rebuildAfterAnimation;
    private int animationGeneration;
    private int sceneBuildGeneration;
    private CapeCarouselDirection? pendingDirection;
    // 保存上次真正渲染的三个身份，用于判断当前属性变化是否能形成连续左右切换动画。
    private AccountCapeOption? previousRenderedCape;
    private AccountCapeOption? selectedRenderedCape;
    private AccountCapeOption? nextRenderedCape;

    public CapeCarousel3DControl()
    {
        viewportRenderCache = new Viewport3DIdleRenderCache(viewport, "Cape");
        ClipToBounds = true;
        Focusable = false;
        // 外层 Grid 已提供最终裁剪边界；避免 Viewport3D 再执行一次昂贵的抗锯齿裁剪。
        viewport.ClipToBounds = false;
        viewport.Camera = new PerspectiveCamera(
            new Point3D(0, 8, 46),
            new Vector3D(0, 0, -46),
            new Vector3D(0, 1, 0),
            28);

        var hoverLayer = CreateHoverLayer();
        leftHoverHint = CreateHoverHint();
        rightHoverHint = CreateHoverHint();
        PositionHoverHint(leftHoverHint, CapeCarouselSlot.Left);
        PositionHoverHint(rightHoverHint, CapeCarouselSlot.Right);
        hoverLayer.Children.Add(leftHoverHint);
        hoverLayer.Children.Add(rightHoverHint);
        Children.Add(hoverLayer);
        Children.Add(viewport);

        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MouseLeave += (_, _) =>
        {
            Cursor = Cursors.Arrow;
            UpdateHoverHint(null);
        };
    }

    public AccountCapeOption? PreviousCape
    {
        get => (AccountCapeOption?)GetValue(PreviousCapeProperty);
        set => SetValue(PreviousCapeProperty, value);
    }

    public AccountCapeOption? SelectedCape
    {
        get => (AccountCapeOption?)GetValue(SelectedCapeProperty);
        set => SetValue(SelectedCapeProperty, value);
    }

    public AccountCapeOption? NextCape
    {
        get => (AccountCapeOption?)GetValue(NextCapeProperty);
        set => SetValue(NextCapeProperty, value);
    }

    public ICommand? PreviousCommand
    {
        get => (ICommand?)GetValue(PreviousCommandProperty);
        set => SetValue(PreviousCommandProperty, value);
    }

    public ICommand? NextCommand
    {
        get => (ICommand?)GetValue(NextCommandProperty);
        set => SetValue(NextCommandProperty, value);
    }

    private static void OnCapePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((CapeCarousel3DControl)d).QueueRebuild();
    }

    private static void OnSelectedCapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (CapeCarousel3DControl)d;
        var newCape = e.NewValue as AccountCapeOption;
        if (newCape is not null)
        {
            if (CapeCarousel3DLayout.CapesRepresentSameVisualItem(newCape, control.nextRenderedCape))
                control.pendingDirection = CapeCarouselDirection.Next;
            else if (CapeCarousel3DLayout.CapesRepresentSameVisualItem(newCape, control.previousRenderedCape))
                control.pendingDirection = CapeCarouselDirection.Previous;
        }

        control.QueueRebuild();
    }

    /// <summary>
    /// 合并同一调度周期内的多个依赖属性变化，并保证重建期间的新请求不会丢失。
    /// </summary>
    private void QueueRebuild()
    {
        viewportRenderCache.Disable("SceneChange");
        sceneBuildGeneration++;
        sceneBuildCancellation?.Cancel();
        QueueRebuildCore();
    }

    private void QueueRebuildCore()
    {
        // 属性可能在同一调度周期内成组变化；只排队一次，同时记住重建过程中到达的新请求。
        if (rebuildQueued)
            return;

        rebuildQueued = true;
        Dispatcher.BeginInvoke(Rebuild, DispatcherPriority.Background);
    }

    /// <summary>
    /// 根据三个披风槽位重建 3D 场景，并在身份连续时复用旧视觉位置作为动画起点。
    /// </summary>
    private async void Rebuild()
    {
        rebuildQueued = false;
        isRebuilding = true;
        var generation = sceneBuildGeneration;
        sceneBuildCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        sceneBuildCancellation = cancellation;

        var previousCape = PreviousCape;
        var selectedCape = SelectedCape;
        var nextCape = NextCape;
        var oldSlotVisuals = currentSlotVisuals.Values.ToList();
        var direction = CapeCarousel3DLayout.CanAnimateTransition(
            pendingDirection,
            previousRenderedCape,
            selectedRenderedCape,
            nextRenderedCape,
            previousCape,
            selectedCape,
            nextCape)
            ? pendingDirection
            : null;
        pendingDirection = null;

        var slotRequests = new[]
        {
            CreateCapeBuildRequest(CapeCarouselSlot.Left, previousCape, SideBrightness),
            CreateCapeBuildRequest(CapeCarouselSlot.Center, selectedCape, CenterBrightness),
            CreateCapeBuildRequest(CapeCarouselSlot.Right, nextCape, SideBrightness)
        };
        var prepared = new Dictionary<CapeModelCacheKey, Model3DGroup>();
        var misses = new Dictionary<CapeModelCacheKey, CapeSlotBuildRequest>();
        foreach (var request in slotRequests)
        {
            if (request.Cape is null)
                continue;
            if (capeModelCache.TryGetValue(request.Key, out var cached))
            {
                prepared[request.Key] = cached;
            }
            else
            {
                misses.TryAdd(request.Key, request);
            }
        }

        try
        {
            var built = await Task.Run(() => BuildMissingCapeModels(misses, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != sceneBuildGeneration || !IsLoaded)
                return;

            foreach (var pair in built)
            {
                prepared[pair.Key] = pair.Value;
                AddCapeModelToCache(pair.Key, pair.Value);
            }

            if (prepared.Count == 0 && oldSlotVisuals.Count > 0 && slotRequests.Any(item => item.Cape is not null))
            {
                isRebuilding = false;
                Log.Warning("Account cape scene retained because every requested model failed to build; generation={Generation}", generation);
                QueueViewportRenderCache();
                return;
            }

            FreezeCurrentAnimations(oldSlotVisuals);
            viewport.Children.Clear();
            currentSlotVisuals.Clear();
            hitSlots.Clear();
            UpdateHoverHint(null);
            var scene = new Model3DGroup
            {
                Children =
                {
                    MinecraftCapePreviewModelBuilder.CreateAmbientLight(),
                    MinecraftCapePreviewModelBuilder.CreateDirectionalLight()
                }
            };
            AddPreparedSlot(scene, slotRequests[0], prepared, oldSlotVisuals, direction);
            AddPreparedSlot(scene, slotRequests[1], prepared, oldSlotVisuals, direction);
            AddPreparedSlot(scene, slotRequests[2], prepared, oldSlotVisuals, direction);
            viewport.Children.Add(new ModelVisual3D { Content = scene });

            previousRenderedCape = previousCape;
            selectedRenderedCape = selectedCape;
            nextRenderedCape = nextCape;
            isRebuilding = false;
            if (direction is not null && currentSlotVisuals.Count > 0)
                AnimateSlots();
            else
            {
                isAnimating = false;
                animationGeneration++;
                if (rebuildAfterAnimation)
                {
                    rebuildAfterAnimation = false;
                    QueueRebuild();
                }
                else
                {
                    QueueViewportRenderCache();
                }
            }

        }
        catch (OperationCanceledException)
        {
            if (generation == sceneBuildGeneration)
                isRebuilding = false;
        }
        catch (Exception ex)
        {
            if (generation == sceneBuildGeneration)
            {
                isRebuilding = false;
                if (!isAnimating)
                    QueueViewportRenderCache();
            }
            Log.Warning(ex, "Account cape scene build failed; generation={Generation}", generation);
        }
    }

    private CapeSlotBuildRequest CreateCapeBuildRequest(CapeCarouselSlot slot, AccountCapeOption? cape, double brightness)
    {
        BitmapSource? texture = null;
        if (cape is not null)
            texture = GetOrRequestCapeTexture(cape);
        var identity = cape is null
            ? string.Empty
            : cape.Id ?? cape.ImageUrl ?? (cape.IsNone ? "none" : cape.DisplayName);
        var source = cape?.ImageUrl;
        var textureVersion = source is not null && capeTextureVersions.TryGetValue(source, out var version) ? version : 0;
        return new CapeSlotBuildRequest(
            slot,
            cape,
            brightness,
            texture,
            new CapeModelCacheKey(identity, source, brightness, textureVersion, texture is not null));
    }

    private static Dictionary<CapeModelCacheKey, Model3DGroup> BuildMissingCapeModels(
        IReadOnlyDictionary<CapeModelCacheKey, CapeSlotBuildRequest> requests,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<CapeModelCacheKey, Model3DGroup>();
        foreach (var pair in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result[pair.Key] = MinecraftCapePreviewModelBuilder.BuildCapeModel(
                    pair.Value.Cape!,
                    pair.Value.Brightness,
                    pair.Value.Texture);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // A broken cape only removes its own slot; other slots stay available.
            }
        }
        return result;
    }

    private void AddPreparedSlot(
        Model3DGroup scene,
        CapeSlotBuildRequest request,
        IReadOnlyDictionary<CapeModelCacheKey, Model3DGroup> prepared,
        IReadOnlyList<SlotVisual> oldSlotVisuals,
        CapeCarouselDirection? direction)
    {
        var cape = request.Cape;
        if (cape is null || !prepared.TryGetValue(request.Key, out var capeModel))
            return;

        var targetPlacement = CapeCarousel3DLayout.GetPlacement(request.Slot);
        var startPlacement = ResolveStartPlacement(cape, request.Slot, oldSlotVisuals, direction);
        var scale = new ScaleTransform3D(startPlacement.Scale, startPlacement.Scale, startPlacement.Scale, 0, 8, 0);
        var translate = new TranslateTransform3D(startPlacement.X, 0, 0);
        var transform = new Transform3DGroup();
        transform.Children.Add(scale);
        transform.Children.Add(translate);
        var slotGroup = new Model3DGroup { Transform = transform };
        slotGroup.Children.Add(capeModel);
        scene.Children.Add(slotGroup);
        RegisterHitModels(slotGroup, request.Slot);
        currentSlotVisuals[request.Slot] = new SlotVisual(cape, request.Slot, scale, translate, targetPlacement);
    }

    private void AddCapeModelToCache(CapeModelCacheKey key, Model3DGroup model)
    {
        if (capeModelCache.ContainsKey(key))
            return;
        capeModelCache[key] = model;
        capeModelCacheOrder.Enqueue(key);
        while (capeModelCacheOrder.Count > CapeModelCacheCapacity)
            capeModelCache.Remove(capeModelCacheOrder.Dequeue());
    }

#if false
    private void RebuildLegacy()
    {
        // 阶段一：冻结本轮请求状态并判断旧场景到新场景是否构成可动画的相邻切换。
        isRebuilding = true;
        rebuildQueued = false;
        var shouldRebuildAgain = rebuildRequestedWhileQueued;
        rebuildRequestedWhileQueued = false;

        var oldSlotVisuals = currentSlotVisuals.Values.ToList();
        var direction = CapeCarousel3DLayout.CanAnimateTransition(
            pendingDirection,
            previousRenderedCape,
            selectedRenderedCape,
            nextRenderedCape,
            PreviousCape,
            SelectedCape,
            NextCape)
            ? pendingDirection
            : null;
        pendingDirection = null;

        // 阶段二：清空场景和命中映射。Hover 同时隐藏，避免指向已经销毁的模型。
        viewport.Children.Clear();
        currentSlotVisuals.Clear();
        hitSlots.Clear();
        UpdateHoverHint(null);

        // 阶段三：创建共享光照并为三个槽位构建独立模型与变换。
        var scene = new Model3DGroup
        {
            Children =
            {
                MinecraftCapePreviewModelBuilder.CreateAmbientLight(),
                MinecraftCapePreviewModelBuilder.CreateDirectionalLight()
            }
        };

        AddSlot(scene, CapeCarouselSlot.Left, PreviousCape, oldSlotVisuals, direction);
        AddSlot(scene, CapeCarouselSlot.Center, SelectedCape, oldSlotVisuals, direction);
        AddSlot(scene, CapeCarouselSlot.Right, NextCape, oldSlotVisuals, direction);

        viewport.Children.Add(new ModelVisual3D { Content = scene });

        // 阶段四：只有场景完整提交后才更新“已渲染身份”，供下一次方向推断使用。
        previousRenderedCape = PreviousCape;
        selectedRenderedCape = SelectedCape;
        nextRenderedCape = NextCape;
        isRebuilding = false;

        if (direction is not null && currentSlotVisuals.Count > 0)
        {
            // 连续切换从旧位置过渡；普通刷新直接显示最终位置，不制造错误移动方向。
            AnimateSlots();
        }
        else
        {
            isAnimating = false;
            animationGeneration++;
            if (rebuildAfterAnimation)
            {
                rebuildAfterAnimation = false;
                QueueRebuild();
            }
        }

        // 重建期间又收到属性变化时，静态场景可立即重建；动画场景延迟到动画结束。
        if (shouldRebuildAgain && direction is null)
            QueueRebuild();
        else if (shouldRebuildAgain)
            rebuildAfterAnimation = true;
    }

    /// <summary>
    /// 为单个逻辑槽位创建模型、变换与命中映射，并记录目标布局。
    /// </summary>
    private void AddSlot(
        Model3DGroup scene,
        CapeCarouselSlot targetSlot,
        AccountCapeOption? cape,
        IReadOnlyList<SlotVisual> oldSlotVisuals,
        CapeCarouselDirection? direction)
    {
        if (cape is null)
            return;

        Model3DGroup capeModel;
        try
        {
            // 模型构建失败只跳过当前槽位，其他两个槽位仍可正常显示和交互。
            var capeTexture = GetOrRequestCapeTexture(cape);
            capeModel = MinecraftCapePreviewModelBuilder.BuildCapeModel(
                cape,
                targetSlot is CapeCarouselSlot.Center ? CenterBrightness : SideBrightness,
                capeTexture);
        }
        catch
        {
            return;
        }

        // 起点可能来自旧视觉当前位置，终点始终由槽位布局常量决定。
        var targetPlacement = CapeCarousel3DLayout.GetPlacement(targetSlot);
        var startPlacement = ResolveStartPlacement(cape, targetSlot, oldSlotVisuals, direction);
        var scale = new ScaleTransform3D(
            startPlacement.Scale,
            startPlacement.Scale,
            startPlacement.Scale,
            0,
            8,
            0);
        var translate = new TranslateTransform3D(startPlacement.X, 0, 0);
        var transform = new Transform3DGroup();
        transform.Children.Add(scale);
        transform.Children.Add(translate);
        capeModel.Transform = CombineTransforms(capeModel.Transform, transform);
        scene.Children.Add(capeModel);

        RegisterHitModels(capeModel, targetSlot);
        currentSlotVisuals[targetSlot] = new SlotVisual(cape, targetSlot, scale, translate, targetPlacement);
    }

    /// <summary>
    /// 返回已缓存纹理；未命中时只启动一次异步加载并暂时使用占位视觉。
    /// </summary>
#endif
    private BitmapSource? GetOrRequestCapeTexture(AccountCapeOption cape)
    {
        if (cape.IsNone || string.IsNullOrWhiteSpace(cape.ImageUrl))
            return null;

        var source = cape.ImageUrl;
        if (capeTextureCache.TryGetValue(source, out var cachedTexture))
            return cachedTexture;

        // HashSet 同时充当在途请求表，避免三个槽位为同一 URL 重复创建 BitmapImage。
        if (capeTextureRequests.Add(source))
            BeginLoadCapeTexture(source);

        return null;
    }

    private void BeginLoadCapeTexture(string source)
    {
        try
        {
            // BitmapImage 自行异步下载；忽略 WPF 全局图片缓存可避免账户切换后使用陈旧纹理。
            var bitmap = new BitmapImage();
            ImageLoadDiagnostics.Observe(bitmap, source);
            bitmap.BeginInit();
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            var sourceUri = new Uri(source, UriKind.RelativeOrAbsolute);
            if (sourceUri.IsAbsoluteUri && sourceUri.IsFile)
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = sourceUri;
            bitmap.EndInit();

            if (bitmap.IsDownloading)
            {
                // 下载失败只释放在途标记，后续场景重建可以再次尝试该 URL。
                bitmap.DownloadCompleted += (_, _) => StoreLoadedCapeTexture(source, bitmap);
                bitmap.DownloadFailed += (_, _) => capeTextureRequests.Remove(source);
            }
            else
            {
                StoreLoadedCapeTexture(source, bitmap);
            }
        }
        catch (Exception exception)
        {
            ImageLoadDiagnostics.Failed(source, exception);
            capeTextureRequests.Remove(source);
        }
    }

    private void StoreLoadedCapeTexture(string source, BitmapSource texture)
    {
        try
        {
            var frozenTexture = FreezeTexture(texture);
            capeTextureCache[source] = frozenTexture;
            capeTextureVersions[source] = capeTextureVersions.TryGetValue(source, out var version) ? version + 1 : 1;
            foreach (var key in capeModelCache.Keys.Where(key => string.Equals(key.TextureSource, source, StringComparison.Ordinal)).ToArray())
                capeModelCache.Remove(key);
            capeTextureRequests.Remove(source);
            QueueTextureRefresh();
        }
        catch
        {
            capeTextureRequests.Remove(source);
        }
    }

    private void QueueTextureRefresh()
    {
        // 动画过程中替换场景会丢失当前变换；纹理到达后延迟到动画结束再统一重建。
        if (isRebuilding || isAnimating)
        {
            rebuildAfterAnimation = true;
            return;
        }

        QueueRebuild();
    }

    private static BitmapSource FreezeTexture(BitmapSource texture)
    {
        // 冻结后的图像可安全跨 Dispatcher 回调复用，并降低 WPF 变更通知成本。
        BitmapSource frozenTexture = texture;
        if (texture is BitmapImage bitmapImage && bitmapImage.IsDownloading)
            return frozenTexture;

        if (!texture.IsFrozen && texture.CanFreeze)
        {
            texture.Freeze();
            frozenTexture = texture;
        }

        return frozenTexture;
    }

    private static Transform3D CombineTransforms(Transform3D existing, Transform3D added)
    {
        if (existing == Transform3D.Identity)
            return added;

        var transform = new Transform3DGroup();
        transform.Children.Add(existing);
        transform.Children.Add(added);
        return transform;
    }

    /// <summary>
    /// 为现有项延续当前位置，为新项选择与切换方向一致的屏外入场位置。
    /// </summary>
    private static CapeCarouselSlotPlacement ResolveStartPlacement(
        AccountCapeOption cape,
        CapeCarouselSlot targetSlot,
        IReadOnlyList<SlotVisual> oldSlotVisuals,
        CapeCarouselDirection? direction)
    {
        if (direction is null)
            return CapeCarousel3DLayout.GetPlacement(targetSlot);

        // 已存在的披风从当前屏幕位置继续移动，只有新进入轮播的项才从边缘入场。
        var oldVisual = oldSlotVisuals.FirstOrDefault(visual => CapesMatch(visual.Cape, cape));
        if (oldVisual is not null)
            return oldVisual.GetCurrentPlacement();

        return direction switch
        {
            CapeCarouselDirection.Next => CapeCarousel3DLayout.GetEntryPlacement(CapeCarouselDirection.Next),
            CapeCarouselDirection.Previous => CapeCarousel3DLayout.GetEntryPlacement(CapeCarouselDirection.Previous),
            _ => CapeCarousel3DLayout.GetPlacement(targetSlot)
        };
    }

    /// <summary>
    /// 同步启动所有槽位变换，并用动画代次阻止旧计时器结束新动画。
    /// </summary>
    private void AnimateSlots()
    {
        viewportRenderCache.Disable("CarouselAnimation");
        // 所有槽位共享缓动和时长，保证中心/侧边模型在视觉上作为一个整体移动。
        var easing = new PowerEase { EasingMode = EasingMode.EaseOut, Power = 7 };
        isAnimating = true;
        var generation = ++animationGeneration;

        foreach (var visual in currentSlotVisuals.Values)
        {
            visual.Translate.BeginAnimation(TranslateTransform3D.OffsetXProperty, CreateAnimation(visual.TargetPlacement.X, easing));
            visual.Scale.BeginAnimation(ScaleTransform3D.ScaleXProperty, CreateAnimation(visual.TargetPlacement.Scale, easing));
            visual.Scale.BeginAnimation(ScaleTransform3D.ScaleYProperty, CreateAnimation(visual.TargetPlacement.Scale, easing));
            visual.Scale.BeginAnimation(ScaleTransform3D.ScaleZProperty, CreateAnimation(visual.TargetPlacement.Scale, easing));
        }

        // WPF Animation 没有统一的组完成事件，这里用略长于动画的计时器收束状态机。
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AnimationMilliseconds + 40)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // 旧动画的计时器不得结束更新后的动画状态。
            if (generation != animationGeneration)
                return;

            CompleteCurrentAnimations();
            isAnimating = false;
            if (!rebuildAfterAnimation)
            {
                QueueViewportRenderCache();
                return;
            }

            rebuildAfterAnimation = false;
            QueueRebuild();
        };
        timer.Start();
    }

    private void CompleteCurrentAnimations()
    {
        foreach (var visual in currentSlotVisuals.Values)
        {
            CarouselAnimationLifecycle.CompleteAndRemoveClocks(
                visual.Scale,
                visual.Translate,
                visual.TargetPlacement.X,
                visual.TargetPlacement.Scale);
        }
    }

    private static void FreezeCurrentAnimations(IEnumerable<SlotVisual> visuals)
    {
        foreach (var visual in visuals)
            CarouselAnimationLifecycle.CaptureCurrentAndRemoveClocks(visual.Scale, visual.Translate);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        QueueRebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        viewportRenderCache.Disable("Unloaded");
        sceneBuildGeneration++;
        animationGeneration++;
        sceneBuildCancellation?.Cancel();
        sceneBuildCancellation?.Dispose();
        sceneBuildCancellation = null;
        FreezeCurrentAnimations(currentSlotVisuals.Values);
        viewport.Children.Clear();
        currentSlotVisuals.Clear();
        hitSlots.Clear();
        capeTextureRequests.Clear();
        rebuildQueued = false;
        isRebuilding = false;
        isAnimating = false;
        rebuildAfterAnimation = false;
    }

    private void QueueViewportRenderCache()
    {
        viewportRenderCache.QueueEnable(() =>
            !isAnimating
            && !isRebuilding
            && !rebuildQueued
            && currentSlotVisuals.Count > 0);
    }

    private static DoubleAnimation CreateAnimation(double to, IEasingFunction easing)
    {
        return new DoubleAnimation(to, TimeSpan.FromMilliseconds(AnimationMilliseconds))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var slot = HitTestSlot(e.GetPosition(this));
        if (slot is CapeCarouselSlot.Left)
            ExecuteCommand(PreviousCommand);
        else if (slot is CapeCarouselSlot.Right)
            ExecuteCommand(NextCommand);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var slot = HitTestSlot(e.GetPosition(this));
        var canClickSide = CanClickSlot(slot);
        Cursor = canClickSide
            ? Cursors.Hand
            : Cursors.Arrow;
        UpdateHoverHint(canClickSide ? slot : null);
    }

    /// <summary>
    /// 将 Viewport3D 命中结果映射回逻辑槽位，供点击与 Hover 提示共用。
    /// </summary>
    private CapeCarouselSlot? HitTestSlot(Point point)
    {
        CapeCarouselSlot? slot = null;
        VisualTreeHelper.HitTest(
            viewport,
            null,
            result =>
            {
                if (result is RayHitTestResult rayResult
                    && rayResult.ModelHit is not null
                    && hitSlots.TryGetValue(rayResult.ModelHit, out var hitSlot))
                {
                    slot = hitSlot;
                    return HitTestResultBehavior.Stop;
                }

                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(TranslatePoint(point, viewport)));

        return slot;
    }

    private bool CanClickSlot(CapeCarouselSlot? slot)
    {
        // 动画期间模型仍在移动，禁止点击可避免用户命令与视觉目标错位。
        if (isAnimating)
            return false;

        return slot switch
        {
            CapeCarouselSlot.Left => PreviousCommand?.CanExecute(null) == true,
            CapeCarouselSlot.Right => NextCommand?.CanExecute(null) == true,
            _ => false
        };
    }

    private void UpdateHoverHint(CapeCarouselSlot? slot)
    {
        leftHoverHint.Visibility = slot is CapeCarouselSlot.Left ? Visibility.Visible : Visibility.Collapsed;
        rightHoverHint.Visibility = slot is CapeCarouselSlot.Right ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Grid CreateHoverLayer()
    {
        return new Grid
        {
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    private static Border CreateHoverHint()
    {
        var hint = new Border
        {
            Width = 116,
            Height = 154,
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        hint.SetResourceReference(Border.BackgroundProperty, "Brush.Control.Hover");
        return hint;
    }

    private static void PositionHoverHint(Border hint, CapeCarouselSlot slot)
    {
        var offset = slot is CapeCarouselSlot.Left ? -175 : 175;
        hint.RenderTransform = new TranslateTransform(offset, 0);
    }

    private static void ExecuteCommand(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
            command.Execute(null);
    }

    private void RegisterHitModels(Model3D model, CapeCarouselSlot slot)
    {
        // RayHitTest 可能命中组内任意面，因此递归登记整棵模型树。
        hitSlots[model] = slot;
        if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
                RegisterHitModels(child, slot);
        }
    }

    private static bool CapesMatch(AccountCapeOption? left, AccountCapeOption? right)
    {
        return CapeCarousel3DLayout.CapesRepresentSameVisualItem(left, right);
    }

    private sealed record CapeSlotBuildRequest(
        CapeCarouselSlot Slot,
        AccountCapeOption? Cape,
        double Brightness,
        BitmapSource? Texture,
        CapeModelCacheKey Key);

    private readonly record struct CapeModelCacheKey(
        string Identity,
        string? TextureSource,
        double Brightness,
        int TextureVersion,
        bool HasTexture);

    private sealed record SlotVisual(
        AccountCapeOption Cape,
        CapeCarouselSlot Slot,
        ScaleTransform3D Scale,
        TranslateTransform3D Translate,
        CapeCarouselSlotPlacement TargetPlacement)
    {
        public CapeCarouselSlotPlacement GetCurrentPlacement()
        {
            return new CapeCarouselSlotPlacement(Translate.OffsetX, Scale.ScaleX);
        }
    }
}
