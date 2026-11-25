# MCP tools / MCP 工具表

WpfLens provides the following 39 MCP tools. See the [tool reference](TOOLS_REFERENCE.md) for parameters, examples and usage constraints.

WpfLens 提供以下 39 个 MCP 工具。参数、示例及使用限制详见[工具参考](TOOLS_REFERENCE.md)。

| Tool / 工具 | Description | 中文说明 |
| --- | --- | --- |
| `wpf_list_processes` | List running WPF applications | 列出运行中的 WPF 应用 |
| `wpf_attach` | Attach to an application, optionally injecting Inspector | 附加应用，可自动注入 Inspector |
| `wpf_get_visual_tree` | Read the visual tree hierarchy | 读取可视化树层级 |
| `wpf_get_element_properties` | Read an element's dependency properties | 读取元素的依赖属性 |
| `wpf_find_elements` | Find elements by AutomationId, type, name, text or properties | 按 AutomationId、类型、名称、文本或属性查找元素 |
| `wpf_find_elements_deep` | Search across windows, adorners and popups | 跨窗口、装饰器和弹出层深度搜索 |
| `wpf_get_layout_info` | Read layout metrics | 读取布局信息 |
| `wpf_get_bindings` | Inspect data bindings | 检查数据绑定 |
| `wpf_get_binding_errors` | Read captured binding errors | 查看捕获的绑定错误 |
| `wpf_clear_binding_errors` | Clear captured binding errors | 清除捕获的绑定错误 |
| `wpf_get_data_context` | Inspect DataContext and its inheritance chain | 检查 DataContext 及其继承链 |
| `wpf_evaluate_binding` | Explain a property's value source and binding path | 解释属性值来源及绑定路径 |
| `wpf_get_resources` | Enumerate resource dictionaries | 枚举资源字典 |
| `wpf_get_styles` | Inspect applied styles and templates | 检查已应用的样式与模板 |
| `wpf_explain_triggers` | Evaluate style and template triggers | 分析样式及模板触发器 |
| `wpf_watch_property` | Monitor property changes | 监视属性变化 |
| `wpf_report` | Aggregate binding/validation errors and UI state | 汇总绑定/验证错误及界面状态 |
| `wpf_snapshot` | Capture an element subtree's state | 保存元素子树状态快照 |
| `wpf_diff` | Compare two snapshots | 比较两个快照的差异 |
| `wpf_set_property` | Edit a dependency property at runtime | 运行时修改依赖属性 |
| `wpf_revert_property` | Undo property edits and restore prior values or bindings | 撤销属性编辑，恢复原值或绑定 |
| `wpf_highlight_element` | Highlight an element in the application | 在应用中高亮元素 |
| `wpf_set_input_mode` | Switch mouse and keyboard input between foreground and background for the attached process | 切换已附加进程的前台或后台鼠标、键盘输入模式 |
| `wpf_click_element` | Click a control, including double/right clicks | 点击控件，支持双击和右键 |
| `wpf_move_mouse` | Move or hover at an element-local point | 移动或悬停到元素局部坐标 |
| `wpf_scroll` | Send vertical or horizontal wheel input | 发送垂直或水平滚轮输入 |
| `wpf_drag` | Drag between elements or by displacement | 在元素之间或按位移拖动 |
| `wpf_touch_gesture` | Send tap, hold, swipe, pinch or rotation gestures | 发送点击、长按、滑动、缩放或旋转手势 |
| `wpf_select_item` | Select an item by visible text or index | 按可见文本或索引选择项目 |
| `wpf_wait_for_element` | Wait for an element's existence, visibility or enabled state | 等待元素存在、可见、隐藏或启用 |
| `wpf_set_text` | Set a control's text or value | 设置控件文本或值 |
| `wpf_send_keys` | Send keyboard shortcuts | 发送键盘快捷键 |
| `wpf_capture_screenshot` | Capture an element, window or scrollable content | 截取元素、窗口或完整滚动内容 |
| `wpf_export_tree` | Export the visual tree as XAML or JSON | 将可视化树导出为 XAML 或 JSON |
| `wpf_record` | Start, stop, inspect or clear workflow recording | 开始、停止、检查或清除工作流录制 |
| `wpf_get_recording` | Retrieve recording state and workflow data | 获取录制状态和工作流数据 |
| `wpf_assert` | Assert an exact dependency-property value | 断言依赖属性的精确值 |
| `wpf_replay` | Replay a recorded workflow | 回放录制的工作流 |
| `wpf_export_test` | Export a recording as an xUnit project or workflow JSON | 将录制导出为 xUnit 项目或工作流 JSON |

[English README](../README.md) | [中文 README](../README.zh-CN.md)
