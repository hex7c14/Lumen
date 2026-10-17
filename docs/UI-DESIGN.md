# UI 设计规格（ui-dev 实现依据）

> 状态：迭代 1 规格（单图）。**迭代 2 的增量为：队列批处理、处理模式/CPU 兼容、系统信息、动漫专精文案**，完整 v2 规格见 `docs/ITERATION-2.md` §5；本文件中的“去 AI 味”视觉原则、颜色/字体/间距/无障碍规则继续有效。

> 目标：像 Windows 自带的图片工具，而不是“AI 生成感”的网页套壳。
> 参考：Microsoft `winui` 技能（refs/winappCli/plugins/winui/agent-plugin/skills/）、`docs/WINUI.md`（winui-arch 产出）。
> 窗口形态：单窗口媒体编辑器（Photos / Clipchamp 形态）。**不使用 NavigationView。**

## 1. 窗口与骨架

- 默认尺寸 1320 × 860 DIP，最小 1040 × 680；用 `AppWindow.Resize` + `GetDpiForWindow` 换算物理像素（见 winui-design 技能）。
- `ExtendsContentIntoTitleBar = true`，自定义标题栏；`MicaBackdrop`（无 Mica 时自动退回 DesktopAcrylic）。
- 标题栏行 48 DIP：左侧 20×20 应用标记 + “Lumen”；中间空白可拖动；右侧系统标题按钮（透明背景、跟随主题）。
- 工具栏行 56 DIP：`打开图片`、`导出`（未出结果时禁用）、分隔线、`适应窗口`、`100%`、`对比` 开关；右侧主按钮 `开始超分`（处理中变为 `取消`，用标准 Accent 样式）。
- 内容区：左侧画布（自适应剩余宽度）＋ 右侧参数面板（宽 300，可折叠，折叠按钮在面板标题右侧）。
- 底部状态条 32 DIP：左：后端标签（DirectML / CPU）与设备名（次要文本）；中：进度条 + 状态文本 + `取消` 小按钮（仅处理中）；右：尺寸信息（`1600 × 1067 → 6400 × 4268`）。
- 全部区域允许拖放图片（`AllowDrop`，DragOver 显示 2px 强调色边框）。

## 2. 画布

- 空状态（未载入图片）：居中虚线圆角矩形 420×220，`FontIcon`（图片类字形）+ “将图片拖到这里，或点击打开”（14px）+ 次要说明 “支持 PNG、JPEG、WebP、BMP”；下方一行小字快捷键 “Ctrl+O”。**不加插画、不加 emoji、不加渐变。**
- 已载入：深/浅主题的纯色画布底（`EditorCanvasBrush`：Dark `#181818`、Light `#F4F4F4`、HighContrast 用系统色），图片居中显示。
- 缩放：`ScrollViewer` + 图片尺寸 = 像素尺寸 × 缩放系数；支持 Ctrl+滚轮、触控板缩放；拖动空白平移。`适应窗口` 按可视区等比缩放；`100%` 为 1:1 像素。
- 对比模式：原图与处理后重叠，右侧被裁切显示结果；中间 2px 强调色分隔线 + 圆形拖拽手柄（24px），顶部左右角小标签 “原图” / “处理后”（半透明背景，12px）。按住 Space 临时显示原图（松开恢复）。
- 画布右下角浮动一条胶囊工具条（`CommandBar` 或自绘圆角 Border）：`-`、缩放百分比、`+`、适应窗口。保持在画布内 16px 边距。
- 处理中：画布不闪烁；顶部居中显示非模态进度信息（`ProgressBar` 值绑定 + “第 12 / 48 块 · 00:12”）；可取消。
- 重要：预览图使用 Core 生成的预览（长边 ≤ 2560），不要用全尺寸图创建 WriteableBitmap；原始像素只用于保存。

## 3. 右侧参数面板

分组顺序（每组上下留 12 DIP，组标题 14px 半粗体，使用 `TextFillColorSecondaryBrush`）：

1. **模型**：`ComboBox`（显示 `DisplayName`，右侧提示 `4×`）；下方 12px 次要文本显示 `Description` 与档位标签（质量 / 均衡 / 极速，纯文字或细边框标签，不要彩色胶囊）。
2. **倍率**：`RadioButtons` 横向（`MaxColumns=3`）：2× / 3× / 4×；选 3× 时说明“由 4× 输出高质量降采样”。
3. **输出**：格式 `ComboBox`（PNG / JPEG / WebP）；JPEG/WebP 时显示质量 `Slider`（0–100，默认 92，右侧数字）；`保留透明通道` `ToggleSwitch`（仅 PNG/WebP 时可用）。
4. **高级**（`Expander` 默认折叠）：推理后端 `ComboBox`（自动 / GPU / CPU）、`优先使用 FP16` `ToggleSwitch`、分块大小 `NumberBox`（0=自动）、分块边距 `NumberBox`（0=自动）。
5. 面板底部固定信息区：输入 `1600 × 1067`、输出预估 `6400 × 4268`、模型大小；超限时显示错误色文本。

## 4. 状态与反馈

- `InfoBar`（放在画布顶部、`Severity` 按错误/警告/成功）用于：处理失败、尺寸超限、保存成功（3 秒自动关闭或手动关闭）。一次只显示一条。
- 处理完成：状态条显示 `DirectML · 用时 12.4s · 48 块`；画布自动切换到对比模式。
- 所有异常写入 `AppLog`，界面显示中文可读信息，不弹原始堆栈。
- 关闭窗口时若正在处理：`ContentDialog` 确认（“正在处理，退出将丢失当前结果” / “取消处理并退出” / “继续处理”）。

## 5. 视觉规范（去 AI 味清单）

- 只使用 WinUI 主题资源：`TextFillColorPrimaryBrush`、`TextFillColorSecondaryBrush`、`TextFillColorTertiaryBrush`、`ControlFillColorDefaultBrush`、`CardBackgroundFillColorDefaultBrush`、`LayerFillColorDefaultBrush`、`DividerStrokeColorDefaultBrush`、`AccentFillColorDefaultBrush`。自定义资源命名按用途（`EditorCanvasBrush`、`CompareDividerBrush`），写全 Light/Dark/HighContrast 三套 ThemeDictionaries。
- 圆角：控件 4，卡片/面板 8；边框 1px。
- 间距：4 / 8 / 12 / 16 / 24 / 32 六档，不出现 7、13、21 这类魔法值。
- 字体：Segoe UI Variable（系统默认），字号 12 / 14 / 20 / 28；标题少而克制。
- 图标：Segoe Fluent Icons 字形（`FontIcon`），按钮优先“图标 + 文本”或纯文本；禁止 emoji。
- 禁止项：紫蓝渐变、玻璃卡片堆叠（`AcrylicBrush` 滥用）、发光阴影、装饰性插画、`✨/🚀/💡`、假的统计数字、“AI 魔法”“一键变清晰”等营销文案、“Powered by AI” 角标。
- 深色/浅色/高对比度都要能看；对比度不足时用系统资源而不是硬编码颜色。
- 所有按钮有 `AutomationProperties.Name`；Tab 顺序自然；键盘：Ctrl+O 打开、Ctrl+S 导出、Ctrl+= / Ctrl+- 缩放、Ctrl+0 适应窗口、Space 临时看原图、Esc 取消处理。

## 6. 交互流程

1. 启动 → 加载设置与模型目录 → 空状态；状态条显示后端能力（例如 `DirectML · NVIDIA GeForce RTX 5060 Laptop GPU`）。
2. 打开/拖入图片 → 后台解码（显示不确定进度 200ms 后出现）→ 画布显示原图 → 自动 `适应窗口` → 右侧输出尺寸估算更新。
3. 调参数 → 点 `开始超分` → 处理中禁用参数面板与打开按钮，主按钮变 `取消` → 进度更新。
4. 完成 → 对比模式展示 → `导出` 可用 → 状态条显示用时与后端。
5. 导出 → `FileSavePicker`（类型与设置同步，默认文件名 `原名_4x.png`，默认目录=源文件目录）→ 后台编码保存 → InfoBar 成功提示（可点击“打开所在文件夹”）。
6. 再次打开或拖入新图 → 清理上一次结果与预览。

## 7. 工程结构（ui-dev 写入范围 `src/Lumen.App/**`、`docs/screenshots/**`）

```
Lumen.App.csproj          已存在且可构建（WindowsAppSDK 2.5.1，unpackaged，self-contained）；改 csproj 时保留这些属性
app.manifest              已存在
App.xaml / App.xaml.cs    已有主题刷子与全局异常处理，可扩展
MainWindow.xaml / .cs     已有 Mica + 自定义标题栏 + 图标，可重构
Views/                    编辑器主视图（工具栏/画布/参数面板/状态条）
Controls/CompareViewer.xaml/.cs  对比、缩放、平移控件（重点）
ViewModels/MainViewModel.cs      CommunityToolkit.Mvvm 源生成器（[ObservableProperty]/[RelayCommand]）
ViewModels/ModelOption.cs
Services/AppServices.cs          手工装配（codec/catalog/settings/compute env/upscaler）
Services/FileDialogService.cs    FileOpenPicker/FileSavePicker + InitializeWithWindow
Services/ImageDisplayService.cs  ImagePixels -> WriteableBitmap（BGRA 预乘）
Assets/                          已有 AppIcon.ico / AppIcon-256.png
```

## 8. 无人值守截图支持（必须实现，便于验收）

为自动化验收加三个**仅调试用**的环境变量（写在代码注释里，不需要 UI 暴露）：

- `LUMEN_OPEN=<图片路径>`：启动后自动打开该图片；
- `LUMEN_AUTOSTART=1`：启动后自动开始一次超分；
- `LUMEN_THEME=Dark|Light`：启动时把根元素 `RequestedTheme` 设为对应值（用于深浅色截图）。

这样 Lead 可以直接：启动 exe（带环境变量）→ `scripts/screenshot.ps1` → 得到对应状态的截图。

## 9. 验收（ui-dev）

- `dotnet build src/Lumen.App/Lumen.App.csproj` 零错误。
- 截图（保存到 `docs/screenshots/`）：`01-empty-dark.png`、`02-loaded-dark.png`、`03-processing-dark.png`、`04-compare-dark.png`、`05-empty-light.png`、`06-loaded-light.png`、`07-panel-expanded.png`。
- 截图里不允许出现：紫蓝渐变、emoji 图标、营销文案、fake 数据。
- 与引擎联调（若 engine-dev 的引擎已就绪）：真实图片完成一次 4× 超分 → 对比 → 导出 PNG 成功（附输出文件路径与截图）。
