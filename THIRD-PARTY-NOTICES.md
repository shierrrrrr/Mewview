# Third-Party Notices

Mewview 使用了以下第三方开源软件与模型。感谢各项目作者。

This document lists third-party software and models used by Mewview.

## NuGet 依赖

### SkiaSharp
- 版本：4.151.2
- 许可证：MIT
- 版权：Copyright (c) 2015-2024 .NET Foundation and Contributors
- 网址：https://github.com/mono/SkiaSharp
- 用途：图像解码/编码、标注栅格化

### RapidOcrNet
- 版本：4.0.2
- 许可证：Apache-2.0
- 网址：https://github.com/RapidAI/RapidOcrNet
- 用途：本地 OCR 引擎封装
- 说明：基于 RapidOCR（Apache-2.0）、PaddleOCR（Apache-2.0）、PdfPig（Apache-2.0）等衍生。

### Microsoft.ML.OnnxRuntime / Managed
- 版本：1.29.0
- 许可证：MIT
- 版权：Copyright (c) Microsoft Corporation
- 网址：https://github.com/microsoft/onnxruntime
- 用途：ONNX 推理运行时

### Clipper2
- 版本：2.0.0
- 许可证：Boost Software License 1.0
- 版权：Copyright (c) 2010-2023 Angus Johnson
- 网址：https://github.com/AngusJohnson/Clipper2
- 用途：多边形裁剪几何计算（RapidOcrNet 传递依赖）

### System.Numerics.Tensors
- 版本：9.0.0
- 许可证：MIT
- 版权：Copyright (c) .NET Foundation and Contributors
- 用途：张量运算（OnnxRuntime.Managed 传递依赖）

## OCR 模型

### PP-OCRv5 ONNX 模型与字典
- 许可证：Apache-2.0（PaddleOCR）
- 来源：RapidOCR 官方模型仓库（ModelScope `RapidAI/RapidOCR`）
- 用途：文本检测、方向分类、中文/英文/韩文识别
- 文件：`ch_PP-OCRv5_det_mobile.onnx`、`ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx`、`ch_PP-OCRv5_rec_mobile.onnx`、`ppocrv5_dict.txt`、`korean_PP-OCRv5_rec_mobile.onnx`、`ppocrv5_korean_dict.txt`
- 版权：Copyright (c) PaddlePaddle Authors

## 许可证全文

- MIT：https://opensource.org/licenses/MIT
- Apache-2.0：https://www.apache.org/licenses/LICENSE-2.0
- BSL-1.0：https://www.boost.org/LICENSE_1_0.txt

> 完整的 Apache-2.0 与 Boost Software License 1.0 许可证文本，请参阅上述官方链接。
