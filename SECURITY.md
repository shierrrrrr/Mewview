# Security Policy

## 报告安全漏洞

如果你发现了安全漏洞，请**不要**在公开的 Issue 中披露细节。

请通过以下方式报告：

- GitHub Security Advisories 中的「Report a vulnerability」
- 或直接联系维护者

我们将尽快确认并处理。

## 隐私与数据说明

Mewview（瞄瞄）是一个纯本地运行的应用：

- 图片与 OCR 内容**不会**上传到任何服务器
- OCR 完全在本地运行（ONNX Runtime 进程内推理）
- 唯一的网络行为是用户主动触发的 OCR 模型**首次下载**
- 无遥测、无分析、无账户、无自动更新

详见 [docs/PRIVACY.md](docs/PRIVACY.md)。
