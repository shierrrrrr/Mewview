# 架构说明 Architecture

## 概览

Mewview 是一个单项目 WPF 应用（C# / .NET 10），采用 code-behind 事件驱动风格，未引入 MVVM 框架，以保持轻量。

```text
Mewview (WPF Application)
│
├── UI 层（.xaml + code-behind）
│   ├── MainWindow          主窗口编排：打开/浏览/标注/OCR/剪贴板/置顶
│   ├── ConfirmSaveDialog   保存确认对话框
│   └── Controls/ImageCanvas  图片视口控件（缩放/平移/标注/裁剪交互）
│
├── Services 层
│   ├── ImageService        图片加载与浏览（WIC + SkiaSharp WebP）
│   ├── OcrService          RapidOcrNet + PP-OCRv5 推理、模型下载
│   ├── AnnotationRenderer  标注光栅化导出
│   └── BitmapConversion    BitmapSource ↔ SKBitmap 转换
│
└── Models 层
    └── Annotations         标注领域模型 + 撤销/重做命令栈
```

## 关键设计

- **图片视口**：`Image` 置于 `Canvas` 中，用 `MatrixTransform` 做缩放/平移；强制 100% = 1 物理像素。
- **标注非破坏性**：标注保存在独立的矢量层（`AnnotationSession`），仅在保存/导出时栅格化到图片；撤销/重做用命令模式双栈。
- **OCR 懒加载**：按语言懒创建 `RapidOcr` 引擎并复用；模型缺失时由 `EnsureModelsAsync` 一次性下载。
- **本地优先**：无任何后台联网；唯一网络访问是用户主动触发的 OCR 模型下载。

## 分发架构

```text
同一套 Application Source Code
        │
        ├── Portable Publish（GitHub Releases，Phase 5）
        │      win-x64 自包含，解压即用
        │
        └── MSIX Packaging（Microsoft Store，Phase 6-8）
               WPF Application + Packaging Project → MSIX
```

两个渠道共用源码，仅打包/分发方式不同。

## 命名与路径约定

- 命名空间：`Mewview` / `Mewview.Services` / `Mewview.Models` / `Mewview.Controls`
- 程序集：`Mewview.dll` / `Mewview.exe`
- 模型缓存：`%LOCALAPPDATA%\Mewview\models`
- 错误日志：`%TEMP%\mewview_error.log`
