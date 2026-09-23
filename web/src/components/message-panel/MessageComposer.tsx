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
  pasteMessageImages,
  ImageComposer,
}: Props) {
  return (
    <div className="message-composer message-composer--images" onPaste={event => {
      if (!sending) pasteMessageImages(event, images, onImagesChange)
    }}>
      <div className="message-composer-input-row">
        <Input.TextArea
          placeholder="输入留言，Ctrl+Enter 发送"
          value={content}
          onChange={onContentChange}
          autoSize={{ minRows: 2, maxRows: 5 }}
          style={{ flex: 1 }}
          onKeyDown={(event) => {
            if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') onSend()
          }}
        />
        <Button type="primary" icon={<IconSend />} onClick={onSend} disabled={(!content.trim() && !images.length) || loading && listLength === 0} loading={sending}>
          发送
        </Button>
      </div>
      <ImageComposer files={images} onChange={onImagesChange} disabled={sending} />
    </div>
  )
}
