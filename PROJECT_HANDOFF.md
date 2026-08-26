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
- 当前版本：`0.3.0`
- 发布方式：框架依赖，不是单文件或自包含包；目标机器需要 .NET 10 Desktop Runtime

项目不接入机翻或模型 API，不执行游戏 EXE，不自动启动游戏，不修改原始源码来包裹 `_()`，不删除 `.rpa`、`.rpyc` 或 `.rpymc`。

## 2. 当前可用状态

截至 2026-08-26：

- Release 构建通过，0 警告、0 错误。
- 自动化测试共 54 项，全部通过。
- 使用本机 `E:\renpy-8.5.2-sdk\renpy.exe` 对临时项目副本执行 lint，已通过。
- 框架依赖发布已生成到 `artifacts\win-x64`。
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
- `ProjectWriter`：校验并安全回写译文与流程注释。
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

## 8. 已知风险与未完全覆盖项

1. **Git 基线缺失**：当前文件基本未跟踪。修改前先复制或建立首个明确提交，不要清理工作树。
2. **补全交互测试不足**：自动化测试覆盖 Tab 不崩溃和缩进结果；补全弹窗已在真实发布窗口中显示并能按 `ju` 筛选 `jump`，但 WPF UI 自动化对弹出窗口焦点/Tab 接受的模拟不稳定。继续开发时应增加 STA 集成测试，直接验证候选接受后的文档文本。
3. **发布脚本需要网络**：`scripts/prepare-tool-runtime.ps1` 每次发布会下载固定的 Python 3.13.7、unrpyc 2.0.4 和 rpatool，并刷新发布目录下受管工具文件。离线构建或供应链加固尚未完成。
4. **工具校验**：发布脚本生成文件 SHA-256 清单，但下载前没有内置的固定上游哈希比对；若面向公开发布，应补充下载物哈希和许可证验收测试。
5. **真实大项目性能**：虚拟化设计存在，但仍应继续用十万节点项目做容器数量、内存和滚动锚点的自动化性能验证。
6. **RPYC 兼容范围**：固定 unrpyc 2.0.4，遇到未知 Ren’Py 节点或新格式必须返回诊断，不要尝试运行游戏脚本。
7. **SDK 自动检测**：当前 UI 可显示未检测到 SDK；设置持久化和多版本选择仍需在不同机器上验证。
8. **测试夹具含生成文件**：`tests/Fixtures/SdkProject` 当前含 cache、save、rpyc 和 lint 产生的日志。不要误把这些文件当生产项目数据；如清理，应先确认测试是否依赖它们。

## 9. 推荐继续开发顺序

1. 先为 AvalonEdit 自动补全增加真正的 STA 交互测试：输入 `ju` → 显示 `jump` → Tab 接受 → 文档变为 `jump target`。
2. 给 TL 的预检、生成、取消和日志页增加 ViewModel 路由测试。
3. 给 RPA/RPYC 单模式页面和计划结果增加测试，确认不会出现重复操作按钮。
4. 给 Python/unrpyc/rpatool 下载添加固定 SHA-256、缓存复用和离线发布选项。
5. 建立 Git 初始基线，再进行大范围重构。
6. 用真实项目副本做端到端保存和外部修改冲突测试，禁止直接对唯一游戏目录试写。

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
检查 git status，注意当前没有可靠 Git 基线。
先运行 dotnet test .\RenPyLocalizationStudio.sln -c Release 建立当前基准。
任何 UI/保存/外部工具修改都必须检查真实实现并做对应回归，不要只改界面文字。
```
