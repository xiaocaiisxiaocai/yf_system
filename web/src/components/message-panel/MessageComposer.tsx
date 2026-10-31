import { Button, Input } from '@arco-design/web-react'
import { IconSend } from '@arco-design/web-react/icon'
import type { ClipboardEvent, ComponentType } from 'react'

interface Props {
  content: string
  images: File[]
  sending: boolean
  loading: boolean
  listLength: number
  onContentChange: (value: string) => void
  onImagesChange: (files: File[]) => void
  onSend: () => void
  /** 供应商共用账号才显示签名框；内部账号不签名。 */
  signerName?: string
  onSignerNameChange?: (value: string) => void
  pasteMessageImages: (
    event: ClipboardEvent<HTMLElement>,
    files: File[],
    onChange: (files: File[]) => void,
  ) => void
  ImageComposer: ComponentType<{
    files: File[]
    onChange: (files: File[]) => void
    disabled?: boolean
  }>
}

export function MessageComposer({
  content,
  images,
  sending,
  loading,
  listLength,
  onContentChange,
  onImagesChange,
  onSend,
  signerName,
  onSignerNameChange,
  pasteMessageImages,
  ImageComposer,
}: Props) {
  return (
    <div className="message-composer message-composer--images" onPaste={event => {
      if (!sending) pasteMessageImages(event, images, onImagesChange)
    }}>
      <Input.TextArea
        className="message-composer-input"
        placeholder="输入留言，Ctrl+Enter 发送"
        value={content}
        onChange={onContentChange}
        autoSize={{ minRows: 2, maxRows: 5 }}
        onKeyDown={(event) => {
          if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') onSend()
        }}
      />
      <div className="message-composer-footer">
        <ImageComposer files={images} onChange={onImagesChange} disabled={sending} />
        {onSignerNameChange && <Input
          className="message-composer-signer"
          size="small"
          aria-label="签名"
          placeholder="签名（提供人姓名）"
          value={signerName}
          maxLength={32}
          disabled={sending}
          onChange={onSignerNameChange}
        />}
        <Button className="message-composer-send" type="primary" size="small" icon={<IconSend />} onClick={onSend} disabled={(!content.trim() && !images.length) || loading && listLength === 0} loading={sending}>
          发送
        </Button>
      </div>
    </div>
  )
}
