# 参与 SimOCR

感谢你愿意改进 SimOCR。这个项目是 Windows 桌面端的截图 → OCR → 结构化导出工具，
以 Apache-2.0 发布；所有识别都在本机完成。

## 环境要求

- Windows 10 / 11（64 位）
- .NET 10 SDK（`dotnet --list-sdks` 应能看到 `10.0.x`）
- 首次构建需要联网还原 NuGet 包；模型与 VC++ 运行库已随仓库提供

## 构建与运行

```powershell
git clone https://github.com/Akitsuki4852/SimOCR.git
cd SimOCR

dotnet build src/SimOCR/SimOCR.csproj -c Debug
.\src\SimOCR\bin\Debug\net10.0-windows\SimOCR.exe
```

## 测试

```powershell
# xUnit（68 个用例；ScreenCaptureTests 需要交互桌面）
dotnet test tests/SimOCR.Tests/SimOCR.Tests.csproj

# 纯逻辑断言（不依赖 testhost 的控制台运行器；5 条用例 / 78 条断言）
tests\SimOCR.Tests\bin\Debug\net10.0-windows\SimOCR.Tests.exe --case=all
```

提交 PR 前建议跑一次核心门禁（会构建并跑一遍自检/验收用例，约几分钟）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/gate-check.ps1
```

## 代码约定

- **注释写“当前行为 + 为什么这样设计”**，不要写内部工单号、版本变更史或对外部文档的引用。
- `ImplicitUsings` 是关闭的：新文件要自己写 `using`；WPF / WinForms 同名类型冲突时写全名。
- 可配置的业务词汇（表头词表、派生列、校验规则）放进 `src/SimOCR/Export/Profiles/*.yaml`，
  不要写进 C#。
- `build/*.ps1` 里含中文的脚本必须保存为 **UTF-8 with BOM**（Windows PowerShell 5.1 的要求）。
- 改了抓屏 / 剪贴板代码后，请跑 `--selftest-capture=200` 与 `--selftest-clipboard`。

## 提交 PR

1. 一个 PR 做一件事，说明动机与行为变化（仓库自带 [PR 模板](.github/PULL_REQUEST_TEMPLATE.md)，提交时自动带出）；
2. 附上验证方式（跑了哪些测试 / 手动步骤）；
3. 不要提交 `dist/`、`bin/`、`obj/`、日志与个人配置；
4. 新代码保持现有风格（nullable 开启、0 警告）。

## 报告问题

请用 [Issues](https://github.com/Akitsuki4852/SimOCR/issues) 并提供：

- SimOCR 版本（设置 → 运行信息）与 Windows 版本；
- 复现步骤与期望结果；
- 相关日志（设置 → 运行信息 → 打开日志目录）。

安全相关问题请按 [SECURITY.md](SECURITY.md) 私下报告，不要开公开 issue。

## 行为准则

参与本项目即表示同意遵守 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)：对事不对人，不人身攻击，不公开他人隐私。
违规的 issue / PR / 评论维护者会直接编辑或删除，不另行解释。

## 许可证

提交贡献即表示同意以 [Apache-2.0](LICENSE) 授权。随包分发的第三方组件见 [NOTICE](NOTICE)。
