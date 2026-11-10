import { IconArrowLeft, IconArrowRight } from '@arco-design/web-react/icon'
import type { Direction } from '../api/types'

/**
 * Who a transfer is exchanged with, from the viewer's side: internal staff see the vendor, a vendor portal account
 * sees the company as "SAA". Outgoing reads "→ 发给 X", incoming "← 来自 X".
 */
function counterpartOf(direction: Direction, companyName: string | null | undefined, realm: 'internal' | 'oem') {
  const outgoing = realm === 'internal' ? direction === 'INTERNAL_TO_OEM' : direction === 'OEM_TO_INTERNAL'
  const name = realm === 'internal' ? companyName || '未知厂商' : 'SAA'
  return { outgoing, name, text: `${outgoing ? '发给' : '来自'} ${name}` }
}

export default function Counterpart({ direction, companyName, realm }: {
  direction: Direction; companyName: string | null | undefined; realm: 'internal' | 'oem'
}) {
  const { outgoing, name } = counterpartOf(direction, companyName, realm)
  return (
    <span className={`oem-counterpart oem-counterpart--${outgoing ? 'out' : 'in'}`} title={`${outgoing ? '发给' : '来自'} ${name}`}>
      {outgoing ? <IconArrowRight /> : <IconArrowLeft />}
      <span className="oem-counterpart-verb">{outgoing ? '发给' : '来自'}</span>
      <span className="oem-counterpart-name">{name}</span>
    </span>
  )
}
