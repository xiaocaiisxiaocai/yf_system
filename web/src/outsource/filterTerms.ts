/** 与服务端同一个切词规则：粘贴的多行每行是一个完整词，单行才按空白和标点拆开。 */
export function filterTerms(text: string): string[] {
  return text.includes('\n')
    ? text.split('\n').map((line) => line.trim()).filter(Boolean)
    : text.split(/[\s,，;；、]+/).filter(Boolean)
}
