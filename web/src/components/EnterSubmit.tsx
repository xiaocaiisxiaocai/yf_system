/**
 * 放在弹窗表单末尾：确认按钮在弹窗页脚、不在表单内，浏览器默认不会按 Enter 提交。
 * 这个不可见的提交按钮让输入框里按 Enter 触发表单的 onSubmit。
 */
export default function EnterSubmit() {
  return <button type="submit" className="visually-hidden" tabIndex={-1} aria-hidden="true" />
}
