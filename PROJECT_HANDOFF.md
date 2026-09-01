# RenPy Localization Studio 项目交接说明

> 给后续 AI / 开发者：先阅读本文件、`README.md` 和根目录约束，再扫描 `src/`、`tests/`。不要仅根据界面文字推断功能是否完成，应检查真实命令、服务实现和测试。

## 1. 项目定位

RenPy Localization Studio 是一个面向 Windows 的 Ren’Py 人工汉化工作台，原项目名为 RenPyFlowTranslator。

- 仓库：`E:\RenPyFlowTranslator`
- 解决方案：`RenPyLocalizationStudio.sln`
- 技术栈：.NET 10、WPF、Windows x64、简体中文
- 应用项目：`src/RenPyLocalizationStudio.App`
- 核心项目：`src/RenPyLocalizationStudio.Core`
- 测试项目：`tests/RenPyLocalizationStudio.Tests`
- 默认发布目录：`artifacts\win-x64`
- 主程序：`artifacts\win-x64\RenPyLocalizationStudio.exe`
- 当前版本：`0.3.9`
- 发布方式：框架依赖，不是单文件或自包含包；目标机器需要 .NET 10 Desktop Runtime

项目不接入机翻或模型 API，不执行游戏 EXE，不自动启动游戏，不修改原始源码来包裹 `_()`，不删除 `.rpa`、`.rpyc` 或 `.rpymc`。

## 2. 当前可用状态

截至 2026-09-01：

- Release 构建通过，0 警告、0 错误。
- .NET 自动化测试共 113 项，另有 4 项 Python RPA 安全测试，全部通过。
- 使用本机 `E:\renpy-8.5.2-sdk\renpy.exe` 对临时项目副本执行 lint，已通过。
- 框架依赖发布由 `scripts/publish.ps1` 生成到被忽略的 `artifacts\win-x64`；本地清理后按需重新生成，正式包以 GitHub Release 为准。
- 发布版已经完成启动烟测。
- 已建立可审阅的 Git 基线与分层重构提交；仍不得使用破坏性 Git 命令覆盖用户改动。

## 3. 主要功能

### 3.1 翻译工作区

- 解析 Ren’Py `.rpy` 与已有 `game/tl/<language>/`。
- 构建剧情控制流，识别 `label`、`menu`、条件分支、`jump`、`call`、`return` 和文件内自然顺序。
- `$`、Python/init 块及 screen 块不进入剧情控制流；screen 字符串仍进入字符串表。
- 不猜测跨文件自然落入，文件末尾生成终点。
- 支持“剧情流 / 字符串表 / 未绑定”三级视图。
- 剧情流支持“路径 / 文件 / Label”三种投影；左侧下方导航内容会随投影切换：
  - 路径：显示未被 jump/call 指向的剧情入口；无入口时回退显示所有 label。
  - 文件：显示源文件及节点数量。
  - Label：显示 label、来源位置和节点数量。
- `jump` / `call` 行支持单击或 Enter 定位目标 label。
- Label 支持单击、双击或 Enter 定位。
- `Alt+↑` / `Alt+↓` 在可编辑译文之间切换并保持编辑焦点。
- 正常“已绑定”状态不重复显示文字；仅异常、共享影响或待处理状态突出显示。
- 同一目标语言下相同 `old` 使用共享译文语义。
- 剧情流代码行可通过右键菜单添加/移除书签；“书签”视图按稳定节点 ID显示并支持左侧定位，书签仅在当前会话保留。
- `Alt+↓` 遇到可解析的 `jump` 时会沿目标 label 继续，定位到目标分支的下一条可编辑译文；动态或无法解析的目标保持线性切换。

### 3.2 TL 生成

- 通过 Ren’Py SDK 执行预检与翻译文件生成。
- 支持 `--empty`、`--strings-only`、`--no-todo`。
- “生成选项”和“执行日志”是两个真正独立的页面，不再堆叠在一个界面。
- 生成页显示目标语言、选项、执行计划、目标目录、参数和 SDK 状态。
- 日志页显示时间、级别、消息、计数和清空操作。
- 外部进程统一后台执行，捕获 stdout/stderr，支持取消和超时诊断。

### 3.3 补丁工作区

- Replace 精确替换规则。
- `zzz.rpy` 向导：默认语言、设置页语言入口、字体覆盖、自定义补丁代码。
- Diff 作为工作区内文档标签展示，不使用模态窗口。
- 受管区块使用 `RLS-ZZZ-BEGIN/END <module>`，标记外内容应保持不变。
- 自定义代码使用 AvalonEdit，具有独立的代码编辑表面。
- 编辑器支持：
  - Tab 四空格缩进；
  - Shift+Tab 减少缩进；
  - Enter 继承缩进，冒号后增加一级；
  - Ctrl+/ 切换注释；
  - Ctrl+Space 或工具栏按钮打开补全；
  - 输入至少两个标识符字符后尝试自动补全；
  - 补全包含 `label`、`jump`、`call`、`return`、`menu`、条件、translate、replace 和语言切换等片段。

### 3.4 额外文本

- 扫描 Character 名称、screen 文本、`renpy.input()`、`renpy.notify()`、翻译函数、define/default/Python 静态字符串等。
- 用户确认后写入受管额外字符串文件。
- 不自动改写原始源码。

### 3.5 项目工具

- RPA 解包和 RPYC/RPYMC 反编译是两个独立模式，共用一个 UnRen 视图；当前页面只显示当前操作按钮，不再同时显示“解包 RPA”和“反编译 RPYC”。
- 路径越界、输出冲突和覆盖已有文件必须诊断或阻止。
- 支持批量移除文件名前缀，默认兼容 `x-`。
- 支持 YAC 风格图片压缩工作流，处理 PNG、JPEG 和静态 WebP；底层使用 SkiaSharp，不运行外部未知图片工具。
- 用户提供的 `rename【去除x-前缀】.exe` 与 `RenpyToolbox.exe` 只用于理解需求，禁止运行、复制或发布其中未知授权代码。

## 4. 架构概览

### 4.1 Core

Core 不依赖 WPF、Dispatcher、ViewModel 或控件。

主要入口：

- `ProjectAnalyzer`：扫描源脚本和 tl，构建 `ProjectSnapshot`。
- `RenPySourceParser`：容错解析 Ren’Py 源脚本。
- `TlParser`：解析官方 translate 块与 strings 表。
- `FlowProjectionService`：生成剧情路径、源文件和 Label 投影。
- `IProjectSaveService` / `ProjectWriter`：规划并安全回写译文与流程注释，返回逐文件提交结果。
- `TranslationValidator`：检查插值、百分号占位符和文本标签。
- `Utf8TextFile`：严格 UTF-8、BOM、换行和末尾换行状态。

服务集中在 `src/RenPyLocalizationStudio.Core/Services/`：

- `IFileSystemService`
- `IProcessRunnerService`
- `IProjectAnalysisService`
- `IRenPySdkService`
- `IArchiveExtractionService`
- `IScriptDecompilerService`
- `IExtraTextScanService`
- `IReplacementRuleService`
- `IManagedPatchService`
- 重命名与图片压缩服务

耗时或 I/O 接口使用：

```csharp
Task<OperationResult<T>> ExecuteAsync(
    TRequest request,
    IProgress<ToolOperationProgress> progress,
    CancellationToken cancellationToken);
```

业务层可预见失败应转换成 `Diagnostic`，不要在公共服务边界向 UI 裸抛文件占用、编码、权限、路径、超时或外部进程异常。

### 4.2 App / MVVM Shell

- `MainWindow` 只承载标题栏、活动栏、侧栏、主视口、Inspector 和任务状态栏。
- `MainViewModel` 是全局路由协调者。
- 六个顶级工作区：翻译、TL、额外文本、补丁、项目工具、诊断。
- `WorkspaceViewModelBase` 及三种区域 ViewModel 承载 Sidebar / MainContent / Inspector。
- `App.xaml` 和 `Themes/WorkspaceTemplates.xaml` 使用隐式 DataTemplate 将 ViewModel 映射到 UserControl。
- 依赖在 `App.xaml.cs` 手工组合，不使用大型 DI 容器。
- `WeakReferenceMessenger` 只传递导航意图，不同步项目数据或译文状态。
- `AvalonEditBindingBehavior` 负责编辑器文本与 ViewModel 双向同步。
- `ScrollAnchorBehavior` 使用稳定条目 ID 恢复位置，不保存纯像素滚动偏移。
- 大列表必须继续使用 `VirtualizingStackPanel` Recycling；不要在列表外增加会破坏虚拟化的 StackPanel 或额外 ScrollViewer。

## 5. 数据与保存语义

- 扫描剧情源码时排除 `game/tl/`。
- 输入只接受有效 UTF-8。
- 读取时记录 BOM、CRLF/LF 和末尾换行；保存时继承原状态。
- 保存前检查原文件哈希，检测外部修改时拒绝覆盖。
- 只重写发生变化的文件。
- 保存流程应保持：验证目标 → 检查预期哈希 → 同目录临时写入 → 重新解析 → 备份 → 原子替换。
- 翻译 ID、old、说话人及源码注释默认不可编辑。
- `new ""` 是合法空译文；只有 old 没有 new 是残缺条目，需要诊断并允许补写。
- 重复 `old` 且译文冲突时必须阻止结构化保存，不能静默选一个。
- 受管注释只删除精确匹配且位于字符串外的完整标记行；用户注释必须保留。

## 6. UI 约束

- 设计目标：`DESIGN_VARIANCE 5 / MOTION 2 / DENSITY 8`。
- 深色 IDE 风格，正文不是纯白，强调色只用于选中、焦点、主要操作和进度。
- 使用融合式自绘标题栏和 WindowChrome，同时保留最小化、最大化、关闭、Alt+Space、缩放和 Snap Layout 命中区。
- 720px 最小宽度；窄窗口自动折叠 Inspector 和侧栏。
- Inspector 分为空、流程不可编辑、完整翻译编辑三种状态；空状态不得实例化隐藏的 AvalonEdit。
- 全局 ScrollBar 已使用暗色窄模板。
- 不恢复旧版大块 GroupBox/Border 拼接式界面。

## 7. 最近一轮关键修复

关键文件：

- `src/RenPyLocalizationStudio.App/ViewModels/WorkspaceModels.cs`
  - `TranslationNavigationBuilder` 构建路径、文件和 Label 导航。
- `src/RenPyLocalizationStudio.App/ViewModels/TranslationWorkspaceViewModel.cs`
  - 分组切换时重建左侧导航，暴露动态 `NavigationHeading`。
- `src/RenPyLocalizationStudio.App/Views/TranslationSidebarView.xaml`
  - 下方导航标题和集合随投影变化。
- `src/RenPyLocalizationStudio.App/ViewModels/ToolWorkspaces.cs`
  - TL 生成选项/日志路由；项目工具模式切换。
- `src/RenPyLocalizationStudio.App/Views/TlMainView.xaml`
  - 生成配置与日志彻底分屏。
- `src/RenPyLocalizationStudio.App/ViewModels/ProjectToolsViewModels.cs`
  - `UnrenOperationKind`，RPA/RPYC 单操作模型。
- `src/RenPyLocalizationStudio.App/Views/UnrenToolView.xaml`
  - 只显示当前操作。
- `src/RenPyLocalizationStudio.App/Behaviors/RenPyCodeEditorBehavior.cs`
  - Tab 崩溃修复、缩进、注释和补全。
- `src/RenPyLocalizationStudio.App/Views/ZzzDocumentView.xaml(.cs)`
  - 差异化代码编辑器与补全入口。
- `tests/RenPyLocalizationStudio.Tests/VisualPresentationTests.cs`
  - 空行 Tab 和三种导航投影回归测试。

Tab 崩溃的根因已经确认：`TextDocument.Insert` 会自动移动 Caret，旧逻辑又额外累加缩进长度，导致 CaretOffset 超过文档长度。现在使用插入前 offset 并将结果限制在 `TextLength` 内。

2026-08-30 的架构审计修复还包括：

- StoryPath 改为非递归控制流遍历；互斥条件、菜单汇合及 `call/return` 继续点具有明确边语义。
- 保存返回逐文件成功/失败/取消结果，部分成功立即刷新对应文件基线；重复 dialogue ID 阻断保存。
- strings 用户注释、额外文本空译文、Replace 多节点环与占位符、zzz 孤立受管标记均有回归覆盖。
- Project Asset Index 使用图片查询字典及脚本场景状态二分定位；外部进程输出采用有界缓冲。
- RPA/RPYC 在执行前展示逐项计划，并校验发布 manifest 中所有运行时文件的长度和 SHA-256。
- `docs/adr/0001-project-snapshot-editing-model.md` 固定 Project Snapshot 的“结构稳定、编辑状态可变”语义。

## 8. 已知风险与未完全覆盖项

1. **补全交互测试不足**：自动化测试覆盖 Tab 不崩溃和缩进结果；WPF UI 自动化对补全弹窗焦点/Tab 接受的模拟仍不稳定，后续应增加专用 STA 交互测试。
2. **首次完整发布需要网络**：Python、unrpyc 与 rpatool 下载物已在 `tools/tool-runtime.lock.json` 固定版本、URL 和 SHA-256；缓存命中后可复用，但全新环境仍需访问上游。
3. **真实 UI 性能**：Core 已覆盖十万节点解析与非递归 StoryPath 投影，仍应继续测量 WPF 容器数量、图片内存和滚动锚点恢复。
4. **RPYC 兼容范围**：固定 unrpyc 2.0.4，遇到未知 Ren’Py 节点或新格式必须返回诊断，不要尝试运行游戏脚本。
5. **SDK 自动检测**：版本排序已按数值 `Version` 处理；多安装目录、权限受限机器和便携 SDK 仍需实机覆盖。

## 9. 推荐继续开发顺序

1. 先为 AvalonEdit 自动补全增加真正的 STA 交互测试：输入 `ju` → 显示 `jump` → Tab 接受 → 文档变为 `jump target`。
2. 给 TL 的预检、生成、取消和日志页增加 ViewModel 路由测试。
3. 给 RPA/RPYC 单模式页面和计划结果增加测试，确认不会出现重复操作按钮。
4. 为工具运行时缓存增加显式离线模式和许可证清单自动验收。
5. 用真实项目副本做端到端部分保存、外部修改冲突和强制覆盖测试，禁止直接对唯一游戏目录试写。

## 10. 常用命令

PowerShell 读取中文文件前：

```powershell
chcp 65001
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
Get-Content -Encoding UTF8 .\PROJECT_HANDOFF.md
```

还原、构建和测试：

```powershell
cd E:\RenPyFlowTranslator
dotnet restore .\RenPyLocalizationStudio.sln
dotnet test .\RenPyLocalizationStudio.sln -c Release
```

开发运行：

```powershell
dotnet run --project .\src\RenPyLocalizationStudio.App\RenPyLocalizationStudio.App.csproj
```

框架依赖发布：

```powershell
.\scripts\publish.ps1
```

Ren’Py 8.5.2 临时副本 lint：

```powershell
.\scripts\verify-sdk-lint.ps1 -SdkExe E:\renpy-8.5.2-sdk\renpy.exe
```

## 11. 接手时必须遵守

- 所有文件使用 UTF-8；修改时不要改变已有 BOM 和换行状态。
- PowerShell 读取中文文件使用 `Get-Content -Encoding UTF8`。
- 不使用 sed/awk 修改中文文件。
- 代码注释使用中文。
- 不执行用户提供的未知 EXE。
- 不运行游戏 EXE，不执行游戏 Python。
- 不删除游戏归档、编译脚本或用户翻译。
- 不自动保存；所有实际项目写入由用户明确确认。
- 保持 Core 与 WPF 隔离，ViewModel 不持有控件实例。
- 修改完成后至少运行 Release 测试；涉及保存或生成时再运行 Ren’Py lint。

## 12. 给下一位 AI 的最短启动指令

```text
扫描 E:\RenPyFlowTranslator，先完整阅读 PROJECT_HANDOFF.md、README.md、根目录约束和相关测试。
检查 git status，保留用户尚未提交的工作树改动。
先运行 dotnet test .\RenPyLocalizationStudio.sln -c Release 建立当前基准。
任何 UI/保存/外部工具修改都必须检查真实实现并做对应回归，不要只改界面文字。
```
