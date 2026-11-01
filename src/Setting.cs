using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using tContentPatch;
using tContentPatch.Content.UI;
using tContentPatch.Content.UI.ModSet;
using Terraria;
using Terraria.UI;

namespace LightsAndShadows
{
    /// <summary>
    /// 原版 Lights And Shadows (yiyang233) 的客户端配置:
    /// 光照强度 / 月光强度 / 阴影强度 / 日光色 / Bloom。
    /// </summary>
    public class Setting : ModSetting
    {
        public class Data
        {
            [JsonProperty("UseLight")] public bool UseLight = true;
            [JsonProperty("LightIntensity")] public float LightIntensity = 1.2f;       // 0.4..1.2
            [JsonProperty("MoonlightIntensity")] public float MoonlightIntensity = 1.1f; // 0.2..1.1
            [JsonProperty("ShadowIntensity")] public float ShadowIntensity = 1.5f;   // 0.6..1.5
            [JsonProperty("LightThroughBackground")] public bool LightThroughBackground = true;
            [JsonProperty("ForegroundShadow")] public float ForegroundShadow = 0f; // 玩家/前景层阴影 0..2
            [JsonProperty("BackgroundShadow")] public float BackgroundShadow = 0f; // 背景层阴影 0..2
            [JsonProperty("DayColor")] public string DayColor = "Alternate";           // Classic / Alternate
            [JsonProperty("UseBloom")] public bool UseBloom = true;
            [JsonProperty("BloomIntensity")] public float BloomIntensity = 1.25f;       // 0.2..1.25
            [JsonProperty("BloomCounts")] public int BloomCounts = 3;                // 1..3

            // 照明物品: 不受地上/地下影响; 白天/夜晚各自的亮度系数
            [JsonProperty("TorchIgnoreDepth")] public bool TorchIgnoreDepth = true;
            [JsonProperty("TorchDayIntensity")] public float TorchDayIntensity = 0.5f;
            [JsonProperty("TorchNightIntensity")] public float TorchNightIntensity = 1.0f;

            // 发光道具自定义光照: 手持/掉落的物品发出指定颜色、强度和半径的光
            [JsonProperty("CustomLights")] public List<CustomLight> CustomLights = new List<CustomLight>
            {
                new CustomLight { Item = "GlowingMushroom", Color = "120,200,255", Intensity = 0.9f, Radius = 0.45f },
                new CustomLight { Item = "CrystalShard", Color = "210,130,255", Intensity = 0.9f, Radius = 0.45f },
                new CustomLight { Item = "StrangeGlowingMushroom", Color = "160,120,255", Intensity = 0.9f, Radius = 0.45f },
                new CustomLight { Item = "FairyBell", Color = "255,160,255", Intensity = 0.9f, Radius = 0.45f },
                new CustomLight { Item = "MagicLantern", Color = "150,255,130", Intensity = 0.9f, Radius = 0.45f },
                new CustomLight { Item = "WispinaBottle", Color = "140,255,190", Intensity = 0.9f, Radius = 0.45f },
                new CustomLight { Item = "SuspiciousLookingTentacle", Color = "150,200,255", Intensity = 0.9f, Radius = 0.45f },
            };
        }

        public class CustomLight
        {
            [JsonProperty("Item")] public string Item = "";              // 物品英文名或 ID
            [JsonProperty("Color")] public string Color = "255,200,120"; // "R,G,B" 或 "#RRGGBB"
            [JsonProperty("Intensity")] public float Intensity = 1.0f;
            [JsonProperty("Radius")] public float Radius = 0.45f;
        }

        internal class SwitchDef
        {
            public string Group;
            public string Text;
            public Func<Data, bool> Get;
            public Action<Data, bool> Set;
            public string ParamText;
            public float ParamMin;
            public float ParamMax;
            public bool IsInt;
            public Func<Data, float> GetP;
            public Action<Data, float> SetP;
            public Func<float, string> Format;
        }

        internal static readonly SwitchDef[] Switches = new SwitchDef[]
        {
            new SwitchDef { Group = "光照", Text = "启用光照 (日月径向光 + 光柱)",
                Get = d => d.UseLight, Set = (d, v) => d.UseLight = v },
            new SwitchDef { Group = "光照", Text = null,
                ParamText = "光照强度", ParamMin = 0.4f, ParamMax = 1.2f,
                GetP = d => d.LightIntensity, SetP = (d, v) => d.LightIntensity = v,
                Format = v => v.ToString("0.00") },
            new SwitchDef { Group = "光照", Text = "照明物品不受地上/地下影响 (火把/荧光棒/自定义光源)",
                Get = d => d.TorchIgnoreDepth, Set = (d, v) => d.TorchIgnoreDepth = v },
            new SwitchDef { Group = "光照", Text = null,
                ParamText = "火把白天亮度", ParamMin = 0f, ParamMax = 1f,
                GetP = d => d.TorchDayIntensity, SetP = (d, v) => d.TorchDayIntensity = v,
                Format = v => v.ToString("0.00") },
            new SwitchDef { Group = "光照", Text = null,
                ParamText = "火把夜晚亮度", ParamMin = 0.2f, ParamMax = 1f,
                GetP = d => d.TorchNightIntensity, SetP = (d, v) => d.TorchNightIntensity = v,
                Format = v => v.ToString("0.00") },
            new SwitchDef { Group = "光照", Text = null,
                ParamText = "月光强度", ParamMin = 0.2f, ParamMax = 1.1f,
                GetP = d => d.MoonlightIntensity, SetP = (d, v) => d.MoonlightIntensity = v,
                Format = v => v.ToString("0.00") },
            new SwitchDef { Group = "光照", Text = null,
                ParamText = "阴影强度", ParamMin = 0.6f, ParamMax = 1.5f,
                GetP = d => d.ShadowIntensity, SetP = (d, v) => d.ShadowIntensity = v,
                Format = v => v.ToString("0.00") },
            new SwitchDef { Group = "阴影分层", Text = null,
                ParamText = "前景/玩家层阴影", ParamMin = 0f, ParamMax = 2f,
                GetP = d => d.ForegroundShadow, SetP = (d, v) => d.ForegroundShadow = v,
                Format = v => v.ToString("0.00") },
            new SwitchDef { Group = "阴影分层", Text = null,
                ParamText = "背景阴影", ParamMin = 0f, ParamMax = 2f,
                GetP = d => d.BackgroundShadow, SetP = (d, v) => d.BackgroundShadow = v,
                Format = v => v.ToString("0.00") },
            new SwitchDef { Group = "光照", Text = "光照穿过背景 (关闭 = 只照亮前景/世界层)",
                Get = d => d.LightThroughBackground, Set = (d, v) => d.LightThroughBackground = v },
            new SwitchDef { Group = "光照", Text = "日光色: 白色 (Alternate) / 关闭 = Classic 黄色",
                Get = d => d.DayColor == "Alternate",
                Set = (d, v) => d.DayColor = v ? "Alternate" : "Classic" },
            new SwitchDef { Group = "Bloom", Text = "开启外发光 (Bloom)",
                Get = d => d.UseBloom, Set = (d, v) => d.UseBloom = v },
            new SwitchDef { Group = "Bloom", Text = null,
                ParamText = "外发光强度", ParamMin = 0.2f, ParamMax = 1.25f,
                GetP = d => d.BloomIntensity, SetP = (d, v) => d.BloomIntensity = v,
                Format = v => v.ToString("0.00") },
            new SwitchDef { Group = "Bloom", Text = null,
                ParamText = "Bloom 等级", ParamMin = 1, ParamMax = 3, IsInt = true,
                GetP = d => d.BloomCounts, SetP = (d, v) => d.BloomCounts = (int)Math.Round(v),
                Format = v => ((int)Math.Round(v)).ToString() },
        };

        public static Data data = new Data();

        private static Setting instance;

        public override string Name => "光影设置";
        public override string Title => "光影: 光影设置";
        public override string FilePath => "setting.json";
        public override Type DataType => typeof(Data);

        public override void Load(object v)
        {
            instance = this;
            if (v is Data d) data = d;
            else
            {
                SetDefault();
                Save();
            }
        }

        public override void SetDefault() => data = new Data();

        public override object GetSaveData() => data;

        public override UIElement GetUI()
        {
            instance = this;

            var sv = new UIScrollViewer2();
            sv.Width.Set(0, 1f);
            sv.Height.Set(0, 1f);

            foreach (SwitchDef def in Switches)
                foreach (UIElement ui in CreateItemUIs(def))
                    sv.AddChild(ui);

            return sv;
        }

        internal static List<UIElement> CreateItemUIs(SwitchDef def)
        {
            var list = new List<UIElement>();
            if (def.Get != null)
                list.Add(CreateSwitch(def.Text, def.Get, def.Set));
            if (def.ParamText != null)
                list.Add(CreateSlider(def));
            return list;
        }

        private static UIElement CreateSwitch(string text, Func<Data, bool> get, Action<Data, bool> set)
        {
            var s = new UIItemSwitch(null, text);
            s.OnUpdate += _ =>
            {
                if (data == null) return;
                s.SetVal(get(data));
                if (s.IsMouseHovering) Main.instance.MouseText(text);
            };
            s.OnValUpdate += v =>
            {
                if (data == null || get(data) == v) return;
                set(data, v);
                if (instance != null) instance.NeedSave = true;
            };
            return s;
        }

        private static UIElement CreateSlider(SwitchDef def)
        {
            UIItemValueSlider s = def.IsInt
                ? new UIItemValueSlider((int)def.ParamMin, (int)def.ParamMax, null, def.ParamText)
                : new UIItemValueSlider(def.ParamMin, def.ParamMax, null, def.ParamText);
            if (def.Format != null) s.FloatToString = def.Format;
            s.SetVal(def.GetP(data));

            float shown = def.GetP(data);
            s.OnValUpdate += v =>
            {
                shown = v;
                if (data == null) return;
                if (Math.Abs(def.GetP(data) - v) < 0.0001f) return;
                def.SetP(data, v);
                if (instance != null) instance.NeedSave = true;
            };
            s.OnUpdate += _ =>
            {
                if (data == null) return;
                float cur = def.GetP(data);
                if (Math.Abs(cur - shown) > 0.0001f) s.SetVal(cur);
                if (s.IsMouseHovering) Main.instance.MouseText(def.ParamText);
            };
            return s;
        }
    }
}
