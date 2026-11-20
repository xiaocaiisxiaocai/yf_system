import { IconArrowLeft, IconArrowRight } from '@arco-design/web-react/icon'
import type { Direction } from '../api/types'

interface CounterpartSource {
  direction: Direction
  companyName: string | null | undefined
  /** 厂商发出时选择的公司接收人（仅姓名）。 */
  recipientNames?: readonly string[]
  /** 公司发出时的发送人姓名。 */
  senderName?: string
}

/**
 * Who a transfer is exchanged with, from the viewer's side. Internal staff see the vendor (and, for a vendor-sent
 * transfer, the people it was addressed to); a vendor portal account sees the people on the company side — the chosen
 * recipients for what it sent, the sender for what it received — and only falls back to "SAA" without a name.
 * Outgoing reads "→ 发给 X", incoming "← 来自 X".
 */
function counterpartOf({ direction, companyName, recipientNames = [], senderName }: CounterpartSource, realm: 'internal' | 'oem') {
  const outgoing = realm === 'internal' ? direction === 'INTERNAL_TO_OEM' : direction === 'OEM_TO_INTERNAL'
  const vendorSide = recipientNames.join('、') || 'SAA'
  const name = realm === 'internal' ? companyName || '未知厂商'
    : outgoing ? vendorSide : senderName?.trim() || 'SAA'
  // Internal staff see who a vendor-sent transfer was addressed to next to the vendor.
  const people = realm === 'internal' && direction === 'OEM_TO_INTERNAL' ? recipientNames.join('、') : ''
  return { outgoing, name, people }
}

export default function Counterpart(props: CounterpartSource & { realm: 'internal' | 'oem' }) {
  const { realm, ...source } = props
  const { outgoing, name, people } = counterpartOf(source, realm)
  const verb = outgoing ? '发给' : '来自'
  return (
    <span className={`oem-counterpart oem-counterpart--${outgoing ? 'out' : 'in'}`} title={`${verb} ${name}${people ? `（接收人：${people}）` : ''}`}>
      {outgoing ? <IconArrowRight /> : <IconArrowLeft />}
      <span className="oem-counterpart-verb">{verb}</span>
      <span className="oem-counterpart-name">{name}</span>
      {people && <span className="oem-counterpart-people">（接收：{people}）</span>}
    </span>
  )
}
