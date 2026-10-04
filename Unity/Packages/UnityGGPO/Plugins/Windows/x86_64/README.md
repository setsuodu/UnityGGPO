# 默认 Windows 缺少动态库，无法启动

1. 使用 lucasg/Dependencies 分析缺少的库。
2. 从 System32 中找到对应的 dll，放到 Plugins\Windows\x86_64\ 下，与 UnityGGPO.dll 同级。