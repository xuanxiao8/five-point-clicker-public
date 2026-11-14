# 五点轮询连点器

最多配置五个坐标，按指定顺序循环点击或触摸。两个版本均有中文界面，支持调整点位顺序、设置次数、暂停和停止。

| 平台 | 下载 | 源码与文档 |
| --- | --- | --- |
| Windows 11 x64 | [独立运行 EXE](downloads/FivePointClicker.exe) | [源码](windows/) · [使用与编译](windows/使用与编译.md) · [测试报告](windows/实际测试报告.md) |
| Android 8.0 及以上 | [签名 APK](downloads/FivePointAndroid.apk) | [源码](android/) · [安装、编译与实测](android/安装与实测.md) |

Windows 版使用 C#、.NET 10 和 WPF。安卓版本使用 Java 与系统无障碍服务，在 Android 12 模拟器上验证了跨应用触摸，真机待测。

## 贡献者

- [xuanxiao8](https://github.com/xuanxiao8) 提出项目需求并维护仓库。
- [OpenAI Codex](https://github.com/codex) 实现 Windows 和 Android 版本，执行测试，并整理构建与发布资料。
