 巴巴博一Bot

一个基于 .NET 8 的 QQ 机器人项目，支持：
- QQ 官方 Webhook 接入
- NapCat 接入
- BW / SW / LB 查询与图片渲染
- 绑定玩家、喊话记录、会话统计等功能
- 会自动记录查过的起床战争/排行榜数据（目前就写了这么多）
- 凌晨时段自动扫描在记录数据库的起床战争数据并作记录
- 每日22点-23点59会扫描在查记录人员的单人排行榜记录
- 

## 功能特性
- `/bw`：Bedwars 战绩查询
- `/sw`：Skywars 战绩查询
- `/lb`：排行榜查询
- `!bind`：绑定布吉岛玩家
- /skin add 正版id 战绩卡绑定皮肤头像
- /bg set/icon/cl 设置大小/图标大小/上层颜色
- /bg 上传战绩卡背景
- /ch add 称号 颜色称号（选填） （审核群号这种都是直接写死在了内部 自己按两边shift找吧）
- 图片渲染与图床上传
- 官方 Webhook 回调校验（op=13）
- Webhook 事件去重（防止重复执行）

## 运行环境
- .NET 8 SDK
- Windows Server（建议）
- Microsoft Edge（用于渲染）
- 可选：图床服务（如自建 upload API）

## 快速开始
1. 克隆项目
2. 需要一个可以用的布吉岛token 指路: https://mcbjd.net/docs/api/bugland-api/
3. 安装依赖并构建：
4. 使用IDE(推荐rider）
   dotnet build
5.
在项目根目录创建 pz/config.json，也可以不创建，程序构建完成跑起来会自己创建
6.
启动：
dotnet run --project BedwarsBot
Webhook 配置
•
回调地址示例：https://你的域名/api/qqbot/webhook
•
程序内部监听端口可用 5001/8090，作为ng转发（前提是你是国内服务器，港澳台服务器做中转)
•
建议反向代理后统一走 443
注意事项
•
不要提交真实密钥、Token、数据库文件
•
pz/config.json 仅本地/服务器使用
•
请确保图床地址可达，否则图片发送会失败
免责声明
本项目仅用于学习与技术研究，请遵守平台规则及相关法律法规。
