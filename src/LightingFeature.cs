using System;
using System.IO;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Graphics.Effects;
using Terraria.ID;
using tContentPatch;

namespace LightsAndShadows
{
    internal class LightingPatchMain : PatchMain
    {
        public override void OnEnterWorld()
        {
            LightingFX.OnEnterWorld();
        }
    }

    /// <summary>无滤镜时也强制原版捕获世界, 这样 EndCapture 前能拿到 screenTarget。</summary>
    [HarmonyPatch(typeof(FilterManager), "CanCapture")]
    internal static class Patch_CanCapture
    {
        private static void Postfix(ref bool __result)
        {
            if (!__result && LightingFX.ShouldForceCapture()) __result = true;
        }
    }

    /// <summary>
    /// 原版 Lights And Shadows 的钩子点: EndCapture。
    /// Prefix 在有滤镜时先拷贝 天空+世界; Postfix 在场景合并后应用作者的光照/光柱/Bloom。
    /// </summary>
    [HarmonyPatch(typeof(FilterManager), "EndCapture", new Type[]
    {
        typeof(RenderTarget2D), typeof(RenderTarget2D), typeof(RenderTarget2D),
        typeof(Vector2), typeof(Vector2), typeof(Vector2)
    })]
    internal static class Patch_EndCapture
    {
        private static void Prefix(RenderTarget2D finalTexture, RenderTarget2D screenTarget1)
        {
            try { LightingFX.BeforeCapture(finalTexture, screenTarget1); }
            catch (Exception ex) { LightsMod.LogRuntimeError("BeforeCapture: " + ex); }
        }

        private static void Postfix(RenderTarget2D finalTexture)
        {
            try { LightingFX.AfterCapture(finalTexture); }
            catch (Exception ex) { LightsMod.LogRuntimeError("AfterCapture: " + ex); }
        }
    }

    /// <summary>
    /// 移植 Workshop 原版 Lights And Shadows (yiyang233) 的渲染管线:
    ///   Light.fx  : ColorTex 颜色渐变 + 以太阳为中心的径向光
    ///   Shadow.fx : 蓝通道阴影图 GetShadow → 以太阳为中心多次放大叠加成光柱 → UseShadow 从光里减掉
    ///   Bloom1.fx : 亮度提取 + DualBlur mip 链 + 遮罩混合
    /// 贴图、参数与混合方式均按作者原版。
    /// </summary>
    internal static class LightingFX
    {
        private const int MaskDiv = 2; // 阴影/光柱图 = 画面 1/2 (原版为 1/3, 提高清晰度)

        private static Effect _lightFx;
        private static Effect _shadowFx;
        private static Effect _bloomFx;
        private static Effect _dualBlurFx;
        private static Effect _darkenFx;
        private static Effect _maskFx;

        // 多个光源用 Max 混合: 各自独立, 相互不叠加, 扎堆也不会爆白
        private static readonly BlendState LightBlend = new BlendState
        {
            ColorBlendFunction = BlendFunction.Max,
            AlphaBlendFunction = BlendFunction.Max,
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
        };
        private static bool _resFailed;

        private static Texture2D _colorClassic;
        private static Texture2D _colorAlt;
        private static Texture2D _colorNight;
        private static Texture2D _torchColor;
        private static Texture2D _white;

        private static RenderTarget2D _shadowMask; // 1/3
        private static RenderTarget2D _light;      // full
        private static RenderTarget2D _rays;       // 1/3
        private static RenderTarget2D _result;     // full
        private static RenderTarget2D _spare;      // full
        private static RenderTarget2D _bloom;      // full
        private static RenderTarget2D _bloomMask;  // full
        private static readonly RenderTarget2D[] BloomMips = new RenderTarget2D[4];

        private static RenderTarget2D _sceneCopy;  // 有滤镜时, 滤镜前的 天空+世界
        private static bool _sceneCopyValid;
        private static RenderTarget2D _worldMask;  // 世界层 alpha, 用于“光照不穿背景”
        private static bool _worldMaskValid;
        private static bool _loggedFirst;
        private static DateTime _lastLog;
        private static int _frames;

        internal static void OnEnterWorld()
        {
            _loggedFirst = false;
            _lastLog = DateTime.MinValue;
            _frames = 0;
            _sceneCopyValid = false;
            LightsMod.LogInfo("=== 进入世界: 原版 Lights And Shadows 管线 ===");
        }

        internal static void DisposeTextures()
        {
            try { DisposeEffect(_lightFx); } catch { }
            try { DisposeEffect(_shadowFx); } catch { }
            try { DisposeEffect(_bloomFx); } catch { }
            try { DisposeEffect(_dualBlurFx); } catch { }
            try { DisposeEffect(_darkenFx); } catch { }
            try { DisposeEffect(_maskFx); } catch { }
            try { DisposeTex(_colorClassic); } catch { }
            try { DisposeTex(_colorAlt); } catch { }
            try { DisposeTex(_colorNight); } catch { }
            try { DisposeTex(_torchColor); } catch { }
            try { DisposeTex(_white); } catch { }
            try { DisposeTex(_shadowMask); } catch { }
            try { DisposeTex(_light); } catch { }
            try { DisposeTex(_rays); } catch { }
            try { DisposeTex(_result); } catch { }
            try { DisposeTex(_spare); } catch { }
            try { DisposeTex(_bloom); } catch { }
            try { DisposeTex(_bloomMask); } catch { }
            for (int i = 0; i < BloomMips.Length; i++) { try { DisposeTex(BloomMips[i]); } catch { } BloomMips[i] = null; }
            try { DisposeTex(_sceneCopy); } catch { }
try { DisposeTex(_worldMask); } catch { }

            _lightFx = null; _shadowFx = null; _bloomFx = null; _dualBlurFx = null; _darkenFx = null; _maskFx = null;
            _colorClassic = null; _colorAlt = null; _colorNight = null; _torchColor = null; _white = null;
            _shadowMask = null; _light = null; _rays = null; _result = null; _spare = null;
            _bloom = null; _bloomMask = null; _sceneCopy = null; _worldMask = null; _worldMaskValid = false;
            _resFailed = false;
            _sceneCopyValid = false;
        }

        private static void DisposeEffect(Effect e) { if (e != null && !e.IsDisposed) e.Dispose(); }
        private static void DisposeTex(Texture2D t) { if (t != null && !t.IsDisposed) t.Dispose(); }

        // ======================= 条件 =======================

        internal static bool ShouldForceCapture()
        {
            if (_resFailed) return false;
            if (!ShouldRun()) return false;
            if (Main.drawToScreen || !Lighting.NotRetro) return false;
            return true;
        }

        private static bool ShouldRun()
        {
            Setting.Data d = Setting.data;
            if (d == null || (!d.UseLight && !d.UseBloom)) return false;
            if (Main.instance == null || Main.graphics == null) return false;
            if (Main.gameMenu || Main.showSplash || Main.mapFullscreen) return false;
            if (WorldGen.isGeneratingOrLoadingWorld) return false;
            Player p = GetLocalPlayer();
            if (p == null || !p.active) return false;
            return true;
        }

        private static Player GetLocalPlayer()
        {
            int index = Main.myPlayer;
            if (index < 0 || index >= Main.player.Length) return null;
            return Main.player[index];
        }

        // ======================= 分层阴影 + 滤镜前拷贝 =======================

        internal static void BeforeCapture(RenderTarget2D finalTexture, RenderTarget2D worldTarget)
        {
            _sceneCopyValid = false;
            if (finalTexture != null) return;
            if (!ShouldRun()) return;
            if (Main.drawToScreen || !Lighting.NotRetro) return;
            if (worldTarget == null) return;

            GraphicsDevice dev = Main.graphics.GraphicsDevice;
            try
            {
                if (!EnsureResources()) return;

                // 阴影分层: 世界/玩家层 与 背景/天空层 各自独立压暗
                ApplyLayerShadows(dev, worldTarget, Setting.data);

                // 光照不穿背景: 在世界层还完整时保存它的 alpha 遮罩, Postfix 用
                _worldMaskValid = false;
                Setting.Data d = Setting.data;
                if (!d.LightThroughBackground)
                {
                    int mw = worldTarget.Width, mh = worldTarget.Height;
                    if (EnsureTarget(ref _worldMask, dev, mw, mh))
                    {
                        dev.SetRenderTarget(_worldMask);
                        dev.Clear(Color.Transparent);
                        DrawSprite(null, worldTarget, new Rectangle(0, 0, mw, mh), Color.White, SpriteEffects.None, BlendState.Opaque);
                        _worldMaskValid = true;
                    }
                }

                // 有滤镜时, 再把(已加阴影的) 天空+世界 拷一份给 Postfix 用
                if (Filters.Scene.HasActiveFilter() && Main.skyTarget != null)
                {
                    int w = worldTarget.Width, h = worldTarget.Height;
                    if (!EnsureTarget(ref _sceneCopy, dev, w, h)) return;

                    dev.SetRenderTarget(_sceneCopy);
                    dev.Clear(Color.Transparent);
                    bool began = false;
                    try
                    {
                        Main.spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend,
                            SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, null, Matrix.Identity);
                        began = true;
                        SpriteEffects effects = Main.GameViewMatrix.Effects;
                        Main.spriteBatch.Draw(Main.skyTarget, Vector2.Zero, null, Color.White, 0f, Vector2.Zero, 1f, effects, 0f);
                        Main.spriteBatch.Draw(worldTarget, Vector2.Zero, null, Color.White, 0f, Vector2.Zero, 1f, effects, 0f);
                    }
                    finally
                    {
                        if (began)
                        {
                            try { Main.spriteBatch.End(); }
                            catch (Exception ex) { LightsMod.LogRuntimeError("BeforeCapture.End: " + ex.Message); }
                        }
                    }
                    _sceneCopyValid = true;
                }
            }
            finally
            {
                try { dev.SetRenderTarget(null); } catch { }
            }
        }

        /// <summary>
        /// 用世界层的光柱遮罩分别压暗两层:
        /// 前景/玩家层 = worldTarget (地形/树/玩家)
        /// 背景层     = Main.skyTarget (视差背景/天空)
        /// 两个强度完全独立。
        /// </summary>
        internal static void AfterCapture(RenderTarget2D finalTexture)
        {
            if (finalTexture != null) return;
            if (!ShouldRun()) return;
            if (Main.drawToScreen || !Lighting.NotRetro) return;
            if (!EnsureResources()) return;

            RenderTarget2D scene = _sceneCopyValid ? _sceneCopy : Main.screenTargetSwap;
            _sceneCopyValid = false;
            if (scene == null || scene.IsDisposed || scene.Width <= 0 || scene.Height <= 0) return;

            // 光照穿背景: 在合并后的画面上做; 不穿背景: Prefix 已经加到世界层
            RunPipeline(scene);
            LogFrame(scene);
        }

        private static void ApplyLayerShadows(GraphicsDevice dev, RenderTarget2D worldTarget, Setting.Data d)
        {
            bool underground = IsUnderground();
            float fg = underground ? 0f : MathHelper.Clamp(d.ForegroundShadow, 0f, 2f) * 0.35f;
            float bg = underground ? 0f : MathHelper.Clamp(d.BackgroundShadow, 0f, 2f) * 0.28f;
            bool worldLight = false; // 关闭“只照世界层”路径: 该路径会写回游戏世界目标, 有紫屏风险
            if (fg <= 0.005f && bg <= 0.005f && !worldLight) return;

            int W = worldTarget.Width, H = worldTarget.Height;
            if (!EnsureTargets(dev, W, H)) return;

            // 太阳 + 火把的 光照/光柱 (worldLight 时顺便构建 _light)
            RenderLightAndRays(dev, worldTarget, true, d, worldLight);

            // 前景/玩家层
            if (fg > 0.005f)
            {
                dev.SetRenderTarget(_result);
                dev.Clear(Color.Black);
                _darkenFx.Parameters["uMaskTex"].SetValue(_rays);
                _darkenFx.Parameters["uStrength"].SetValue(fg);
                DrawSprite(_darkenFx.CurrentTechnique.Passes["Darken"], worldTarget,
                    new Rectangle(0, 0, W, H), Color.White, SpriteEffects.None, BlendState.Opaque);
                dev.SetRenderTarget(worldTarget);
                DrawSprite(null, _result, new Rectangle(0, 0, W, H), Color.White, SpriteEffects.None, BlendState.Opaque);
            }

            // 背景层
            if (bg > 0.005f && Main.skyTarget != null && !Main.skyTarget.IsDisposed)
            {
                int bw = Main.skyTarget.Width, bh = Main.skyTarget.Height;
                dev.SetRenderTarget(_result);
                dev.Clear(Color.Black);
                _darkenFx.Parameters["uMaskTex"].SetValue(_rays);
                _darkenFx.Parameters["uStrength"].SetValue(bg);
                DrawSprite(_darkenFx.CurrentTechnique.Passes["Darken"], Main.skyTarget,
                    new Rectangle(0, 0, bw, bh), Color.White, SpriteEffects.None, BlendState.Opaque);
                dev.SetRenderTarget(Main.skyTarget);
                DrawSprite(null, _result, new Rectangle(0, 0, bw, bh), Color.White, SpriteEffects.None, BlendState.Opaque);
            }

            // 光照不穿背景: 直接把 (光 - 光柱) 加到世界层
            if (worldLight && d.UseLight)
            {
                _shadowFx.Parameters["tex0"].SetValue(_rays);
                dev.SetRenderTarget(worldTarget);
                DrawSprite(_shadowFx.CurrentTechnique.Passes[1], _light,
                    new Rectangle(0, 0, W, H), Color.White, SpriteEffects.None, BlendState.Additive);
            }
        }

        private static void RunPipeline(RenderTarget2D scene)
        {
            Setting.Data d = Setting.data;
            GraphicsDevice dev = Main.graphics.GraphicsDevice;
            int W = scene.Width, H = scene.Height;
            if (!EnsureTargets(dev, W, H)) return;

            SamplerState old1 = dev.SamplerStates[1];
            SamplerState old2 = dev.SamplerStates[2];
            string step = "start";
            try
            {
                int mipCount = Math.Max(1, Math.Min(3, d.BloomCounts)) + 1;
                if (mipCount > BloomMips.Length) mipCount = BloomMips.Length;
                EnsureBloomMips(dev, W, H, mipCount);

                bool flip = Main.LocalPlayer != null && Main.LocalPlayer.gravDir == -1f;
                SpriteEffects finalEffects = flip ? SpriteEffects.FlipVertically : SpriteEffects.None;

                step = "copy-scene";
                dev.SetRenderTarget(_spare);
                dev.Clear(Color.Black);
                DrawSprite(null, scene, new Rectangle(0, 0, W, H), Color.White, SpriteEffects.None, BlendState.Opaque);

                step = "light-rays";
                RenderLightAndRays(dev, scene, false, d, true);

                // 光照不穿背景: 用世界层 alpha 把 _light 限制在前景/世界层

                step = "composite";
                dev.SetRenderTarget(_result);
                dev.Clear(Color.Black);
                DrawSprite(null, _spare, new Rectangle(0, 0, W, H), Color.White, SpriteEffects.None, BlendState.Opaque);
                if (d.UseLight)
                {
                    _shadowFx.Parameters["tex0"].SetValue(_rays);
                    DrawSprite(_shadowFx.CurrentTechnique.Passes[1], _light, new Rectangle(0, 0, W, H), Color.White, SpriteEffects.None, BlendState.Additive);
                }

                step = "bloom";
                if (d.UseBloom)
                {
                    BloomPass(dev, d, W, H, mipCount, finalEffects);
                }
                else
                {
                    dev.SetRenderTarget(null);
                    DrawSprite(null, _result, new Rectangle(0, 0, W, H), Color.White, finalEffects, BlendState.Opaque);
                }
            }
            catch (Exception ex)
            {
                LightsMod.LogRuntimeError("Pipeline@" + step + ": " + ex.Message);
                throw;
            }
            finally
            {
                try { dev.SetRenderTarget(null); } catch { }
                dev.SamplerStates[1] = old1;
                dev.SamplerStates[2] = old2;
                for (int i = 0; i < 3; i++) dev.Textures[i] = null;
            }
        }
        private static bool IsUnderground()
        {
            Player p = Main.LocalPlayer;
            return p != null && p.Center.Y >= Main.worldSurface * 16.0 + 800.0;
        }

        private static void RenderLightAndRays(GraphicsDevice dev, RenderTarget2D scene, bool flip, Setting.Data d, bool buildLight)
        {
            int W = scene.Width, H = scene.Height;
            if (!EnsureTargets(dev, W, H)) return;

            float dayLight, moonK;
            ComputeDayLight(d, out dayLight, out moonK);
            bool underground = IsUnderground();

            // 阴影图只在地表有意义: 地下蓝通道整片都低, 会把整个洞穴当成阴影
            if (!underground)
            {
                dev.SetRenderTarget(_shadowMask);
                dev.Clear(Color.Black);
                float m = 1f - Main.bgAlphaFrontLayer[2] * 0.09f;
                if (!Main.dayTime) m = 0.02f;
                _shadowFx.Parameters["uScreenResolution"].SetValue(new Vector2(W, H));
                _shadowFx.Parameters["m"].SetValue(m);
                _shadowFx.Parameters["uPos"].SetValue(ToScreenCoords(GetSunPos(scene, flip), scene));
                DrawSprite(_shadowFx.CurrentTechnique.Passes[0], scene,
                    new Rectangle(0, 0, W / MaskDiv, H / MaskDiv), Color.White, SpriteEffects.None, BlendState.Additive);
            }

            // 光照图: 太阳 + 火把/灯 (只在需要时构建)
            if (buildLight && d.UseLight)
            {
                dev.SetRenderTarget(_light);
                dev.Clear(Color.Black);
                LightPass(scene, d, dayLight, moonK, flip);
                if (!d.LightThroughBackground && _worldMaskValid)
                {
                    dev.SetRenderTarget(_result);
                    dev.Clear(Color.Black);
                    _maskFx.Parameters["uMaskTex"].SetValue(_worldMask);
                    DrawSprite(_maskFx.CurrentTechnique.Passes["Mask"], _light,
                        new Rectangle(0, 0, W, H), Color.White, SpriteEffects.None, BlendState.Opaque);
                    dev.SetRenderTarget(_light);
                    DrawSprite(null, _result, new Rectangle(0, 0, W, H), Color.White, SpriteEffects.None, BlendState.Opaque);
                }

                AddTorchGlows(scene, flip, d, underground);
            }

            // 光柱图: 地下没有太阳阴影, 只保留火把径向光; 地表的太阳/火把光柱照旧
            dev.SetRenderTarget(_rays);
            dev.Clear(Color.Black);
            if (!underground)
            {
                DrawRays(scene, d, dayLight, W, H, flip);
            }
        }

        private struct LightSource
        {
            public Vector2 World;
            public Color Color;
            public float Intensity;
            public float Radius;
        }

        private static void AddTorchGlows(RenderTarget2D scene, bool flip, Setting.Data d, bool underground)
        {
            // 照明物品亮度: 默认不受地上/地下影响, 只看白天/夜晚
            float dayScale;
            if (d.TorchIgnoreDepth)
            {
                dayScale = Main.dayTime ? d.TorchDayIntensity : d.TorchNightIntensity;
            }
            else
            {
                dayScale = 1f;
                if (!underground)
                {
                    Player lp = Main.LocalPlayer;
                    float depthTiles = (lp != null ? lp.Center.Y / 16f : 0f) - (float)Main.worldSurface;
                    float depthScale = MathHelper.Clamp(depthTiles / 45f, 0f, 1f);
                    dayScale = 0.35f + 0.65f * depthScale;
                    if (Main.dayTime) dayScale *= d.TorchDayIntensity;
                }
            }
            dayScale = MathHelper.Clamp(dayScale, 0f, 2f);

            var lights = CollectLights(scene);
            for (int i = 0; i < lights.Count; i++)
            {
                LightSource src = lights[i];
                Vector2 uv = WorldToUV(src.World, scene, flip);
                if (uv.X < -0.05f || uv.X > 1.05f || uv.Y < -0.05f || uv.Y > 1.05f) continue;

                _lightFx.Parameters["uPos"].SetValue(uv);
                _lightFx.Parameters["uRadius"].SetValue(src.Radius);
                _lightFx.Parameters["uPower"].SetValue(1.6f);
                _lightFx.Parameters["uTint"].SetValue(ToVector3(src.Color));
                _lightFx.Parameters["uAlphaOut"].SetValue(1f);
                _lightFx.Parameters["uSceneTex"].SetValue(scene);
                _lightFx.Parameters["uSceneMix"].SetValue(1f);
                _lightFx.Parameters["tex0"].SetValue(_white);
                _lightFx.Parameters["intensity"].SetValue(src.Intensity * dayScale);
                _lightFx.Parameters["t"].SetValue(0.5f);
                DrawSprite(_lightFx.CurrentTechnique.Passes["Light"], _white,
                    new Rectangle(0, 0, scene.Width, scene.Height), Color.White, SpriteEffects.None, LightBlend);
            }
        }

        private static Vector3 ToVector3(Color c)
        {
            return new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
        }

        private static Setting.CustomLight FindCustomLight(int itemType)
        {
            var list = Setting.data != null ? Setting.data.CustomLights : null;
            if (list == null || list.Count == 0) return null;
            string name = null;
            try { name = Terraria.ID.ItemID.Search.GetName(itemType); } catch { }
            for (int i = 0; i < list.Count; i++)
            {
                Setting.CustomLight e = list[i];
                if (e == null || string.IsNullOrEmpty(e.Item)) continue;
                if (!string.IsNullOrEmpty(name) &&
                    string.Equals(e.Item, name, StringComparison.OrdinalIgnoreCase)) return e;
                int id;
                if (int.TryParse(e.Item, out id) && id == itemType) return e;
            }
            return null;
        }

        private static Color ParseLightColor(string s, Color fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim();
            try
            {
                if (s.StartsWith("#") && s.Length >= 7)
                {
                    int v = Convert.ToInt32(s.Substring(1, 6), 16);
                    return new Color((v >> 16) & 255, (v >> 8) & 255, v & 255, 255);
                }
                string[] p = s.Split(',');
                if (p.Length >= 3)
                {
                    int r = int.Parse(p[0].Trim());
                    int g = int.Parse(p[1].Trim());
                    int b = int.Parse(p[2].Trim());
                    return new Color(MathHelper.Clamp(r, 0, 255), MathHelper.Clamp(g, 0, 255),
                        MathHelper.Clamp(b, 0, 255), 255);
                }
            }
            catch { }
            return fallback;
        }

        private static Color SampleLightColor(int tx, int ty, Color fallback)
        {
            try
            {
                Color lit = Lighting.GetColor(tx, ty);
                float max = Math.Max(lit.R, Math.Max(lit.G, lit.B));
                if (max < 8) return fallback;
                float k = 255f / max;
                return new Color(
                    (byte)MathHelper.Clamp(lit.R * k, 0f, 255f),
                    (byte)MathHelper.Clamp(lit.G * k, 0f, 255f),
                    (byte)MathHelper.Clamp(lit.B * k, 0f, 255f),
                    255);
            }
            catch { return fallback; }
        }

        private static readonly System.Collections.Generic.List<LightSource> _torchCache =
            new System.Collections.Generic.List<LightSource>();

        private static System.Collections.Generic.List<LightSource> CollectLights(RenderTarget2D scene)
        {
            _torchCache.Clear();
            Player p = Main.LocalPlayer;
            if (p != null)
            {
                Item held = p.HeldItem;
                Setting.CustomLight heldCustom = held != null ? FindCustomLight(held.type) : null;
                bool heldLight = held != null && held.stack > 0 &&
                    ((held.createTile > 0 && IsLightTile((ushort)held.createTile)) ||
                     ItemID.Sets.Glowsticks[held.type] || heldCustom != null);
                if (heldLight)
                {
                    int tx = (int)(p.Center.X / 16f);
                    int ty = (int)(p.Center.Y / 16f);
                    Color hc;
                    float intensity = 1.1f;
                    float radius = 0.55f;
                    if (held.createTile == TileID.Torches)
                    {
                        hc = TorchStyleColor(held.placeStyle, 0f);
                    }
                    else if (held.createTile == TileID.Campfire)
                    {
                        hc = TorchStyleColor(CampfireToTorchStyle(held.placeStyle), 0f);
                    }
                    else if (ItemID.Sets.Glowsticks[held.type])
                    {
                        hc = ItemGlowstickColor(held.type);
                        intensity = 1.0f;
                        radius = 0.45f;
                    }
                    else
                    {
                        hc = SampleLightColor(tx, ty, new Color(255, 170, 80));
                    }
                    if (heldCustom != null)
                    {
                        hc = ParseLightColor(heldCustom.Color, hc);
                        intensity = MathHelper.Clamp(heldCustom.Intensity, 0f, 5f);
                        radius = MathHelper.Clamp(heldCustom.Radius, 0.05f, 3f);
                    }
                    _torchCache.Add(new LightSource
                    {
                        World = p.Center + new Vector2(0f, -8f),
                        Color = hc,
                        Intensity = intensity,
                        Radius = radius,
                    });
                }
            }

            for (int i = 0; i < Main.maxProjectiles; i++)
            {
                Projectile proj = Main.projectile[i];
                if (proj == null || !proj.active) continue;
                if (!IsGlowstickProjectile((short)proj.type)) continue;
                _torchCache.Add(new LightSource
                {
                    World = proj.Center,
                    Color = GlowstickColor((short)proj.type, proj.whoAmI * 0.17f),
                    Intensity = 1.0f,
                    Radius = 0.45f,
                });
            }

            // 地上掉落的发光道具 (CustomLights 中配置过的物品)
            for (int i = 0; i < Main.maxItems; i++)
            {
                WorldItem it = Main.item[i];
                if (it == null || !it.active || it.stack <= 0) continue;
                Setting.CustomLight cl = FindCustomLight(it.type);
                if (cl == null) continue;
                _torchCache.Add(new LightSource
                {
                    World = it.Center,
                    Color = ParseLightColor(cl.Color, new Color(255, 200, 120)),
                    Intensity = MathHelper.Clamp(cl.Intensity, 0f, 5f),
                    Radius = MathHelper.Clamp(cl.Radius, 0.05f, 3f),
                });
            }

            float zoom = Math.Abs(Main.GameViewMatrix.ZoomMatrix.M11);
            if (zoom <= 0.01f) zoom = 1f;
            int tilesW = (int)(scene.Width / zoom / 16f) + 4;
            int tilesH = (int)(scene.Height / zoom / 16f) + 4;
            int x0 = (int)(Main.screenPosition.X / 16f) - 2;
            int y0 = (int)(Main.screenPosition.Y / 16f) - 2;
            int x1 = x0 + tilesW;
            int y1 = y0 + tilesH;
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) continue;
                    Tile t = Main.tile[x, y];
                    if (t == null || !t.active()) continue;
                    ushort type = t.type;
                    if (!IsLightTile(type)) continue;
                    Color lc = SampleLightColor(x, y, new Color(255, 170, 80));
                    float intensity = 1.1f;
                    Vector2 lightPos = new Vector2(x * 16 + 8, y * 16 + 8);
                    float radius = 0.55f;
                    if (type == TileID.Torches)
                    {
                        lc = TorchStyleColor(t.frameY / 22, x * 0.7f + y * 0.3f);
                    }
                    else if (type == TileID.Campfire)
                    {
                        // 3x2 家具: 只取左上角, 否则一个篝火会被算成 6 个光源
                        if (t.frameX != 0 || (t.frameY % 36) != 0) continue;
                        lc = TorchStyleColor(CampfireToTorchStyle(t.frameY / 36), x * 0.7f + y * 0.3f);
                        lightPos = new Vector2(x * 16 + 24, y * 16 + 16);
                        intensity = 0.95f;
                        radius = 0.6f;
                    }
                    else if (type == TileID.Candles || type == TileID.ChineseLanterns ||
                             type == TileID.Jackolanterns)
                    {
                        intensity = 0.8f;
                    }
                    _torchCache.Add(new LightSource
                    {
                        World = lightPos,
                        Color = lc,
                        Intensity = intensity,
                        Radius = radius,
                    });
                }
            }

            // 光源太多时只保留离玩家最近的, 避免每帧几百次全屏绘制
            const int MaxLights = 48;
            if (_torchCache.Count > MaxLights)
            {
                Vector2 pc = Main.LocalPlayer != null ? Main.LocalPlayer.Center : Vector2.Zero;
                _torchCache.Sort((a, b) =>
                    Vector2.DistanceSquared(a.World, pc).CompareTo(Vector2.DistanceSquared(b.World, pc)));
                _torchCache.RemoveRange(MaxLights, _torchCache.Count - MaxLights);
            }
            return _torchCache;
        }

        private static bool IsGlowstickProjectile(short type)
        {
            return type == ProjectileID.Glowstick || type == ProjectileID.StickyGlowstick
                || type == ProjectileID.SpelunkerGlowstick || type == ProjectileID.BouncyGlowstick
                || type == ProjectileID.FairyGlowstick || type == ProjectileID.RainbowGlowstick;
        }

        private static int CampfireToTorchStyle(int style)
        {
            switch (style)
            {
                case 1: return 8;   // 诅咒篝火 -> 诅咒火把
                case 2: return 7;   // 恶魔篝火 -> 恶魔火把
                case 3: return 9;   // 冰冻篝火 -> 冰火把
                case 4: return 11;  // 灵液篝火 -> 灵液火把
                case 5: return 14;  // 彩虹篝火 -> 彩虹火把
                case 6: return 5;   // 超亮篝火 -> 白火把
                case 7: return 13;  // 骨篝火 -> 骨火把
                case 8: return 16;  // 沙漠篝火 -> 沙漠火把
                case 9: return 17;  // 珊瑚篝火 -> 珊瑚火把
                case 10: return 18; // 腐化篝火 -> 腐化火把
                case 11: return 19; // 猩红篝火 -> 猩红火把
                case 12: return 20; // 神圣篝火 -> 神圣火把
                case 13: return 21; // 丛林篝火 -> 丛林火把
                case 14: return 22; // 蘑菇篝火 -> 蘑菇火把
                case 15: return 23; // 微光篝火 -> 微光火把
                default: return 0;  // 普通篝火 -> 普通火把
            }
        }

        private static Color TorchStyleColor(int style, float phase)
        {
            switch (style)
            {
                case 0: return new Color(255, 170, 80);    // 普通火把: 暖黄
                case 1: return new Color(100, 180, 255);   // 蓝火把
                case 2: return new Color(255, 80, 70);     // 红火把
                case 3: return new Color(100, 255, 100);   // 绿火把
                case 4: return new Color(190, 120, 255);   // 紫火把
                case 5: return new Color(255, 245, 225);   // 白火把
                case 6: return new Color(255, 230, 100);   // 黄火把
                case 7: return new Color(110, 40, 205);    // 恶魔火把: 深紫
                case 8:
                {
                    // 诅咒火把: 黄绿 ↔ 纯绿 平滑渐变
                    float t = 0.5f + 0.5f * (float)Math.Sin(Main.GlobalTimeWrappedHourly * 2.5f + phase);
                    return Color.Lerp(new Color(185, 255, 80), new Color(70, 255, 70), t);
                }
                case 9: return new Color(120, 200, 255);   // 冰火把: 蓝
                case 10: return new Color(255, 150, 60);   // 橙火把
                case 11: return new Color(255, 235, 90);   // 灵液火把: 黄
                case 13: return new Color(235, 230, 200);  // 骨火把
                case 14:                                   // 彩虹火把: 循环变色
                    return HsvToColor((Main.GlobalTimeWrappedHourly * 0.25f + phase * 0.03f) % 1f, 0.75f, 1f);
                case 15: return new Color(255, 130, 210);  // 粉火把
                case 16: return new Color(255, 220, 140);  // 沙漠火把
                case 17: return new Color(255, 140, 150);  // 珊瑚火把
                case 18: return new Color(170, 120, 255);  // 腐化火把
                case 19: return new Color(255, 90, 90);    // 猩红火把
                case 20: return new Color(200, 220, 255);  // 神圣火把
                case 21: return new Color(140, 255, 100);  // 丛林火把
                case 22: return new Color(120, 200, 255);  // 蘑菇火把: 蓝
                case 23: return new Color(255, 180, 255);  // 微光火把
                default: return new Color(255, 170, 80);
            }
        }

        private static Color GlowstickColor(short type, float phase)
        {
            if (type == ProjectileID.SpelunkerGlowstick) return new Color(255, 215, 90);
            if (type == ProjectileID.BouncyGlowstick) return new Color(120, 255, 120);
            if (type == ProjectileID.FairyGlowstick) return new Color(255, 150, 255);
            if (type == ProjectileID.RainbowGlowstick)
                return HsvToColor((Main.GlobalTimeWrappedHourly * 0.6f + phase) % 1f, 0.7f, 1f);
            return new Color(100, 200, 255); // Glowstick / StickyGlowstick: 蓝
        }

        private static Color ItemGlowstickColor(int itemType)
        {
            if (itemType == ItemID.SpelunkerGlowstick) return new Color(255, 215, 90);
            if (itemType == ItemID.BouncyGlowstick) return new Color(120, 255, 120);
            if (itemType == ItemID.FairyGlowstick) return new Color(255, 150, 255);
            if (itemType == ItemID.RainbowGlowstick)
                return HsvToColor(Main.GlobalTimeWrappedHourly * 0.6f % 1f, 0.7f, 1f);
            return new Color(100, 200, 255);
        }

        private static Color HsvToColor(float h, float s, float v)
        {
            h = h - (float)Math.Floor(h);
            float i = h * 6f;
            int ii = (int)i;
            float f = i - ii;
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);
            float r, g, b;
            switch (ii % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return new Color((byte)(r * 255f), (byte)(g * 255f), (byte)(b * 255f), (byte)255);
        }

        private static bool IsLightTile(ushort type)
        {
            return type == TileID.Torches || type == TileID.Campfire || type == TileID.Candles
                || type == TileID.Chandeliers || type == TileID.HangingLanterns || type == TileID.Lamps
                || type == TileID.Lampposts || type == TileID.ChineseLanterns || type == TileID.SkullLanterns
                || type == TileID.Jackolanterns;
        }

        private static Vector2 WorldToUV(Vector2 worldPos, RenderTarget2D render, bool flip)
        {
            Vector2 p = Vector2.Transform(worldPos - Main.screenPosition, Main.GameViewMatrix.ZoomMatrix);
            if (flip && Main.LocalPlayer != null && Main.LocalPlayer.gravDir == -1f)
                p.Y = render.Height - p.Y;
            return new Vector2(p.X / render.Width, p.Y / render.Height);
        }

        private static void ComputeDayLight(Setting.Data d, out float dayLight, out float moonK)
        {
            Player p = Main.LocalPlayer;
            Color skies = Main.ColorOfTheSkies;
            float num = (1f - 1.2f * skies.R * 0.3f + skies.G * 0.6f + skies.B * 0.1f) / 255f;
            float num2 = d.LightIntensity - 0.1f;
            if (p != null)
            {
                if (p.ZoneCrimson) num += 0.3f;
                if (p.ZoneSnow && !p.ZoneCrimson && !p.ZoneCorrupt) num -= Main.bgAlphaFrontLayer[7] * 0.1f;
            }

            moonK = 1f;
            if (Main.moonPhase == 0) moonK = 1.01f;
            if (Main.moonPhase == 3 || Main.moonPhase == 5) moonK = 0.9f;
            if (Main.moonPhase == 4) moonK = 0.6f;

            dayLight = 0.8f * num2 * (1f + num * 0.4f);
            if (p != null)
            {
                float tiles = p.Center.Y / 16f;
                if (tiles > Main.worldSurface - 150.0)
                {
                    float depth = tiles - ((float)Main.worldSurface - 150f);
                    moonK *= 1f - depth / 200f;
                    dayLight *= 1f - depth / 600f;
                }
            }
            if (dayLight < 0f) dayLight = 0f;
            if (moonK < 0f) moonK = 0f;
        }

        private static void LightPass(RenderTarget2D scene, Setting.Data d, float dayLight, float moonK, bool flip)
        {
            Player p = Main.LocalPlayer;
            float intensity = Main.dayTime ? dayLight : (0.8f * d.MoonlightIntensity * moonK);
            float t = Main.dayTime ? (float)(Main.time / 54000.0) : (float)(Main.time / 32400.0);
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;

            Texture2D ramp = Main.dayTime
                ? (d.DayColor == "Alternate" ? _colorAlt : _colorClassic)
                : _colorNight;

            _lightFx.Parameters["uScreenResolution"].SetValue(new Vector2(scene.Width, scene.Height));
            _lightFx.Parameters["uPos"].SetValue(ToScreenCoords(GetSunPos(scene, flip), scene));
            _lightFx.Parameters["uRadius"].SetValue(3f);
            _lightFx.Parameters["uPower"].SetValue(1f);
            _lightFx.Parameters["uTint"].SetValue(Vector3.One);
            _lightFx.Parameters["uAlphaOut"].SetValue(0f);
            _lightFx.Parameters["uSceneTex"].SetValue(scene);
            _lightFx.Parameters["uSceneMix"].SetValue(0f);
            _lightFx.Parameters["tex0"].SetValue(ramp);
            _lightFx.Parameters["intensity"].SetValue(intensity);
            _lightFx.Parameters["t"].SetValue(t);

            bool drawLight = p == null || p.Center.Y < Main.worldSurface * 16.0 + 800.0;
            if (drawLight)
            {
                DrawSprite(_lightFx.CurrentTechnique.Passes["Light"], _white,
                    new Rectangle(0, 0, scene.Width, scene.Height), Color.White, SpriteEffects.None, BlendState.NonPremultiplied);
            }
        }

        private static void DrawRays(RenderTarget2D scene, Setting.Data d, float dayLight, int W, int H, bool flipY)
        {
            Vector2 center = GetSunPos(scene, flipY) / MaskDiv;
            const int quality = 3;

            bool began = false;
            try
            {
                Main.spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Additive,
                    SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, null, Matrix.Identity);
                began = true;
                if (Main.dayTime)
                {
                    for (int i = 0; i < 30; i++)
                    {
                        float a = 0.55f * (30 - i) / 120f;
                        float alpha = 255f * a * (d.ShadowIntensity + 0.1f) * (dayLight * 0.5f - 0.1f) * 1.4f;
                        Color c = Color.White;
                        c.A = (byte)MathHelper.Clamp(alpha, 0f, 255f);
                        float scale = 1f + i * (1f / quality) * (0.012f + quality * 0.01f);
                        Main.spriteBatch.Draw(_shadowMask, center, null, c, 0f, center, scale, SpriteEffects.None, 0f);
                    }
                }
                else
                {
                    for (int j = 0; j < 20; j++)
                    {
                        float a = (20f - j) / 190f;
                        Main.spriteBatch.Draw(_shadowMask, center, null, Color.White * a, 0f, center, 1f + j * 0.01f, SpriteEffects.None, 0f);
                    }
                }
            }
            finally
            {
                if (began)
                {
                    try { Main.spriteBatch.End(); }
                    catch (Exception ex) { LightsMod.LogRuntimeError("Rays.End: " + ex.Message); }
                }
            }
        }

        private static void BloomPass(GraphicsDevice dev, Setting.Data d, int W, int H, int mipCount, SpriteEffects finalEffects)
        {
            dev.SetRenderTarget(_spare);
            dev.Clear(Color.Black);
            DrawSprite(null, _result, new Rectangle(0, 0, W, H), Color.White, SpriteEffects.None, BlendState.Opaque);

            dev.SetRenderTarget(_bloom);
            dev.Clear(Color.Black);
            _bloomFx.Parameters["m"].SetValue(0.71f - d.BloomIntensity * 0.01f);
            DrawSprite(_bloomFx.CurrentTechnique.Passes["Bloom"], _spare, new Rectangle(0, 0, W, H), Color.White, SpriteEffects.None, BlendState.Opaque);

            RenderTarget2D source = _bloom;
            for (int i = 0; i < mipCount; i++)
            {
                DrawBloomBlur(dev, d, source, BloomMips[i], "Downsample");
                source = BloomMips[i];
            }
            for (int i = mipCount - 1; i >= 0; i--)
            {
                RenderTarget2D dest = (i == 0) ? _bloom : BloomMips[i - 1];
                DrawBloomBlur(dev, d, source, dest, "Upsample");
                source = dest;
            }

            // 光照不穿背景: Bloom 也只留前景/世界层

            dev.SetRenderTarget(_bloomMask);
            dev.Clear(Color.Black);
            _bloomFx.Parameters["uMaskOffset"].SetValue(new Vector2(1f / W, 1f / H));
            DrawSprite(_bloomFx.CurrentTechnique.Passes["KawaseMask"], _spare, new Rectangle(0, 0, W, H), Color.White, SpriteEffects.None, BlendState.Opaque);

            dev.SetRenderTarget(null);
            _bloomFx.Parameters["tex0"].SetValue(_spare);
            _bloomFx.Parameters["maskTexture"].SetValue(_bloomMask);
            _bloomFx.Parameters["p"].SetValue(4f);
            _bloomFx.Parameters["m2"].SetValue(d.BloomIntensity);
            DrawSprite(_bloomFx.CurrentTechnique.Passes["Blend"], _bloom,
                new Rectangle(0, 0, W, H), Color.White, finalEffects, BlendState.Opaque);
        }

        private static void DrawBloomBlur(GraphicsDevice dev, Setting.Data d, RenderTarget2D source, RenderTarget2D dest, string pass)
        {
            if (source == null || dest == null) return;
            float k;
            switch (Math.Max(1, Math.Min(3, d.BloomCounts)))
            {
                case 1: k = 2.5f; break;
                case 2: k = 1.5f; break;
                case 3: k = 1.25f; break;
                default: k = 2f; break;
            }
            dev.SetRenderTarget(dest);
            dev.Clear(Color.Black);
            _dualBlurFx.Parameters["uHalfTexelSize"].SetValue(new Vector2(k / source.Width, k / source.Height));
            DrawSprite(_dualBlurFx.CurrentTechnique.Passes[pass], source, new Rectangle(0, 0, dest.Width, dest.Height), Color.White, SpriteEffects.None, BlendState.Opaque);
        }

        // ======================= 绘制 (先 Begin 再 Apply pass, 避免多 pass 效果重复绘制) =======================

        private static void DrawSprite(EffectPass pass, Texture2D texture, Rectangle dest,
            Color color, SpriteEffects effects, BlendState blend)
        {
            bool began = false;
            try
            {
                Main.spriteBatch.Begin(SpriteSortMode.Immediate, blend, SamplerState.LinearClamp,
                    DepthStencilState.None, RasterizerState.CullNone, null, Matrix.Identity);
                began = true;
                if (pass != null) pass.Apply();
                Main.spriteBatch.Draw(texture, dest, null, color, 0f, Vector2.Zero, effects, 0f);
            }
            finally
            {
                if (began)
                {
                    try { Main.spriteBatch.End(); }
                    catch (Exception ex) { LightsMod.LogRuntimeError("SpriteBatch.End: " + ex.Message); }
                }
            }
        }

        // ======================= 太阳位置 =======================

        internal static Vector2 GetSunPos(RenderTarget2D render, bool flipY)
        {
            float half = Main.dayTime ? 27000f : 16200f;
            float skyShift = 0f;
            float den = (float)(Main.worldSurface * 16.0 - 600.0);
            if (Math.Abs(den) > 1f) skyShift = (-Main.screenPosition.Y) / den * 200f;
            float k = 1f - (float)Main.time / half;
            float px = (1f - k) * render.Width / 2f - 100f * k;
            float py = skyShift + k * k * 250f + 180f;
            if (flipY && Main.LocalPlayer != null && Main.LocalPlayer.gravDir == -1f)
                py = render.Height - py;
            return new Vector2(px, py);
        }

        internal static Vector2 ToScreenCoords(Vector2 vec, RenderTarget2D render)
        {
            return vec / new Vector2(render.Width, render.Height);
        }

        // ======================= 资源 =======================

        private static bool EnsureResources()
        {
            if (_lightFx != null && !_lightFx.IsDisposed) return true;
            if (_resFailed) return false;
            try
            {
                GraphicsDevice dev = Main.graphics.GraphicsDevice;
                _lightFx = LoadEffect(dev, "LightsAndShadows.Light.fxo", 1);
                _shadowFx = LoadEffect(dev, "LightsAndShadows.Shadow.fxo", 2);
                _bloomFx = LoadEffect(dev, "LightsAndShadows.Bloom1.fxo", 3);
                _dualBlurFx = LoadEffect(dev, "LightsAndShadows.DualBlur.fxo", 2);
                _darkenFx = LoadEffect(dev, "LightsAndShadows.Darken.fxo", 1);
                _maskFx = LoadEffect(dev, "LightsAndShadows.Mask.fxo", 1);
                _colorClassic = LoadRawImage(dev, "LightsAndShadows.ColorTex.rawimg");
                _colorAlt = LoadRawImage(dev, "LightsAndShadows.ColorTex_Alt.rawimg");
                _colorNight = LoadRawImage(dev, "LightsAndShadows.ColorTex2.rawimg");
                _torchColor = new Texture2D(dev, 1, 1, false, SurfaceFormat.Color);
                _torchColor.SetData(new[] { new Color(255, 180, 100, 255) });
                _white = new Texture2D(dev, 1, 1, false, SurfaceFormat.Color);
                _white.SetData(new[] { Color.White });
                LightsMod.LogInfo("原版着色器/贴图已加载");
                return true;
            }
            catch (Exception ex)
            {
                _resFailed = true;
                LightsMod.LogRuntimeError("资源加载失败: " + ex);
                return false;
            }
        }

        private static byte[] ReadResource(string name)
        {
            using (Stream s = typeof(LightingFX).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("缺少内嵌资源: " + name);
                byte[] data = new byte[s.Length];
                int off = 0;
                while (off < data.Length)
                {
                    int n = s.Read(data, off, data.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
                if (off != data.Length) throw new InvalidOperationException("资源读取不完整: " + name);
                return data;
            }
        }

        private static Effect LoadEffect(GraphicsDevice dev, string resource, int passCount)
        {
            byte[] raw = ReadResource(resource);
            int header = 8 + passCount * 8;
            byte[] wrapped = new byte[header + raw.Length];
            wrapped[0] = 0xCF; wrapped[1] = 0x0B; wrapped[2] = 0xF0; wrapped[3] = 0xBC;
            wrapped[4] = (byte)(header & 0xFF);
            wrapped[5] = (byte)((header >> 8) & 0xFF);
            wrapped[6] = (byte)((header >> 16) & 0xFF);
            wrapped[7] = (byte)((header >> 24) & 0xFF);
            Buffer.BlockCopy(raw, 0, wrapped, header, raw.Length);
            return new Effect(dev, wrapped);
        }

        private static Texture2D LoadRawImage(GraphicsDevice dev, string resource)
        {
            byte[] d = ReadResource(resource);
            if (d.Length < 12) throw new InvalidOperationException("rawimg 太小: " + resource);
            int w = BitConverter.ToInt32(d, 4);
            int h = BitConverter.ToInt32(d, 8);
            int need = 12 + w * h * 4;
            if (w <= 0 || h <= 0 || d.Length < need) throw new InvalidOperationException("rawimg 尺寸异常: " + resource);
            Color[] px = new Color[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                int o = 12 + i * 4;
                px[i] = new Color(d[o], d[o + 1], d[o + 2], d[o + 3]);
            }
            Texture2D tex = new Texture2D(dev, w, h, false, SurfaceFormat.Color);
            tex.SetData(px);
            return tex;
        }

        // ======================= 目标 =======================

        private static bool EnsureTargets(GraphicsDevice dev, int W, int H)
        {
            int sw = Math.Max(1, W / MaskDiv), sh = Math.Max(1, H / MaskDiv);
            bool ok = true;
            ok &= EnsureTarget(ref _light, dev, W, H);
            ok &= EnsureTarget(ref _result, dev, W, H);
            ok &= EnsureTarget(ref _spare, dev, W, H);
            ok &= EnsureTarget(ref _bloom, dev, W, H);
            ok &= EnsureTarget(ref _bloomMask, dev, W, H);
            ok &= EnsureTarget(ref _shadowMask, dev, sw, sh);
            ok &= EnsureTarget(ref _rays, dev, sw, sh);
            return ok;
        }

        private static bool EnsureBloomMips(GraphicsDevice dev, int W, int H, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int w = Math.Max(1, W >> (i + 1));
                int h = Math.Max(1, H >> (i + 1));
                if (!EnsureTarget(ref BloomMips[i], dev, w, h)) return false;
            }
            return true;
        }

        private static bool EnsureTarget(ref RenderTarget2D target, GraphicsDevice dev, int w, int h)
        {
            if (target != null && !target.IsDisposed && target.Width == w && target.Height == h) return true;
            try
            {
                if (target != null) target.Dispose();
                target = new RenderTarget2D(dev, w, h, false, SurfaceFormat.Color, DepthFormat.None);
                return true;
            }
            catch (Exception ex)
            {
                target = null;
                LightsMod.LogRuntimeError("渲染目标创建失败: " + ex);
                return false;
            }
        }

        // ======================= 日志 =======================

        private static void LogFrame(RenderTarget2D scene)
        {
            _frames++;
            bool first = !_loggedFirst;
            if (!first && (DateTime.Now - _lastLog).TotalSeconds < 10.0) return;
            _loggedFirst = true;
            _lastLog = DateTime.Now;

            Setting.Data d = Setting.data;
            Vector2 sun = GetSunPos(scene, false);
            LightsMod.LogInfo(string.Format(
                "原版光影: 光照={0} 强度={1:F2} 月光={2:F2} 阴影={3:F2} 日光色={4} Bloom={5} Bloom强度={6:F2} 等级={7} 太阳=({8:F0},{9:F0}) 屏幕={10}x{11} {12} 帧={13}",
                d.UseLight, d.LightIntensity, d.MoonlightIntensity, d.ShadowIntensity, d.DayColor,
                d.UseBloom, d.BloomIntensity, d.BloomCounts,
                sun.X, sun.Y, scene.Width, scene.Height,
                Main.dayTime ? "白天" : "夜晚", _frames));
        }
    }
}
