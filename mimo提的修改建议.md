# ComfyTunnel UI 修改意见

> 目标：在**不改动现有信息架构和布局骨架**的前提下，提升视觉层级、状态表达与一致性。
> 优先级：P0 = 必须改，P1 = 强烈建议，P2 = 锦上添花。

---

## 0. 全局约束（先读这个）

- 颜色只允许出现以下色板，禁止临时新增颜色：

```css
:root {
  --bg:        #0B0B0C;   /* 窗口底色 */
  --surface:   #141416;   /* 卡片/面板 */
  --surface-2: #0E0E10;   /* 输入框/凹陷元素 */
  --border:    rgba(255,255,255,0.07);
  --border-2:  rgba(255,255,255,0.09);
  --accent:    #E8556D;   /* 品牌红，只用于主 CTA 与品牌标 */
  --accent-soft: rgba(232,85,109,0.35);
  --text-1:    #F2F2F3;
  --text-2:    #A9A9AE;
  --text-3:    #6E6E6E;
  --state-ok:   #3FB984;  /* 已连接 */
  --state-warn: #E5A54B;  /* 重连中 / 未保存口令 */
  --state-err:  #E5484D;  /* 连接失败 */
}
```

- **红色配给制**：一屏最多 2 处实心红（一个主 CTA + 一个品牌标）。其余红色一律用 `--accent-soft` 或 hover 才出现。
- **圆角三档**：控件 8px / 卡片 12px / 面板 16px，不得混用。
- **间距**：只用 4 的倍数（4/8/12/16/24/32/48/64）。
- **面板质感**（暗色 UI 不靠阴影靠内高光）：

```css
.panel {
  background: var(--surface);
  border: 1px solid var(--border);
  box-shadow: inset 0 1px 0 rgba(255,255,255,0.05);
  border-radius: 16px;
}
.input {
  background: var(--surface-2);
  border: 1px solid var(--border-2);
  border-radius: 8px;
}
```

- **字体**：
  - 中文统一用 MiSans / HarmonyOS Sans / 思源黑体（三选一，只用一个），字重 400 / 500 / 700。
  - 英文小标签 `letter-spacing: 0.02em`；中文正文不加字距，`line-height: 1.6`。
  - **所有数字**（IP、端口、延迟、时间）用等宽字体（JetBrains Mono 或 IBM Plex Mono）并加 `font-variant-numeric: tabular-nums`。

---

## 1. 左页（主页）

### 1.1 [P0] 主圆钮文案与状态语义

按钮文案必须随状态切换，禁止固定写死：

| 状态 | 按钮文案 | 按钮样式 |
|---|---|---|
| 未配置服务器 | 添加服务器 | 实心 accent |
| 已配置未连接 | 连接 | 实心 accent |
| 连接中 | 连接中…（转圈动画） | 实心 accent，disabled |
| 已连接 | 断开 | **描边按钮，不使用实心红** |
| 连接失败 | 重试 | 实心 accent，下方一行 11px 错误原因（`--state-err`） |

**验收**：5 种状态都有对应 UI，且「断开」不占用实心红配额。

### 1.2 [P0] 「添加服务器」卡片图标

三条横线图标读作「菜单」，改为 `Server` / `HardDrive` 图标，或直接用 `+`。
下拉箭头若不实现真正的服务器列表，就**移除箭头**。

### 1.3 [P1] 服务器列表选中态

若保留列表形态，每项增加：
- 左侧 2px accent 竖条（选中时）
- 右侧延迟徽标（等宽数字 + 状态色：`<100ms` ok / `100-300ms` warn / `>300ms` err）
- hover 背景 `rgba(255,255,255,0.04)`

### 1.4 [P1] 圆钮「舞台」与垂直留白

中段垂直留白断层，圆钮像漂在空中。两种方案二选一：

- **A**：三段式布局 —— `justify-content: space-between` 分为「标题区 / 主操作区 / 页脚」，让空白成为有意的呼吸。
- **B**：圆钮背后加一圈极淡径向辉光（`radial-gradient(rgba(232,85,109,0.10), transparent 70%)` + blur）或点阵网格纹理。

状态文案（「尚未连接」等）**上移至贴圆钮 16px**，不要隔 40px。

### 1.5 [P1] 页脚信息

「本地加密保存」的保密说明移到**密码输入框旁**（见 2.4），页脚只保留状态：连接时长 / 隧道端口 / 本地地址。

---

## 2. 右页（连接设置）

### 2.1 [P0] 表单分组

现在 8 个字段等宽等高、无分组，扫视找不到重点。改为三组，每组前加组标题（12px、500、`--text-3`、`letter-spacing: 0.04em`）：

```
身份        连接名称
────────────────────────────
主机        服务器地址 [        ]   SSH 端口 [ 22 ]
            用户名     [        ]
认证        (密码 | 私钥)          ← segmented control
            SSH 密码   [        ]  ☑ 记住密码 / 私钥口令
────────────────────────────
▸ 从 SSH 命令导入
▸ ComfyUI 连接设置
```

**验收**：三个视觉组清晰可辨，组间用 1px `--border` 分隔线或 24px 间距。

### 2.2 [P0] 「密码 / 私钥」分段控件

现在是两个裸文字 + 暗底方块，affordance 太弱。改为标准 segmented control：

```css
.seg {
  display: inline-flex;
  padding: 3px;
  background: var(--surface-2);
  border: 1px solid var(--border-2);
  border-radius: 999px;
}
.seg .item { padding: 6px 14px; border-radius: 999px; color: var(--text-2); }
.seg .item.active {
  background: var(--accent);
  color: #fff;
  transition: all 150ms ease-out;
}
```

- 切换到「私钥」时，下方字段替换为：私钥路径（带「选择文件」）+ 私钥口令（可选）。
- 切换有 150ms 过渡，不要瞬移。

### 2.3 [P0] 输入框聚焦态 vs 错误态必须可区分

- **聚焦**：`2px var(--accent-soft)` 外环 + 1px `--accent-soft` 边框。
- **错误**：1px `--state-err` 边框 **+ 下方 11px `--state-err` 错误提示文字**。
- 禁止两种状态都只是「红边」。

```css
.input:focus-visible {
  outline: 2px solid var(--accent-soft);
  outline-offset: 1px;
  border-color: var(--accent-soft);
}
.input.error {
  border-color: var(--state-err);
}
.field-error { font-size: 11px; color: var(--state-err); margin-top: 6px; }
```

### 2.4 [P1] 自绘复选框

原生复选框在暗色下不可控。自绘：

- 16×16、圆角 4px、边框 `--border-2`
- 选中：填充 `--accent` + 白色勾（SVG）
- 旁补 11px 灰字「保存在系统钥匙串」—— 这句从页脚移过来

### 2.5 [P1] 折叠行（从 SSH 命令导入 / ComfyUI 连接设置）

现在像静态文本。要求：
- 整行可点击（不是只有 `+` 可点）
- hover 背景 `rgba(255,255,255,0.04)`
- `+` 图标改为 chevron，展开时旋转 90°（150ms）
- 展开内容左侧缩进 4px 与标题对齐

### 2.6 [P1] 输入框 placeholder 必填

placeholder 比标签更能解释字段，全部补上：

| 字段 | placeholder |
|---|---|
| 连接名称 | 例如：训练机 |
| 服务器地址 | example.com 或 192.168.1.10 |
| SSH 端口 | 22 |
| 用户名 | root |
| SSH 密码 | 留空则每次询问 |
| SSH 命令导入 | `ssh -i ~/.ssh/id_rsa user@host -p 2222` |

### 2.7 [P0] 「移除连接」与「保存」分离

- 「移除连接」为**文字按钮**，hover 变 `--state-err`，点击弹二次确认（"确定移除该连接？此操作不可撤销。" / 取消 · 移除）。
- 与右侧「保存并返回」之间用 1px 分隔线或至少 24px 间距隔开。
- 「移除连接」放在**左下**，与保存按钮同一水平线。

---

## 3. 主按钮与窗口控制

### 3.1 [P1] 「保存并返回」按钮规格

```css
.btn-primary {
  height: 44px;
  padding: 0 22px;
  background: var(--accent);
  color: #fff;
  font-weight: 600;
  border-radius: 10px;
  transition: filter 150ms ease-out, transform 150ms ease-out;
}
.btn-primary:hover  { filter: brightness(1.06); }
.btn-primary:active { transform: scale(0.98); }
.btn-primary:disabled { opacity: 0.45; pointer-events: none; }
```

加键盘提示：按钮右侧或 tooltip 显示 `⌘S` / `Ctrl+S`（11px，半透明）。

### 3.2 [P1] 窗口控制钮

- 关闭钮**常态透明无红框**，hover 时 `#E8554D` 底 + 白 ×。
- 三个钮统一 40×40 命中区，视觉图标 12px。

---

## 4. 动效（克制）

- hover / focus：`150ms ease-out`
- 状态切换（未连接→已连接）：`250ms cubic-bezier(0.2, 0.8, 0.2, 1)`
- 连接中：圆钮外圈旋转弧线（`stroke-dasharray` + rotate），不要整圈乱转
- 页面切换（列表 ↔ 设置）：`translateX(12px)` + 淡入
- **一屏不得超过 3 处同时运动的元素**

---

## 5. 验收清单

- [ ] 5 种连接状态（未配置/未连接/连接中/已连接/失败）均有对应 UI
- [ ] 一屏内实心红 ≤ 2 处
- [ ] 输入框聚焦态与错误态肉眼可区分
- [ ] 「密码 / 私钥」为 segmented control，切换时下方字段联动
- [ ] 表单分 3 组，有组标题
- [ ] 所有数字为等宽字体
- [ ] 所有输入框有 placeholder
- [ ] 「移除连接」有二次确认，且与保存按钮视觉分离
- [ ] 圆角只用 8 / 12 / 16 三档
- [ ] 无任何控件使用系统默认样式（原生复选框、原生 select）