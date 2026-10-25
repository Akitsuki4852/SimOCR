# 安全策略

## 支持的版本

安全修复只针对最新发布版本（见 [Releases](https://github.com/Akitsuki4852/SimOCR/releases)）。
旧版本请先升级再复现。

## 报告漏洞

**请不要开公开 issue。** 请使用 GitHub 的私密漏洞报告：

https://github.com/Akitsuki4852/SimOCR/security/advisories/new

报告里请尽量包含：

- 受影响的版本；
- 复现步骤或最小样例；
- 影响范围（例如“能在安装目录之外写文件”“能让 OCR 进程崩溃”）。

## 范围

重点关注：

- 安装器（自解压、目录穿越、注册表/快捷方式写入）；
- 文件读写（保存目录、导出文件、配置与日志）；
- 剪贴板与截图数据处理；
- 第三方依赖（RapidOcrNet / ONNX Runtime / SkiaSharp 等，见 [NOTICE](NOTICE)）。

以下不算安全问题：需要本机管理员权限才能利用的问题、用户自己解压/修改安装包导致的问题、
以及第三方模型的识别准确率问题。

## 处理方式

维护者会在确认后尽快回复并给出修复计划。本项目没有漏洞赏金。
