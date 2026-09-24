# 架构说明 Architecture

## 概览

Mewview（瞄瞄）是一个单项目 WPF 应用（C# / .NET 10），采用 code-behind 事件驱动风格，未引入 MVVM 框架，以保持轻量。

```text
Mewview (WPF Application)
│
├── UI 层（.xaml + code-behind）
│   ├── MainWindow          主窗口编排：打开/浏览/标注/OCR/剪贴板/置顶
│   ├── ConfirmSaveDialog   保存确认对话框
│   └── Controls/ImageCanvas  图片视口控件（缩放/平移/标注/裁剪交互）
│
├── Services 层
│   ├── ImageFormatRegistry 格式登记表：扩展名 / 显示名 / 解码引擎 / 帧形态 / 保存能力（单一来源）
│   ├── ImageService        图片加载与浏览（WPF/WIC 解码 + SkiaSharp WebP + 帧源装配）
│   ├── FrameSources        多帧实现：逐帧物化为独立位图（多页 TIFF 懒解码 / 动图常驻）
│   ├── FrameAnimator       动图播放定时器，窗口失焦时暂停
│   ├── OcrService          RapidOcrNet + PP-OCRv5 推理、模型下载
│   ├── AnnotationRenderer  标注光栅化导出
│   └── BitmapConversion    BitmapSource ↔ SKBitmap 转换
│
└── Models 层
    ├── Annotations         标注领域模型 + 撤销/重做命令栈
    └── ImageDocument       文件已打开的形态：帧、是否可翻页/播放、是否可标注
```

## 关键设计

- **图片视口**：`Image` 置于 `Canvas` 中，用 `MatrixTransform` 做缩放/平移；强制 100% = 1 物理像素。
- **格式单一来源**：`ImageFormatRegistry` 是扩展名、对话框过滤器、解码分派与"能否覆盖保存"的唯一出处。新增格式只需在表里加一行，其他文件不得硬编码扩展名。
- **解码分两条路**：能自解码的走 WPF/WIC（含多页与动图），WebP 走 SkiaSharp（自带编解码，不依赖系统扩展）；HEIC/AVIF 交给 Windows 的可选解码扩展，因此在这台机器上能否打开取决于系统装了哪些组件。
- **换帧不改视图**：多帧翻页/播放必须走 `ImageCanvas.UpdateImageSource`（只换位图），不能用 `SetImage`——后者会重跑开场缩放规则，每翻一页都把视图弹回适应窗口。
- **跨线程只传冻结位图**：解码与文件夹枚举在后台线程完成，但 `BitmapDecoder` / `BitmapFrame` 是 `DispatcherObject`，只能被创建它的线程碰。因此加载过程只允许交出已 `Freeze()` 的位图；多页 TIFF 的首页在后台物化，其余页的 decoder 等真正要翻到那一页时才创建（届时必然在 UI 线程上）。
- **标注非破坏性**：标注保存在独立的矢量层（`AnnotationSession`），仅在保存/导出时栅格化到图片；撤销/重做用命令模式双栈。
- **OCR 懒加载**：按语言懒创建 `RapidOcr` 引擎并复用；模型缺失时由 `EnsureModelsAsync` 一次性下载。
- **本地优先**：无任何后台联网；唯一的网络访问是用户主动触发的 OCR 模型下载，唯一的对外交互是用户确认后打开 Microsoft Store 搜索页。

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
- 显示名：跟随系统 UI 语言（`zh` → `瞄瞄`，其他 → `Mewview`），统一由 `AppDisplayName.Current` 提供，仅用于窗口标题与弹窗标题。EXE 版本资源（`FileDescription` / `ProductName`）由编译器静态烧录，**无法**随语言变化。
- 模型缓存：`%LOCALAPPDATA%\Mewview\models`
- 错误日志：`%TEMP%\mewview_error.log`
