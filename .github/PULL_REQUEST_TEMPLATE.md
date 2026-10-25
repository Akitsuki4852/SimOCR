## 这个 PR 做了什么

<!-- 一句话说明动机与行为变化；纯重构请写明"无行为变化"并给出依据（测试/自检）。 -->

## 对应的 Issue

<!-- 有就填 #编号；没有可以写"无"。 -->

## 验证方式

<!-- 至少勾一项，并补上实际跑的命令或手动步骤。 -->

- [ ] `dotnet build SimOCR.slnx -c Release`（0 警告 / 0 错误）
- [ ] `tests\SimOCR.Tests\bin\Release\net10.0-windows\SimOCR.Tests.exe --case=all`
- [ ] `dotnet test tests\SimOCR.Tests\SimOCR.Tests.csproj -c Release`
- [ ] 手动步骤：（请写明）

## 自查

- [ ] 一个 PR 只做一件事
- [ ] 没有提交 `dist/`、`bin/`、`obj/`、日志或个人配置
- [ ] 新增文件自己写了 `using`（本仓库关闭了 `ImplicitUsings`）
- [ ] 含中文的 `build/*.ps1` 仍保存为 **UTF-8 with BOM**
- [ ] 改了抓屏 / 剪贴板相关代码时，跑过 `--selftest-capture=200` 与 `--selftest-clipboard`

## 说明

<!-- 可选：取舍、已知限制、后续待办。 -->
