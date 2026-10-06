# ClipHarbor · 拾贴

复制即收纳，随时找回来。macOS 26+ 原生剪贴板管理工具，SwiftUI 与 Liquid Glass。

## MVP

- 自动记录文本、RTF、链接、图片和 Finder 文件引用；相同内容去重。
- 类型分类、文件子类型筛选、搜索、收藏、备注及删除。
- 菜单栏和全局快捷面板：默认 `⌥⌘V`，可切换为 `⌃⌥V`。
- Quick Look 预览；再次复制文件保留 Finder 文件语义。
- 可选自动粘贴（需要辅助功能权限），默认仅复制。
- 登录启动、记录类型、保留期限、数量和图片大小限制、应用排除及清理设置。
- 数据只存在本机 `~/Library/Application Support/ClipHarbor`。删除历史不会删除原文件。

文件只保存原位置引用；移动或删除后可能失效。应用排除以复制时前台应用判断，敏感标记过滤不能识别所有密码。原文件重新定位、导入导出、OCR、同步、任意快捷键录制和应用内更新尚未实现。

## 本地开发

需要 macOS 26、Xcode 26 和 XcodeGen：

```bash
brew install xcodegen
xcodegen generate
open ClipHarbor.xcodeproj
```

Scheme 选择 `ClipHarbor`。运行测试：

```bash
xcodebuild -project ClipHarbor.xcodeproj -scheme ClipHarbor -destination 'platform=macOS' test
```

生成 Apple Silicon / Intel 通用 ZIP 和 DMG：

```bash
bash scripts/build.sh
```

## 自动构建与发布

推送 `main`、提交 PR 或手动运行 Actions：测试并上传 ZIP、DMG 和 SHA256 校验文件。
推送 `v*` 标签：通过测试后自动创建 GitHub Release。

```bash
git tag v0.1.0
git push origin v0.1.0
```

默认产物使用 ad-hoc 签名，没有 Apple 公证，适合开发和自用；下载后可能被 Gatekeeper 拦截。正式独立分发需 Apple Developer Program，并设置仓库 Actions Secrets：

| Secret | 内容 |
| --- | --- |
| APPLE_CERTIFICATE_P12_BASE64 | Developer ID Application 证书及私钥的 P12，Base64 编码 |
| APPLE_CERTIFICATE_PASSWORD | P12 导出密码 |
| APPLE_SIGNING_IDENTITY | Developer ID Application 签名身份完整名称 |
| APPLE_ID | 公证账号 |
| APPLE_APP_PASSWORD | Apple 应用专用密码 |
| APPLE_TEAM_ID | Developer Team ID |

配置完整后，标签发布会签名并公证应用及 DMG。不要将证书或密码提交到仓库。

## 手工验收

复制纯文本、带格式文本、链接、截图、单个及混合文件；确认分类、去重、搜索和复制结果。
测试删除原文件后的失效反馈、暂停记录、排除应用、收藏保留和图片缓存清理。
在多显示器及全屏应用中唤起面板，确认关闭后恢复原应用焦点。
自动粘贴分别测试未授权、授权和目标应用焦点改变的情况。
