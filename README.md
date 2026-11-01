# 流光叠影 · LightsAndShadows for tPlainModLoader

作者：丷米苏丷

参考 Workshop「Lights And Shadows」（作者 yiyang233，Workshop id `2670628346`）的渲染思路，在 **Terraria 1.4.5.8 + tPlainModLoader** 上实现的光影增强模组。

## 功能

- 日月径向光、蓝通道阴影、多层光柱、Bloom mip 链
- 前景/玩家层与背景层阴影分别调节，互不影响
- 可开关「光照是否穿过背景」
- 火把 / 荧光棒按实际样式发光：冰蓝、诅咒绿渐变、灵液黄、恶魔深紫、彩虹循环等
- 篝火自动使用对应火把的颜色
- 发光道具自定义光照（`setting.json` → `CustomLights`），手持 / 掉落均可
- 照明物品不受地上/地下影响；白天 / 夜晚亮度独立可调
- 火把光按场景亮度遮挡，不穿墙；多个光源使用 Max 混合，不叠加爆白
- 洞穴 / 地下保留火把光晕，不会整体发紫或死黑

## 构建

需要：

- .NET SDK（项目目标 `net472`，会通过 NuGet 拉取 reference assemblies）
- XNA Framework 4.0（Terraria 运行环境自带）
- Terraria 1.4.5.8 + tPlainModLoader（游戏根目录需有 `Terraria.exe`、`tContentPatch.dll`、`0Harmony.dll`、`Newtonsoft.Json.dll`、`ReLogic.dll`）

```powershell
dotnet build -c Release -p:TerrariaDir="D:\steam\steamapps\common\Terraria"
```

默认输出到 `<TerrariaDir>\Mods\LightsAndShadows\`。也可以用 `-p:ModsDir="..."` 自定义输出目录。

### 修改着色器

`shaders/*.fx` 修改后需要重新编译成 `*.fxo`：

```powershell
fxc /T fx_2_0 /Fo shaders\Light.fxo shaders\Light.fx
```

## 配置

模组目录下的 `setting.json`，默认值即作者当前配置：

```json
{
  "UseLight": true,
  "LightIntensity": 1.2,
  "MoonlightIntensity": 1.1,
  "ShadowIntensity": 1.5,
  "LightThroughBackground": true,
  "ForegroundShadow": 0.0,
  "BackgroundShadow": 0.0,
  "DayColor": "Alternate",
  "UseBloom": true,
  "BloomIntensity": 1.25,
  "BloomCounts": 3,
  "TorchIgnoreDepth": true,
  "TorchDayIntensity": 0.5,
  "TorchNightIntensity": 1.0,
  "CustomLights": [
    { "Item": "CrystalShard", "Color": "210,130,255", "Intensity": 0.9, "Radius": 0.45 }
  ]
}
```

`CustomLights` 的 `Item` 支持英文物品名或数字 ID，`Color` 支持 `"R,G,B"` 或 `"#RRGGBB"`。

## 致谢 / 版权

- 原始模组：**Lights And Shadows** by **yiyang233**（Steam Workshop `2670628346`）
- 本项目的着色器与颜色渐变贴图参考 / 派生自原作者作品，相关权利归原作者所有
- 本项目代码仅供学习与个人使用，请勿用于商业用途
