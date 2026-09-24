# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/) 格式，版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)，具体判定规则见 [docs/VERSIONING.md](docs/VERSIONING.md)。

## [Unreleased]

## [0.2.0] - 2026-09-24

### Added
- 显示名随系统语言解析：中文系统窗口标题显示「瞄瞄」，其他语言显示 Mewview；EXE 版本资源固定为双语 `Mewview（瞄瞄）`
- 新增格式：TIFF / TIF（含多页翻页）、GIF（含动图播放）、ICO、JPEG XR（.jxr / .wdp）、HEIC（.heic / .heif）、AVIF
- 多页 TIFF 翻页（`PageUp` / `PageDown`）与动图 GIF 自动播放；多帧图片不提供标注、裁剪与保存
- HEIC / AVIF 缺少系统解码扩展时给出明确提示，并可选择打开 Microsoft Store 搜索页

### Changed
- 发布体积 266 MB → 158 MB：剔除 `libSkiaSharp.pdb`（85 MB 原生调试符号）、包内无人引用的 OCR 模型（13.1 MB）、未使用的语言资源（8.3 MB）、`DiaSymReader`（2.1 MB）
- 图片格式清单收敛为单一来源（`ImageFormatRegistry`），扩展名不再散落在对话框过滤器与保存判定里
- 打开图片改为后台解码：48 MP 的 HEIC 约 600 ms 的解码期间窗口保持响应，并显示等待光标与「正在打开 xxx …」状态
- 在文件夹中按 `←` / `→` 浏览时自动跳过打不开的文件（缺解码扩展、损坏），改为最后一次性提示跳过数量，不再逐个弹窗阻断浏览

### Known Issues
- HEIC 的透明通道会被 Windows 解码器丢弃
- HEIC / AVIF 的色度按 BT.709 解释，标记为 BT.601 的文件会轻微偏色

详见 [docs/KNOWN_ISSUES.md](docs/KNOWN_ISSUES.md)。

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
