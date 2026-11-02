# PillionRider

脱战后自动骑上队友的多人坐骑。

## 前置条件

无。装好就能用，不需要任何其它插件。

（插件通过游戏自己的聊天框入口发送指令，走的是 Dalamud 自带的 `FFXIVClientStructs`，不依赖第三方库。）

## 安装

1. 游戏内输入 `/xlsettings`，切到 **实验性** 标签，在 **自定义插件库** 里加入下面任意一条：

   ```
   https://cdn.jsdelivr.net/gh/SmilessNEKO/PillionRider@main/pluginmaster.json
   https://raw.githubusercontent.com/SmilessNEKO/PillionRider/main/pluginmaster.json
   ```

2. 输入 `/xlplugins`，搜索 **PillionRider**，安装并启用。

3. 启用后游戏右上角状态栏会出现 `PillionRider: on`，点一下可切换启停；输入 `/pr` 打开设置窗口。
