# RenPy Localization Studio - 开发交接记录 (Master Handover Document)

> **v0.3.0 架构更新（2026-08-26）**：项目已建立 Git 基线；分析、语言发现和保存统一通过 `IFileSystemService`，素材预览使用可失效的 `Project Asset Index`，后台工作由 `WorkspaceTaskCoordinator` 管理。测试已扩展到 54 项。发布脚本使用 `tools/tool-runtime.lock.json` 校验 Python 3.13.7、unrpyc 2.0.4 和固定提交的 rpatool，并生成不含 PDB 的框架依赖 ZIP 与 SHA-256。以下旧章节中的测试数量、缓存实现和版本号仅作为历史记录，以本段、`CONTEXT.md`、`README.md` 与代码为准。

> **文档创建时间**：2026-08-26  
> **文档版本**：v2.0 (全功能与动效重构完整版)  
> **适用对象**：接手本项目的 AI 模型（如 Gemini / Claude / GPT）或全栈/WPF 开发人员。

---

## 目录
1. [项目概览与关键路径](#1-项目概览与关键路径)
2. [本次迭代完成的核心特性与架构详解](#2-本次迭代完成的核心特性与架构详解)
3. [完整代码改动清单（文件级别索引）](#3-完整代码改动清单文件级别索引)
4. [核心算法与设计模式](#4-核心算法与设计模式)
5. [常见问题排查与注意事项](#5-常见问题排查与注意事项)
6. [编译、测试与发布标准流程](#6-编译测试与发布标准流程)

---

## 1. 项目概览与关键路径

- **项目名称**：RenPy Localization Studio (Ren'Py 流程翻译工坊)
- **技术栈**：.NET 10.0, WPF (Windows Presentation Foundation), MVVM (CommunityToolkit.Mvvm), AvalonEdit
- **解决方案文件**：`E:\RenPyFlowTranslator\RenPyLocalizationStudio.sln`
- **项目结构**：
  - `src/RenPyLocalizationStudio.Core`：Ren'Py 脚本 AST 解析器、TL 解析生成、补丁生成、诊断、SDK 交互。
  - `src/RenPyLocalizationStudio.App`：WPF 桌面应用（主窗口、6 大工作区 ViewModel、XAML 视图、动效与 UI 服务）。
  - `tests/RenPyLocalizationStudio.Tests`：54 项 xUnit 自动化测试套件。
- **正式成品输出路径**：`E:\RenPyFlowTranslator\artifacts\win-x64\RenPyLocalizationStudio.exe`
- **发布脚本**：`E:\RenPyFlowTranslator\scripts\publish.ps1`

---

## 2. 本次迭代完成的核心特性与架构详解

### 🎨 1. 主题色实时渐变插值引擎（60fps Color Morphing）
- **功能表现**：在右上角设置面板中提供 8 款高饱和流行预设强调色（粉红、紫、蓝、青绿、绿、橙、红、金黄）及十六进制自定义输入。
- **底层实现**：在 `ThemeService` 中实现基于 `DispatcherTimer`（15ms/帧）的帧插值算法。点击任意色块时，全局 `BrandAccentBrush`、`BrandAccentHoverBrush` 和 `BrandAccentSoftBrush` 在 **220ms 内以 CubicEase 曲线实时平滑渐变**，界面所有 DynamicResource 控件实时呈现色彩流动质感。

### 🚀 2. 连续物理动效体系
- **左侧导航栏待选框平滑滑动（Floating Selection Pill）**：在 Activity Bar 顶层实现独立的动画 Canvas 浮层。切换功能区时，半透明胶囊背景与左侧强调色小立柱在 **200ms 内沿 Y 轴平滑滑动**对准目标图标。
- **主工作区切换滑动浮入（Slide & Fade）**：切换工作区时，侧边栏自左向右平移（-16px → 0px），主屏自下而上微上浮（10px → 0px），伴随透明度渐变（180ms）。
- **按钮微按压（Micro-interactions）**：全局按钮在鼠标按住（MouseDown/Click）时产生 `Scale(0.96)` 微按压回弹；预设色块 Hover 时放大至 `1.18x` 并显示高亮外环。
- **响应式抽屉滑动**：窗口缩放导致侧边栏/检查器折叠展开时，宽度采用 180ms 动画平滑过渡。

### 🎮 3. 剧情流与游戏画面实时预览（垂直拆分 + 上下可拖拽 GridSplitter）
- **三层垂直拆分结构**：
  - **上半部分**：保留完整的剧情流/对白列表（`ListView FlowList`）。
  - **中间分割条**：配有专属 `RowGridSplitterStyle`（5px 高、36x2px 抓手、悬浮变色）的 `GridSplitter`，支持鼠标自由上下拉拽调整窗口比例。
  - **下半部分**：全新的**游戏画面实时预览面板**。
- **Ren'Py 脚本上下文与素材匹配引擎（`RenPyImagePreviewService`）**：
  - **语法智能剥离**：自动过滤 `with dissolve`、`at center`、`as ...`、`behind ...`、`onlayer ...` 等 Ren'Py 转场与位置子句，精准提取纯净图片 Tag（如 `scene ch1_intro2_2 with dissolve` 提取为 `ch1_intro2_2`）。
  - **全项目 Path-Agnostic 扁平递归检索**：完美契合 Ren'Py 原生机制，无论图片在 `game/images/` 根目录还是 `game/images/ch1/`、`game/images/Hospital/` 等任意深层子目录，下划线与空格智能互通，毫秒级定位 `.png`/`.webp`/`.jpg`。
  - **功能完备**：多图层标签切换（背景/立绘）、分辨率与文件大小显示、完整路径悬浮提示、一键“打开所在目录”（Windows 资源管理器高亮定位）。

### 🛠 4. 额外文本工作区（ExtraText）分类筛选与批量操作
- **实时分类联动**：全面接入 `Sidebar.SelectedItem` 与 `SearchText` 监听，点击左侧 `Character`、`Input`、`Define`、`ScreenText`、`Tooltip` 等任意分类时，中间列表实时响应并动态筛选（`VisibleCandidates`）。
- **全部候选与批量操作**：侧边栏首项增加“全部候选”，工具栏提供“全选当前分类”、“取消全选”与数量统计（如 `35 / 196 项`）。

### 🖼 5. 原生暗黑模态弹窗（`ModernDialog`）
- 彻底替换旧版 Win32 白底 `MessageBox.Show`。
- 实现原生暗黑风格模态窗（圆角 8px、DropShadowEffect 柔和阴影、Fluent 矢量彩色状态图标、Primary/Ghost 风格按钮、支持 `Enter`/`Esc` 快捷键与窗口拖拽）。

### 🛠 6. TL 生成模块增加“自定义 SDK 路径”
- 界面增加“SDK 路径”显示行及 `选择...` / `清除` 按钮，支持手动浏览指定 `renpy.exe`。
- 新增 `SdkPath` 字段并持久化到本地 `settings.json`，下次启动自动恢复并优先使用。

### 📐 7. 三列视觉分界与全局排版规范化
- **三列竖向分界**：在 ActivityBar、项目导航侧边栏、中间工作区与右侧检查器之间加入了清晰的 `1px BackgroundLevel2` 竖向边框与头部边框，层次感显著提升。
- **字号保底**：全面消灭散落在各处的 `8px/9px/10px` 文字，将正文与辅助说明统一提高到 `11px ~ 13px`。
- **加宽滚动条**：滚动条宽度由 10px 提升至 12px，滑块可抓取宽度扩大至 10px。
- **清除多余滚动条**：全局禁用 `ListBox`/`ListView` 的横向滚动条，AvalonEdit 开启 `WordWrap="True"`。

---

## 3. 完整代码改动清单（文件级别索引）

```
E:\RenPyFlowTranslator\
├── RenPyLocalizationStudio.sln
├── CHANGELOG_UI_REFAC.md                       # 改动日志
├── HANDOVER.md                                 # 本交接总览文档
├── scripts/
│   ├── prepare-tool-runtime.ps1                # 修复 PS 5.1 兼容性
│   └── publish.ps1                             # 正式发布脚本 (输出至 artifacts\win-x64)
├── src/
│   ├── RenPyLocalizationStudio.Core/
│   │   ├── Models.cs                           # AST 节点、Region、TranslationUnit 数据模型
│   │   ├── RenPySourceParser.cs                # 核心 Ren'Py 语法解析器
│   │   └── Services/                           # 核心业务服务
│   ├── RenPyLocalizationStudio.App/
│   │   ├── Themes/
│   │   │   └── StudioTheme.xaml                # 全局调色板、预设色、按钮微交互、Splitter 与 Tab 样式
│   │   ├── Services/
│   │   │   ├── UiServices.cs                   # ModernDialog 模态弹窗、ThemeService 60fps 颜色插值、FileDialog
│   │   │   └── RenPyImagePreviewService.cs     # Ren'Py 图片上下文解析、子句剥离与递归寻图引擎
│   │   ├── ViewModels/
│   │   │   ├── MainViewModel.cs                # 主窗口 VM (当前工作区、主题色)
│   │   │   ├── ProjectSessionViewModel.cs      # 项目会话 (持久化 SdkPath、语言、快照)
│   │   │   ├── TranslationWorkspaceViewModel.cs# 翻译工作区 VM、ImagePreviewViewModel
│   │   │   └── ToolWorkspaces.cs               # TL 生成、额外文本 (分类过滤)、项目工具 VM
│   │   └── Views/
│   │       ├── MainWindow.xaml / .cs           # 导航浮动滑块动效、三列分界、工作区滑动切换
│   │       ├── TranslationMainView.xaml        # 垂直上下分割、GridSplitter、游戏画面预览面板
│   │       ├── TranslationSidebarView.xaml     # 导航栏排版与对比度优化
│   │       ├── TranslationInspectorView.xaml   # 原文/译文清晰分界
│   │       ├── ExtraTextMainView.xaml          # 额外文本分类过滤与批量勾选
│   │       ├── TlMainView.xaml                 # 自定义 SDK 路径选择
│   │       └── DiffDocumentView.xaml           # AvalonEdit 自动换行防白块
│   └── tests/
│       └── RenPyLocalizationStudio.Tests/
│           └── WorkspaceViewModelTests.cs      # 单元测试 (含 ImagePreview 与 ExtraText 测试)
```

---

## 4. 核心算法与设计模式

### 1. 浮动滑块连续滑动算法 (`MainWindow.xaml.cs`)
```csharp
private void UpdateActivityIndicator(bool animate = true)
{
    if (ActivityBarList == null || ActivitySelectionIndicator == null) return;
    int index = ActivityBarList.SelectedIndex;
    if (index < 0) { ActivitySelectionIndicator.Opacity = 0; return; }

    // 46px 图标高 + 4px 垂直外边距 = 50px 步进
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

### 2. Ren'Py 语句子句剥离与图片匹配算法 (`RenPyImagePreviewService.cs`)
```csharp
// 1. 剥离转场与位置子句
var match = ClauseFilterRegex().Match(rest); // 过滤 with, at, as, behind, onlayer, zorder
var cleanTag = match.Success ? match.Groups["tag"].Value.Trim() : rest.Trim();

// 2. 全项目扁平递归索引匹配 (Path-Agnostic)
// 支持完全匹配、下划线与空格互转匹配、忽略分隔符压缩匹配、目录相对路径匹配
```

### 3. 60fps 换色实时插值 (`UiServices.cs`)
```csharp
_colorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
_colorTimer.Tick += (_, _) =>
{
    var elapsed = (DateTime.UtcNow - _animStartTime).TotalMilliseconds;
    var progress = Math.Clamp(elapsed / 220.0, 0.0, 1.0);
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

## 5. 常见问题排查与注意事项

1. **发布时报 MSB3026 / 进程锁死**：
   - 解决方案：在运行 `publish.ps1` 前必须先执行 `Stop-Process -Name "RenPyLocalizationStudio" -Force -ErrorAction SilentlyContinue`。
2. **C# 中 Thickness 构造函数**：
   - C# 代码中 `new Thickness(...)` 仅支持 1 个参数或 4 个参数（`left, top, right, bottom`），传入 2 个参数会导致 `CS7036` 编译错误。
3. **ListBox 底部出现灰色横向滑块**：
   - WPF 的 ListBox 默认 `HorizontalScrollBarVisibility` 为 `Auto`。如需彻底消除，必须在样式中显式设为 `Disabled`。

---

## 6. 编译、测试与发布标准流程

在 PowerShell 终端中依次执行以下标准命令：

```powershell
# 1. 强杀已有进程防文件锁死
Stop-Process -Name "RenPyLocalizationStudio" -Force -ErrorAction SilentlyContinue

# 2. 运行单元测试套件（确保 54/54 全部通过）
dotnet test E:\RenPyFlowTranslator\tests\RenPyLocalizationStudio.Tests

# 3. 编译并发布到成品交付目录
cd E:\RenPyFlowTranslator
.\scripts\publish.ps1
```

发布完成后即可在 `E:\RenPyFlowTranslator\artifacts\win-x64\RenPyLocalizationStudio.exe` 启动并检验最新成品！
