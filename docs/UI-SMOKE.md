# Lumen App 无人值守 UIA 冒烟（UI-SMOKE）

> 脚本：`scripts/ui-smoke.ps1`（Windows PowerShell 5.1，纯 ASCII）
> 依据：本机实测可用的 `winapp ui`（UI Automation，**不需要 Dev Mode**）+ `PrintWindow` 截图
> 相关：`docs/WINUI.md`（WinUI 构建/运行/截图配方）、`docs/UI-REVIEW.md`（UI 代码评审）、`scripts/screenshot.ps1`

## 0. 当前状态（2026-10-17 19:10）

| 阶段 | 状态 | 证据 |
|---|---|---|
| 启动 → 找窗口（按 PID 归属）→ 断言标题/关键控件 → 截图 → WM_CLOSE 退出 | ✅ 已实测通过（旧构建，控件靠 Name 兜底） | `artifacts/ui-smoke/phase1-check/`（ok=True，4.4s，exit 0） |
| **AutomationId 精确断言（19:07 新构建）** | ✅ 已实测通过：`missingAutomationIds=[]`、`fallbackControls=[]`；空状态 15/18 控件经 `automationId` 命中（其余 3 个 `BtnZoomIn/BtnZoomOut/BtnCancel` 仅载入/处理中可见，属正常缺席），载入+超分 17/18 + 窗口标题 | `artifacts/ui-smoke/automation-id-check/`（空状态，4.5s）、`artifacts/ui-smoke/automation-id-check-loaded/`（载入状态，4.5s）、`artifacts/ui-smoke/final-upscale-check/`（完整点击超分，12.0s） |
| **最终构建复验（19:15 Debug，含对话框主题改动）** | ✅ ok=True，4.2s，`via=automationId` 15/18（空状态）+ 窗口标题，`missingAutomationIds=[]`（不占 GPU） | `artifacts/ui-smoke/final-empty-check/` |
| **源码冻结版复验（19:17.24 Debug / 19:17.37 Release）** | ✅ 空状态 ok=True 4.3s；载入态 ok=True 4.3s（含 BtnZoomIn/Out）；两者 `missingAutomationIds=[]`、`fallbackControls=[]`（不占 GPU） | `artifacts/ui-smoke/final-build-empty/`、`artifacts/ui-smoke/final-build-loaded/` |
| 载入图片（`LUMEN_OPEN`）→ 点击"开始超分"→ 轮询完成 → 截图 → 退出 | ✅ 已实测通过（旧构建 4x 超分 14.4s；**19:07 新构建 12.0s**，全部 AutomationId） | `artifacts/ui-smoke/loaded-upscale-check/`（旧构建）、**`artifacts/ui-smoke/final-upscale-check/`（新构建：ok=True，3 张截图，`phases.upscale=completed`，exit 0）** |
| 深色/浅色两套、面板展开、处理失败/取消等状态 | ⏳ 待 UI 稳定后由 Lead 通知复跑 | — |
| **迭代 2 v2 冒烟脚本骨架（准备阶段）** | ✅ `-DryRun` 13/13 场景通过、PS 5.1 解析通过、纯 ASCII、GPU guard 生效 | `scripts/ui-smoke.ps1` + `artifacts/ui-smoke/v2-dryrun-*/plan.json`（见 §11） |
| **迭代 2 v2 完整矩阵（源码冻结版 20:01:08 Release）** | ✅ **13/14 场景通过**（含 GPU 2 场景、CPU 3 场景、兼容性 4 场景）；1 场景因 ui-dev 重建 exe 时启动失败（并发构建，非产品缺陷，修复版复跑） | `artifacts/ui-smoke/v2-final-20261017-2020/`、`v2-run-20261017-2008/`（见 §12） |
| **迭代 2 P1 缺陷复现（批量 >8 张误报失败）** | ❌ 已复现并定位：`TrimResultPreviewCache` 枚举期修改集合（`MainViewModel.cs:982`）；9/9 输出已写但 UI 显示"处理失败" → Lead 裁决解冻修复 | `v2-run-20261017-2008/15-repro-preview-cache-9/`（result.json/截图/app-log-excerpt） |
| **迭代 3 性能抽测脚本（准备阶段）** | ✅ `artifacts/ui-smoke/perf-v3.ps1`（纯 ASCII/PS5.1）：DryRun 自检 + package 静态基线通过；14 场景冒烟脚本在 D 盘仓库 `-DryRun` 13/13；**未启动应用、未占 GPU** | `artifacts/ui-smoke/perf-v3-20261017-211606/plan.json`、`perf-v3-20261017-211629/result.json`、`v3-dryrun-*/plan.json`（见 §13） |

> ⚠️ **GPU 串行**：19:08 新构建首次复跑超分时，与 ui-dev 的并发真实导出测试同时占用 DirectML，
> 引擎侧抛 `OnnxRuntimeException: [ErrorCode:Fail] bad allocation`（`TileInference.RunAndConsume:139`），
> 冒烟按"处理失败"如实退出（非 UI 缺陷）。**结论：带 `-StartUpscale` 的冒烟必须串行跑**；
> 19:12:56 GPU 空闲后串行复跑即成功（12.0s，ok=True）。
>
> 本阶段按 Lead 要求先交付**骨架 + 可运行的"启动/截图/退出"**；"开始超分"点击与完成轮询也已实现并实测可行。

## 1. 前置条件

- `winapp` CLI ≥ 0.7（本机 0.7.1，`%LOCALAPPDATA%\Microsoft\WindowsApps\winapp.exe`）；
- 已构建的 `src/Lumen.App/bin/**/win-x64/Lumen.exe`（脚本默认选**最新**的一个；也可 `-ExePath` 指定）；
- 本机环境：无 VS、无系统 Windows SDK、无 Dev Mode、无管理员 —— `winapp ui` 与 PrintWindow 均不需要它们；
- Windows PowerShell 5.1（本机无 pwsh）；脚本仅 ASCII，避免 UTF-8 无 BOM 中文解析失败。

## 2. 用法

```powershell
# 基础：空状态深色，启动→断言→截图→退出（最快，约 5s）
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ui-smoke.ps1 -Theme Dark

# 载入图片 + 真实点击"开始超分" + 等完成 + 截图 + 退出
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ui-smoke.ps1 `
    -OpenImage .\spike\winui-smoke\Assets\Square150x150Logo.scale-200.png `
    -StartUpscale -UpscaleTimeoutSeconds 180

# 浅色主题 + 面板截图（深/浅色各跑一次即可得到对比证据）
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ui-smoke.ps1 -Theme Light

# 先构建再冒烟（默认不构建，避免和 ui-dev 抢文件锁）
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ui-smoke.ps1 -Build

# 只启动不关闭，便于人工查看（会打印 PID）
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ui-smoke.ps1 -KeepAlive
```

退出码：`0` = 所有 required 断言通过且无致命错误；`1` = 有 required 断言失败或窗口/启动/完成轮询失败。
结果 JSON：`artifacts/ui-smoke/<时间戳>/result.json`（`-ArtifactDirectory` 可指定固定目录）。

## 3. 参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `-ExePath` | 最新 `src/Lumen.App/bin/**/Lumen.exe` | 指定被测 exe（自动按 `LastWriteTime` 选最新） |
| `-Build` | 关 | 先 `dotnet build src/Lumen.App -c Debug -p:Platform=x64`；qa 默认不抢 ui-dev 的构建 |
| `-OpenImage` | 空 | 设置 `LUMEN_OPEN=<绝对路径>`（UI-DESIGN §8 调试变量） |
| `-Theme` | `Dark` | `LUMEN_THEME=Dark/Light`；`-Theme ''` 表示跟随系统 |
| `-Autostart` | 关 | 设置 `LUMEN_AUTOSTART=1`（需配合 `-OpenImage`；启动后自动超分） |
| `-SaveImage` | 空 | 设置 `LUMEN_SAVE=<绝对路径>`，验证自动导出链路 |
| `-StartUpscale` | 关 | 冒烟中通过 UIA 点击"开始超分"并轮询完成（需 `-OpenImage`） |
| `-UpscaleTimeoutSeconds` | 300 | 处理完成最长等待 |
| `-WindowTimeoutSeconds` | 60 | 等窗口超时 |
| `-ArtifactDirectory` | `artifacts/ui-smoke/<yyyyMMdd-HHmmss>` | 截图与 result.json 目录 |
| `-NoScreenshot` | 关 | 跳过截图（只做窗口+控件断言） |
| `-KeepAlive` | 关 | 不关闭进程（打印 PID 供人工检查） |
| `-DotnetRoot` | `C:\Users\0x7c14\dotnet10` | `-Build` 时找不到 dotnet 的兜底路径 |

## 4. 脚本做了什么

1. **解析依赖**：找 `winapp`、解析 exe、建 artifact 目录；
2. **可选构建**（`-Build`）；
3. **启动**：把 `LUMEN_OPEN/LUMEN_THEME/LUMEN_AUTOSTART/LUMEN_SAVE` 注入子进程环境（结束后恢复父进程环境），
   `Start-Process` 工作目录 = exe 目录（资源/图标按应用目录解析）；
4. **按 PID 找窗口**：`winapp ui list-windows --json` 过滤 `processId == 自己启动的 PID`
   （本机可能有 ui-dev 的多个 Lumen 实例，不能按进程名/标题猜）；拿不到时回退 `Process.MainWindowHandle`；
5. **断言窗口标题** = `Lumen`；
6. **取一次 UIA 快照**（`winapp ui inspect --json -d 10 -w <hwnd>`），把树拍平成列表，按
   `AutomationId`（首选）→ `Name`（当前中文标签，脚本用 `[regex]::Unescape('\uXXXX')` 保持 ASCII）→
   `NameRegex`（如尺寸文本 `1600 × 1067 → 6400 × 4268`）匹配 19 个关键控件；
   每个控件记录 `found / via / type / offscreen / detail`；
7. **截图**：`PrintWindow(PW_RENDERFULLCONTENT)` 截窗口（失败回退 CopyFromScreen），像素采样自检
   （<8 种颜色判为疑似黑屏），输出 `01-initial.png`；
8. **可选交互**：`-StartUpscale` 时用 `winapp ui invoke` 点击主按钮（先试 `BtnStart`，再试
   `开始超分`/`开始超分或取消处理`），2s 后截 `02-processing.png`；之后每 2s 拉一次 UIA 快照，
   直到出现"处理完成"（或"处理失败/引擎不可用"），截 `03-completed.png`；
9. **退出**：对**自己启动的窗口**发 `WM_CLOSE`，等 10s；未退出则强杀（并记录 note）；
   异常路径 `finally` 也会清理自己启动的进程；**绝不会 kill 其他 Lumen 实例**；
10. **输出** `result.json` + 控制台摘要 + 退出码。

## 5. result.json 字段

```jsonc
{
  "ok": true,                       // 所有 required 断言通过 + 无致命错误 + 处理未超时/失败
  "startedAt": "2026-10-17T19:02:41.0000000+08:00",
  "elapsedSeconds": 14.4,
  "exePath": "C:\\...\\src\\Lumen.App\\bin\\Debug\\...\\Lumen.exe",
  "pid": 15184,
  "hwnd": 5376040,
  "windowTitle": "Lumen",
  "screenshots": ["...\\01-initial.png", "...\\02-processing.png", "...\\03-completed.png"],
  "assertions": [
    { "key": "BtnStart", "automationId": "BtnStart", "required": true,
      "found": true, "via": "name", "type": "Button", "detail": "开始超分", "offscreen": false }
  ],
  "missingAutomationIds": ["BtnOpenImage", "..."],   // ui-dev 待补/待复验的 ID；新构建后应为空
  "fallbackControls": ["BtnStart", "..."],           // 靠 Name 兜底命中的控件（新构建后应为空）
  "phases": { "initial": "ok", "upscale": "completed", "close": "exited" },
  "notes": ["..."]
}
```

## 6. 控件定位契约（AutomationId 清单）

脚本内置契约（`scripts/ui-smoke.ps1` 中 `$contract`）；`Required=yes` 的缺失会让 `ok=false`。
ui-dev 已在 19:03 的 `EditorView.xaml` 中补齐全部 ID（31 个），**新构建后预期 `missingAutomationIds` 为空**。

| AutomationId | 控件 | Required | 说明 |
|---|---|---|---|
| `BtnOpenImage` | 工具栏"打开图片" | ✅ | |
| `BtnEmptyOpen` | 空状态"打开图片" | | 与工具栏区分用 |
| `BtnExport` | "导出" | ✅ | |
| `BtnFit` | 工具栏"适应窗口" | ✅ | |
| `BtnFitCanvas` | 画布浮动条"适应窗口" | | |
| `BtnZoom100` | "100%" | ✅ | |
| `BtnZoomIn` / `BtnZoomOut` | 放大/缩小 | | 仅载入后可见 |
| `BtnCompare` | "对比" ToggleButton | ✅ | |
| `BtnStart` | 主按钮（开始超分/取消） | ✅ | Name 绑定 `StartButtonText`，UIA 可区分状态 |
| `CanvasViewer` | 画布 CompareViewer | | 建议同时加 `AutomationProperties.Name` |
| `BtnCollapsePanel` / `BtnExpandPanel` | 折叠/展开参数面板 | ✅ / | |
| `CmbModel` | 模型下拉 | ✅ | |
| `RadioScale` | 倍率 RadioButtons 组 | ✅ | |
| `CmbFormat` | 输出格式 | ✅ | |
| `SliderQuality` | 质量滑块 | | 仅 JPEG/WebP 显示 |
| `TogglePreserveAlpha` | 保留透明通道 | ✅ | |
| `ExpanderAdvanced` | "高级" Expander | ✅ | 注意统一命名（原 AdvancedExpander 已废弃） |
| `CmbBackend` / `ToggleFp16` / `NumTileSize` / `NumTilePadding` | 高级面板 | | 折叠时 offscreen |
| `TxtStatus` / `TxtStatusDetail` / `TxtDimensions` / `TxtBackend` | 状态条文本 | ✅ / ✅ / ✅ / | |
| `BtnCancel` | 状态条"取消" | | 仅处理中可见 |
| `ProgressBarStatus` / `ProgressBarOverlay` | 进度条 | | |
| `InfoBarStatus` | InfoBar | | 加 `AutomationId` 便于断言提示 |

> 当前（旧构建）除 `CompareToggle`（来自 `x:Name`）与 `AdvancedExpander` 外都没有 AutomationId，
> 脚本用名称兜底跑通了；这正是本文档要 ui-dev 补 ID 的原因。

## 7. 截图证据与自检

| 文件 | 状态 | 说明 |
|---|---|---|
| `artifacts/ui-smoke/phase1-check/01-initial.png` | ✅ | 空状态 Dark（旧构建，1306×853，read_image 人工确认） |
| `artifacts/ui-smoke/automation-id-check/01-initial.png` | ✅ | 19:07 新构建空状态（AutomationId 断言 19/19） |
| `artifacts/ui-smoke/automation-id-check-loaded/01-initial.png` | ✅ | 新构建载入状态（含 BtnZoomIn/Out 等 ID） |
| `artifacts/ui-smoke/final-empty-check/01-initial.png` | ✅ | 19:15 最终 Debug 构建空状态（全部 AutomationId，4.2s，不占 GPU） |
| `artifacts/ui-smoke/final-build-empty/01-initial.png` | ✅ | **19:17 源码冻结版（Release 19:17:37）空状态**：ok=True 4.3s，missing=[] |
| `artifacts/ui-smoke/final-build-loaded/01-initial.png` | ✅ | **19:17 源码冻结版载入态**：ok=True 4.3s，含 BtnZoomIn/Out，missing=[]（read_image 人工确认 228% 适应窗口、300×300→1200×1200 预估） |
| `artifacts/ui-smoke/final-upscale-check/01-initial.png` | ✅ | 新构建载入 300×300 图片（228% 适应窗口，输入/输出预估正确） |
| `artifacts/ui-smoke/final-upscale-check/02-processing.png` | ✅ | 处理中（进度覆盖层） |
| `artifacts/ui-smoke/final-upscale-check/03-completed.png` | ✅ | 完成：InfoBar「处理完成 已生成 1200×1200，输出=FP16」、对比模式、分隔线、"原图/处理后"、状态条 `处理完成 DirectML · 用时 3.0s · 4 块`（read_image 人工确认） |
| `artifacts/ui-smoke/loaded-upscale-check/*.png` | ✅ | 旧构建同链路对照证据（3 张） |
| `artifacts/ui-smoke/probe-inspect-*.json` | ℹ️ | 评审期间采集的 UIA 树样例（含全部控件 Name/ID） |

截图自检：脚本会统计采样到的不同颜色数（<8 视为疑似黑屏并在 `notes` 标注）；
由于 `PrintWindow` 对透明/Acrylic 区域可能返回 alpha=0，本脚本对 Lumen（Mica 背景）实测正常。

## 8. 已知限制 / 注意事项

1. **并发实例**：ui-dev 迭代时会启动/清理自己的 Lumen。脚本按 PID 归属窗口、只关自己启动的进程；
   但"其他进程按名字 kill 全部 Lumen"（19:00 前的旧截图脚本行为）会让冒烟出现
   `Window disappeared before the initial screenshot`。现在 ui-dev 已改为只 kill 自己的 PID。
2. **`winapp ui` 是 UIA**：要求交互式桌面会话且窗口未最小化；本机 Session 1 满足。锁屏/RDP 最小化时失败。
3. **完成判定**：旧构建靠"处理完成"文本名/Snapshot 轮询；新构建建议改用 `TxtStatus` + `-p IsEnabled`
   （`BtnCancel` 消失 / `BtnExport` 变可用）减少对文案的依赖。
4. **原生 Picker 未自动化**：`FileOpenPicker/FileSavePicker` 需要单独窗口（`PickerHost`）和文件名输入，
   本脚本不点它们；导出链路用 `-SaveImage`（`LUMEN_SAVE`）验证。
5. **浅色主题**：`-Theme Light` 只设 `RootGrid.RequestedTheme`；若对话框/弹层主题不一致（见
   `docs/UI-REVIEW.md` 存疑 1），截图可暴露该问题。
6. **不要修改 `src/**`**：本脚本只读 exe + `winapp ui`，不改源码；构建产物由 ui-dev/Lead 负责。

## 9. 复跑清单（UI 稳定后）

1. 等 ui-dev 给出新 `Lumen.exe` 路径（或在 Lead 通知后自行 `dotnet build src/Lumen.App -p:Platform=x64`）；
2. `scripts/ui-smoke.ps1 -Theme Dark` → 期望 `ok=true`、`fallbackControls` 空、`missingAutomationIds` 空；
3. `scripts/ui-smoke.ps1 -OpenImage <测试图> -StartUpscale` → 期望 `phases.upscale=completed`；
4. `scripts/ui-smoke.ps1 -Theme Light` 各跑一遍，与 `docs/screenshots/` 的人工截图对照；
5. 把新的 `artifacts/ui-smoke/<stamp>/result.json` 附到验收说明里。

## 10. 故障排查

| 现象 | 原因 | 处理 |
|---|---|---|
| `winapp CLI not found` | PATH 未刷新 | `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`，或确认 `%LOCALAPPDATA%\Microsoft\WindowsApps\winapp.exe` |
| `No Lumen.exe ... Build src/Lumen.App first` | 没有构建产物 | `-Build`，或让 ui-dev/Lead 构建后重跑 |
| `Lumen exited during startup with code N` | 启动即崩（缺模型/设置损坏） | 看 `%LOCALAPPDATA%\Lumen\logs\lumen.log`；确认 `models/` 存在 |
| `Window disappeared before the initial screenshot` | 其他进程按名杀掉了所有 Lumen，或应用崩溃 | 协调 ui-dev 只 kill 自己的 PID；重跑 |
| `Processing did not report completion within Ns` | 大图/模型加载慢；或引擎报错弹 InfoBar | 加大 `-UpscaleTimeoutSeconds`；看截图与 app log；用 `-NoScreenshot` 先快速定位 |
| 截图疑似黑屏（notes 提示 blank） | 窗口被遮挡/最小化/桌面锁定 | 确保交互式桌面；`-NoScreenshot` 先验证断言 |
| `result.json` 的 `missingAutomationIds` 非空 | ui-dev 还没加/构建旧 | 等新构建；或把清单发给 ui-dev |

---

## 11. 迭代 2（v2）：冒烟矩阵与脚本骨架（准备阶段，2026-10-17 20:00）

> 状态：**脚本骨架已完成**，`scripts/ui-smoke.ps1` 已按场景化重构（向后兼容 v1），
> 11 个 v2 场景全部通过 `-DryRun` 自检（PS 5.1 解析 OK、纯 ASCII、fixtures/环境变量/计划 JSON 正确）。
> 按 Lead 要求**尚未启动应用**；等通知"UI 可构建/源码冻结"后按矩阵串行执行并产出证据。

### 11.1 场景矩阵（`-Scenario <name>`）

| # | Scenario | 目标 | 动作 | 环境变量要点 | GPU | 关键断言/产物 |
|---|---|---|---|---|---|---|
| 1 | `empty` | v1 回归：空状态 | 仅加载 | `LUMEN_THEME` | 否 | v1 18 控件 + 窗口标题 |
| 2 | `single` | v1 回归：单图/单图超分 | `-StartUpscale` 可选 | `LUMEN_OPEN`、`LUMEN_AUTOSTART` | 可选 | 控件 + 完成状态 |
| 3 | `batch-load` | 队列填充不处理 | 仅加载 | `LUMEN_OPEN_MANY`（2 有效 + 1 损坏） | 否 | 队列控件、`N 张` 汇总、输入文件名出现在 UIA |
| 4 | `batch-run` | 批量自动处理 + 失败隔离 | `wait-batch` | `LUMEN_OPEN_MANY`+`LUMEN_SAVE_DIR`+`LUMEN_AUTOSTART`+`LUMEN_BACKEND=auto` | **是（串行）** | 汇总 `成功 N / 失败 M`、失败项存在、输出目录文件数+尺寸 |
| 5 | `batch-cpu` | CPU 兼容批量 | `wait-batch` | 同上 + `LUMEN_BACKEND=cpu` | 否 | 汇总 + `TxtBackend` 含 CPU + 输出尺寸 |
| 6 | `batch-skip` | 冲突策略 `Skip`（Lead 裁决 C-1） | `wait-batch` | 1 有效图 + `SAVE_DIR` + `AUTOSTART` + `LUMEN_BACKEND=cpu` + `LUMEN_CONFLICT=skip` + `LUMEN_PATTERN={name}_smoke` | 否 | 预置同名输出文件：断言队列项显示"跳过"、汇总含 `跳过 N`、**预置文件字节不变（不写入/不覆盖）**；Skipped 的 `OutputPath` 为拟路径 |
| 7 | `cpu-single` | CPU 单图 | `wait-single` | `LUMEN_OPEN`+`LUMEN_BACKEND=cpu`+`AUTOSTART` | 否 | 完成 + CPU 标签 |
| 8 | `gpu-single` | GPU 单图 | `wait-single` | `LUMEN_OPEN`+`LUMEN_BACKEND=gpu`+`AUTOSTART` | **是（串行）** | 完成 + DirectML 标签 |
| 9 | `compat-empty-models` | 缺模型不崩溃 | 仅加载 | `LUMEN_MODELS_ROOT=<空目录>` | 否 | 中文模型提示、开始按钮禁用、进程存活 |
| 10 | `compat-bad-inputs` | 坏图失败隔离 | `wait-batch` | 有效;损坏;有效 + `SAVE_DIR`+`AUTOSTART` | **是（串行）** | 汇总含失败、其余成功、输出 2 个文件 |
| 11 | `compat-missing-input` | 输入不存在 | 仅加载 | `LUMEN_OPEN_MANY=<不存在路径>` | 否 | 中文错误提示、不崩溃 |
| 12 | `system-info` | 系统信息对话框 | `system-info` | 无 | 否 | `DlgSystemInfo` + OS/CPU/GPU/DirectML/模型目录/日志路径字段 + 截图 |

补充运行维度（与任意场景组合）：
- `-Theme Dark|Light`：同一场景深浅色各跑一次（v2 验收要求深/浅两组截图）；
- `-Backend auto|gpu|cpu`：覆盖 `LUMEN_BACKEND`；
- `-ModelsRoot <dir>`、`-Pattern <token>`、`-ConflictPolicy rename|overwrite|skip`、`-Scale 2|3|4`；
- `-SaveDir <dir>`（批量输出目录；默认 `<artifact>/out`）。

### 11.2 新增参数（`scripts/ui-smoke.ps1`）

| 参数 | 默认 | 说明 |
|---|---|---|
| `-Scenario` | `single` | 上表 11 个场景之一 |
| `-DryRun` | 关 | 只准备 fixtures/环境并输出 `plan.json`，**不启动应用**（准备阶段自检用） |
| `-OpenMany` | 空 | `;` 分隔的多张输入；不传时自动生成 fixtures |
| `-SaveDir` | 空（batch 场景自动 `<artifact>/out`） | 批量输出目录 |
| `-Backend` | 空 | `auto`/`gpu`/`cpu` → `LUMEN_BACKEND` |
| `-Pattern` / `-Scale` / `-ConflictPolicy` | 空 | `LUMEN_PATTERN` / `LUMEN_SCALE` / `LUMEN_CONFLICT` |
| `-ModelsRoot` | 空 | `LUMEN_MODELS_ROOT` 覆盖（兼容性用例） |
| `-FixturesRoot` / `-UseFixtures` | 空 | 指定测试图来源；否则自动在 anime 样本 / `assets/samples` / spike 资源中找**可解码**图片 |
| `-AllowGpuBusy` | 关 | 推理场景默认拒绝在 GPU 忙时运行（串行保护） |
| `-ExePath` / `-Build` / `-KeepAlive` / `-NoScreenshot` / `-UpscaleTimeoutSeconds` / `-WindowTimeoutSeconds` | 同 v1 | 复用 |

### 11.3 GPU 串行保护（真实推理场景）

脚本在启动前检查：`nvidia-smi --query-gpu=utilization.gpu,memory.used` + 进程列表
（`Lumen` / `Lumen.Cli` / `python`）。任一忙（util ≥30% 或有其他实例/CLI/Python 进程）→
**拒绝执行**推理场景，退出码 `3` 并写 `result.json`（`phases.gpu-guard=refused`）。
等待 GPU 空闲或显式 `-AllowGpuBusy`（不推荐）。
非推理场景（`empty/single(无超分)/batch-load/batch-cpu/cpu-single/compat-*/system-info`）不受影响。
DryRun 会打印当前 GPU 状态（实测：engine-dev 跑 `Lumen.Cli` 时 util 91–100%，guard 正确识别）。
CPU 模式用 `LUMEN_BACKEND=cpu`（fp32），不占 GPU，可在 GPU 忙时执行（但会与 CPU 侧 benchmark 抢 CPU，仍建议错峰）。

### 11.4 v2 控件 AutomationId 契约（需 ui-dev 落实；脚本 `$contractV2`）

分组（`RequiredFor` 表示"在哪些场景必须存在"）：

- 队列：`QueueList`（ListView）、`TxtQueueSummary`（底部统计 `N 张 · 已完成 X / 失败 Y`）、
  `BtnAddImages`、`BtnAddFolder`、`BtnClearQueue`；队列项 **UIA Name 约定 `"文件名 · 状态"`**（如 `a.png · 失败`），
  脚本用文件名子串 + 失败/完成关键字断言；
- 处理模式：`CmbComputeMode`（或 `RadioComputeMode`/`SegComputeMode`）、`TxtComputeModeHint`、`TxtBackend`；
- 批量输出：`TxtOutputDirectory`、`BtnPickOutputDir`、`BtnClearOutputDir`、`TxtFileNamePattern`、
  `CmbConflictPolicy`、`ToggleOpenOutputFolder`；
- 模型：`CmbModel`（保留）、`ToggleShowGeneralModels`；
- 状态条/反馈：`TxtBatchProgress`（`第 N / M 张`）、`TxtStatus`、`TxtStatusDetail`、`InfoBarStatus`、
  `ProgressBarStatus`/`ProgressBarOverlay`、`BtnCancel`；
- 系统信息：`BtnSystemInfo`、`DlgSystemInfo`、`TxtSysOs`、`TxtSysCpu`、`TxtSysGpu`、`TxtSysDirectMl`、
  `TxtSysModels`、`TxtSysLogPath`、`BtnOpenLogFolder`、`BtnCopySystemInfo`、`BtnOpenModelsFolder`；
- 主按钮：优先 `BtnStartAll`（队列 >1 时文案"开始处理全部"），兼容 `BtnStart`；`AutomationProperties.Name`
  必须随状态变化（`开始超分`/`开始处理全部`/`取消`）。

### 11.5 需要 ui-dev 支持的调试环境变量（迭代 2）

| 变量 | 用途 | 状态 |
|---|---|---|
| `LUMEN_OPEN` / `LUMEN_OPEN_MANY` | 单张 / 多张（`;` 分隔）自动载入 | 规格 §5.2.8；待实现 |
| `LUMEN_SAVE_DIR` | 批量输出目录（脚本默认 `<artifact>/out`） | 待实现 |
| `LUMEN_BACKEND=auto\|gpu\|cpu` | 覆盖处理模式（CPU 兼容验证） | 待实现 |
| `LUMEN_PATTERN` | 覆盖文件名模板 | 待实现 |
| `LUMEN_AUTOSTART=1` | 自动开始全部（批量） | 待实现 |
| `LUMEN_CONFLICT=rename\|overwrite\|skip` | 覆盖冲突策略（Lead 已要求）；`skip` 由 `batch-skip` 场景覆盖 | 待实现（Lead 已确认） |
| `LUMEN_FORCE_NO_DML=1` | 自动模式下模拟无 DirectML → 自动 CPU + 提示（兼容性用例） | 待实现（Lead 已确认） |
| `LUMEN_SCALE` / `LUMEN_THEME` / `LUMEN_SAVE_TO` | 沿用 v1 | 已有 |
| `LUMEN_MODELS_ROOT` | 模型目录覆盖（Core `AppPaths` 已支持） | 已有 |

### 11.6 Fixtures 生成规则与已知数据坑

脚本自动在 `<artifact>/fixtures/` 生成：
- `fixture-1/2.png`：从**动漫 PNG 源**（优先 `assets/samples/anime/*.png`，真 PNG）解码后
  **归一化为长边 ≤512px 的 PNG**（缩略语料，保证 CPU/GPU 冒烟耗时可控；例如 512×354）；
- `corrupt.png`：28 字节非图片内容（坏图隔离用例）；
- `empty-models/`：空目录（缺模型兼容性用例）。

数据坑记录：`tools/model-pipeline/samples/anime/*.jpg` 实际内容是 **RIFF/WebP**（扩展名与内容不符），
`System.Drawing`（GDI+）无法解码 —— 脚本按可解码性过滤，不依赖该目录；应用/引擎用 Skia 按内容解码不受影响。
仅本脚本的尺寸校验依赖 GDI+（PNG/JPEG/BMP）；WebP 输出只做数量校验。

### 11.7 输出目录校验规则（批量场景）

`Test-BatchOutputs`：比较 `LUMEN_SAVE_DIR` 内图片文件数 vs 可解码输入数（损坏图不计），
并用 `System.Drawing` 读回每张输出尺寸，与"输入尺寸 × 目标倍率"做多重集比较（顺序无关）。
WebP 输出（GDI+ 不可读）时只做数量校验并在 `notes` 说明。

`batch-skip` 场景额外规则（依据 Lead 对 C-1 的裁决：Completed=实际路径、**Skipped=拟输出路径（未写入）**、
Failed/Canceled=null）：
- 启动前在输出目录预创建"按模板计算出的目标文件"（内容为 sentinel 字节，记录 SHA256）；
- 批处理完成后断言：该文件仍存在且 SHA256 不变、输出目录图片数仍为 1（即未被写入/覆盖）；
- UIA 断言：队列项出现"跳过"、汇总文本含 `跳过 N`。
脚本已实现 `skipSentinel`（`result.json` 记录 path/hashBefore）与上述三条断言。

### 11.8 DryRun 自检结果（准备阶段证据）

2026-10-17 20:05 本机执行 **12 个场景** `-DryRun`：**12/12 exit 0**，PS 5.1 解析 OK，纯 ASCII（0 非 ASCII 字节）。
证据：`artifacts/ui-smoke/v2-dryrun-<scenario>/plan.json`（含 env/fixtures/expectedOutputs/plannedChecks/GPU 状态；
`batch-skip` 的 plan 含 `skipPolicy=true` + `skipSentinelPath`）。
示例（`batch-run`）：`LUMEN_OPEN_MANY` 3 项（2 有效 + 1 损坏）、`LUMEN_SAVE_DIR`、`AUTOSTART=1`、
`expectedOutputs=2`、GPU guard 显示 `busy=True`（engine-dev 的 `Lumen.Cli`/python 占用）→ 正式跑时会拒绝并等待。
示例（`batch-skip`）：fixture 512×354 归一化 PNG，sentinel `out/fixture-1_smoke.png` 已预置。

### 11.9 拿到 UI 新构建后的执行顺序（建议）

1. 先跑非推理场景（可并行 GPU 任务，但建议等源码冻结）：`empty`、`batch-load`、`compat-empty-models`、
   `compat-missing-input`、`system-info`（各约 5–10s）；
2. 与 engine-dev 协调 GPU 空闲后串行跑：`gpu-single`、`batch-run`、`compat-bad-inputs`（各约 15–120s）；
3. `batch-cpu`、`cpu-single`（CPU 慢，单张 300×300 约十几秒～1 分钟；超时默认 600s）；
4. 深浅色各跑一遍关键场景（`empty`/`batch-load`/`system-info`）以匹配 v2 截图验收；
5. 任何失败：先看 `result.json` 的 `assertions`/`notes`/`missingAutomationIds`，再读
   `%LOCALAPPDATA%\Lumen\logs\lumen.log`；可复现缺陷直接报 ui-dev/engine-dev 并附 artifacts 路径。

---

## 12. 迭代 2 v2 实测结果（源码冻结版 20:01:08 Release，2026-10-17 20:01–20:20）

执行命令（每个场景）：
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ui-smoke.ps1 `
  -Scenario <name> -Theme Dark -ExePath <Release Lumen.exe> -ArtifactDirectory <dir>
```
GPU 场景启动前自动检查 `nvidia-smi`（util≥30%）+ `Lumen/Lumen.Cli/python` 进程；忙则拒绝（exit 3）。

### 12.1 矩阵结果（`artifacts/ui-smoke/v2-final-20261017-2020/`）

| # | 场景 | 结果 | 耗时 | 断言/截图 | 关键证据 |
|---|---|---|---|---|---|
| 1 | `empty`（Dark） | ✅ ok=True | 7s | 19 / 1 | 空状态 + 19 v1 控件全部 automationId |
| 2 | `empty`（Light） | ✅ ok=True | 6s | 19 / 1 | 浅色主题正常 |
| 3 | `single`（静态） | ✅ ok=True | 6s | 19 / 1 | 无图片单图空态 |
| 4 | `batch-load` | ✅ ok=True | 9s | 53 / 1 | 队列 3 项（2 有效+1 损坏）文件名在 UIA 中可见 |
| 5 | `compat-empty-models` | ✅ ok=True | 9s | 53 / 1 | 中文"未找到可用模型"提示 + 开始禁用 + 不崩溃 |
| 6 | `compat-missing-input` | ✅ ok=True（修复版复跑） | 4s | 53 / 2 | 首次因重建竞态失败；修复版（20:18:46）单独复跑通过，状态文案"路径不存在/不可用"（P2-3） |
| 7 | `compat-no-dml` | ✅ ok=True | 11s | 53 / 2 | `LUMEN_FORCE_NO_DML=1` → 自动切 CPU + InfoBar 提示 + `TxtBackend` 含 CPU |
| 8 | `system-info` | ✅ ok=True | 13s | 58 / 2 | 系统信息 7 字段全部 AutomationId 命中 + 截图 |
| 9 | `compat-bad-inputs`（CPU） | ✅ ok=True | 34s | 52 / 2 | summary `成功 2 / 失败 1 · 用时 26.7s`，2 个输出尺寸正确 |
| 10 | `batch-skip` | ✅ ok=True | 9s | 54 / 2 | summary `成功 0 / 失败 0 / 跳过 1`；sentinel SHA256 不变、目录仅 1 文件 |
| 11 | `batch-cpu` | ✅ ok=True | 28s | 51 / 2 | summary `成功 2 / 失败 0 · 用时 21.6s`，输出 2/2 尺寸正确，`TxtBackend`=CPU |
| 12 | `cpu-single` | ✅ ok=True | 8s | 52 / 2 | 单图 CPU 完成、状态条 CPU |
| 13 | `gpu-single` | ✅ ok=True | 7s | 51 / 2 | 单图 GPU 完成 |
| 14 | `batch-run`（GPU） | ✅ ok=True | 9s | 51 / 2 | summary `成功 2 / 失败 1 · 用时 2.5s`，输出 2/2 尺寸正确 |

**合计：14 场景中 13 通过**（唯一失败为 ui-dev 同时重建 exe 时启动竞态，非产品缺陷；同类已在此前 run 通过）。

### 12.2 P1 缺陷复现与修复复验（批量 >8 张误报失败）

- **修复前复现**（Release 20:01:08）：`-Scenario batch-run -OpenMany "<9 张图;分隔>" -SaveDir <out>`（9 张动漫 PNG，GPU 串行）
  - 引擎 `批处理结束：共 9 张，成功 9，失败 0`、输出目录 9 个文件全部写出；
  - 但 UI `TxtStatus='处理失败'`、`TxtStatusDetail='Collection was modified; enumeration operation may not execute.'`，
    完成 InfoBar 不出现；App log 栈：`MainViewModel.TrimResultPreviewCache` **line 982** →
    `ApplyBatchResults` **1272** → `StartBatchAsync` **1089**；
  - 证据：`artifacts/ui-smoke/v2-run-20261017-2008/15-repro-preview-cache-9/`
    （`result.json`、`15-repro-failure.png`、`ui-dump.json`、`app-log-excerpt.txt`）。
- **修复版复验（Release 20:20:15，`artifacts/ui-smoke/v2-fixed-9img-20261017-2023/`）**：✅ 通过
  - `ok=True`，64s（UI 端到端），`phases={"initial":"ok","batch":"completed","summary":"成功 9 / 失败 0 · 用时 54.6s","close":"exited"}`；
  - 输出 9/9，尺寸多重集校验 `dimsOk=True`（4096×4096 / 7680×9308 / 7680×5304 等）；
  - App log：`批处理结束：共 9 张，成功 9，失败 0`，**无新增 `Collection was modified`**；
  - 截图 `03-completed.png`（read_image 人工确认）：InfoBar「批量处理完成 成功 9 / 失败 0 · 用时 54.6s」+
    **"打开输出文件夹"** 按钮、队列 9 项全部"完成 · N s"、状态条"批量完成 成功 9 / 失败 0 · 用时 54.6s"、画布对比模式；
  - 同轮验证"调试变量不写回 settings.json"保护：9 张用例（`LUMEN_PATTERN/_SAVE_DIR/_BACKEND/_AUTOSTART` 均注入）
    关闭后 `%LOCALAPPDATA%\Lumen\settings.json` 仍为干净默认（`BatchOutputDirectory=null`、模板默认、`ComputeMode=auto` 等）。
- 根因/最小修复：见 `docs/UI-REVIEW.md` §1.2 P1-v2-1（已修并关闭）。

### 12.3 其它缺陷（P2，记录不改）

见 `docs/UI-REVIEW.md` §1.2 P2-v2-1…8。本轮实际修复范围（ui-dev 20:18:46 构建）：
**P1-v2-1（P1）+ P2-v2-1（失败时保留打开输出文件夹）+ P2-v2-3（missing-input 状态文案）**；
其余 P2 由 Lead 决定本轮不修并记录（含 P2-v2-7 `_thumbnailGate.Dispose` 竞态）。

### 12.4 修复版复跑（最终 Release 20:20:15）

| 用例 | 命令 | 期望 | 结果 |
|---|---|---|---|
| 9 张批量（P1 复验） | `-Scenario batch-run -OpenMany "<9 张>" -SaveDir <out>` | summary `成功 9 / 失败 0`、完成 InfoBar 出现、日志无 `Collection was modified`、9 个输出 | ✅ **ok=True，64s；`成功 9 / 失败 0 · 用时 54.6s`；输出 9/9 dimsOk；InfoBar+打开输出文件夹截图确认；日志无 Collection 异常**（`v2-fixed-9img-20261017-2023/`） |
| `batch-load` | `-Scenario batch-load` | ok=True（53 断言） | ✅ ok=True，8s，53 断言，missing=[]（`v2-final-fixed-20261017-2025/`） |
| `batch-skip` | `-Scenario batch-skip` | ok=True，sentinel 不变 | ✅ ok=True，8s，54 断言，`跳过 1`，sentinel SHA256 不变 |
| `system-info` | `-Scenario system-info` | ok=True（58 断言） | ✅ ok=True，10s，58 断言 |
| `compat-missing-input` | `-Scenario compat-missing-input` | ok=True + 状态文案"路径不存在/不可用"（P2-3 修复） | ✅ ok=True，4s，53 断言（含新增 `MissingInputStatusText` 断言通过） |

修复版也顺带确认：多实例并存（ui-dev 自测实例）时按 PID 归属窗口正常，脚本未干扰对方进程；
修复版新增"调试变量不写回 settings.json"保护经 9 张用例验证有效（见 §12.2）。

### 12.6 最终矩阵汇总（QA 结论）

- **14 场景矩阵：14/14 通过**（13 场景用 20:01/20:20 冻结版直接通过；`compat-missing-input` 首轮因并发重建
  exe 启动失败，修复版 20:20:15 单独复跑通过）。
- **P1 复验：通过**（9 张 batch-run：成功 9/失败 0、InfoBar+打开输出文件夹、9 输出尺寸正确、无 Collection 异常）。
- **P2 修复复验：通过**（P2-v2-1 打开输出文件夹保留、P2-v2-3 missing-input 文案）。
- **未修 P2（Lead 裁决记录）**：P2-v2-2 单项失败即时 InfoBar、P2-v2-4 自定义目录为空静默回退、
  P2-v2-5 跳过数未入 QueueSummary、P2-v2-6 缩略图全尺寸解码、P2-v2-7 `_thumbnailGate.Dispose` 竞态、P2-v2-8
  `IsLoadingPreview` 边界；均已在 `docs/UI-REVIEW.md` §1.2 记录风险等级（低～中，不阻塞本轮验收）。
- 证据根目录：`artifacts/ui-smoke/v2-final-20261017-2020/`（14 场景）、`artifacts/ui-smoke/v2-fixed-9img-20261017-2023/`
  （P1 修复复验）、`artifacts/ui-smoke/v2-final-fixed-20261017-2025/`（修复版受影响场景）、
  `artifacts/ui-smoke/v2-run-20261017-2008/`（P1 修复前复现证据）。

### 12.5 环境/并发注意（本轮实测教训）

- **重建窗口期不要跑冒烟**：ui-dev 重建 exe 时覆盖 `bin\Release\...\Lumen.exe`，若恰在 `Start-Process`
  窗口会导致 "Lumen exited during startup with code -1"（本轮 `compat-missing-input` 唯一失败即此原因）；
  复跑前确认 exe 时间戳稳定。
- 多实例：脚本按 PID 归属窗口、只关自己启动的实例；ui-dev/Lead 的实例不受影响。
- GPU 串行：`batch-run`/`gpu-single` 前先确认 GPU 空闲；与 engine-dev 的 `Lumen.Cli` 并发会
  `bad allocation`（v1 已有记录），脚本 guard 已能拒绝。

---

## 13. 迭代 3（本体优化）性能抽测与发布包检查（准备阶段，2026-10-17 21:15–21:20）

> 仓库根已迁移到 `D:\Project\Lumen`（旧 `C:\Users\0x7c14\Documents\deepseek-harness\default-workspace` 已清空）。
> 本文件、`scripts/ui-smoke.ps1`、`artifacts/ui-smoke/**` 都相对新仓库根；旧 v1/v2 证据仍在 C 盘旧目录
> （`C:\...\default-workspace\artifacts\ui-smoke\`，只读留档，见 §12 路径）。
> 本阶段只做准备：**不启动应用、不占 GPU**；等 Lead 通知 UI/Core 冻结后跑真实测量。

### 13.1 新增脚本：`artifacts/ui-smoke/perf-v3.ps1`（独立测量，不动冻结中的冒烟矩阵）

- 纯 ASCII / PS5.1 / `#requires -Version 5.1`；与 `scripts/ui-smoke.ps1` 解耦，避免改动冻结的 14 场景矩阵。
- 模式（`-Mode`）：`all`（默认）| `startup` | `single` | `batch9` | `scale` | `package`。
- 参数：`-ExePath`（默认 `artifacts\Lumen\Lumen.exe`，缺失时取最新 `src\Lumen.App\bin\...\Release\Lumen.exe`）、
  `-ArtifactDirectory`（默认 `artifacts\ui-smoke\perf-v3-<时间戳>`）、`-ExpectationsPath`（默认 `artifacts/ui-smoke/perf-v3-expectations.json`）、
  `-StartupSamples 5`、`-BatchCount 9`、`-TolerancePercent 15`、`-AllowGpuBusy`、`-DryRun`、`-KeepAlive`、`-TimeoutSeconds 900`。
- 测量口径：

| 模式 | 测量内容 | 判定 |
|---|---|---|
| `startup` | 从 `Start-Process` 到主窗口出现（按 PID 归属窗口），≥5 次；中位/最小/最大/均值 | 仅记录；与 PERFORMANCE §8/UI-NOTES 数字交叉核对 |
| `single` | 1024×1024 动漫 PNG → 4x 单图，UI 端到端墙钟（启动→窗口→app log「批处理结束」）+ 输出尺寸 | 输出必须 = 4096×4096 |
| `batch9` | 9 张动漫图（脚本从 `assets/samples/anime` 复制生成）→ 批量墙钟、峰值 WorkingSet、完成后 WorkingSet | 输出数 = 9 且日志 `成功 9 / 失败 0` |
| `scale` | 同一 1024² 图分别 2x/3x；输出尺寸 | 2x=2048×2048、3x=3072×3072 |
| `package` | `artifacts\Lumen`：存在性、exe、文件数、MB、`*.pdb`、`DirectML.Debug*`/`Lumen*.Debug.dll`、`createdump.exe` 提示、模型清单 | `noPdb`、`noDebugDll`、`exePresent` + 体积/文件数记录（与 README/ACCEPTANCE 核对） |

- 完成判定：读 `%LOCALAPPDATA%\Lumen\logs\lumen.log` 的新增行「批处理结束：共 N 张，成功 S，失败 F，跳过 K，取消 C，耗时 M ms」，
  用 `ExpectCount` 匹配（≥1），避免误读旧日志；同时按 200ms 采样 `WorkingSet64`/`PeakWorkingSet64`，
  完成后额外 settle 1.5s 再记 WorkingSet。
- `[perf]` 日志：推理模式注入 `LUMEN_PERF_LOG=1`，把本次新增的 `[perf]` 行（addpaths / thumbnails ready /
  batch start / batch done 含 workingSet/peak/managed）收集到 `result.<mode>.perfLog`，供与 UI-NOTES 数字交叉核对。
- fixtures 口径：批量默认用 `assets/samples/anime` 真 PNG（1024²/1920² 等）；若要与 ui-dev 的
  `src/Lumen.App/tools/testdata/perf10`（10 张 JPG）口径对齐，传 `-FixturesRoot src\Lumen.App\tools\testdata\perf10`。
- 交叉核对：`artifacts/ui-smoke/perf-v3-expectations.json`（占位，冻结后填 `startupMedianMs/singleTotalS/batch9WallS/batch9WorkingSetMB/packageMB`，
  0 表示跳过）；脚本按 `-TolerancePercent`（默认 15%）报 `OK/MISMATCH`。

### 13.2 准备阶段证据（未启动应用/未占 GPU）

- `-DryRun -Mode all`：`artifacts/ui-smoke/perf-v3-20261017-211606/plan.json`
  （exe/paths/GPU 状态/fixtures 数/package 概况/plannedMeasurements）。
- `-Mode package` 静态基线（21:16:29）：`artifacts/ui-smoke/perf-v3-20261017-211629/result.json`
  - **532 files / 250.8 MB / pdb=0 / debugDll=0 / createdump.exe=present（advisory）**（§18 根目录布局后：263.2 MB、zip 107.1 MB）；
  - 模型：`realesr-cugan-4x-conservative`（`model.onnx` 5.65MB + `model.fp16.onnx` 2.85MB，共 8.11MB）；
  - 交叉核对：expectations 中 `packageMB=356.4`（迭代 2 README/ACCEPTANCE 旧数字）→ `MISMATCH -29.6%`。
    **原因**：`scripts/build.ps1` 已新增发布清理（移除 `*.pdb`/`*.lib`/`DirectML.Debug.dll`，约 -100MB），
    当前真实发布体积是 250.8MB；README/ACCEPTANCE 的 356MB 文案待 Lead/文档同步更新后改 expectations。
- 14 场景冒烟脚本在 D 盘仓库 `-DryRun`：**13/13 exit 0**（`artifacts/ui-smoke/v3-dryrun-<scenario>/plan.json`）；
  默认 exe 解析到 Debug（最终矩阵会显式传 `-ExePath <Release>` 或 `-Build Release`）。
- ui-dev 另有 `src/Lumen.App/tools/perf.ps1` + `testdata/perf10`（10 张 JPG）做优化前后对比（依赖 `LUMEN_PERF_LOG`）；
  QA 用独立脚本 `artifacts/ui-smoke/perf-v3.ps1` 复核，交叉核对时可用 `-FixturesRoot` 对齐其 JPG 口径与 `[perf]` 日志。
- 准备阶段 GPU 状态示例：`Lumen.Cli` 由 engine-dev 占用（util 52–69%）→ 推理模式会被 guard 拒绝；
  性能抽测需等 GPU 空闲并与 engine-dev 串行。

### 13.3 冻结后执行计划（Lead 通知「UI/Core 冻结」后）

1. `scripts/build.ps1 -Configuration Release -Publish` 产出最终 `artifacts/Lumen`（确认 0 警告 0 错误 + 发布自检通过 + 体积/无 pdb）。
2. 14 场景回归矩阵（Release exe）：非推理场景先跑，GPU 场景与 engine-dev 串行；结果写入 §12 同格式新表（v3）。
3. `perf-v3.ps1 -Mode all`（GPU 串行、≥5 次启动采样）：startup/single(1024²→4096²)/batch9/WorkingSet/2x/3x/package；
   新证据目录 `artifacts/ui-smoke/perf-v3-<时间戳>/result.json`。
4. 交叉核对：先按冻结后 `docs/PERFORMANCE.md §8` 与 `docs/UI-NOTES` 的性能数字更新 `perf-v3-expectations.json`，
   再重跑 `-Mode package`/`all` 看 `OK/MISMATCH`；不一致项逐条列在本节并报 Lead。
5. 发现 P0/P1 立即报 Lead + ui-dev（复现命令/result.json/截图/日志）；P2 记录 `docs/UI-REVIEW.md` §1.3。
6. 结果回填：本节加「13.4 冻结后实测结果」表（数字/证据/交叉核对结论），`docs/UI-REVIEW.md` §1.3 更新 P0/P1/P2 与结论。

