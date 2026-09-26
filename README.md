# ISFP-Connect-Lite

ISFP 云际模拟飞行连飞平台 · 轻量化连飞客户端（Windows / WPF）

- 无边框单条 bar UI，深色主题，可拖动
- 当前仅支持 X-Plane（XP11/12，通过 ISFP-xLink 插件通信）
- 服务器：`fsd.flyisfp.com:6809`

## 功能

- 飞行员登录（`#AP`，Network Rating=1，Protocol Revision=9）
- 位置上报（`@` 包 5 秒一次，PBH 编码姿态）
- 管制员/其他飞行员列表 → 推送给 xLink 渲染 CSL
- 文本消息（私聊 / 频率通话 / 接收 MOTD），可收可回
- METAR 请求（`$AX`/`$AR`），转发给 xLink EFB
- 模型匹配应答（PI:GEN / FSIPI）
- "飞行计划"按钮一键跳转 `https://www.flyisfp.com/flight-plan`（网页提交，客户端不发 FP 包）
- 航路查询暂不支持（xLink 查询会返回提示）

协议实现范围与不兼容项见 `docs/FSD-Client-Notes.md`。

## 构建

```powershell
cd src/ISFPConnectLite
dotnet build -c Release
# 产物: bin/Release/net10.0-windows10.0.19041.0/ISFP-Connect-Lite.exe
```

## 自测

```powershell
.\bin\Release\net10.0-windows10.0.19041.0\ISFP-Connect-Lite.exe --selftest
```

覆盖 PBH 编码、各 FSD 包构造/解析、频率换算、认证算法确定性。

## 使用

1. 启动 X-Plane（需已安装 ISFP-xLink 插件，监听 51001 端口）
2. 启动本软件
3. 点 ⚙ 设置：**真实姓名、CID、密码**（保存到软件根目录 `config.json`，可勾选记住密码）
4. bar 上填 **呼号、机型 ICAO、航司 ICAO（涂装）**，点"连线"（任一项缺失/无效都会提示拒绝连线）
5. 在线后 bar 上显示频率、应答机、地速、高度；💬 打开消息面板收发文本；"飞行计划"打开提交网页；⏻ 断开连接

### 连线必填项

| 项 | 填写位置 | 说明 |
| --- | --- | --- |
| 呼号 | bar 主界面 | 如 CCA1234、N123CA |
| 机型 ICAO | bar 主界面 | 4 位代码（A320/B738/C172） |
| 航司 ICAO（涂装） | bar 主界面 | 如 CES、CCA |
| 真实姓名 | 设置 | 登录包 Real Name 字段 |
| CID | 设置 | 平台数字 ID |
| 密码 | 设置 | 平台密码（可勾选记住） |

## 图标

bar 与窗口/任务栏图标统一使用 `src/ISFPConnectLite/assets/logo.png`（当前为占位图，替换同名文件即可，建议 256×256 透明底 PNG）。

## 配置文件

软件根目录 `config.json`（exe 同目录，已加入 .gitignore，含密码请勿提交/外传）：

```json
{
  "Callsign": "CES1234",
  "Cid": "1234",
  "Password": "***",
  "RealName": "quanquan",
  "AircraftIcao": "A20N",
  "AirlineIcao": "CES",
  "RememberPassword": true
}
```

## 项目结构

```
src/ISFPConnectLite/
  Fsd/          FSD 协议层（连接、包构造/解析、会话、自测）
  Xlink/        xLink 插件 TCP JSON 客户端 + 网络数据模型
  Themes/       深色主题资源
  assets/       logo
docs/
  xLink_doc.md  xLink 插件通信协议
  FSD-Client-Notes.md  FSD 实现范围与不兼容项
```
