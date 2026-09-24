# 版本规范 Versioning

本项目遵循[语义化版本](https://semver.org/lang/zh-CN/)（SemVer），版本号形如 `MAJOR.MINOR.PATCH`。

Mewview 是面向最终用户的桌面应用，没有下游程序调用它的 API，因此下面把 SemVer 的三位**按“用户视角”重新定义**。

## 三位分别代表什么

| 位置 | SemVer 原义 | 本项目的含义 | 例子 |
|---|---|---|---|
| MAJOR | 不兼容的变更 | 用户被迫改变习惯、重新配置、重新下载，或旧系统被抛弃 | 改动设置/模型缓存的存储位置或格式、移除已有功能、抬高最低 Windows 版本 |
| MINOR | 向后兼容的新功能 | 用户能做以前做不到的事 | 新增图片格式、新增工具或快捷键、打开速度明显变快 |
| PATCH | 向后兼容的问题修复 | 用户遇到的问题被修好，但没有新东西可做 | 某格式打不开、颜色偏差、崩溃、文案错误 |

## 0.x 阶段的特殊规则

SemVer 规定 `0.y.z` 表示“初态开发，任何东西都可能变”。因此在 **1.0.0 之前，破坏性变更只占用 MINOR 位**，不动 MAJOR：

- 修 bug → `0.2.1`
- 新功能、破坏性变更 → `0.3.0`
- `1.0.0` 不是“功能攒够了”，而是“**承诺不再随便破坏**”——当你认为设置存储、模型缓存路径、命令行参数、文件关联这些用户会依赖的契约已经稳定，再切 1.0.0。此后破坏性变更才轮到 MAJOR。

## 该发哪个版本：按顺序问三个问题

1. 这一版里有**用户看得见的新能力**吗？（新格式、新工具、新快捷键、明显更快的打开）
   → 有：**MINOR**
2. 有让**老用户付出代价**的变化吗？（见上表 MAJOR 一行的例子）
   → 有：当前处于 0.x 记 **MINOR**；已 ≥1.0 记 **MAJOR**
3. 都没有，只是修好了用户遇到的毛病
   → **PATCH**

只有内部重构（例如把扩展名收敛进 `ImageFormatRegistry`）、测试脚手架、文档措辞这类**用户看不见**的改动，不单独发版，跟着下一个版本一起走。

## 本项目的破坏性变更清单

以下任何一项都要按上面的规则顶位（0.x 时记 MINOR）：

- 改动模型缓存位置 `%LOCALAPPDATA%\Mewview\models`（用户需重新下载约 36 MB 模型）
- 改动设置、最近文件、窗口状态的存储位置或格式
- 改动命令行参数或文件关联行为
- 移除用户已在使用的功能
- 抬高最低 Windows 版本（更换目标框架时可能发生）
- 改动已保存产物的格式（例如将来的标注侧车文件）

## 版本号只有一个来源

**`src/Mewview/Mewview.csproj` 的 `<Version>` 是唯一权威来源**，它同时决定了程序集与 EXE 的文件版本／产品版本，以及 GitHub Release 的 tag（`v` + 该版本号）。

其余地方（`CHANGELOG.md`、Release 说明、其他文档）都从它派生，**不要另行写死版本号**。因此文档中不再出现“当前版本（x.y.z）”这类表述——那是一个必然过期的重复来源。同理，`docs/KNOWN_ISSUES.md` 只描述“当前版本”，不带具体号。

## 发版流程

1. 确认工作区干净、`main` 与 `origin/main` 一致，本地构建 0 警告 0 错误。
2. 把 `<Version>` 改为目标版本号。
3. 将 `CHANGELOG.md` 顶部的 `[Unreleased]` 改为 `[目标版本] - YYYY-MM-DD`，并新增一个空的 `[Unreleased]` 留待下次；内容按 Added / Changed / Fixed / Known Issues 分节。
4. 提交并推送到 `main`。
5. 打 tag `vX.Y.Z` 并推送。tag 会触发 `.github/workflows/release.yml`：
   - **先校验 tag 与 csproj 的 `<Version>` 是否一致，不一致直接失败**（防止版本号漂移）
   - 然后构建 win-x64 自包含便携版、打包 ZIP、生成 `SHA256SUMS.txt`、创建 GitHub Release
6. 下载 Release 里的 ZIP 解压运行一次，确认能打开图片再公布。

## 预发布版本

需要先给人试用时用 `X.Y.Z-beta.N`（例如 `0.3.0-beta.1`），tag 写成 `v0.3.0-beta.1`。发布时在 GitHub 上勾选 “Set as a pre-release”，这样 README 里的“最新版”链接不会指向它。

## Release 资产命名约定

`release.yml` 用 `github.ref_name`（即 tag，含 `v` 前缀）拼出资产名：

- `Mewview-vX.Y.Z-win-x64-portable.zip`
- `SHA256SUMS.txt`

GitHub 还会自动附加 Source code (zip) 与 Source code (tar.gz)。一个 Release 应当只包含这四类内容：**构建产物 + 校验和 + 两个自动生成的源码包**；不放调试符号、PDB 或未压缩的中间产物。
