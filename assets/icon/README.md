# 应用图标 / Application Icon

Mewview 品牌图标资源。

## 文件

| 文件 | 用途 |
|---|---|
| `icon.png` | 高清源图（2048×2048 RGBA），设计基准 |
| `icon.ico` | Windows 应用图标（多尺寸 16/24/32/48/64/128/256），由 `icon.png` 生成 |

## 接入方式

已在 `src/Mewview/Mewview.csproj` 中启用：

```xml
<ApplicationIcon>..\..\assets\icon\icon.ico</ApplicationIcon>
```

`ApplicationIcon` 同时决定 EXE 图标（资源管理器）与窗口/任务栏图标。

## 重新生成 icon.ico

修改 `icon.png` 后执行：

```bash
python -c "from PIL import Image; im=Image.open('assets/icon/icon.png').convert('RGBA'); im.save('assets/icon/icon.ico', format='ICO', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])"
```
