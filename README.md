# POOLS Freecam Plugin

为 Steam 游戏 **POOLS**（Tensori 开发的步行模拟游戏）开发的自由无重力穿墙视角 Mod。

## 功能

- **F1** — 开关 freecam 模式
- **WASD** — 前后左右移动
- **Q/E** — 下降/上升
- **鼠标** — 环视
- **Left Shift** — 加速

## 效果

freecam 模式下会创建一个全新的独立相机，完全脱离玩家层级，自由飞行、穿墙、无重力。

---

## 问题定位与解决流程

以下记录按实际排查顺序整理。每项都区分了日志或程序集能证明的事实，以及仍需进游戏确认的表现。

### 1. 先确认加载链路

- 游戏运行在 Unity **6000.0.61f1**、IL2CPP、.NET 6；interop 程序集由 BepInEx 6 bleeding edge **be.791** 生成。
- 最初试过 BepInEx 6 pre.2 和 MelonLoader 0.7.3；前者的 Cpp2IL 不支持该游戏的 metadata v31.1，后者的 `version.dll` 代理没有被游戏加载。
- 后来确认 be.791 能生成 interop assemblies，插件日志也出现 `Chainloader startup complete` 和 `POOLS Freecam ... loaded`，说明加载器与插件入口已工作。

### 2. 根据异常调用栈修复旧 DLL 问题

**现象**：日志报 `System.NotSupportedException: Method unstripping failed`，调用栈指向 `UnityEngine.Object.FindObjectOfType<Rigidbody>()` 和 `InputGetAxisPatch.Postfix`。

**定位**：源码中的通用刚体搜索在该 IL2CPP 环境下触发了不支持的方法调用。检查时间戳后还发现游戏目录里的 DLL 比修改后的源码旧，游戏当时运行的仍是旧构建。

**处理**：删除“搜索任意 Rigidbody 并清零速度”的做法，改为从游戏控制器本身处理玩家移动；重新构建，并在游戏退出后替换插件 DLL。

**验证**：构建成功；构建产物与部署 DLL 的 SHA-256 一致。日志里的 `SteamUserStats.SetStat` 失败和 DLSS3 警告不在插件调用栈中，与这次异常分开处理。

### 3. 针对性停用玩家控制器

**现象**：切换到独立相机后，玩家角色仍可能响应移动输入。

**定位**：`GetAxis/GetAxisRaw` 是旧 Input Manager 接口，不能覆盖游戏通过新 Input System 读取的所有输入。检查 `Assembly-CSharp.dll` 后，找到主控制器单例 `FPSController.Instance`；其组件包含玩家移动和镜头更新逻辑。程序集里还存在 `motorFPSController`、`rbFPSController` 等其他类型，因此不再猜测某个刚体就是玩家。

**处理**：freecam 开启时暂时禁用 `FPSController.Instance`，记录它原本的 `enabled` 状态；退出 freecam 时按记录恢复。若单例为空，插件会写警告日志。

### 4. 把自由相机更新放到绘制前

**现象**：相机移动和鼠标输入原先都在 `Input.GetAxis("Mouse X")` 的 Harmony Postfix 中处理，更新时机依赖游戏何时读取该轴。

**定位**：Unity 6 interop 暴露了 `RenderPipelineManager.beginCameraRendering`，其回调带有当前相机参数，可以只在 freecam 相机绘制前更新变换。当前的构建代码也保留了渲染回调不能注册时的旧输入轴回退路径。

**处理**：将 F1 检测、鼠标读取、相机旋转和位移迁到该渲染回调；Harmony 输入 hook 只在 freecam 开启时屏蔽玩家读取的旧 Input 轴。读取鼠标时使用标志位，避免插件自己的 `GetAxis` 调用又被同一个 hook 清零。

**验证状态**：1.1.0 已构建并部署，构建成功且部署文件哈希一致。重新进游戏后应看到 `render callback registered`，随后看到一次 `Render callback is active`。是否消除体感卡顿仍需实际转动相机验证；若回调正常但仍顿挫，再检查帧时间和渲染负载。

## 已知限制 / 后续可优化

1. **玩家移动屏蔽依赖主控制器单例**：freecam 开启时会尝试禁用 `FPSController.Instance`，退出时恢复其原有启用状态。如果单例不存在，日志会提示玩家仍可能移动。插件不会清零场景中随便找到的 Rigidbody，以免误改非玩家物理对象。

2. **相机更新入口**：鼠标采样和自由相机移动优先在 SRP 的 `beginCameraRendering` 回调中处理，使变换在该相机绘制前更新；若渲染回调无法注册，则回退到原来的 `Input.GetAxis("Mouse X")` 更新方式。

3. **按键检测为 Windows 专用**：目前用 user32.dll 的 GetAsyncKeyState；可考虑检查游戏窗口焦点，避免切出游戏时仍响应全局按键。

4. **相机恢复**：开启失败时会清理临时相机并保持模式关闭；关闭时恢复原相机原有的 enabled 状态。若游戏在 freecam 期间销毁或替换原相机，仍需要针对场景切换做额外处理。

---

## 文件结构

```
Pools/
├── BepInEx/
│   ├── core/           # BepInEx 核心文件
│   ├── interop/        # Il2CppInterop 生成的 interop DLLs
│   └── plugins/
│       └── FreecamPlugin.dll  # 本插件
├── FreecamPlugin/      # 插件源码项目
│   ├── Plugin.cs
│   └── FreecamPlugin.csproj
└── POOLS.exe
```

## 技术栈

- BepInEx 6.0.0-be.791 (IL2CPP bleeding edge)
- Il2CppInterop
- HarmonyX
- .NET 6
- Windows API (user32.dll GetAsyncKeyState)

## 关键经验

1. **先读调用栈，再判断错误归属**：插件异常与同一份日志里的 Steam 或图形警告不一定相关。
2. **确认实际加载的产物**：源码改好不代表游戏已经加载新 DLL；比较时间戳和 SHA-256 可以排除旧构建。
3. **按组件职责处理角色移动**：不要猜测场景里的哪个刚体属于玩家；优先识别游戏控制器，并保存其状态以便恢复。
4. **区分游戏方法与 Unity 原生方法**：不能因为 Unity 有 `Camera.LateUpdate` 或 `Camera.Render` 的概念，就假设 IL2CPP 里一定有可供 Harmony 调用的 managed 方法。
5. **把相机变换安排在该相机绘制前**：freecam 优先使用 `beginCameraRendering`；仍需在游戏运行时确认回调确实触发、鼠标手感符合预期。
6. **Win32 按键读取绕过 Unity 输入后端，但属于全局读取**：切出游戏时也可能响应，后续可按需增加窗口焦点检查。

