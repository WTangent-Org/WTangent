# Changelog

## [0.7.1](https://github.com/WTangent-Org/WTangent/compare/v0.7.0...v0.7.1) (2026-08-22)


### 🐛 修复

* release.yml deps 检出指向 WTangent-Org 新仓库 ([57c1972](https://github.com/WTangent-Org/WTangent/commit/57c197220a0dc607827285a0c21c8a456838b7d2))

## [0.7.0](https://github.com/WTangent-Org/WTangent/compare/v0.6.0...v0.7.0) (2026-08-22)


### ✨ 新功能

* Application 宿主实现并注入组件（轮 A） ([a644cb8](https://github.com/WTangent-Org/WTangent/commit/a644cb8ce968c13ba38e643ab9b965a9a4ff525e))
* components.json 加 git 组件，移除 web 组件条目（web 命令归 tui） ([cfdf51e](https://github.com/WTangent-Org/WTangent/commit/cfdf51eadd9077549a0f731b1891fd3d8b7dc158))
* IEntry 元组命令（父路径挂接）+ 三形态（cmd/sub/tool）+ 类型字段废弃 ([b6b4bd7](https://github.com/WTangent-Org/WTangent/commit/b6b4bd7e377138ad374c2a2d0bbecfe1345b0568))
* IEntry 手写入口（0.0.3）——类型字段废弃，能力由 Entry 声明（Commands/Default/Tools + StartAsync 生命周期） ([2155c8a](https://github.com/WTangent-Org/WTangent/commit/2155c8a8b7b9dcda249dfc667599f72135e24062))
* wtangent 空壳启动器 + Client 接收器（WTangent-Org 全新开始） ([5397d48](https://github.com/WTangent-Org/WTangent/commit/5397d48ac0ecec1dbc5a538199dfc9ebd32f6e2d))
* 组件类型收敛 ui/cmd/tool + client 组件拆分（remote/run/web 归 client；tui 纯 UI；serve type=cmd；官方组件自动安装） ([fcfa0ef](https://github.com/WTangent-Org/WTangent/commit/fcfa0efacdf7e200fef10e971068e85daf42daab))


### 🐛 修复

* ci deps checkout 指向新仓库 WTangent-Org（旧 wtommy932 地址已失效） ([4b13b39](https://github.com/WTangent-Org/WTangent/commit/4b13b396961612706cbcf8bb7fca21a594045533))
* release.yml 重复头部 ([fe8c14f](https://github.com/WTangent-Org/WTangent/commit/fe8c14f084892cd3ba456802476b428a6fdba566))


### 🧹 其他

* csproj 文件名统一 WTangent.*（workflow/release-please/deps 引用同步） ([398f0a8](https://github.com/WTangent-Org/WTangent/commit/398f0a8d548dfb1c406646daae5884c077eee414))

## [0.6.0](https://github.com/wtommy932/WtAgent/compare/v0.5.0...v0.6.0) (2026-08-19)


### ✨ 新功能

* 组件命令注册到空壳命令树（Entry.Commands + Default） ([a6d8cd6](https://github.com/wtommy932/WtAgent/commit/a6d8cd642a69fc42c348af30a86de043ae46b43d))
* 组件改 dll 加载——zip 下载解压 + 编译期引用（deps）+ Entry.Run 调用 ([d95b461](https://github.com/wtommy932/WtAgent/commit/d95b46183f63d892efbc4e64b844da4bbd3d8bb6))
* 组件索引化（apt 模式）——components.json 上 GitHub，update/remove 命令，Entry/hasDefault 反射推导，启动静默刷新+版本提示 ([c1d3878](https://github.com/wtommy932/WtAgent/commit/c1d3878f61b798beb62af1515f7471e68cf539e9))
* 组件表/索引/安装脚本同步 WtAgent（前缀统一，组件仓库改名） ([8dbdcfb](https://github.com/wtommy932/WtAgent/commit/8dbdcfbb68cca03fb74e21bb075ab075835927ce))
* 顶级 headless 自动选 gui/tui（有桌面→gui，无桌面→tui，不可用回退） ([b1f5298](https://github.com/wtommy932/WtAgent/commit/b1f529847b2b6cffac05ec81142e03def62224ae))


### 🐛 修复

* 版本对比去 v 前缀（.version 存 tag 带 v） ([6e18804](https://github.com/wtommy932/WtAgent/commit/6e188041c3073006c8f17ef07f3e6da007a0b6d3))
* 静态 HttpClient Timeout 构造初始化（复用不再抛异常）+ 索引超时放宽 15s + 失败信息带真实异常 ([dfcc789](https://github.com/wtommy932/WtAgent/commit/dfcc78988b44c4eff31a2fdcdb87be5a1b0e51f7))

## [0.5.0](https://github.com/wtommy932/Agent/compare/v0.4.0...v0.5.0) (2026-08-18)


### ✨ 新功能

* install server/client + 未装提示下载 + 未知子命令透传 client（wsl 式） ([7f8ce28](https://github.com/wtommy932/Agent/commit/7f8ce282e44dc765ffa6a35ef976ab231a73e34b))

## [0.4.0](https://github.com/wtommy932/Agent/compare/v0.3.0...v0.4.0) (2026-08-18)


### ✨ 新功能

* 构建失败禁合并（轮询只等 CLEAN） ([f1d356e](https://github.com/wtommy932/Agent/commit/f1d356edf5c195d637a6f38ef24486e368eb7b1b))


### 🐛 修复

* 自动合并轮询显式 -R 仓库并暴露错误（诊断 UNKNOWN） ([bfb920d](https://github.com/wtommy932/Agent/commit/bfb920d5db9f4b321552f3d87b18b5f8bd3ae7ed))


### 🧹 其他

* 移除误提交的构建产物（bin/obj，.gitignore 生效） ([3fed9b1](https://github.com/wtommy932/Agent/commit/3fed9b17f635f98d48ad1749e3b433c24cf098b7))

## [0.3.0](https://github.com/wtommy932/Agent/compare/v0.2.0...v0.3.0) (2026-08-18)


### ✨ 新功能

* agent upgrade 检查并更新组件（.version 记录已装版本） ([b5b5ae9](https://github.com/wtommy932/Agent/commit/b5b5ae9929159e9d92eccf58a0180af4525b524b))


### 🧹 其他

* README（空壳仓库极简内容） ([b5b5ae9](https://github.com/wtommy932/Agent/commit/b5b5ae9929159e9d92eccf58a0180af4525b524b))

## [0.2.0](https://github.com/wtommy932/Agent/compare/v0.1.0...v0.2.0) (2026-08-18)


### ✨ 新功能

* agent 启动器——install serve|client + 顶级启动 client + serve（未来 headless 自动选 GUI/TUI） ([cad6f33](https://github.com/wtommy932/Agent/commit/cad6f33b78ab6c900bcfbd337c5990b659167dc5))
* 无则自动下载 + headless 检查（未来 GUI/TUI 切换） ([043a526](https://github.com/wtommy932/Agent/commit/043a5264233c490a27f93ed6e221379159ee02f0))


### 🐛 修复

* AssetName 按平台后缀（匹配 Server/Client release 资产命名） ([e8d180f](https://github.com/wtommy932/Agent/commit/e8d180f4eda05220826afbdf8aac5d6ec1f65d2b))
* serve 组件指向 Agent.Server 仓库（改名后下载 404） ([db04262](https://github.com/wtommy932/Agent/commit/db042622006131315f0feec8d89232b3b9ad7316))


### 🧹 其他

* 换官方 Dotnet.gitignore（github/gitignore） ([985620e](https://github.com/wtommy932/Agent/commit/985620e9f4ccd30db8dc1e5b2841d06e17e01758))
* 接入 install 脚本 + release-please + 七平台 agent 资产 CI ([db04262](https://github.com/wtommy932/Agent/commit/db042622006131315f0feec8d89232b3b9ad7316))
