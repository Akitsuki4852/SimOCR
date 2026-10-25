# SimOCR — 开发者文档

面向有编程基础的读者。安装使用说明请看 [README.md](README.md)。

> 一句话定位：Windows 桌面端的**截图 → OCR → 结构化导出**工具，便携、离线优先。
> 目标形态是一个双击即用的安装器 exe，不需要用户预装 .NET / Python / VC++ 运行库。

**当前版本：0.10.10**（`Directory.Build.props` 里的 `<Version>`）

---

## 1. 技术栈

| 层面 | 选型 | 备注 |
| --- | --- | --- |
| 语言 / 运行时 | **C# 13**（`LangVersion=latest`）、**.NET 10**、`net10.0-windows` | 单一目标框架，**仅 x64** |
| UI | **WPF**（XAML） | 主窗口、覆盖层、设置窗、识别结果窗 |
| 托盘 | **WinForms** `NotifyIcon` | 零额外依赖；因此项目同时启用 `UseWPF` + `UseWindowsForms` |
| 屏幕采集 | **GDI `BitBlt`**（P/Invoke，`Capture/ScreenCaptureService.cs`） | 先冻结整屏，再在冻结帧上框选；实测 1920×1080 约 27 ms/次 |
| OCR 推理 | **ONNX Runtime** + **RapidOcrNet 4.2.0**（纯托管封装） | 引擎参数在 `Settings/AppSettings.cs` 与 `src/SimOCR/Ocr/RecognitionService.cs`，取值理由写在该文件的类注释里 |
| OCR 模型 | **PaddleOCR PP-OCRv5** 中文三件套（det / rec / cls）+ 18383 字字典 | 约 21 MB，随仓库与安装包分发，Apache-2.0 |
| 表格还原 | **自研规则算法**（`Export/` 下 22 个源文件） | 行聚类 → 列检测 → 表头定列 → 字段绑定 → 派生列与算术校验 |
| 导出 | XLSX / DOCX 直接写 **OOXML**，PDF 手写对象结构 | 不依赖 Office，也不依赖第三方文档库 |
| 打包 | **`dotnet publish`**（self-contained + ReadyToRun） | 便携 zip、单文件 exe |
| 安装器 | **NativeAOT** + COM `IProgressDialog` | 安装器本体约 2.4 MB；进度窗是纯 P/Invoke 画的 |
| 测试 | **xUnit** + `Microsoft.NET.Test.Sdk` | `tests/SimOCR.Tests`，68 条用例；纯逻辑断言另走控制台 runner（5 用例 / 78 断言） |
| 构建脚本 | **PowerShell**（Windows PowerShell 5.1 兼容） | `build/` 目录。含中文的脚本**必须存为 UTF-8 BOM**，否则 5.1 会按 ANSI 解析失败；`fetch-models.ps1` / `fetch-vcredist.ps1` / `make-icon.ps1` 保持 ASCII-only |

### 为什么是这几个关键取舍

- **不用 `PublishTrimmed` / NativeAOT 做主程序**：WPF 不支持裁剪与 AOT。所以主程序走 self-contained + 单文件自解压；只有不依赖 UI 框架的**安装器**才用 NativeAOT。
- **便携 zip 优于压缩单文件**（`build/publish.ps1` 里有实测数据）：压缩单文件运行时要解压进内存，空闲 WorkingSet 约 310 MB；解压版按文件映射，约 106 MB。代价是 zip 比压缩单文件大约 11 MB（0.10.10 实测：约 96 MB vs 约 85 MB），仍以解压版为推荐形态。
- **`ImplicitUsings=disable`**：WPF 与 WinForms 的隐式 using 会冲突（`Application` / `Point` / `Rectangle` / `MouseEventArgs` 等），改由 `src/SimOCR/GlobalUsings.cs` 精确控制。新增文件忘记写 using 时先看这个文件。
- **DPI**：`app.manifest` 里显式声明 **PerMonitorV2**（WPF 只认这个），同时 `NoWarn` 掉 WinForms 的 `WFO0003`。
- **内存策略**写进 `runtimeconfig.json`（见 `SimOCR.csproj`）：工作站 GC + `ConserveMemory=7` + `DynamicAdaptationMode=1` + `RetainVM=false`。这是一个「托盘常驻 + 偶尔截图」的进程，内存比吞吐重要。

---

## 2. 从 clone 到跑起来

### 2.1 前置要求

- **Windows 10 / 11 x64**
- **.NET 10 SDK**（`global.json` 把版本钉在 `10.0.401`，`rollForward: latestFeature`；
  验证：`dotnet --list-sdks` 应能看到 `10.0.x`）
- 首次构建需要联网还原 NuGet 包（`RapidOcrNet` 及其传递依赖）

```powershell
git clone https://github.com/Akitsuki4852/SimOCR.git
cd SimOCR
```

### 2.2 获取 OCR 模型

模型（`models/v5/`，约 21 MB）**已经随仓库提供**，clone 完即可直接构建。

如果想重新下载或校验：

```powershell
# 从 ModelScope 的 RapidAI/RapidOCR v3.9.2 拉取，逐个 SHA256 校验
powershell -NoProfile -ExecutionPolicy Bypass -File build/fetch-models.ps1
# 强制重新下载
powershell -NoProfile -ExecutionPolicy Bypass -File build/fetch-models.ps1 -Force
```

产物（目录名 `v5` 是上游命名，内容即 PP-OCRv5）：

| 文件 | 模型 | 体积 |
| --- | --- | --- |
| `det.onnx` | `ch_PP-OCRv5_det_mobile` | 4.6 MB |
| `rec.onnx` | `ch_PP-OCRv5_rec_mobile` | 15.9 MB |
| `cls.onnx` | `ch_ppocr_mobile_v2.0_cls` | 0.6 MB |
| `dict.txt` | `ppocrv5_dict`（18383 行） | 0.07 MB |

构建时会把它们拷到输出目录的 **`models/v5-cn/`**（刻意与 RapidOcrNet 包自带的 latin 模型 `models/v5` 区分开，并且会把那份 13 MB 的无用 latin 模型从输出里删掉，见 `SimOCR.csproj` 的 `SkipBundledLatinModels` / `DropBundledLatinModels` 两个 target）。

### 2.3 构建、运行、测试

```powershell
# 构建（Debug）
dotnet build src/SimOCR/SimOCR.csproj -c Debug

# 运行刚构建出来的主程序
.\src\SimOCR\bin\Debug\net10.0-windows\SimOCR.exe

# xUnit（68 条用例；其中 ScreenCaptureTests 需要交互桌面，会真实抓屏）
dotnet test tests/SimOCR.Tests/SimOCR.Tests.csproj

# 纯逻辑断言的控制台 runner（门禁用例走这条；--case=all = 5 用例 / 78 断言）
tests\SimOCR.Tests\bin\Debug\net10.0-windows\SimOCR.Tests.exe --case=all

# 只跑某一条控制台用例
tests\SimOCR.Tests\bin\Debug\net10.0-windows\SimOCR.Tests.exe --case=snap-geometry
tests\SimOCR.Tests\bin\Debug\net10.0-windows\SimOCR.Tests.exe --case=manual-line-split
tests\SimOCR.Tests\bin\Debug\net10.0-windows\SimOCR.Tests.exe --case=profile-source

# 解决方案文件（新版 slnx 格式）
dotnet build SimOCR.slnx
```

控制台 runner 与 xUnit 共用同一份断言源码，只是不经过 testhost：`--case=all` 跑 5 条用例 / 78 条断言，
其中 `snap-geometry` 承担窗口吸附的 28 条纯几何用例、`manual-line-split` 承担线格检测的手工分割线断言、
`profile-source` 校验配置档的单一来源、`table-tsv-diff` 校验表格差异工具；
`--list-facts` 列出可被 vstest 发现的测试方法（`[Fact]` / `[Theory]`）与展开后的用例数。

### 2.4 VC++ 运行时（app-local，重要）

`onnxruntime.dll` 的导入表依赖 `VCRUNTIME140(_1)` / `MSVCP140(_1)`，
这些**不是 Windows 自带**的；干净机器上缺失会导致 ONNX 加载失败、OCR 不可用。

- DLL 已随仓库提交在 `runtime/vcredist-x64/`，构建与发布时会自动拷到输出目录。
- 需要重新获取时：`build/fetch-vcredist.ps1`（从构建机的 `System32` 取出，带签名校验并写 `PROVENANCE.txt`）。
- **发布闸门**：`publish.ps1` 会检查这 4 个 DLL，缺失直接拒绝出包。

---

## 3. 出安装包与发布产物

> **发布前必读**：`README.md` 顶部的绿色下载按钮指向
> `https://github.com/Akitsuki4852/SimOCR/releases/latest`。
> 该链接要能生效，需要满足两个条件：
> 1. **仓库是 Public**（私有仓库未登录访问会得到 404）；
> 2. **至少发布过一个 Release**，并把 `dist/SimOCR-<ver>-setup-win-x64.exe`
>    （以及可选的便携 zip）作为附件上传。没有 release 时 `releases/latest` 同样是 404。
>
> README 里的安装包名写成「`SimOCR-…-setup-win-x64.exe`（认准后缀）」，
> 发布时保持这个命名即可，不需要额外改名。


```powershell
# 一条命令出三种产物（安装器 exe + 便携 zip + 单文件 exe），各自带 .sha256
powershell -NoProfile -ExecutionPolicy Bypass -File build/publish.ps1 -Version 0.10.10

# 常用开关
#   -NoCompression    关闭单文件压缩（体积/首启对比用）
#   -NoReadyToRun     关闭 R2R
#   -FrameworkDependent  出框架依赖包（要求用户预装 .NET，一般不用）
#   -SkipInstaller    只出便携 zip 与单文件 exe
#   -Runtime          默认 win-x64
```

产物落在 `dist/`：

| 产物 | 说明 |
| --- | --- |
| `SimOCR-<ver>-setup-win-x64.exe` | **推荐分发形态**。自解压安装器（NativeAOT），内嵌便携 zip，约 100 MB |
| `SimOCR-<ver>-portable-win-x64.zip` | 绿色免安装包，解压即用 |
| `SimOCR-<ver>-win-x64.exe` | 自包含单文件 exe（可选） |

发布流程内部顺序不能颠倒：**先便携 zip → 再卸载器 → 最后安装器**（后两者要把前者的产物作为嵌入资源）。

### 安装器命令行

```powershell
.\dist\SimOCR-0.10.10-setup-win-x64.exe                      # 安装并启动（已装则直接启动）
.\dist\SimOCR-0.10.10-setup-win-x64.exe --verify --silent    # 完整性自检，退出码 0 = 正常
.\dist\SimOCR-0.10.10-setup-win-x64.exe --uninstall          # 卸载
.\dist\SimOCR-0.10.10-setup-win-x64.exe --help               # 全部参数
```

其他参数：`--dir <路径>`、`--force`、`--no-launch`、`--no-shortcuts`、`--no-registry`、`--no-progress`、`--purge-data`。
退出码：`0`=成功 `1`=失败 `2`=安装包异常 `3`=被占用 `4`=用户取消。

### 回归与其他脚本

```powershell
# 核心门禁：构建 + 12 条自检/验收用例 + 纯逻辑 runner + xUnit（默认跳过需要内部素材的回归）
powershell -NoProfile -ExecutionPolicy Bypass -File build/gate-check.ps1

# 顺带证明"断言仍然能失败"（缺陷注入，慢）
powershell -NoProfile -ExecutionPolicy Bypass -File build/gate-check.ps1 -IncludeInjection

# 安装器自动化回归：装到临时目录，不碰真实环境
powershell -NoProfile -ExecutionPolicy Bypass -File build/test-installer.ps1 `
  -SetupExe "dist/SimOCR-0.10.10-setup-win-x64.exe"

# 空闲内存测量
powershell -NoProfile -ExecutionPolicy Bypass -File build/measure-memory.ps1

# 改图标源图后重新生成 Assets/SimOCR.ico（16~256 多尺寸）
powershell -NoProfile -ExecutionPolicy Bypass -File build/make-icon.ps1
```

---

## 4. 主程序命令行（自检 / 自动化）

正常使用不需要这些参数；**CI 与验收**依赖它们。自检开关跑完即退出，不建常驻托盘；
`--selftest-overlay*` 会短暂显示覆盖层用于端到端验证，`--accept-*` 系列需要真实桌面与鼠标。退出码统一为：
`0`=通过 `1`=失败 `2`=用法/运行异常 `4`=断言失败 `5`=用例不成立（环境问题，不是缺陷）。

### 4.1 日常与冒烟自检

| 开关 | 作用 |
| --- | --- |
| `--capture` | 启动后立即进入截图 |
| `--profile=<文件或档名>` | 指定表格解析配置档（优先级最高，覆盖设置里的档名） |
| `--bootstrap-selftest` | 启动自检后立即退出 |
| `--selftest-capture[=N]` | 免弹窗连拍 N 次（默认 200），统计 GDI / 句柄 / 内存增量 |
| `--selftest-clipboard` | 剪贴板写入 + 跨进程读回（**会覆盖当前剪贴板**） |
| `--selftest-clipboard-hold[=N]` | 复制后保持存活 N 秒，便于用 Word 手工验证粘贴 |
| `--selftest-overlay` | 覆盖层端到端自检 |
| `--selftest-overlay-hold[=N]` | 同上，但保持画面 N 秒（截图取证用） |
| `--selftest-linegrid[=图片路径]` | 线格检测链路；不给图片则用当前屏幕。**手工分割线断言**改由 `SimOCR.Tests.exe --case=manual-line-split` 承担 |
| `--selftest-snap` | 窗口吸附的**真实窗口探针**（DWM / GetWindowRect / 候选枚举三方几何一致性 + DPI 感知）。**纯几何 28 条**改由 `SimOCR.Tests.exe --case=snap-geometry` 承担 |
| ~~`--selftest-profile`~~ | **已退役**：直接调用会报用法错误并**退出 2**，同时指路 `SimOCR.Tests.exe --case=profile-source` |
| `--read-clipboard=<报告路径>` | 读出剪贴板内容写报告（供跨进程读回断言使用） |
| `--snap-host-window=x,y,w,h[;秒]` | 开一扇已知几何的宿主窗（由 `--accept-snap` 自己拉起） |
| `--snap-probe-occlusion[=…]` | 遮盖探针几何断言 |

### 4.2 OCR 与导出自检（会真实加载模型）

| 开关 | 作用 |
| --- | --- |
| `--selftest-ocr[=图片路径]` | OCR 引擎链路；不给图片则识别当前屏幕 |
| `--selftest-export[=图片路径]` | 四种导出器（TXT / DOCX / XLSX / PDF） |
| `--selftest-ocr-window[=图片路径]` | 识别结果窗口（预览 + 框叠加 + 悬停工具条 + 编辑模式） |
| `--selftest-ocr-mouse[=图片路径]` | 识别结果窗口的鼠标交互（`SendInput` 真实事件） |
| `--selftest-ocr-clipboard[=图片路径]` | 托盘「识别剪贴板图片」整链路（**会覆盖剪贴板**） |
| `--selftest-overlay-ocr[=图片路径]` | 覆盖层 → OCR 端到端 |

### 4.3 真实素材验收（需要真桌面）

| 开关 | 作用 |
| --- | --- |
| `--accept-table=<图片1>[;图片2…]` | 真实鼠标框选 → 导出 xlsx |
| `--accept-snap` | 吸附验收：**自建宿主窗**，不再依赖记事本等外部程序；悬停高亮 / 单击抓取 / 拖动吸附 |
| `--accept-enhance` | 配合 `--accept-table`，走增强识别档 |
| `--accept-txt` | 配合 `--accept-table`，按 TXT 纯文本口径验收 |

### 4.4 控制台纯逻辑 runner

`SimOCR.Tests.exe` 同时是 xUnit 程序集和普通控制台程序；断言源码只有一份，两条路径都能执行，
门禁走控制台这条。

| 用例 | 内容 |
| --- | --- |
| `--case=all` | 全部 5 条用例 / 78 条断言 |
| `--case=snap-geometry` | 窗口吸附的 28 条纯几何用例 |
| `--case=manual-line-split` | 线格检测的手工分割线断言 |
| `--case=profile-source` | 配置档的单一来源 |
| `--case=table-tsv-diff` | 表格 TSV 对比 |
| `--case=export-core` | 表格还原与导出核心：区间合并 / 行聚类 / 阅读顺序 / 单元格归一 / 四种导出的产物结构（31 条断言） |
| `--list-facts` | 列出可被 vstest 发现的测试方法（44 条，含 Theory；展开后 68 个用例） |

```powershell
tests\SimOCR.Tests\bin\Release\net10.0-windows\SimOCR.Tests.exe --case=all
tests\SimOCR.Tests\bin\Release\net10.0-windows\SimOCR.Tests.exe --list-facts
```

---

## 5. 目录结构

```
src/SimOCR/                 主程序（WPF）
  Interop/                  所有 P/Invoke、显示器与 DPI、全局热键
  Capture/                  冻结帧抓屏（GDI BitBlt）
  Overlay/                  覆盖层窗口、选区模型、截图会话编排
  Storage/                  PNG 落盘、剪贴板、已知文件夹、文件名模板
  Pinning/                  贴图置顶窗口与服务
  Ocr/                      OCR 引擎封装、识别结果窗口、竖带补识
  Export/                   表格还原与导出（XLSX / DOCX / PDF / TXT）
    Profiles/               表格解析配置档（YAML，随构建与发布产物分发）
      default.yaml          通用档：只认普通表格列名，不含任何行业规则
      example-invoice-packing.yaml  可选行业档示例：演示行业约定怎么写进配置
  Settings/                 配置模型、读写、设置窗口
  Tray/                     托盘图标与菜单
  Startup/                  单实例、开机自启
  Diagnostics/              路径策略、日志、命令行自检
src/SimOCR.Installer/       自解压安装器（NativeAOT，进度窗为纯 P/Invoke）
tests/SimOCR.Tests/         xUnit 单元/集成测试（68 条）+ 控制台纯逻辑 runner（5 用例 / 78 断言）
build/                      PowerShell 构建与回归脚本
  publish.ps1               出安装器 / 便携 zip / 单文件 exe（唯一"发布必需"的脚本）
  gate-check.ps1            核心门禁：构建 + 自检/验收用例 + 纯逻辑 runner + xUnit
  inject-check.ps1          缺陷注入门禁：证明断言仍然能失败（由 gate-check -IncludeInjection 调用）
  analysis.ruleset          CA2213 / CA2000 / CA1823 提升为错误（Directory.Build.props 引用）
  fixtures/                 随仓库提交的合成夹具（linegrid.png / linegrid-expected.txt）
  fetch-models.ps1          重新下载或校验 models/v5 下的 PP-OCRv5 模型
  fetch-vcredist.ps1        从构建机 System32 重新提取 app-local VC++ 运行库
  test-installer.ps1        安装器回归（装到临时目录，不碰真实环境）
  measure-memory.ps1        启动主程序并测空闲内存
  make-icon.ps1             由方形 PNG 生成多尺寸 Assets/SimOCR.ico
models/v5/                  PP-OCRv5 中文模型（随仓库提供）
runtime/vcredist-x64/       app-local VC++ 运行库（含 PROVENANCE.txt）
docs/                       README 用的示例图（合成素材，不含真实单据）
```

### 关键类速查

| 想了解 | 看这里 |
| --- | --- |
| 程序启动、DI 组装、命令行分派 | `src/SimOCR/AppComposition.cs` |
| 截图 → 框选 → 出结果 的完整会话 | `src/SimOCR/Overlay/CaptureSessionController.cs` |
| OCR 引擎参数与调用 | `src/SimOCR/Ocr/RecognitionService.cs` |
| 表格还原总入口 | `src/SimOCR/Export/OcrTableLayout.cs` |
| 行列切分 | `Export/LineGridDetector.cs`、`TableColumnDetector.cs`、`FastTableBuilder.cs` |
| 字段语义与校验 | `Export/TableFieldBinder.cs`、`FieldSemantics.cs`、`CellValidator.cs` |
| 导出格式实现 | `Export/XlsxExporter.cs`、`DocxExporter.cs`、`PdfExporter.cs`、`OoxmlWriter.cs` |

---

## 6. 文档

本仓库**刻意只保留源码级文档**：设计取舍与算法理由写在代码注释里，可配置的业务词汇写在
`src/SimOCR/Export/Profiles/*.yaml` 里。产品需求、引擎调研与识别行为的完整叙述、逐版本变更记录
属于开发资料，不在本仓库发布。

| 位置 | 内容 |
| --- | --- |
| `src/SimOCR/Export/Profiles/*.yaml` | 表格解析配置档：表头词表、取值规则、派生列、校验规则、阈值。**改这里就能适配新表格，不需要改代码** |
| `src/SimOCR/Export/*.cs` 的类注释 | 各算法的取舍与失败案例（列切分、行聚类、字段绑定、逐格取值、判定闸门） |
| `src/SimOCR/AppComposition.cs` | 启动顺序、配置档加载优先级（`--profile=` > 设置 > default.yaml > 内置兜底） |
| [LICENSE](LICENSE) | Apache-2.0 |
| [NOTICE](NOTICE) | 第三方组件与模型声明 |
| [README.md](README.md) | 面向使用者的说明（安装、快捷键、常见问题） |
| [README.en.md](README.en.md) | 同一份使用说明的英文版（对外曝光用，内容比中文版短） |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 构建、测试、提 PR 的约定 |
| [SECURITY.md](SECURITY.md) | 漏洞报告方式 |
| [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) | 社区行为准则（改编自 Contributor Covenant 2.1） |

---

## 7. 开发时容易踩的坑

1. **隐式 using 已关闭**：新文件要自己写 using；WPF/WinForms 同名类型冲突时要写全名。
2. **改了图标源图**要重跑 `build/make-icon.ps1`，否则 exe 与托盘仍是旧图标。
3. **脚本编码**：`build/*.ps1` 里含中文的脚本必须存成 **UTF-8 BOM**（Windows PowerShell 5.1 会把无 BOM 脚本按本地 ANSI 读取，中文字面量会乱码甚至整份脚本解析失败）。`gate-check.ps1` 有前置自检会拦这个；`fetch-models.ps1` / `fetch-vcredist.ps1` / `make-icon.ps1` 保持 ASCII-only。
4. **不要发布框架依赖包**：`dotnet publish` 指定 `-r` 在 .NET 6+ 不再隐含 self-contained。漏了 `--self-contained=true` 会打出一个很小的包，在干净机器上起不来（`publish.ps1` 有内容闸门专门拦这个）。
5. **模型目录有两份**：`models/v5` 是上游原始目录名，输出目录是 `models/v5-cn`。改路径时两处都要动。
6. **单文件自解压会写 `%TEMP%\.net\…`**：曾实测因该目录创建失败而无法启动，因此便携 zip 才是推荐形态。
7. **改抓屏/剪贴板代码后**，跑 `--selftest-capture=200` 与 `--selftest-clipboard` 验证 GDI/句柄无泄漏、跨进程可读。

---

## 8. 本仓库不包含的内容

这是一个**只放源码与可复现构建**的仓库。以下内容不在此发布：

- **产品需求文档、OCR 引擎调研、识别行为讲解**：设计取舍写进代码注释，可配置的业务词汇写进
  `src/SimOCR/Export/Profiles/*.yaml`，读这两处即可理解实现。
- **逐版本变更记录（CHANGELOG）**：历史行为变化写在源码注释与 PR / Release 说明里。
- **私有回归素材**：真实单据语料、Python 参考实现、算法原型与评测脚本、验收截图与虚拟机报告。

**注释以「当前行为 + 设计取舍」为主，不需要先读另一份文档。** 注释里不写内部工单号与审计条目号
（它们指向不随仓库发布的开发资料，对读者没有意义）；`0.10.x 起：` 这类前缀只表示
"这条行为从哪个版本开始"，是时间锚点而不是待办编号。
表格还原的算法结论已沉淀在 `src/SimOCR/Export/` 的代码注释与配置档注释里，可独立阅读。

---

## 9. 许可证

本项目以 **Apache-2.0** 发布，见 [LICENSE](LICENSE)。
随包分发的第三方组件（RapidOcrNet、ONNX Runtime、PaddleOCR PP-OCRv5 模型、VC++ 运行库等）声明见 [NOTICE](NOTICE)。
