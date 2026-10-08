import { Alert, Image, Typography } from '@arco-design/web-react'
import videoViewsExample from '../assets/upload-examples/supplier-video-views.jpg'
import adjustmentNotesExample from '../assets/upload-examples/supplier-adjustment-notes.jpg'
import stepAssemblyExample from '../assets/upload-examples/company-step-assembly.jpg'
import motionFlowExcelExample from '../assets/upload-examples/company-motion-flow-excel.jpg'
import { MOTION_FLOW_NAMING_EXAMPLE } from '../utils/uploadMaterialRules'

export type UploadDirection = 'C2S' | 'S2C'

/** 上传弹窗顶部的资料要求：公司发给供应商时列出硬性要求并附示例图，供应商发给公司时只展示资料示例。 */
export default function UploadMaterialGuide({ direction }: { direction: UploadDirection }) {
  if (direction === 'C2S') {
    return (
      <div className="upload-material-guide">
        <Alert
          type="info"
          title="发给供应商的资料要求"
          content={(
            <ol className="upload-material-guide-list">
              <li>
                <b>A. STEP 格式 3D 图（必需，至少 1 个 .step/.stp）</b>：机台与 ROBOT 相关总装图，有干涉风险的机构都要在总图内，
                如载具对接机构、输送机构、空压盒和手臂会经过的机构等。
              </li>
              <li>
                <b>B. 动作流程说明 Excel</b>：命名为「{MOTION_FLOW_NAMING_EXAMPLE}」（扩展名可为 .xls/.xlsx/.xlsm/.xlsb；每段 X 为 1~10 位，空格个数不限，机/機、动作/動作均可），
                并注明具体使用的手臂规格，如 15KG、25KG。
              </li>
            </ol>
          )}
        />
        <div className="upload-material-examples" role="group" aria-label="资料要求示例">
          <figure>
            <Image src={stepAssemblyExample} alt="A. STEP 格式 3D 总装图示例" width="100%" />
            <figcaption>A. STEP 格式 3D 总装图示例</figcaption>
          </figure>
          <figure>
            <Image src={motionFlowExcelExample} alt="B. 动作流程说明 Excel 示例" width="100%" />
            <figcaption>B. 动作流程说明 Excel 示例</figcaption>
          </figure>
        </div>
      </div>
    )
  }
  return (
    <div className="upload-material-guide" role="group" aria-label="上传资料示例">
      <Typography.Text type="secondary">资料示例（仅供参考，不限制上传内容）：</Typography.Text>
      <div className="upload-material-examples">
        <figure>
          <Image src={videoViewsExample} alt="A. 三个以上视角视频（2 个以上循环动作）示例" width="100%" />
          <figcaption>A. 三个以上视角视频（2 个以上循环动作）</figcaption>
        </figure>
        <figure>
          <Image src={adjustmentNotesExample} alt="B. 调整信息说明示例" width="100%" />
          <figcaption>（如有）B. 调整信息说明</figcaption>
        </figure>
      </div>
    </div>
  )
}
