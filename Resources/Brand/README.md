# 拾贴图标

`ClipHarbor-source.png` 是带透明边距的青绿色应用图标源图，由 ImageGen 于 2026-10-08 生成。`ClipHarbor.png` 用于应用内展示；`ClipHarbor.icns` 用于 macOS Finder；`ClipHarbor.ico` 用于 Windows 应用、窗口和托盘。

在 macOS 运行 `python3 scripts/generate-icons.py`，从源图生成原生格式及各分辨率。发布构建直接使用已提交的资源。

macOS 菜单栏使用 `Sources/AppBrand.swift` 中的系统 `clipboard` 模板符号，由系统根据菜单栏背景着色。应用图标在 `Resources/Info.plist` 中明确声明，打包脚本会通过 `integration/verify-app-icons.swift` 检查实际应用包。
