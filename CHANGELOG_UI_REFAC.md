# RenPy Localization Studio - UI 重构与交互动画改动日志 (Handover Changelog)

> **文档用途**：供后续模型或开发人员快速掌握本次 UI/UX 重构、动效升级与功能拓展的完整技术细节。

---

## 目录
1. [项目概况与发布路径](#1-项目概况与发布路径)
2. [改动汇总与核心特性](#2-改动汇总与核心特性)
3. [详细改动文件清单](#3-详细改动文件清单)
4. [核心架构与动效实现细节](#4-核心架构与动效实现细节)
5. [编译与发布流程](#5-编译与发布流程)

---

## 1. 项目概况与发布路径

- **技术栈**：.NET 10.0, WPF (Windows Presentation Foundation), MVVM (CommunityToolkit.Mvvm), AvalonEdit
- **解决方案文件**：`E:\RenPyFlowTranslator\RenPyLocalizationStudio.sln`
- **正式成品输出路径**：`E:\RenPyFlowTranslator\artifacts\win-x64\RenPyLocalizationStudio.exe`
- **发布脚本**：`E:\RenPyFlowTranslator\scripts\publish.ps1`

---

## 2. 改动汇总与核心特性

### 🎨 1. 自定义与 8 款预设强调色（实时 60fps 平滑渐变）
- 在顶部设置弹窗（`...`）新增 8 种高饱和流行预设色块（粉红、紫色、经典蓝、青绿、翠绿、活力橙、珊瑚红、金黄）。
- 重构 `ThemeService`，采用 `DispatcherTimer` 帧插值算法，在 **220ms 内以 CubicEase 曲线实时平滑渐变**全局 `BrandAccentBrush` 及其派生画刷。

### 🚀 2. 交互动效体系（零性能负担）
- **左侧导航栏待选框滑动（Floating Selection Pill）**：在 Activity Bar 顶层实现独立动画浮层，点击切换功能区时，高亮胶囊与左侧强调色立柱在 **200ms 内流畅沿 Y 轴平移**对准新选项。
- **工作区切换动效（Slide & Fade）**：切换主工作区时，侧边栏自左向右平移（-16px → 0px），主屏自下而上微上浮（10px → 0px），伴随透明度渐变（180ms）。
- **微交互反馈（Micro-interactions）**：所有按钮在 Hover 时平滑高亮，Click/MouseDown 时产生 `Scale(0.96)` 微按压回弹；预设色块 Hover 时放大至 `1.18x` 并显示外环。
- **抽屉响应式折叠**：窗口宽度变化触发折叠/展开时，侧边栏与检查器宽度采用 180ms 动画平滑过渡。

### 🖼 3. 全局暗黑模态弹窗（替换原生 Win32 MessageBox）
- 彻底废除白底 Win32 `MessageBox.Show`。
- 实现 `ModernDialog` 原生暗黑风格模态窗（圆角 8px、阴影、Fluent 彩色状态图标、Primary/Ghost 风格按钮、支持 `Enter`/`Esc` 快捷键与窗口拖拽）。

### 🎮 4. 剧情流与游戏画面实时预览垂直拆分（上下可拖拽 GridSplitter）
- 中间区域重构为上下垂直两层：
  - **上半部分**：保留完整的剧情流 / 节点对白列表（`ListView FlowList`）。
  - **中间分割条**：可自由上下拖拽的 `GridSplitter`（带有柔和把手与 `SizeNS` 鼠标光标）。
  - **下半部分**：全新的**游戏画面与素材实时预览面板**。
- **Ren'Py 脚本上下文与素材匹配引擎（`RenPyImagePreviewService` 算法深度优化）**：
  - 自动剥离 Ren'Py 转场与位置子句（如 `with dissolve`、`at center`、`as ...` 等），精准捕获真正图像 Tag（如 `ch1_intro2_2`）。
  - 实现 Ren'Py 原生特性的**路径无关（Path-Agnostic）递归匹配**：扫描 `game/images/` 及其任意子文件夹，下划线/空格智能互通，无论在子目录还是根目录均能毫秒级定位。
  - 支持多图（背景/立绘）切换、分辨率显示与“打开所在目录”功能。
- **三列视觉分界增强**：
  - 为左侧导航栏、中央区域、右侧检查器加入清晰可见的 `1px BackgroundLevel2` 竖向分割线与头部边框，层次感显著提升。

### 🛠 5. 额外文本工作区分类筛选与搜索联动修复
- **侧边栏分类响应**：全面接入 `Sidebar.SelectedItem` 与 `SearchText` 监听，点击 `Character`、`Input`、`Define`、`ScreenText` 等类别时，中央列表实时动态过滤（`VisibleCandidates`）。
- **新增“全部候选”与批量勾选操作**：侧边栏首项增加“全部候选”，工具栏增加“全选当前分类”与“取消全选”便捷按钮，并实时显示分类过滤统计（如 `35 / 196 项`）。

### 🛠 6. TL 生成模块增加“自定义 SDK 路径”
- 界面增加“SDK 路径”显示行及 `选择...` / `清除` 按钮，支持手动浏览指定 `renpy.exe`。
- 新增 `SdkPath` 字段并持久化到本地 `settings.json`，下次启动自动恢复并优先使用。

### 👁 7. 全面排版与样式规范化
- **消除所有过小字体**：全面消灭散落在各处的 `8px/9px/10px` 文字，将正文与辅助说明统一提高到 `11px ~ 13px`。
- **提升对比度**：调优 `TextSubtleBrush`、`PathTextBrush` 与 `SpeakerTextBrush`（说话人名字不透明度由 0.62 提至 0.78）。
- **加宽滚动条**：滚动条宽度由 10px 提升至 12px，滑块可抓取宽度扩大至 10px。
- **清除多余横向滚动条**：全局禁用 `ListBox`/`ListView` 的横向滚动条，AvalonEdit 开启 `WordWrap="True"`。
- **清晰分界**：翻译检查器重新梳理布局，增加明确的“原文”与“译文”模块标题及独立背景边框。

---

## 3. 详细改动文件清单

### A. 全局主题与服务层
| 文件路径 | 变更要点 |
|---|---|
| `src/RenPyLocalizationStudio.App/Themes/StudioTheme.xaml` | 调亮对比度画刷；定义 8 款 AccentColor 资源；实现 `PresetColorButtonStyle`、带有按压变换的 `GhostButtonStyle`/`PrimaryButtonStyle`；更新 `ActivityItemStyle` 配合滑动浮层；全局禁用 `ListBox`/`ListView` 横向滚动条；修复 ComboBox 下拉模板 |
| `src/RenPyLocalizationStudio.App/Services/UiServices.cs` | 增加 `ModernDialog`（暗黑模态弹窗类）；更新 `ConfirmationService` 全部使用 `ModernDialog`；重构 `ThemeService` 支持实时帧插值动画；`IFileDialogService` 新增 `SelectSdkExecutable`；`AppSettings` 新增 `SdkPath` 字段 |

### B. 主窗口与应用主流程
| 文件路径 | 变更要点 |
|---|---|
| `src/RenPyLocalizationStudio.App/MainWindow.xaml` | 修复语言 ComboBox 挤压与文本输入问题；设置面板加入 8 色圆点色板；Activity Bar 加入 `Canvas` + `ActivitySelectionIndicator` 浮层；主工作区标记 `MainHost` |
| `src/RenPyLocalizationStudio.App/MainWindow.xaml.cs` | 实现 `UpdateActivityIndicator`（滑块 Y 轴动画）；监听 `CurrentWorkspace` 触发 `OnWorkspaceChanged`（主屏滑动动效）；实现 `AnimateWidth`（抽屉响应式滑动）；接入色板点击事件 `PresetColor_Click` |
| `src/RenPyLocalizationStudio.App/App.xaml.cs` | 注入 `dialogs` 到 `TlWorkspaceViewModel` 构造函数 |

### C. ViewModel 模型层
| 文件路径 | 变更要点 |
|---|---|
| `src/RenPyLocalizationStudio.App/ViewModels/ProjectSessionViewModel.cs` | 增加 `SdkPath` 属性（Observable），并在 `InitializeAsync` 和 `PersistSettingsAsync` 中读写配置 |
| `src/RenPyLocalizationStudio.App/ViewModels/ToolWorkspaces.cs` | `TlWorkspaceViewModel` 接入 `ChooseSdkCommand`、`ClearSdkCommand`、`SdkPathDisplay`，执行时传入用户指定 SDK 路径 |

### D. 各功能视图
| 文件路径 | 变更要点 |
|---|---|
| `Views/TlMainView.xaml` | 增加 SDK 路径配置与操作按钮；全量提升 12+ 处 `FontSize="10"` 为 `FontSize="11"`；加宽按钮 Padding |
| `Views/TranslationInspectorView.xaml` | 重构翻译检查器，明确分离“原文”与“译文”区块，添加圆角卡片背景 |
| `Views/TranslationSidebarView.xaml` | 修正 9px 极小标签路径为 11px；调整导航与分组间距 |
| `Views/TranslationMainView.xaml` | 优化工具栏按钮内边距（`12,6`）与状态徽章间距；移除 ProgressBar 上的无效属性 |
| `Views/DiffDocumentView.xaml` | 开启 `WordWrap="True"`，防止底部白块横向滚动条残留；根边距统一 |
| `Views/DiagnosticsMainView.xaml` / `ExtraTextMainView.xaml` | 统一头部边距（`16,8`）；按钮补充 `8px` 水平间距；字体提升至 11px |
| `Views/PatchHostView.xaml` / `ZzzDocumentView.xaml` | 标签页关闭按钮增大至 `22px`；补全按钮与字体规范化 |

### E. 构建与发布脚本
| 文件路径 | 变更要点 |
|---|---|
| `scripts/prepare-tool-runtime.ps1` | 修复 Windows PowerShell 5.1 兼容性问题（移除 .NET Core 专属 API，改用原生文件写入） |
| `scripts/publish.ps1` | 修正发布路径与输出结构 |

---

## 4. 核心架构与动效实现细节

### 1. 浮动滑块动画原理 (`MainWindow.xaml.cs` & `MainWindow.xaml`)
```csharp
private void UpdateActivityIndicator(bool animate = true)
{
    if (ActivityBarList == null || ActivitySelectionIndicator == null) return;
    int index = ActivityBarList.SelectedIndex;
    if (index < 0) { ActivitySelectionIndicator.Opacity = 0; return; }

    // 每个图标高度 46px + 上下边距各 2px = 50px 步进
    double targetY = index * 50 + 2;

    if (animate && IsLoaded)
    {
        var anim = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        IndicatorTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }
    else
    {
        IndicatorTransform.BeginAnimation(TranslateTransform.YProperty, null);
        IndicatorTransform.Y = targetY;
    }
}
```

### 2. 60fps 主题色插值算法 (`UiServices.cs`)
```csharp
_colorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
_colorTimer.Tick += (_, _) =>
{
    var elapsed = (DateTime.UtcNow - _animStartTime).TotalMilliseconds;
    var progress = Math.Clamp(elapsed / DurationMs, 0.0, 1.0);
    double ease = 1.0 - Math.Pow(1.0 - progress, 3); // Cubic Ease Out

    byte r = (byte)(_startColor.R + (_targetColor.R - _startColor.R) * ease);
    byte g = (byte)(_startColor.G + (_targetColor.G - _startColor.G) * ease);
    byte b = (byte)(_startColor.B + (_targetColor.B - _startColor.B) * ease);

    ApplyInstantColor(Color.FromRgb(r, g, b));
    if (progress >= 1.0) { _colorTimer?.Stop(); _colorTimer = null; }
};
_colorTimer.Start();
```

---

## 5. 编译与发布流程

如需再次编译并输出到成品目录，请在 PowerShell 中执行：

```powershell
# 1. 关闭正在运行的程序以防文件锁死
Stop-Process -Name "RenPyLocalizationStudio" -Force -ErrorAction SilentlyContinue

# 2. 校验编译
dotnet build E:\RenPyFlowTranslator\RenPyLocalizationStudio.sln

# 3. 发布到成品目录 (artifacts\win-x64)
cd E:\RenPyFlowTranslator
.\scripts\publish.ps1
```

发布完成后即可在 `E:\RenPyFlowTranslator\artifacts\win-x64\RenPyLocalizationStudio.exe` 验证最新效果。
