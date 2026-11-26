import type { PropsWithChildren } from 'react'
import { Popconfirm } from '@arco-design/web-react'
import type { PopconfirmProps } from '@arco-design/web-react'

/** 破坏性确认气泡：默认将确认按钮标为危险红，并保留调用方传入的其余 okButtonProps。 */
export default function DangerPopconfirm({ okButtonProps, ...rest }: PropsWithChildren<PopconfirmProps>) {
  return (
    <Popconfirm
      {...rest}
      okButtonProps={{ status: 'danger', ...okButtonProps }}
    />
  )
}
