/** Columns of the IPMS MES monitoring table, in IPMS order: `prop` is the MES field name, `width` the width IPMS gives it. */
export interface MesColumn { label: string; prop: string; width: number }
export interface MesGroup { label: string; columns: MesColumn[] }
export type MesLayoutItem = MesColumn | MesGroup

const col = (label: string, prop: string, width: number): MesColumn => ({ label, prop, width })

/** A stage with plan date, actual date and completion ratio, named by the MES field prefix. */
const stage = (label: string, prefix: string, ratioWidth = 60, actualProp = `${prefix}ActualDate`): MesGroup => ({
  label,
  columns: [col('计划完成', `${prefix}PlanDate`, 85), col('实际完成', actualProp, 85), col('完成占比', `${prefix}Workorderbgbl`, ratioWidth)],
})

const charge = (label: string, prefix: string, width: number) => col(label, `${prefix}ChargeUser`, width)

export const isGroup = (item: MesLayoutItem): item is MesGroup => 'columns' in item

/** Columns that stay in view on the left while the rest scrolls. */
export const MES_FIXED_COLUMNS: MesColumn[] = [
  col('客户名称', 'customerName', 100), col('工单号', 'workOrder', 85), col('机型', 'deviceModels', 110),
  col('计划交期', 'planEndDate', 100), col('客户交期', 'customerEndDate', 100), col('生产批次', 'productionBatch', 100),
]

export const MES_LAYOUT: MesLayoutItem[] = [
  { label: '影响天数', columns: [col('功变单', 'functionChangeDay', 60), col('厂内异常', 'innerExceptionDay', 80), col('备料超期', 'materialDay', 80)] },
  col('签收日期', 'applyEndTime', 85),
  {
    label: '研发机构',
    columns: [
      col('BOM完成', 'bomActualDate', 100), col('BOM计划', 'bomPlanDate', 85), col('完成占比', 'bomReportProgress', 60), col('责任人', 'bomChargeUser', 100),
      col('2D计划', 'twodPlanDate', 85), col('2D完成', 'twodActualDate', 100), col('完成占比', 'twodReportProgress', 60), col('责任人', 'twodChargeUser', 100),
      col('机架图计划', 'jjtPlanDate', 85), col('机架图实际', 'jjtActualDate', 85), col('完成占比', 'jjtReportProgress', 60), col('责任人', 'jjtChargeUser', 100),
    ],
  },
  {
    label: '研发电控',
    columns: [col('计划完成', 'yfdkPlanDate', 85), col('实际完成', 'yfdkActualDate', 85), col('责任部门', 'yfdkChargeUser', 150), col('完成占比', 'yfdkWorkorderbgbl', 60)],
  },
  {
    label: '采购',
    columns: [
      col('计划完成', 'cgPlanDate', 85), col('实际完成', 'cgActualDate', 85), col('责任部门', 'cgChargeUser', 150), col('进料比', 'poNum', 60),
      col('进料日', 'poDate', 100), col('领料日期', 'pickDate', 100), col('完成占比', 'cgWorkorderbgbl', 60),
    ],
  },
  stage('制造-组装', 'zzzz'),
  stage('制造-自控', 'zzzk', 60, 'zzzkACtualDate'),
  stage('制造-组装调试', 'zzzzts'),
  stage('制造-自控调试', 'zzzkts'),
  stage('自主检查-组装', 'zzjczz'),
  stage('自主检查-自控', 'zzjczk'),
  stage('智能化准备', 'znhzb'),
  stage('可视化联调', 'kshlt'),
  stage('智能化联调', 'znhlt'),
  stage('ORT-组装', 'ortzz', 80),
  stage('ORT-自控', 'ortzk'),
  stage('带板验证', 'dbyz'),
  stage('成品稽核', 'cpjh'),
  stage('验机', 'yj'),
  stage('清洁打包-自控', 'qjdbzk'),
  stage('清洁打包-组装', 'qjdbzz'),
  {
    label: '责任单位',
    columns: [
      charge('制造-组装', 'zzzz', 150), charge('制造-自控', 'zzzk', 150), charge('制造-组装调试', 'zzzzts', 250), charge('制造-自控调试', 'zzzkts', 250),
      charge('自主检查-组装', 'zzjczz', 150), charge('自主检查-自控', 'zzjczk', 150), charge('智能化准备', 'znhzb', 250), charge('可视化联调', 'kshlt', 250),
      charge('智能化联调', 'znhlt', 250), charge('ORT-组装', 'ortzz', 150), charge('ORT-自控', 'ortzk', 150), charge('带板验证', 'dbyz', 150),
      charge('成品稽核', 'cpjh', 150), charge('验机', 'yj', 150), charge('清洁打包-组装', 'qjdbzz', 250), charge('清洁打包-自控', 'qjdbzk', 250),
    ],
  },
  col('数量', 'deviceNum', 60), col('原交期', 'deliveryDate', 85), col('交期倒计时', 'deliveryCountDown', 120), col('已报工工时', 'workHource', 120),
  col('工单状态', 'productionStatus', 100), col('工艺', 'processName', 120), col('原交期天数', 'historyDay', 150), col('启用后剩余天数', 'startDay', 150),
  col('出机时间', 'outMachineDate', 100), col('是否超期', 'delayType', 100), col('出机状态', 'outMachineStatus', 100), col('制程', 'manufacturingProcess', 200),
]

/** Fields MES sends as a code that IPMS shows as words; an unknown code is shown as it is. */
export const MES_CODE_TEXT: Record<string, Record<string, string>> = {
  productionStatus: { '0': '未分配', '1': '已分配', '2': '任务中', '3': '任务完成', '4': '取消审批中' },
  delayType: { '0': '否', '1': '是' },
  outMachineStatus: { '0': '未出机', '1': '已出机' },
}
