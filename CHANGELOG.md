# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/) 格式，版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

## [0.1.0] - 2026-09-18

### Added
- 图片查看：支持 JPG / PNG / WEBP / BMP
- 打开方式：文件对话框（Ctrl+O）、拖拽、剪贴板粘贴（Ctrl+V）、命令行参数
- 图片浏览：上一张 / 下一张（← / →）
- 缩放：滚轮、Ctrl+滚轮、100%、适应窗口、实际大小
- 本地 OCR：中文+英文、韩文（PP-OCRv5，首次使用自动下载模型并缓存）
- 标注工具：画线、箭头、矩形、文字
- 裁剪
- 窗口置顶（Ctrl+Shift+Space）
- 剪贴板：复制图片（Ctrl+C）、粘贴打开（Ctrl+V）
- 撤销 / 重做（Ctrl+Z / Ctrl+Y）
- 非破坏性编辑：保存 / 另存为

### Known Issues
- 超大图片需要较多内存
- OCR 准确率受图片质量影响

详见 [docs/KNOWN_ISSUES.md](docs/KNOWN_ISSUES.md)。
