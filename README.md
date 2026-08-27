# RenPy Localization Studio

当前版本：**v0.3.6**。

RenPy Localization Studio 是一个面向 Ren’Py 人工本地化流程的 Windows 桌面工作台。它把 tl 生成、剧情流翻译、额外文本、替换规则、`zzz.rpy` 补丁以及解包/反编译安全服务收敛到同一项目界面。

## 主要能力

- 识别 `label`、`menu`、`if/elif/else`、`jump`、`call`、`return` 和文件内自然流程。
- 隔离 `$`、`python`、`init python`、`init` 与 `screen` 代码块，不执行游戏代码。
- 将菜单字符串虚拟挂回实际分支；相同 `old` 始终共享一个译文，并显示影响位置。
- 菜单字符串的官方行号发生小幅偏移时，会在同一源码文件内按唯一 `old` 回退绑定；多个候选仍作为歧义项处理。
- 单击剧情流中的 `jump` 或 `call` 行，或选中后按 `Enter`，可直接定位到对应 `label`；搜索条件遮挡目标时会自动清除搜索。
- 左侧 label 列表支持单击、双击或按 `Enter` 定位，重复定位当前选中项同样生效。
- 在译文输入框中按 `Alt+↑` / `Alt+↓` 可切换到上一条或下一条可编辑译文，并继续保持输入焦点。
- 单独索引 screen/界面字符串、未绑定翻译与静态分析诊断。
- 普通 strings、screen 文本和数据文本只进入字符串表；“未绑定剧情”仅显示未能挂接的 dialogue 翻译块。
- 检查插值、百分号占位符和 Ren’Py 文本标签差异。
- 可写入幂等的 `RFT-FLOW-BEGIN/END` 流程注释。
- 原子保存、外部修改检测、最近一次 `.rls.bak` 备份，并继承原文件 BOM 与换行风格；可经二次确认强制覆盖外部修改，且不会绕过结构校验。
- 通过本机 Ren’Py SDK 预检或增量生成 tl，后台捕获日志并支持取消。
- 按剧情路径、源文件或 label 浏览同一流程图。
- 可在代码行右键添加/移除书签，并通过“书签”选项卡快速筛选和定位；书签仅保存在当前会话中。
- 在“书签”视图单击、双击、按 `Enter` 或使用“定位原代码”可回到完整剧情流中的原始文件与行号。
- `Alt+↓` 遇到可解析的 `jump` 时会跟随目标 label，直接进入目标分支的下一条可编辑译文。
- “更多设置”可开启译文防抖自动保存（停止输入约 0.9 秒后写入）；项目、分组和上次选中条目会保存到用户设置并在下次分析后恢复。
- `Alt+←` / `Alt+→` 在编辑器中保留单词移动；离开编辑器后会拦截，避免误触历史或窗口导航。
- 扫描 Character、screen、input、notify、define/default 等额外文本，人工确认后写入受管补丁。
- 校验精确 replace 规则，并使用受管区块生成 `zzz.rpy`。
- 解包/反编译服务强制项目内路径、冲突跳过和源文件保留策略。
- 批量移除文件名前缀，默认兼容 `x-`，执行前显示完整映射并阻断同名冲突与预览后的外部修改。
- YAC 图片压缩工作流支持 PNG、JPEG 和静态 WebP；默认输出到项目内独立目录，也可显式选择原位替换并创建 `.rls.bak`。
- 深色 IDE 工作台、Mica 渐进增强、三套强调色与自定义色、AvalonEdit 纯文本编辑器和占位符令牌栏。

## 使用

1. 启动 RenPy Localization Studio，选择包含 `game` 目录的项目根目录。
2. 可使用“TL 预检/生成 TL”，或直接选择已有目标语言。
3. 选择 `game/tl/` 下的语言并点击“分析”。
4. 在“剧情流”或“字符串表”中选择条目并编辑译文。
5. 点击“保存”，或点击“保存并刷新注释”同步流程边界注释；遇到确认无误的外部修改冲突时，可从“更多设置”选择“强制覆盖外部修改”。

工具不会执行游戏 EXE、不会删除 `.rpa/.rpyc/.rpymc`，也不会自动翻译文本。跨文件的无显式跳转自然落入会显示为“文件结束”。

## 开发与验证

```powershell
dotnet restore .\RenPyLocalizationStudio.sln
dotnet test .\RenPyLocalizationStudio.sln -c Release
dotnet run --project .\src\RenPyLocalizationStudio.App\RenPyLocalizationStudio.App.csproj
```

使用本机 Ren’Py SDK 对临时夹具执行安全 lint：

```powershell
.\scripts\verify-sdk-lint.ps1 -SdkExe E:\renpy-8.5.2-sdk\renpy.exe
```

发布 Windows x64 程序（需要 .NET 10 Desktop Runtime）：

```powershell
.\scripts\publish.ps1
```

默认发布目录为 `artifacts\win-x64`。主程序为框架依赖版本，目标电脑必须安装 **.NET 10 Desktop Runtime x64**。官方 Release 已包含校验过的隔离 Python、unrpyc 与 rpatool 运行时，不需要另外下载归档工具。

生成可发布 ZIP 与 SHA-256：

```powershell
.\scripts\publish.ps1 -Package
```
