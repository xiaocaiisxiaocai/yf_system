/** 重新加载集中在这里。location.reload 在测试环境里不能替换。 */
export const documentNavigation = {
  reload: () => window.location.reload(),
}
